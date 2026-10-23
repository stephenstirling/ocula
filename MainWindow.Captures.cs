using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Ocula.Models;
using Ocula.Services;

namespace Ocula;

/// <summary>
/// Capta, the sibling screenshot app, hands captures over with ocula: links (OculaLink):
/// Send to Ocula imports one in the background, Edit opens one in the editor, and "Open all
/// captures in Ocula" shows them all. Capta's PNGs carry its details (CaptaCapture), which the
/// indexer stores so captures can be found by app, window title or the text in them.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Smart albums made the first time Capta asks for its captures.</summary>
    private static readonly (string Name, string Query)[] CaptureAlbums =
    {
        ("All captures", "capture"),
        ("Captures with text", "capture hastext"),
    };

    public async Task HandleLinkAsync(OculaLink link)
    {
        // Not about captures at all: leave Capta's folder setting alone.
        if (link.Verb == "person")
        {
            await OpenPersonLinkAsync(link.Id);
            return;
        }
        if (link.Verb == "webdownload")
        {
            OpenWebDownloadFromLink(link);
            return;
        }

        var folder = OculaLink.CaptureFolderOrDefault(link.Folder ?? AppSettings.CapturesFolder);
        AppSettings.CapturesFolder = folder;
        try
        {
            switch (link.Verb)
            {
                case "captures":
                    AddCaptureFolder(folder);
                    await OpenCapturesAsync();
                    break;

                case "import" when link.File != null:
                    await IndexCaptureAsync(await AdoptAsync(link.File, folder));
                    break;

                case "edit" when link.File != null:
                {
                    var path = await AdoptAsync(link.File, folder);
                    if (await OpenPathAsync(path) is { } item) OpenEditor(item);
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't open the capture from Capta", e.Message);
        }
    }

    /// <summary>Copies a temporary capture into the capture folder, which joins the library.</summary>
    private async Task<string> AdoptAsync(string file, string captureFolder)
    {
        var path = await Task.Run(() => OculaLink.Adopt(file, captureFolder));
        AddCaptureFolder(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>
    /// Settings > Privacy > "Search the text in screenshots". On: the text in Capta's PNGs is read again
    /// into the library. Off: it is forgotten and no longer read, stored or searched.
    /// </summary>
    public async Task SetCaptureTextSearchAsync(bool on)
    {
        AppSettings.CaptureTextSearch = on;
        if (on)
        {
            await _index.MarkCapturesForRereadAsync(); // the crawler reads their text into the library again,
            _crawler.Start(RootFolders);               // in the background

            // Screenshots already on screen get their text back now, without reloading the grid.
            var shown = _all.Where(i => i.Capture != null).ToList();
            var texts = await Task.Run(() => shown.Select(i => (Item: i, Text: CaptaCapture.Read(i.Path)?.Text)).ToList());
            foreach (var (item, text) in texts) item.CaptureText = text;
            if (_current != null) ShowCaptureDetails(_current);
        }
        else
        {
            await _index.ClearCaptureTextAsync();
            foreach (var item in _all) item.CaptureText = null; // so it can't be saved back with a rating change
            if (_current != null) ShowCaptureDetails(_current);
            _ = LoadSidebarAsync(); // counts of the "with text" smart album
        }
    }

    /// <summary>
    /// The Capta card in the details panel: how the screenshot was taken, and the text found in it. Everything here
    /// came off a screen or out of a file, so it only ever goes into TextBlocks, as plain text.
    /// </summary>
    private void ShowCaptureDetails(ImageItem? item)
    {
        if (item?.Capture is not { Length: > 0 } capture)
        {
            CaptureCard.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = capture.Split('\n'); // mode, source app, window title (CaptaCapture.Summary)
        CaptureSource.Text = string.Join(" · ", parts.Where(p => p.Length > 0));
        var text = AppSettings.CaptureTextSearch ? item.CaptureText : null;
        CaptureTextBox.Text = text ?? string.Empty;
        CaptureTextExpander.Visibility = text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        CaptureCard.Visibility = Visibility.Visible;
    }

    /// <summary>Adds the folder to the library unless it's already in it.</summary>
    private void AddCaptureFolder(string folder)
    {
        if (OculaLink.IsInside(folder, RootFolders)) return;
        Directory.CreateDirectory(folder);
        AddRootFolder(folder, save: true);
    }

    /// <summary>Indexes one new capture without changing what's on screen.</summary>
    private async Task IndexCaptureAsync(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var item = (await Task.Run(() => FolderScanner.Scan(folder)))
            .FirstOrDefault(i => i.AllPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        if (item != null) await Indexer.IndexAsync(item, _index, withThumbnail: true);
    }

    /// <summary>Shows the "All captures" smart album, creating Capta's albums the first time.</summary>
    private async Task OpenCapturesAsync()
    {
        var albums = await _index.GetSmartAlbumsAsync();
        var added = false;
        foreach (var (name, query) in CaptureAlbums)
        {
            if (albums.Any(a => a.Query == query)) continue;
            await _index.SaveSmartAlbumAsync(name, query);
            added = true;
        }
        if (added)
        {
            albums = await _index.GetSmartAlbumsAsync();
            await LoadSidebarAsync();
        }

        var all = albums.First(a => a.Query == CaptureAlbums[0].Query);
        var search = new SearchQuery(all.Query);
        await OpenRowsAsync(await _index.SearchAsync(search), ViewKind.SmartAlbum, all.Id, all.Name, search.Describe());
    }
}
