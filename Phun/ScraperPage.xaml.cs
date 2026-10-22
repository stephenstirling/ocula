using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Ocula.Library;

namespace Ocula.Views;

public sealed partial class ScraperPage : Page
{
    // TODO: read these from Ocula's settings instead of hard-coding.
    private const string DownloadsFolder = @"Z:\Babes";
    private static readonly string? ScraperUrl = "http://192.168.86.62:8765";   // set to null to use the status file only
    private const string? ScraperToken = null;                        // same value as STATUS_TOKEN, if you set one

    private ScraperMonitor? _monitor;

    public ScraperPage() => InitializeComponent();

    // Poll only while the tab is visible.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _monitor = new ScraperMonitor(DownloadsFolder, ScraperUrl, ScraperToken);
        _monitor.StatusChanged += s => DispatcherQueue.TryEnqueue(() => Render(s));
        _monitor.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
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

        ScanNowButton.IsEnabled = !s.Offline && s.State != "scanning" && ScraperUrl is not null;
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
