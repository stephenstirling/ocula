using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

public sealed record SmartStatus(bool Running, bool Waiting, int Done, int Total);

/// <summary>
/// Analyses photos in the background after the library has indexed them, one at a time and
/// only while you're not busy (it waits for a short pause in keyboard and mouse use). Each
/// result is saved in the library, so a photo is only ever analysed once per model.
/// </summary>
public sealed class SmartIndexer
{
    private readonly IndexService _index;
    private readonly SmartService _smart;
    private CancellationTokenSource? _cancel;
    private Task? _task;

    public event Action<SmartStatus>? StatusChanged;

    public bool IsRunning => _task is { IsCompleted: false };

    public SmartIndexer(IndexService index, SmartService smart)
    {
        _index = index;
        _smart = smart;
    }

    public void Start()
    {
        if (!SmartService.Enabled || IsRunning) return;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _task = Task.Run(() => RunAsync(token));
    }

    public void Stop() => _cancel?.Cancel();

    private async Task RunAsync(CancellationToken cancel)
    {
        try
        {
            if (!await _smart.LoadAsync()) return;
            var model = _smart.ModelId;
            var (done, total) = await _index.GetSmartProgressAsync(model);
            Report(true, false, done, total);

            while (!cancel.IsCancellationRequested)
            {
                var batch = await _index.GetUnanalysedAsync(model, 40);
                if (batch.Count == 0)
                {
                    // The library crawler may still be adding photos; check again shortly.
                    await Task.Delay(TimeSpan.FromSeconds(20), cancel);
                    (done, total) = await _index.GetSmartProgressAsync(model);
                    if (done >= total) break;
                    continue;
                }

                foreach (var (path, modified) in batch)
                {
                    cancel.ThrowIfCancellationRequested();

                    // Wait for a quiet moment so typing and scrolling stay smooth.
                    if (SecondsSinceInput() < 1.5)
                    {
                        Report(true, true, done, total);
                        while (SecondsSinceInput() < 1.5) await Task.Delay(500, cancel);
                    }

                    var vector = await AnalyseAsync(path);
                    await _index.SaveEmbeddingAsync(path, modified, model, vector);
                    done++;
                    if (done % 10 == 0)
                    {
                        _smart.NotifyChanged();
                        Report(true, false, done, total);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose (settings changed or the app is closing).
        }
        catch (Exception ex)
        {
            // A model problem shouldn't take the app down; analysis stops, and says why in the log.
            DiagnosticLog.Write("Smart recognition: analysis stopped", ex);
        }
        finally
        {
            _smart.NotifyChanged();
            var (d, t) = await SafeProgressAsync();
            Report(false, false, d, t);
        }
    }

    private async Task<float[]?> AnalyseAsync(string path)
    {
        try
        {
            // Online-only OneDrive files would be downloaded just to read them: skip.
            var attributes = (int)new FileInfo(path).Attributes;
            if ((attributes & (0x00040000 | 0x00400000 | (int)FileAttributes.Offline)) != 0) return null;

            var bytes = await ThumbnailService.GetBytesAsync(path, 448, allowDecode: true);
            return bytes == null ? null : await Task.Run(() => _smart.EncodeImage(bytes));
        }
        catch
        {
            return null; // unreadable file: recorded as "nothing to read" so it isn't retried forever
        }
    }

    private async Task<(int, int)> SafeProgressAsync()
    {
        try { return await _index.GetSmartProgressAsync(_smart.ModelId); }
        catch { return (0, 0); }
    }

    private void Report(bool running, bool waiting, int done, int total) =>
        StatusChanged?.Invoke(new SmartStatus(running, waiting, done, total));

    // ---------- Idle detection ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    internal static double SecondsSinceInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 99;
        return unchecked((uint)Environment.TickCount - info.Time) / 1000.0;
    }
}
