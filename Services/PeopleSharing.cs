using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

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
    /// The folder Porta reads. A packaged Ocula can't use %LocalAppData%\Stirling\Ocula: Windows redirects a packaged app's writes there to
    /// its own private package folder, which no other app sees. So a packaged build uses the publisher cache folder "Stirling" (declared in
    /// Package.appxmanifest, shared by every app from the same publisher, here Ocula, Capta and Porta) and a subfolder "Ocula"; on disk that is
    /// %LocalAppData%\Publishers\&lt;publisher id&gt;\Stirling\Ocula. An unpackaged run (a developer build) uses %LocalAppData%\Stirling\Ocula as is.
    /// Null if a packaged build can't get the shared folder, in which case nothing is written.
    /// </summary>
    public static string? Folder { get; } = ResolveFolder();

    private static string? ResolveFolder()
    {
        bool packaged;
        try { _ = Windows.ApplicationModel.Package.Current.Id; packaged = true; }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { packaged = false; }

        if (!packaged)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stirling", "Ocula");

        try
        {
            return Path.Combine(ApplicationData.Current.GetPublisherCacheFolder("Stirling").Path, "Ocula");
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
            DiagnosticLog.Write("Share people with Porta: the shared publisher folder isn't available, so nothing is written", e);
            return null;
        }
    }

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
            if (Folder is not { } folder) return;
            if (!AppSettings.SharePeopleWithPorta || !FaceIndexer.Enabled)
            {
                await Task.Run(() => PeopleExport.Delete(folder)).ConfigureAwait(false);
                return;
            }
            var rows = await _index.GetPeopleForExportAsync().ConfigureAwait(false);
            await Task.Run(() => PeopleExport.Write(folder, rows, DateTimeOffset.UtcNow)).ConfigureAwait(false);
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
        if (Folder is not { } folder) return;
        await _work.WaitAsync().ConfigureAwait(false);
        try { PeopleExport.Delete(folder); }
        finally { _work.Release(); }
    }

    public void Dispose()
    {
        _index.PeopleChanged -= RequestRefresh;
        _debounce.Dispose();
    }
}
