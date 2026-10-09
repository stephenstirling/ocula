using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

public sealed record FaceStatus(bool Running, bool Waiting, int Done, int Total);

/// <summary>
/// Scans photos for faces in the background (only while you're not busy) and sorts each new
/// face into a group of similar faces.
///
/// Grouping is deliberately cautious: a face joins an existing group only if it is clearly
/// close to that group's average face; otherwise it starts a new group. Two groups for one
/// person is easy to fix (give both the same name and they merge); one group holding two
/// people would be worse.
/// </summary>
public sealed class FaceIndexer
{
    /// <summary>Cosine similarity needed to join a group. SFace's own "same person" cut-off is 0.363; this is stricter.</summary>
    private const float JoinThreshold = 0.45f;

    private readonly IndexService _index;
    private readonly FaceService _faces;
    private CancellationTokenSource? _cancel;
    private Task? _task;

    public event Action<FaceStatus>? StatusChanged;
    public event Action? GroupsChanged;

    public bool IsRunning => _task is { IsCompleted: false };

    public FaceIndexer(IndexService index, FaceService faces)
    {
        _index = index;
        _faces = faces;
    }

    public static bool Enabled => AppSettings.PeopleEnabled && FaceModels.IsInstalled;

    public void Start()
    {
        if (!Enabled || IsRunning) return;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _task = Task.Run(() => RunAsync(token));
    }

    public void Stop() => _cancel?.Cancel();

    private async Task RunAsync(CancellationToken cancel)
    {
        try
        {
            if (!await _faces.LoadAsync()) return;

            // The groups live in memory while scanning, and are saved with each photo.
            var groups = (await _index.GetCentroidsAsync()).ToDictionary(g => g.Id, g => (g.Centroid, g.Count));
            var (done, total) = await _index.GetFaceProgressAsync();
            Report(true, false, done, total);

            while (!cancel.IsCancellationRequested)
            {
                var batch = await _index.GetUnscannedAsync(30);
                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), cancel); // the crawler may still be adding photos
                    (done, total) = await _index.GetFaceProgressAsync();
                    if (done >= total) break;
                    continue;
                }

                foreach (var (path, modified) in batch)
                {
                    cancel.ThrowIfCancellationRequested();

                    if (SmartIndexer.SecondsSinceInput() < 1.5)
                    {
                        Report(true, true, done, total);
                        while (SmartIndexer.SecondsSinceInput() < 1.5) await Task.Delay(500, cancel);
                    }

                    var found = await ScanAsync(path);
                    var placed = new List<(FaceResult, long)>();
                    var changed = new Dictionary<long, (float[] Centroid, int Count)>();

                    foreach (var face in found)
                    {
                        var id = await PlaceAsync(face.Embedding, groups, changed);
                        placed.Add((face, id));
                    }
                    await _index.SaveFacesAsync(path, modified, placed, changed);

                    done++;
                    if (found.Count > 0) GroupsChanged?.Invoke();
                    if (done % 10 == 0) Report(true, false, done, total);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }
        catch
        {
            // A model or file problem shouldn't take the app down; scanning just stops.
        }
        finally
        {
            var progress = (0, 0);
            try { progress = await _index.GetFaceProgressAsync(); } catch { }
            Report(false, false, progress.Item1, progress.Item2);
            GroupsChanged?.Invoke();
        }
    }

    private async Task<List<FaceResult>> ScanAsync(string path)
    {
        try
        {
            // Skip OneDrive online-only files: reading them would download them.
            var attributes = (int)new FileInfo(path).Attributes;
            if ((attributes & (0x00040000 | 0x00400000 | (int)FileAttributes.Offline)) != 0) return new List<FaceResult>();

            // 960 px on the long edge is enough to see faces in most photos without being slow.
            var bytes = await ThumbnailService.GetBytesAsync(path, 960, allowDecode: true);
            return bytes == null ? new List<FaceResult>() : await Task.Run(() => _faces.Analyse(bytes));
        }
        catch
        {
            return new List<FaceResult>(); // unreadable: recorded as scanned so it isn't retried forever
        }
    }

    /// <summary>Finds the closest group, or starts a new one, and updates that group's average face.</summary>
    private async Task<long> PlaceAsync(float[] embedding,
        Dictionary<long, (float[] Centroid, int Count)> groups,
        Dictionary<long, (float[] Centroid, int Count)> changed)
    {
        long bestId = 0;
        var bestScore = JoinThreshold;
        foreach (var (id, group) in groups)
        {
            var score = Dot(embedding, group.Centroid);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        if (bestId == 0)
        {
            bestId = await _index.CreatePersonAsync(embedding);
            groups[bestId] = (embedding.ToArray(), 0);
        }

        // New average = old average weighted by how many faces it already holds, plus this face.
        var (centroid, count) = groups[bestId];
        var merged = new float[embedding.Length];
        for (var i = 0; i < merged.Length; i++) merged[i] = centroid[i] * count + embedding[i];
        var norm = MathF.Sqrt(merged.Sum(v => v * v));
        if (norm > 0) for (var i = 0; i < merged.Length; i++) merged[i] /= norm;

        groups[bestId] = (merged, count + 1);
        changed[bestId] = (merged, count + 1);
        return bestId;
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i];
        return sum;
    }

    private void Report(bool running, bool waiting, int done, int total) =>
        StatusChanged?.Invoke(new FaceStatus(running, waiting, done, total));
}
