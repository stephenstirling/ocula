using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

public sealed record SmartSuggestion(string Label, float Confidence);
public sealed record SmartMatch(string Path, float Score);

/// <summary>
/// On-device image recognition with CLIP. One model turns a photo into a list of 512 numbers
/// (an "embedding"); the other turns a sentence into the same kind of list. Photos and
/// sentences that mean similar things end up close together, which gives us:
///
///   - natural-language search ("sunset over water"): rank photos by closeness to the sentence
///   - tag suggestions: compare each photo with a list of common labels (SmartLabels)
///
/// Everything runs on this PC. Models load on first use and are shared by the whole app.
/// </summary>
public sealed class SmartService : IDisposable
{
    /// <summary>CLIP's learned temperature: turns cosine similarity into a sharper probability.</summary>
    private const float LogitScale = 100f;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private ClipModel? _model;
    private SmartTier _loadedTier;
    private float[][]? _labelVectors;

    private List<(string Path, float[] Vector)>? _library;
    private int _libraryVersion = -1;
    private int _version;

    public static bool Enabled => AppSettings.SmartEnabled && SmartModels.IsInstalled(AppSettings.SmartTier);

    public string ModelId => SmartModels.ModelId(AppSettings.SmartTier);

    /// <summary>Call after new embeddings are saved so the next search sees them.</summary>
    public void NotifyChanged() => Interlocked.Increment(ref _version);

    // ================= Loading =================

    public async Task<bool> LoadAsync()
    {
        var tier = AppSettings.SmartTier;
        await _loadLock.WaitAsync();
        try
        {
            if (_model != null && _loadedTier == tier) return true;
            if (!SmartModels.IsInstalled(tier)) return false;

            return await Task.Run(() =>
            {
                Unload();
                // Leave some cores free so the app stays responsive while photos are analysed.
                _model = ClipModel.Load(SmartModels.Folder(tier), Environment.ProcessorCount / 2);
                _loadedTier = tier;
                _labelVectors = null;
                return true;
            });
        }
        catch
        {
            Unload();
            return false;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private void Unload()
    {
        _model?.Dispose();
        _model = null;
        _labelVectors = null;
        _library = null;
    }

    // ================= Encoding =================

    /// <summary>Turns a JPEG/PNG (a thumbnail is plenty) into a normalised embedding. Call from a background thread.</summary>
    public float[] EncodeImage(byte[] imageBytes) =>
        (_model ?? throw new InvalidOperationException("Smart recognition isn't loaded.")).EncodeImage(imageBytes);

    public float[] EncodeText(string text) =>
        (_model ?? throw new InvalidOperationException("Smart recognition isn't loaded.")).EncodeText(text);

    // ================= Tag suggestions =================

    /// <summary>Best-matching labels for a photo, most confident first. Call from a background thread.</summary>
    public IReadOnlyList<SmartSuggestion> SuggestTags(float[] imageVector, int max = 6, float minConfidence = 0.10f)
    {
        var labels = SmartLabels.All;
        var vectors = LabelVectors();
        if (vectors.Length == 0) return Array.Empty<SmartSuggestion>();

        // Softmax over all labels: confidence says how much better a label fits than the rest.
        var scores = new float[labels.Count];
        for (var i = 0; i < scores.Length; i++) scores[i] = LogitScale * Dot(imageVector, vectors[i]);
        var top = scores.Max();
        var exp = scores.Select(s => MathF.Exp(s - top)).ToArray();
        var sum = exp.Sum();

        return exp.Select((e, i) => new SmartSuggestion(labels[i], e / sum))
                  .Where(s => s.Confidence >= minConfidence)
                  .OrderByDescending(s => s.Confidence)
                  .Take(max)
                  .ToList();
    }

    private float[][] LabelVectors()
    {
        if (_labelVectors is { } ready && ready.Length == SmartLabels.All.Count) return ready;

        // Computing 200 sentences takes a few seconds, so keep the result on disk.
        var cache = Path.Combine(SmartModels.Folder(_loadedTier), "labels.bin");
        var vectors = TryReadLabelCache(cache) ?? BuildLabelVectors(cache);
        _labelVectors = vectors;
        return vectors;
    }

    private float[][] BuildLabelVectors(string cachePath)
    {
        var vectors = SmartLabels.All.Select(l => EncodeText($"a photo of {l}")).ToArray();
        try
        {
            using var writer = new BinaryWriter(File.Create(cachePath));
            writer.Write(SmartLabels.All.Count);
            writer.Write(vectors[0].Length);
            writer.Write(LabelHash());
            foreach (var v in vectors) foreach (var f in v) writer.Write(f);
        }
        catch (IOException)
        {
            // Not fatal: they'll be rebuilt next time.
        }
        return vectors;
    }

    private static float[][]? TryReadLabelCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            using var reader = new BinaryReader(File.OpenRead(cachePath));
            var count = reader.ReadInt32();
            var dim = reader.ReadInt32();
            var hash = reader.ReadInt32();
            if (count != SmartLabels.All.Count || hash != LabelHash()) return null;

            var vectors = new float[count][];
            for (var i = 0; i < count; i++)
            {
                vectors[i] = new float[dim];
                for (var j = 0; j < dim; j++) vectors[i][j] = reader.ReadSingle();
            }
            return vectors;
        }
        catch
        {
            return null;
        }
    }

    // ================= Search =================

    /// <summary>
    /// Ranks every analysed photo against a sentence. Keeps the close matches only: anything
    /// within a small margin of the best score (and above a floor), so a nonsense query
    /// returns little rather than the "least bad" 3000 photos.
    /// </summary>
    public async Task<List<SmartMatch>> SearchAsync(string text, IndexService index, int limit = 3000)
    {
        if (!await LoadAsync()) return new List<SmartMatch>();

        var query = await Task.Run(() => EncodeText(text));

        if (_library == null || _libraryVersion != _version)
        {
            _library = await index.GetEmbeddingsAsync(ModelId);
            _libraryVersion = _version;
        }
        var library = _library;

        return await Task.Run(() =>
        {
            var scored = library.Select(item => new SmartMatch(item.Path, Dot(query, item.Vector))).ToList();
            if (scored.Count == 0) return scored;

            var best = scored.Max(s => s.Score);
            var cutoff = Math.Max(0.19f, best - 0.07f);
            return scored.Where(s => s.Score >= cutoff)
                         .OrderByDescending(s => s.Score)
                         .Take(limit)
                         .ToList();
        });
    }

    /// <summary>A hash of the label list that is the same every run (string.GetHashCode isn't).</summary>
    private static int LabelHash()
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in string.Join('|', SmartLabels.All)) hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    public void Dispose()
    {
        Unload();
        _loadLock.Dispose();
    }
}
