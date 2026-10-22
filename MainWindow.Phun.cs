using Ocula.Services;
using Microsoft.UI.Xaml;

namespace Ocula;

/// <summary>
/// The Phun scraper tab: a live view of the forum scraper that runs in Docker on the NAS (see tools/phun-scraper). It opens in its own
/// window from the sidebar entry "Phun scraper…", which only appears when Settings > Phun scraper has it switched on.
/// </summary>
public sealed partial class MainWindow
{
    private PhunWindow? _phun;

    private void OpenPhun()
    {
        if (_phun == null)
        {
            _phun = new PhunWindow(this);
            _phun.Closed += (_, _) => _phun = null;
        }
        _phun.Activate();
    }

    /// <summary>"Open in Ocula" on the Phun page: shows the picture in the main window.</summary>
    public async System.Threading.Tasks.Task OpenFromPhunAsync(string path)
    {
        Activate();
        await OpenPathAsync(path);
    }

    /// <summary>Called by Settings when the Phun scraper is switched on or off: adds or removes the sidebar entry.</summary>
    public void PhunSettingsChanged()
    {
        if (!AppSettings.PhunTabEnabled) _phun?.Close();
        _ = LoadSidebarAsync();
    }
}
