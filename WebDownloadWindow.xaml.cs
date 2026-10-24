using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;

namespace Ocula;

/// <summary>
/// Download from the web: browse to a page (so sign-ins and script-built pages work), grab the images on
/// it, pick which to keep, tick suggested tags found in the page text, and save them to a folder.
/// Ocula only fetches what the user chose. It does not get past paywalls, ad screens or logins.
/// </summary>
public sealed partial class WebDownloadWindow : Window
{
    private readonly MainWindow _main;
    private readonly HttpClient _http = WebDownloader.CreateClient();
    private readonly ObservableCollection<WebImage> _images = new();
    private readonly ObservableCollection<TagChip> _tags = new();
    private PageSnapshot? _page;
    private string? _cookies;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _download;
    private bool _ready;

    // Set by OpenFromLink (the owner's browser extension, docs/CHROME-EXTENSION.md).
    private string? _linkUrl;                       // page to open once the browser pane has started
    private bool _grabOnLoad;                       // grab the pictures as soon as that page has loaded
    private IReadOnlyList<string> _linkTags = Array.Empty<string>();
    private string? _linkMedia;                     // only pictures whose address has this text start ticked
    private bool _exactFolder;                      // "Save to" is the final folder, not a root that gets a page-title folder

    public WebDownloadWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 840));
        Root.RequestedTheme = main.CurrentTheme;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;

        ImageGrid.ItemsSource = _images;
        TagList.ItemsSource = _tags;
        UrlBox.Text = AppSettings.WebLastUrl;
        DestinationBox.Text = AppSettings.WebDestination;
        SourceToggle.IsOn = AppSettings.WebSaveSource;
        SizeBox.SelectedIndex = 0;
        _ready = true;

        Closed += (_, _) =>
        {
            _work?.Cancel();
            _download?.Cancel();
            _http.Dispose();
        };

        _ = InitialiseBrowserAsync();
        UpdateDestinationNote();
    }

    // ================= Browser pane =================

    private async Task InitialiseBrowserAsync()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.SourceChanged += (_, _) =>
            {
                if (PickerPane.Visibility == Visibility.Collapsed) UrlBox.Text = Browser.CoreWebView2.Source;
            };
            Browser.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true; // open pop-ups in the same pane
                Browser.CoreWebView2.Navigate(e.Uri);
            };
            Browser.CoreWebView2.NavigationCompleted += async (_, e) => await OnNavigationCompletedAsync(e);
            if (_linkUrl != null)
            {
                var url = _linkUrl;
                _linkUrl = null;
                Navigate(url, grab: true);
            }
            else if (UrlBox.Text.Length > 0) Navigate(UrlBox.Text);
        }
        catch (Exception ex)
        {
            ShowMessage("The browser pane couldn't start", ex.Message + "\n\nOcula needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and recent Windows 10 updates.");
        }
    }

    private void Navigate(string text, bool grab = false)
    {
        // Only a page a link asked for is grabbed on its own. An address the user types waits for the Grab button, and
        // leaves the link's tags, picture filter and folder behind.
        _grabOnLoad = grab;
        if (!grab)
        {
            _linkTags = Array.Empty<string>();
            _linkMedia = null;
            LinkNote.Visibility = Visibility.Collapsed;
            if (_exactFolder)
            {
                _exactFolder = false;
                DestinationBox.Text = AppSettings.WebDestination;
            }
        }

        text = text.Trim();
        if (text.Length == 0 || Browser.CoreWebView2 == null) return;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowMessage("That isn't a web address", "Type an address that starts with http:// or https://.");
            return;
        }
        ShowBrowser();
        Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    private void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Navigate(UrlBox.Text);
    }

    private void Go_Click(object sender, RoutedEventArgs e) => Navigate(UrlBox.Text);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (PickerPane.Visibility == Visibility.Visible) ShowBrowser();
        else if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack();
    }

    private void ShowBrowser()
    {
        _work?.Cancel();
        PickerPane.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        GrabButton.Visibility = Visibility.Visible;
        DownloadButton.IsEnabled = false;
        SelectedText.Text = string.Empty;
        if (Browser.CoreWebView2 != null) UrlBox.Text = Browser.CoreWebView2.Source;
    }

    // ================= Opened from a link =================

    /// <summary>
    /// ocula:webdownload from the browser extension: opens the page, grabs its pictures once it has loaded, ticks the
    /// tags, and suggests the save folder. Nothing is downloaded until the user presses Download.
    /// </summary>
    /// <param name="folder">The folder to save into, already checked to be inside a library folder, or null.</param>
    /// <param name="media">Only pictures whose address contains this text start ticked (others stay listed, unticked).</param>
    public void OpenFromLink(string url, IReadOnlyList<string> tags, string? folder, bool folderIgnored, string? media, bool trim = false)
    {
        TrimToggle.IsOn = trim;
        _linkTags = tags;
        _linkMedia = string.IsNullOrEmpty(media) ? null : media;
        _exactFolder = folder != null;
        if (folder != null) DestinationBox.Text = folder; // set after _exactFolder, so the note reads right
        UpdateDestinationNote();

        LinkNote.Text = "Opened from the browser extension." +
                        (folderIgnored ? " The folder it suggested is outside your library folders, so it was ignored." : string.Empty);
        LinkNote.Visibility = Visibility.Visible;

        UrlBox.Text = url;
        ShowBrowser(); // leaves the picker if an earlier page was grabbed
        if (Browser.CoreWebView2 != null) Navigate(url, grab: true);
        else _linkUrl = url; // InitialiseBrowserAsync opens it once the pane has started
    }

    private async Task OnNavigationCompletedAsync(Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_grabOnLoad) return;
        // The pane starts on a blank page; wait for a real one.
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var shown) ||
            (shown.Scheme != Uri.UriSchemeHttp && shown.Scheme != Uri.UriSchemeHttps)) return;
        _grabOnLoad = false;

        if (!e.IsSuccess)
        {
            ShowMessage("The page didn't load", $"{e.WebErrorStatus}. Check the address or your connection, then try again.");
            return;
        }
        try
        {
            await Task.Delay(1500); // let script-built and lazy-loaded pictures appear
            await GrabAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Web download: grabbing the page from a link failed", ex);
        }
    }

    /// <summary>The link's tags, ticked. A suggested chip with the same label is just ticked.</summary>
    private void ApplyLinkTags()
    {
        foreach (var label in _linkTags)
        {
            var existing = _tags.FirstOrDefault(t => string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.On = true;
            else _tags.Add(new TagChip { Label = label, Reason = "From the browser extension", On = true });
        }
        _linkTags = Array.Empty<string>(); // once; a later Grab on the same page keeps what the user ticked
        if (_tags.Count > 0) NoTagsText.Visibility = Visibility.Collapsed;
    }

    /// <summary>With a link's picture filter, only matching pictures stay ticked. The rest are listed, unticked.</summary>
    private void ApplyLinkMedia()
    {
        if (_linkMedia == null) return;
        foreach (var image in _images)
        {
            var matches = image.ThumbUrl.Contains(_linkMedia, StringComparison.OrdinalIgnoreCase) ||
                          image.FullUrl.Contains(_linkMedia, StringComparison.OrdinalIgnoreCase) ||
                          (image.LinkUrl?.Contains(_linkMedia, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!matches) image.Selected = false;
        }
    }

    // ================= Grab =================

    private async void Grab_Click(object sender, RoutedEventArgs e) => await GrabAsync();

    private async Task GrabAsync()
    {
        if (Browser.CoreWebView2 == null) return;
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var cancel = _work.Token;

        Busy.IsActive = true;
        GrabButton.IsEnabled = false;
        try
        {
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(PageScanner.GrabScript);
            var json = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(json)) throw new InvalidOperationException("The page didn't return anything.");
            _page = PageScanner.Parse(json);
            AppSettings.WebLastUrl = _page.Url;

            var pageUri = new Uri(_page.Url);
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(pageUri.GetLeftPart(UriPartial.Authority));
            _cookies = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

            _images.Clear();
            foreach (var image in _page.Images) _images.Add(image);

            PageTitle.Text = _page.Title.Length > 0 ? _page.Title : pageUri.Host;
            PageHost.Text = pageUri.Host;
            _tags.Clear();
            foreach (var chip in TagSuggester.Suggest(_page)) _tags.Add(chip);
            ApplyLinkTags();
            NoTagsText.Visibility = _tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDestinationNote();

            Browser.Visibility = Visibility.Collapsed;
            PickerPane.Visibility = Visibility.Visible;
            GrabButton.Visibility = Visibility.Collapsed;

            if (_images.Count == 0)
            {
                FoundText.Text = "No images found on this page.";
                UpdateSelection();
                return;
            }

            ApplySizeFilter(selectEligible: true);
            FoundText.Text = $"{_images.Count:N0} images found";
            _ = LoadThumbnailsAsync(_images.ToList(), pageUri, cancel);
            _ = ResolveAsync(pageUri, cancel);
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't read this page", ex.Message);
        }
        finally
        {
            Busy.IsActive = false;
            GrabButton.IsEnabled = true;
        }
    }

    private async Task LoadThumbnailsAsync(List<WebImage> images, Uri page, CancellationToken cancel)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(images.Select(async image =>
        {
            try
            {
                await gate.WaitAsync(cancel);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, image.ThumbUrl);
                    request.Headers.Referrer = page;
                    if (!string.IsNullOrEmpty(_cookies) && new Uri(image.ThumbUrl).Host == page.Host)
                        request.Headers.TryAddWithoutValidation("Cookie", _cookies);
                    using var response = await _http.SendAsync(request, cancel);
                    if (!response.IsSuccessStatusCode) return;
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
                    if (cancel.IsCancellationRequested) return;
                    image.Thumbnail = await ThumbnailService.ToBitmapAsync(bytes);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* no preview for this one; it can still be downloaded */ }
        }));
    }

    private async Task ResolveAsync(Uri page, CancellationToken cancel)
    {
        var linked = _images.Count(i => i.LinkUrl != null);
        if (linked == 0) return;

        FoundText.Text = $"{_images.Count:N0} images found · looking for full-size versions…";
        try
        {
            var progress = new Progress<int>(done =>
                FoundText.Text = $"{_images.Count:N0} images found · checked {done:N0} of {linked:N0} links");
            await PageScanner.ResolveAsync(_http, _images, page, _cookies, progress, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var larger = _images.Count(i => i.HasLargerVersion);
        var missing = _images.Count(i => i.Problem != null);
        FoundText.Text = $"{_images.Count:N0} images found"
                         + (larger > 0 ? $" · {larger:N0} with a larger version" : string.Empty)
                         + (missing > 0 ? $" · {missing:N0} couldn't be found in full size" : string.Empty);

        foreach (var image in _images.Where(i => i.Problem != null)) image.Selected = false;
        ApplySizeFilter(selectEligible: false);
        UpdateSelection();
    }

    // ================= Picking =================

    private int MinSide => SizeBox.SelectedIndex switch { 1 => 1500, 2 => 3000, _ => 0 };

    private void ApplySizeFilter(bool selectEligible)
    {
        var min = MinSide;
        foreach (var image in _images)
        {
            // Pictures whose real size isn't known yet are kept eligible.
            image.Eligible = min == 0 || image.LongSide == 0 || image.LongSide >= min;
            if (!image.Eligible) image.Selected = false;
            else if (selectEligible && image.CanDownload) image.Selected = true;
        }
        ApplyLinkMedia();
        UpdateSelection();
    }

    private void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplySizeFilter(selectEligible: true);
    }

    private void Grid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WebImage image || !image.Eligible || !image.CanDownload) return;
        image.Selected = !image.Selected;
        UpdateSelection();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var all = _images.Where(i => i.Eligible && i.CanDownload).ToList();
        var turnOn = all.Any(i => !i.Selected);
        foreach (var image in all) image.Selected = turnOn;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = _images.Count(i => i.Selected);
        SelectedText.Text = count == 0 ? string.Empty : $"{count:N0} selected";
        DownloadButton.Content = count == 0 ? "Download" : $"Download {count:N0} images";
        DownloadButton.IsEnabled = count > 0 && _download == null;
        SelectAllLink.Content = _images.Where(i => i.Eligible && i.CanDownload).All(i => i.Selected) && count > 0 ? "Clear selection" : "Select all";
    }

    private void ExtraTag_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var text = ExtraTagBox.Text.Trim();
        if (text.Length == 0) return;
        if (!_tags.Any(t => string.Equals(t.Label, text, StringComparison.OrdinalIgnoreCase)))
            _tags.Add(new TagChip { Label = text, Reason = "Added by you", On = true });
        ExtraTagBox.Text = string.Empty;
        NoTagsText.Visibility = Visibility.Collapsed;
    }

    // ================= Destination =================

    private void Destination_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateDestinationNote();
    }

    private string TargetFolder() => _exactFolder
        ? DestinationBox.Text.Trim()
        : Path.Combine(DestinationBox.Text.Trim(), WebDownloader.FolderNameFor(_page?.Title ?? string.Empty));

    private void UpdateDestinationNote()
    {
        var text = DestinationBox.Text.Trim();
        FolderNote.Text = text.Length == 0 ? string.Empty
            : _exactFolder
                ? "Photos go straight into this folder (" + (Directory.Exists(text) ? "it already exists" : "it will be created") + ")."
                : "Photos go into the folder: " + TargetFolder();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            _exactFolder = false; // a folder picked here is a root again, with the page title added
            DestinationBox.Text = folder.Path;
        }
    }

    // ================= Download =================

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_page == null) return;
        var chosen = _images.Where(i => i.Selected).ToList();
        if (chosen.Count == 0) return;

        var destination = DestinationBox.Text.Trim();
        if (destination.Length == 0 || !Path.IsPathRooted(destination))
        {
            ShowMessage("Choose where to save", "Pick a folder in “Save to”.");
            return;
        }

        if (!_exactFolder) AppSettings.WebDestination = destination; // a link's folder is for this download only
        AppSettings.WebSaveSource = SourceToggle.IsOn;

        var tags = _tags.Where(t => t.On).Select(t => t.Label).ToList();
        if (SourceToggle.IsOn)
        {
            var host = new Uri(_page.Url).Host;
            tags.Add("source: " + (host.StartsWith("www.") ? host[4..] : host));
            if (_page.Title.Length > 0) tags.Add("page: " + _page.Title);
        }

        _download = new CancellationTokenSource();
        DownloadBar.Value = 0;
        DownloadBar.Visibility = Visibility.Visible;
        DownloadButton.IsEnabled = false;
        CancelButton.Content = "Stop";

        WebDownloadResult? result = null;
        var folder = TargetFolder();
        var trimOn = TrimToggle.IsOn; // read here, on the window's own thread
        try
        {
            var progress = new Progress<WebDownloadProgress>(p =>
            {
                DownloadBar.Value = (double)p.Done / p.Total;
                SelectedText.Text = $"Downloading {p.Done:N0} of {p.Total:N0}";
            });
            result = await WebDownloader.RunAsync(_http, chosen, folder, new Uri(_page.Url), _cookies, progress, _download.Token,
                trimOn ? file => JustJaredTrim.TrimFile(file) : null);
        }
        catch (OperationCanceledException)
        {
            SelectedText.Text = "Stopped";
        }
        catch (Exception ex)
        {
            ShowMessage("The download stopped", ex.Message);
        }
        finally
        {
            _download = null;
            DownloadBar.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Cancel";
            UpdateSelection();
        }

        if (result == null) return;

        if (result.Saved.Count > 0)
        {
            SelectedText.Text = "Saving tags…";
            await _main.TagDownloadsAsync(result.Saved, tags);
        }

        var summary = $"Saved {result.Saved.Count:N0} photos";
        if (result.Duplicates > 0) summary += $", skipped {result.Duplicates:N0} you already had";
        if (result.Failures.Count > 0) summary += $", {result.Failures.Count:N0} failed";
        summary += ".";
        if (result.Failures.Count > 0)
            summary += "\n\n" + string.Join("\n", result.Failures.Take(8)) + (result.Failures.Count > 8 ? "\n…" : string.Empty);

        if (result.Saved.Count > 0)
        {
            await _main.OpenAfterImportAsync(destination, folder);
            Close();
        }
        else
        {
            ShowMessage("Nothing was saved", summary);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_download != null) _download.Cancel();
        else Close();
    }

    // ================= Helpers =================

    private async void ShowMessage(string title, string message)
    {
        try
        {
            await new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            }.ShowAsync();
        }
        catch (Exception)
        {
            // A dialog was already open; the message is lost rather than crashing.
        }
    }
}
