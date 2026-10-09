using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace Ocula;

/// <summary>Download from the web: the window, and writing tags into the photos it saved.</summary>
public sealed partial class MainWindow
{
    private WebDownloadWindow? _web;

    private void WebButton_Click(object sender, RoutedEventArgs e)
    {
        if (_web == null)
        {
            _web = new WebDownloadWindow(this);
            _web.Closed += (_, _) => _web = null;
        }
        _web.Activate();
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
