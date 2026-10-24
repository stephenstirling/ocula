using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Ocula.Models;
using Ocula.Services;

namespace Ocula;

/// <summary>Download from the web: the window, and writing tags into the photos it saved.</summary>
public sealed partial class MainWindow
{
    private WebDownloadWindow? _web;

    private void WebButton_Click(object sender, RoutedEventArgs e) => EnsureWebWindow().Activate();

    private WebDownloadWindow EnsureWebWindow()
    {
        if (_web == null)
        {
            _web = new WebDownloadWindow(this);
            _web.Closed += (_, _) => _web = null;
        }
        return _web;
    }

    /// <summary>
    /// ocula:webdownload from the owner's browser extension (docs/CHROME-EXTENSION.md): opens the Web download window on the
    /// page, grabs its pictures and ticks the tags. Nothing is saved until the user presses Download. The suggested folder is
    /// used only when it is inside a library folder, because any web page can launch an ocula: link.
    /// </summary>
    public void OpenWebDownloadFromLink(OculaLink link)
    {
        if (link.Url == null) return;
        var folder = OculaLink.DownloadFolderOrNull(link.Folder, RootFolders);
        var web = EnsureWebWindow();
        web.OpenFromLink(link.Url, link.Tags ?? Array.Empty<string>(), folder,
                         folderIgnored: !string.IsNullOrWhiteSpace(link.Folder) && folder == null, media: link.Media, trim: link.TrimJustJared);
        web.Activate();
    }

    /// <summary>Adds the ticked tags to freshly downloaded photos, writing them into the files and the library.</summary>
    public async Task TagDownloadsAsync(List<string> paths, List<string> tags)
    {
        if (tags.Count == 0) return;
        var items = paths.Select(path => new ImageItem(path, null)).ToList();
        await ApplyToItemsAsync(items, item =>
        {
            foreach (var tag in tags)
                if (!item.Tags.Any(t => string.Equals(t, tag, System.StringComparison.OrdinalIgnoreCase)))
                    item.Tags.Add(tag);
        });
    }
}
