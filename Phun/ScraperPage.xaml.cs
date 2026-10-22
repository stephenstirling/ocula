using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Ocula.Library;

namespace Ocula.Views;

public sealed partial class ScraperPage : Page
{
    // Read from Settings > Phun scraper each time the page opens.
    private string _downloadsFolder = string.Empty;
    private string? _scraperUrl;
    private string? _scraperToken;

    private ScraperMonitor? _monitor;

    public ScraperPage() => InitializeComponent();

    // Poll only while the tab is visible.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _downloadsFolder = Ocula.Services.AppSettings.PhunDownloadsFolder;
        _scraperUrl = Ocula.Services.AppSettings.PhunScraperUrl is { Length: > 0 } url ? url : null;
        _scraperToken = Ocula.Services.AppSettings.PhunScraperToken is { Length: > 0 } token ? token : null;
        _monitor = new ScraperMonitor(_downloadsFolder, _scraperUrl, _scraperToken);
        _monitor.StatusChanged += s => DispatcherQueue.TryEnqueue(() => Render(s));
        _monitor.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Stop();

    /// <summary>Stops polling the scraper. Also called when the window that hosts the page closes.</summary>
    public void Stop()
    {
        _monitor?.Dispose();
        _monitor = null;
    }

    private void Render(ScraperStatus s)
    {
        SummaryText.Text = s.Summary;
        SubText.Text = s.Offline ? "" :
            s.LastScanFinished is { } f ? $"Last scan finished {f.ToLocalTime():g}" : "No scan finished yet";

        OfflineBar.IsOpen = s.Offline;
        ErrorBar.IsOpen = !s.Offline && !string.IsNullOrEmpty(s.LastError);
        ErrorBar.Message = s.LastError ?? "";

        TagBar.IsOpen = !s.Offline && s.MetadataFailed > 0;
        TagBar.Message = s.MetadataFailed == 1
            ? "1 image couldn't be tagged, so it can't be found by tag. The activity list below says which."
            : $"{s.MetadataFailed} images couldn't be tagged, so they can't be found by tag. The activity list below says which.";

        ScanBar.Value = s.ThreadProgress;
        ScanCountText.Text = $"{s.ThreadsDone} / {s.ThreadsTotal} threads";
        ThreadText.Text = string.IsNullOrEmpty(s.ThreadTitle) ? "Waiting for next scan" : s.ThreadTitle;
        LinkBar.Value = s.LinkProgress;
        LinkCountText.Text = $"{s.ThreadLinksDone} / {s.ThreadLinksTotal}";

        SavedText.Text = s.ImagesSaved.ToString();
        UpgradedText.Text = s.Upgrades.ToString();
        DupText.Text = s.DuplicatesSkipped.ToString();
        FailedText.Text = s.FailedDownloads.ToString();

        // newest first
        EventList.ItemsSource = Enumerable.Reverse(s.Events).Take(60).ToList();

        ScanNowButton.IsEnabled = !s.Offline && s.State != "scanning" && _scraperUrl is not null;
    }

    private async void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        ScanNowButton.IsEnabled = false;
        if (_monitor is not null && !await _monitor.ScanNowAsync())
        {
            ErrorBar.Message = "Couldn't reach the scraper to start a scan.";
            ErrorBar.IsOpen = true;
        }
    }
}
