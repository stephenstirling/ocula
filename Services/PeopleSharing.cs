using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>
/// "Share people with Porta" (Settings > Privacy, off by default). While it is on, keeps people-index.json and the face pictures up
/// to date (see <see cref="PeopleExport"/>). Refreshing is debounced (<see cref="RefreshDebouncer"/>): a burst of changes, such as a face
/// scan saving photo after photo, causes one write a few seconds after the last change, and never longer than a minute after the first.
/// The files are removed when the switch is turned off, when People is off, and when all face data is deleted. Nothing is sent over the
/// internet.
/// </summary>
public sealed class PeopleSharing : IDisposable
{
    private readonly IndexService _index;
    private readonly RefreshDebouncer _debounce;
    private readonly SemaphoreSlim _work = new(1, 1);

    public PeopleSharing(IndexService index)
    {
        _index = index;
        _debounce = new RefreshDebouncer(() => _ = RefreshAsync(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
        _index.PeopleChanged += RequestRefresh;
    }

    /// <summary>
    /// %LocalAppData%\Stirling\Ocula, where Porta reads people-index.json and people-thumbs\&lt;id&gt;.png. Windows normally redirects a packaged
    /// app's writes under %LocalAppData% into its private package folder, so Package.appxmanifest excludes %LocalAppData%\Stirling from that
    /// redirection (it needs the unvirtualizedResources capability); without the exclusion the files would exist only in Ocula's view of the disk.
    /// </summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stirling", "Ocula");

    /// <summary>Asks for a refresh soon. Cheap to call often; does nothing while sharing is off.</summary>
    public void RequestRefresh()
    {
        if (AppSettings.SharePeopleWithPorta) _debounce.Request();
    }

    /// <summary>Writes the files now (sharing on and People on) or removes them (either off).</summary>
    public async Task RefreshAsync()
    {
        _debounce.Cancel();
        await _work.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!AppSettings.SharePeopleWithPorta || !FaceIndexer.Enabled)
            {
                await Task.Run(() => PeopleExport.Delete(Folder)).ConfigureAwait(false);
                return;
            }
            var rows = await _index.GetPeopleForExportAsync().ConfigureAwait(false);
            await Task.Run(() => PeopleExport.Write(Folder, rows, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            DiagnosticLog.Write("Share people with Porta: couldn't update the people file", e);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <summary>Deletes the file and pictures at once and cancels any refresh that was waiting.</summary>
    public async Task RemoveAllAsync()
    {
        _debounce.Cancel();
        await _work.WaitAsync().ConfigureAwait(false);
        try { PeopleExport.Delete(Folder); }
        finally { _work.Release(); }
    }

    public void Dispose()
    {
        _index.PeopleChanged -= RequestRefresh;
        _debounce.Dispose();
    }
}
