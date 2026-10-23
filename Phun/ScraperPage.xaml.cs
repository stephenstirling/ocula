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
using Ocula.Services;

namespace Ocula.Views;

public sealed partial class ScraperPage : Page
{
    // Read from Settings > Phun Forum Scraper each time the page opens.
    private string _downloadsFolder = string.Empty;
    private string? _scraperUrl;

    private ScraperMonitor? _monitor;
    private MainWindow? _main;                 // for "Open in Ocula"
    private int _savedAtLastLoad = -1;         // reload the grid when the scraper's saved count changes
    private bool _loadingRecent;
    private readonly List<RecentImage> _recent = new();
    private const int RecentCount = 24;

    public ScraperPage() => InitializeComponent();

    // Poll only while the tab is visible.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _main = e.Parameter as MainWindow;
        _savedAtLastLoad = -1;
        _downloadsFolder = Ocula.Services.AppSettings.PhunDownloadsFolder;
        _scraperUrl = Ocula.Services.AppSettings.PhunScraperUrl is { Length: > 0 } url ? url : null;
        Ocula.Services.AppSettings.ForgetPhunToken();
        _monitor = new ScraperMonitor(_downloadsFolder, _scraperUrl);
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

        if (s.ImagesSaved != _savedAtLastLoad) _ = LoadRecentAsync(s.ImagesSaved);
    }

    // ---------- Recently saved ----------

    private async Task LoadRecentAsync(int savedCount)
    {
        if (_loadingRecent || _downloadsFolder.Length == 0)
        {
            if (_downloadsFolder.Length == 0) RecentNote.Text = "Set the downloads folder in Settings > Phun Forum Scraper to see the pictures.";
            return;
        }
        _loadingRecent = true;
        try
        {
            var folder = _downloadsFolder;
            List<IndexedImage> rows;
            try
            {
                // The library file is on the NAS and the scraper writes to it: read it off the UI thread, and expect it to be missing or busy.
                rows = await Task.Run(() => new LibraryIndex(folder).Query(limit: RecentCount).ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                RecentNote.Text = "Couldn't read the scraper's library file. It will try again.";
                return;
            }

            _savedAtLastLoad = savedCount;
            RecentNote.Text = rows.Count == 0 ? "Nothing saved yet" : "Newest first, from the scraper's library file";

            var keep = (RecentGrid.SelectedItem as RecentImage)?.Image.FullPath;
            _recent.Clear();
            _recent.AddRange(rows.Select(r => new RecentImage(r)));
            RecentGrid.ItemsSource = _recent.ToList();
            if (keep != null) RecentGrid.SelectedItem = _recent.FirstOrDefault(r => r.Image.FullPath == keep);

            foreach (var tile in _recent.ToList()) _ = LoadThumbAsync(tile);
        }
        finally
        {
            _loadingRecent = false;
        }
    }

    private static async Task LoadThumbAsync(RecentImage tile)
    {
        try
        {
            if (await ThumbnailService.GetBytesAsync(tile.Image.FullPath, 200) is { } bytes)
                tile.Thumb = await ThumbnailService.ToBitmapAsync(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // The tile just keeps its grey background.
        }
    }

    private async void RecentGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentGrid.SelectedItem is not RecentImage tile)
        {
            DetailCard.Visibility = Visibility.Collapsed;
            return;
        }
        var image = tile.Image;
        DetailCard.Visibility = Visibility.Visible;
        DetailName.Text = image.Name;
        DetailMeta.Text = string.Join(" · ", new[]
        {
            image.SizeText, image.DownloadedAt is { } d ? $"saved {d.ToLocalTime():g}" : null,
            image.ThreadTitle is { Length: > 0 } t ? $"from “{t}”" : null
        }.Where(s => !string.IsNullOrEmpty(s)));
        DetailTags.Text = "Reading the file…";
        DetailTagWarning.IsOpen = false;

        // What is really in the file, read the way Ocula reads it, so a missing tag shows up here rather than in a search.
        string[] inFile;
        try
        {
            inFile = (await MetadataStore.ReadAsync(new Ocula.Models.ImageItem(image.FullPath, null))).Tags.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            if (RecentGrid.SelectedItem == tile) DetailTags.Text = "Couldn't read the file.";
            return;
        }
        if (RecentGrid.SelectedItem != tile) return;   // moved on while reading

        DetailTags.Text = inFile.Length == 0 ? "None" : string.Join(", ", inFile);
        var missing = image.MissingFrom(inFile);
        if (missing.Count > 0)
        {
            DetailTagWarning.Message = $"The scraper meant to write: {string.Join(", ", missing)}. A search for them won't find this picture.";
            DetailTagWarning.IsOpen = true;
        }
    }

    private async void OpenInOcula_Click(object sender, RoutedEventArgs e)
    {
        if (RecentGrid.SelectedItem is RecentImage tile && _main != null) await _main.OpenFromPhunAsync(tile.Image.FullPath);
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RecentGrid.SelectedItem is not RecentImage tile) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{tile.Image.FullPath}\"") { UseShellExecute = true });
    }
}
