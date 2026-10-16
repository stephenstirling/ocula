using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Ocula.Models;
using Ocula.Services;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using FileAttributes = System.IO.FileAttributes;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Ocula;

public sealed partial class MainWindow : Window
{
    private readonly IndexService _index = new();
    private readonly CameraImageService _cameras = new();
    private readonly List<ImageItem> _all = new();
    private readonly ObservableCollection<ImageItem> _images = new();
    private readonly ObservableCollection<string> _tags = new();
    private readonly DispatcherQueueTimer _reloadTimer;
    private readonly Button[] _labelButtons;

    private FolderNode? _folder;
    private ImageItem? _current;
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _watcher;
    private int _selectionVersion;
    private bool _ready;
    private bool _updatingRating;
    private CancellationTokenSource? _exportCts;
    private SettingsWindow? _settings;
    private MapWindow? _map;
    private readonly LibraryCrawler _crawler;
    private readonly DispatcherQueueTimer _crawlStartTimer;
    private bool _searchMode;

    // Culling: burst stacks and compare
    private const double StackGapSeconds = 1.5;
    private readonly Dictionary<ImageItem, string> _stackOf = new();
    private readonly Dictionary<string, List<ImageItem>> _stacks = new();
    private readonly HashSet<string> _expandedStacks = new();
    private bool _refilling;
    private CompareWindow? _compare;

    // HDR preview
    private readonly CanvasDevice _canvasDevice = CanvasDevice.GetSharedDevice();
    private CanvasSwapChain? _swapChain;
    private CanvasBitmap? _hdrBitmap;
    private int _hdrOrientation = 1;
    private DisplayInformation? _displayInfo;
    private DisplayHdr _displayHdr = DisplayHdr.Standard;
    private (List<string> Cameras, List<string> Tags)? _suggestions;
    private string? _lastExportFolder;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        Thumbs.ItemsSource = _images;
        TagList.ItemsSource = _tags;
        _labelButtons = new[] { LabelRed, LabelYellow, LabelGreen, LabelBlue, LabelPurple };

        // Theme: restore the saved choice and keep the caption buttons readable when it changes.
        Root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        ThemePicker.SelectedIndex = AppSettings.Theme switch { "Light" => 0, "Dark" => 1, _ => 2 };
        ApplyTheme(AppSettings.Theme);

        // Folder watching: batch bursts of file events (e.g. a card import) into one refresh.
        _reloadTimer = DispatcherQueue.CreateTimer();
        _reloadTimer.Interval = TimeSpan.FromMilliseconds(700);
        _reloadTimer.IsRepeating = false;
        _reloadTimer.Tick += async (_, _) => await ReloadCurrentFolderAsync();

        // Background indexing of the whole library starts a few seconds after launch,
        // once the window is up and the first folder has had a chance to load.
        _crawler = new LibraryCrawler(_index);
        _crawler.StatusChanged += status => DispatcherQueue.TryEnqueue(() => UpdateCrawlStatus(status));
        _crawlStartTimer = DispatcherQueue.CreateTimer();
        _crawlStartTimer.Interval = TimeSpan.FromSeconds(5);
        _crawlStartTimer.IsRepeating = false;
        _crawlStartTimer.Tick += (_, _) => _crawler.Start(RootFolders);
        _crawlStartTimer.Start();

        InitialiseCardWatch();
        _ = LoadSidebarAsync();
        InitialiseViewing();
        InitialiseUpdates();
        InitialiseSmart();
        InitialisePeople();
        UpdateEditButton();

        // Watch the monitor's HDR state, so the preview updates if HDR is switched on or off
        // or the window moves to another screen.
        HdrPreviewToggle.IsOn = AppSettings.HdrPreview;
        try
        {
            _displayInfo = DisplayInformation.CreateForWindowId(AppWindow.Id);
            _displayInfo.AdvancedColorInfoChanged += (_, _) => DispatcherQueue.TryEnqueue(async () =>
            {
                ReadDisplayHdr();
                if (_current != null) await UpdateHdrPreviewAsync(_current, _selectionVersion);
            });
            ReadDisplayHdr();
        }
        catch
        {
            _displayHdr = DisplayHdr.Standard; // older Windows App SDK or no display info: standard preview only
        }

        var folders = AppSettings.Folders;
        if (folders.Count == 0) folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        foreach (var folder in folders) AddRootFolder(folder, save: false);

        Closed += (_, _) =>
        {
            _settings?.Close();
            _people?.Close();
            _web?.Close();
            _faceIndexer?.Stop();
            _map?.Close();
            _compare?.Close();
            _import?.Close();
            _slideshow?.Close();
            _viewer?.Close();
            ClosePhotoWindows();
            _editor?.Close();
            VideoPlayer.Unload();
            _presenter?.Close();
            _cardTimer?.Stop();
            _crawler.Stop();
            _hdrBitmap?.Dispose();
            _swapChain?.Dispose();
            _watcher?.Dispose();
            _index.Dispose();
        };

        ClearDetails();
        _ready = true;
    }

    // ================= Theme =================

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((ThemePicker.SelectedItem as SegmentedItem)?.Tag is not string theme) return;
        AppSettings.Theme = theme;
        ApplyTheme(theme);
        _settings?.ApplyTheme(theme);
    }

    private void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        UpdateCaptionButtons();
    }

    // The minimise/maximise/close buttons don't follow the app theme by themselves.
    private void UpdateCaptionButtons()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(24, 255, 255, 255) : ColorHelper.FromArgb(24, 0, 0, 0);
    }

    // ================= Folders =================

    private void AddRootFolder(string path, bool save)
    {
        if (!Directory.Exists(path)) return;
        if (FolderTree.RootNodes.Any(n => string.Equals(((FolderNode)n.Content).FullPath, path, StringComparison.OrdinalIgnoreCase))) return;

        FolderTree.RootNodes.Add(new TreeViewNode
        {
            Content = new FolderNode(path),
            HasUnrealizedChildren = SafeSubfolders(path).Count > 0
        });

        if (save)
        {
            AppSettings.Folders = FolderTree.RootNodes.Select(n => ((FolderNode)n.Content).FullPath).ToList();
            _crawler.Start(RootFolders); // include the new folder in library search
        }
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!args.Node.HasUnrealizedChildren) return;
        var folder = (FolderNode)args.Node.Content;
        foreach (var dir in SafeSubfolders(folder.FullPath))
        {
            args.Node.Children.Add(new TreeViewNode
            {
                Content = new FolderNode(dir),
                HasUnrealizedChildren = SafeSubfolders(dir).Count > 0
            });
        }
        args.Node.HasUnrealizedChildren = false;
    }

    private static IReadOnlyList<string> SafeSubfolders(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => d.FullName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: FolderNode folder })
            await LoadFolderAsync(folder, keepSelection: false);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) AddRootFolder(folder.Path, save: true);
    }

    private async Task LoadFolderAsync(FolderNode folder, bool keepSelection)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        var keepPath = keepSelection ? _current?.Path : null;
        _folder = folder;
        _searchMode = false;
        ResetLibraryView();
        FolderTitle.Text = folder.ToString();
        if (!keepSelection) SearchBox.Text = string.Empty;

        List<ImageItem> items;
        Dictionary<string, IndexRow> rows;
        _loadingFolder = !keepSelection;
        if (_loadingFolder) { _all.Clear(); _images.Clear(); UpdateState(); }
        try
        {
            items = await Task.Run(() => FolderScanner.Scan(folder.FullPath), token);
            rows = await _index.GetFolderAsync(folder.FullPath);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _loadingFolder = false;
            UpdateState();
            ShowError("Couldn't open this folder", ex.Message);
            return;
        }
        _loadingFolder = false;
        if (token.IsCancellationRequested) return;

        // Use the library for anything that hasn't changed since it was last read.
        var needsIndexing = new List<ImageItem>();
        foreach (var item in items)
        {
            if (rows.TryGetValue(item.Path, out var row) && row.Modified == item.Modified)
            {
                item.ApplyIndex(row);
            }
            else
            {
                if (row != null) item.Flag = row.Flag; // picks/rejects survive the file being edited
                needsIndexing.Add(item);
            }
        }

        _all.Clear();
        _all.AddRange(items);
        ApplyFilter();
        Watch(folder.FullPath);

        var reselect = keepPath == null ? null : _images.FirstOrDefault(i => string.Equals(i.Path, keepPath, StringComparison.OrdinalIgnoreCase));
        if (reselect != null) Thumbs.SelectedItem = reselect;
        else ClearDetails();

        _ = IndexInBackgroundAsync(needsIndexing, token); // keep going in the background
    }

    private async Task ReloadCurrentFolderAsync()
    {
        if (_folder != null) await LoadFolderAsync(_folder, keepSelection: true);
    }

    // ================= Library indexing =================

    private async Task IndexInBackgroundAsync(List<ImageItem> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) return;
            await IndexItemAsync(item);
        }
        // Newly read dates and sharpness can change stacks and the "hide soft" filter.
        if (!token.IsCancellationRequested && (FiltersActive || StacksToggle.IsChecked == true)) ApplyFilter();
    }

    /// <summary>Reads an image's metadata from disk and saves it to the library.</summary>
    private Task IndexItemAsync(ImageItem item) => Indexer.IndexAsync(item, _index);

    // ================= Thumbnails and filtering =================

    private bool FiltersActive =>
        SearchBox.Text.Trim().Length > 0 || RatingFilter.SelectedIndex > 0 || LabelFilter.SelectedIndex > 0 ||
        HideSoftToggle.IsChecked == true;

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        UpdateSuggestions();

        if (!_searchMode) ApplyFilter(); // live filter of the open folder
        else if (sender.Text.Trim().Length == 0 && _folder != null) await LoadFolderAsync(_folder, keepSelection: false);
    }

    // ================= Library search =================

    private static readonly string[] SearchHints =
    {
        "? sunset over water", "camera:", "tag:", "label:red", "rating>=4", $"year:{DateTime.Now.Year}", "date:", "hdr", "raw", "gps", "folder:"
    };

    private async void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _suggestions ??= await _index.GetSuggestionsAsync();
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion != null) return; // picking a suggestion only completes the text
        await RunLibrarySearchAsync(sender.Text);
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string chosen) return;
        var text = sender.Text;
        var start = text.LastIndexOf(' ') + 1;
        sender.Text = text[..start] + chosen + (chosen.EndsWith(':') ? string.Empty : " ");
    }

    private void UpdateSuggestions()
    {
        var text = SearchBox.Text;
        var last = text[(text.LastIndexOf(' ') + 1)..];
        static string Quote(string v) => v.Contains(' ') ? $"\"{v}\"" : v;

        IEnumerable<string> items = Array.Empty<string>();
        if (last.StartsWith("camera:", StringComparison.OrdinalIgnoreCase) && _suggestions is { } a)
            items = a.Cameras.Where(c => c.Contains(last[7..].Trim('"'), StringComparison.OrdinalIgnoreCase)).Select(c => "camera:" + Quote(c));
        else if (last.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) && _suggestions is { } b)
            items = b.Tags.Where(t => t.Contains(last[4..].Trim('"'), StringComparison.OrdinalIgnoreCase)).Select(t => "tag:" + Quote(t));
        else if (last.Length > 0)
            items = SearchHints.Where(h => h.StartsWith(last, StringComparison.OrdinalIgnoreCase) && h != last);

        SearchBox.ItemsSource = items.Take(8).ToList();
    }

    /// <summary>Searches every indexed photo, not just the open folder, and shows the results in the grid.</summary>
    private async Task RunLibrarySearchAsync(string text)
    {
        if (await TrySmartSearchAsync(text)) return; // "? sunset over water"
        var query = new SearchQuery(text);
        if (query.IsEmpty)
        {
            if (_searchMode && _folder != null) await LoadFolderAsync(_folder, keepSelection: false);
            return;
        }

        const int limit = 3000;
        _lastQuery = text.Trim();
        var rows = await _index.SearchAsync(query, limit);
        await OpenRowsAsync(rows, ViewKind.Search, 0, "Search results", query.Describe(), limit);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _searchMode || SearchBox.Text.TrimStart().StartsWith('?') ? string.Empty : SearchBox.Text.Trim(); // search results are already matched; "?" is a describe-it search
        var minRating = Math.Max(0, RatingFilter.SelectedIndex);
        var label = LabelFilter.SelectedIndex > 0 ? Labels.Names[LabelFilter.SelectedIndex - 1] : null;
        var hideSoft = HideSoftToggle.IsChecked == true;

        bool Matches(ImageItem i) =>
            i.Rating >= minRating &&
            (label == null || i.Label == label) &&
            (!hideSoft || !i.IsSoft) &&
            (query.Length == 0 ||
             i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             (i.Camera?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
             i.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));

        var visible = _all.Where(Matches).ToList();
        BuildStacks();
        if (StacksToggle.IsChecked == true) visible = CollapseStacks(visible);

        // Rebuild the grid without losing the selection.
        var selected = Thumbs.SelectedItems.OfType<ImageItem>().ToList();
        _refilling = true;
        _images.Clear();
        foreach (var item in visible) _images.Add(item);
        ShowInGrid(visible); // month groups in the timeline
        foreach (var item in selected.Where(_images.Contains)) Thumbs.SelectedItems.Add(item);
        _refilling = false;
        if (selected.Count > 0 && Thumbs.SelectedItems.Count == 0) ClearDetails();

        UpdateState();
        var hidden = _all.Count - visible.Count;
        FolderCount.Text = hidden == 0 ? $"{_all.Count} images" : $"{visible.Count} shown · {_all.Count} images";
    }

    /// <summary>Groups photos taken within a moment of each other on the same camera.</summary>
    private void BuildStacks()
    {
        _stackOf.Clear();
        _stacks.Clear();
        foreach (var item in _all) item.StackCount = 0;
        if (StacksToggle.IsChecked != true) return;

        var timed = _all.Where(i => i.Taken != null).OrderBy(i => i.Taken).ToList();
        var run = new List<ImageItem>();
        void Close()
        {
            if (run.Count > 1)
            {
                var key = run[0].Path;
                _stacks[key] = run;
                foreach (var member in run) _stackOf[member] = key;
            }
            run = new List<ImageItem>();
        }

        for (var k = 0; k < timed.Count; k++)
        {
            var item = timed[k];
            var prev = k > 0 ? timed[k - 1] : null;
            var continues = prev != null && item.Camera == prev.Camera &&
                            (item.Taken!.Value - prev.Taken!.Value).TotalSeconds <= StackGapSeconds;
            if (!continues) Close();
            run.Add(item);
        }
        Close();
    }

    /// <summary>The photo shown for a collapsed stack: picked, then best rated, then sharpest.</summary>
    private static ImageItem StackCover(List<ImageItem> members) =>
        members.OrderByDescending(i => i.Flag).ThenByDescending(i => i.Rating)
               .ThenByDescending(i => i.Sharpness ?? -1).First();

    private List<ImageItem> CollapseStacks(List<ImageItem> visible)
    {
        var shown = new HashSet<ImageItem>(visible);
        var coveredStacks = new HashSet<string>();
        var result = new List<ImageItem>();
        foreach (var item in visible)
        {
            if (!_stackOf.TryGetValue(item, out var key)) { result.Add(item); continue; }
            var members = _stacks[key];
            if (_expandedStacks.Contains(key))
            {
                item.StackCount = item == StackCover(members) ? members.Count : 0;
                result.Add(item);
                continue;
            }
            // One tile per collapsed stack: its cover, or the first visible member if the
            // cover is hidden by a filter.
            var cover = StackCover(members);
            var representative = shown.Contains(cover) ? cover : item;
            if (item == representative && coveredStacks.Add(key))
            {
                item.StackCount = members.Count;
                result.Add(item);
            }
        }
        return result;
    }

    private void CullingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_ready) ApplyFilter();
        if (_current != null) UpdateCullingUI(_current);
    }

    // Load a thumbnail only when its tile is created on screen.
    private void Thumbs_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ImageItem item || item.ThumbnailRequested) return;
        item.ThumbnailRequested = true;
        _ = LoadThumbnailAsync(item);
    }

    private async Task LoadThumbnailAsync(ImageItem item)
    {
        try
        {
            var bytes = item.CachedThumb;
            if (bytes == null && item.Indexed) bytes = await _index.GetThumbAsync(item.Path); // e.g. search results
            if (bytes == null)
            {
                bytes = await ThumbnailService.GetBytesAsync(item.Path, 384, allowDecode: !item.IsCloudOnly);
                if (bytes == null) return;
                item.CachedThumb = bytes;
                if (item.Indexed) await _index.SaveAsync(item.ToRow());
            }
            item.Thumbnail = await ThumbnailService.ToBitmapAsync(bytes);
            if (item.Indexed) item.CachedThumb = null;
        }
        catch
        {
            // Leave the tile blank.
        }
    }

    private void SizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (Thumbs?.ItemsPanelRoot is ItemsWrapGrid grid)
        {
            grid.ItemWidth = e.NewValue;
            grid.ItemHeight = e.NewValue * 0.75 + 56;
        }
    }

    // ================= Details panel =================

    private async void Thumbs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refilling) return; // the grid is being rebuilt; selection is restored afterwards
        var count = Thumbs.SelectedItems.Count;
        if (count == 0) { ClearDetails(); return; }

        SelectionNote.Text = $"Rating, label and tag changes apply to all {count} selected images.";
        SelectionNote.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;

        var item = e.AddedItems.OfType<ImageItem>().LastOrDefault() ?? Thumbs.SelectedItem as ImageItem;
        if (item == null || item == _current) return;

        var token = ++_clickToken;
        if (ShouldWaitForDoubleClick())
        {
            await Task.Delay(SingleClickDelayMs);
            if (token != _clickToken) return; // a double-click or another selection came first
        }
        await ShowDetailsAsync(item);
    }

    private async Task ShowDetailsAsync(ImageItem item)
    {
        _current = item;
        var version = ++_selectionVersion;
        bool Stale() => version != _selectionVersion;

        FileName.Text = item.Name;
        ShowCaptureDetails(item);
        BadgeText.Text = item.Badge;
        BadgeBorder.Visibility = item.BadgeVisibility;
        Preview.Source = item.Thumbnail;
        SyncFilmstrip(item);
        HideHdrPreview(); // don't leave the previous photo's HDR preview on screen
        await ShowVideoAsync(item);
        if (_presenter != null) _ = _presenter.FollowAsync(item);
        DateTaken.Text = string.Empty;
        CameraName.Text = "Reading metadata…";
        HdrTitle.Text = "Checking dynamic range…";
        HdrDescription.Text = string.Empty;
        Exposure.Text = string.Empty;
        CameraImage.Source = null;
        CameraFallback.Visibility = Visibility.Visible;
        CameraCredit.Visibility = Visibility.Collapsed;
        TagStorageNote.Text = MetadataStore.Describe(item);

        try
        {
            var previewTask = ThumbnailService.GetBytesAsync(item.Path, 1600, allowDecode: !item.IsCloudOnly,
                                                             highPriority: true, compress: false);
            await IndexItemAsync(item);
            if (Stale()) return;

            DateTaken.Text = item.Taken?.ToString("f") ?? (item.IsCloudOnly ? "Online-only file" : "Date unknown");
            CameraName.Text = item.Camera ?? (item.IsCloudOnly ? "Download the file to see camera details" : "Unknown camera");
            Exposure.Text = item.Exposure ?? string.Empty;
            UpdateHdrCard(item.IsCloudOnly ? null : item.Hdr, item.IsCloudOnly);
            UpdateUserDataUI(item);
            UpdateCullingUI(item);
            _ = UpdateSmartSuggestionsAsync(item, version);

            var preview = await previewTask;
            if (Stale()) return;
            if (preview != null) Preview.Source = await ThumbnailService.ToBitmapAsync(preview);
            _ = UpdateHdrPreviewAsync(item, version);

            if (item.Camera != null)
            {
                var camera = await _cameras.GetAsync(item.Camera);
                if (Stale() || camera == null) return;
                CameraImage.Source = new BitmapImage(new Uri(camera.LocalPath));
                CameraFallback.Visibility = Visibility.Collapsed;
                CameraCredit.NavigateUri = new Uri(camera.SourcePageUrl);
                CameraCredit.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            if (!Stale()) ShowError("Couldn't read this image", ex.Message);
        }
    }

    private void UpdateHdrCard(string? kind, bool unknown = false)
    {
        HdrTitle.Text = unknown ? "Dynamic range unknown" : HdrDetector.Title(kind);
        HdrDescription.Text = unknown ? "Download the file to check whether it's HDR." : HdrDetector.Description(kind);

        var isHdr = kind != null && !unknown;
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var muted = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        HdrIconText.Text = isHdr ? "HDR" : "SDR";
        HdrIconText.Foreground = isHdr ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
                                       : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        HdrIcon.Background = isHdr ? accent : new SolidColorBrush(Colors.Transparent);
        HdrIcon.BorderBrush = isHdr ? accent : muted;
    }

    // ================= HDR preview =================

    private void ReadDisplayHdr()
    {
        if (_displayInfo == null) return;
        var info = _displayInfo.GetAdvancedColorInfo();
        var hdr = info.CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange;
        var sdrWhite = info.SdrWhiteLevelInNits > 0 ? (float)info.SdrWhiteLevelInNits : 80f;
        // Some monitors don't report a peak; assume a typical entry-level HDR screen.
        var peak = info.MaxLuminanceInNits > sdrWhite ? (float)info.MaxLuminanceInNits : Math.Max(sdrWhite * 4, 600f);
        _displayHdr = new DisplayHdr(hdr, sdrWhite, peak);
    }

    private async Task UpdateHdrPreviewAsync(ImageItem item, int version)
    {
        HideHdrPreview();
        var kind = item.IsCloudOnly ? null : item.Hdr;
        if (kind is null or HdrDetector.Capture)
        {
            // Not HDR, or taken in HDR mode but saved as standard range: nothing extra to show,
            // apart from accurate colours for wide-gamut photos.
            HdrPreviewToggle.Visibility = Visibility.Collapsed;
            HdrPreviewNote.Text = string.Empty;
            await TryColourManagedPreviewAsync(item, version);
            return;
        }

        HdrPreviewToggle.Visibility = Visibility.Visible;
        if (!HdrRenderer.CanRender(item.Path, kind))
        {
            HdrPreviewToggle.IsEnabled = false;
            HdrPreviewNote.Text = "HDR preview isn't available for this kind of HDR photo yet. Open in Photos to see it in HDR.";
            return;
        }
        if (!_displayHdr.IsHdr)
        {
            HdrPreviewToggle.IsEnabled = false;
            HdrPreviewNote.Text = "To see this photo in HDR, turn on \"Use HDR\" in Windows Settings > System > Display.";
            return;
        }

        HdrPreviewToggle.IsEnabled = true;
        if (!HdrPreviewToggle.IsOn)
        {
            HdrPreviewNote.Text = "Showing the standard version.";
            return;
        }

        HdrPreviewNote.Text = "Preparing HDR preview…";
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var maxSize = (int)Math.Clamp(Math.Max(PreviewHost.ActualWidth, PreviewHost.ActualHeight) * scale * 1.5, 512, 2048);
        var display = _displayHdr;

        HdrImage? image = null;
        try
        {
            image = await Task.Run(() => HdrRenderer.RenderAsync(item.Path, kind, maxSize, display));
        }
        catch
        {
            // Fall through to the message below.
        }
        if (version != _selectionVersion) return;

        if (image == null)
        {
            HdrPreviewNote.Text = "This photo's HDR data couldn't be read, so the standard version is shown.";
            return;
        }

        ShowHdrPreview(image);
        HdrPreviewNote.Text = $"Showing in HDR. This screen goes up to {display.PeakNits:N0} nits, " +
                              $"{display.Headroom:0.#}× brighter than normal white.";
    }

    private void ShowHdrPreview(HdrImage image)
    {
        var bytes = MemoryMarshal.AsBytes(image.Pixels.AsSpan()).ToArray();
        ShowAdvancedPreview(CanvasBitmap.CreateFromBytes(_canvasDevice, bytes, image.Width, image.Height,
                                                         DirectXPixelFormat.R16G16B16A16Float), image.Orientation, "HDR");
    }

    /// <summary>Shows a floating-point (scRGB) bitmap on the HDR-capable surface: HDR or colour-managed photos.</summary>
    private void ShowAdvancedPreview(CanvasBitmap bitmap, int orientation, string badge)
    {
        _hdrBitmap?.Dispose();
        _hdrBitmap = bitmap;
        _hdrOrientation = orientation;
        HdrBadgeText.Text = badge;
        HdrPanel.Visibility = Visibility.Visible;
        HdrBadge.Visibility = Visibility.Visible;
        Preview.Visibility = Visibility.Collapsed;
        DrawHdrPreview();
    }

    private void HideHdrPreview()
    {
        HdrPanel.Visibility = Visibility.Collapsed;
        HdrBadge.Visibility = Visibility.Collapsed;
        Preview.Visibility = VideoPlayer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        _hdrBitmap?.Dispose();
        _hdrBitmap = null;
    }

    private void HdrPanel_SizeChanged(object sender, SizeChangedEventArgs e) => DrawHdrPreview();

    /// <summary>
    /// Draws the HDR bitmap into a 16-bit floating-point swap chain. Windows treats that
    /// format as scRGB, so values above 1.0 show brighter than SDR white on an HDR screen.
    /// </summary>
    private void DrawHdrPreview()
    {
        if (_hdrBitmap == null || HdrPanel.Visibility != Visibility.Visible) return;
        var w = (float)HdrPanel.ActualWidth;
        var h = (float)HdrPanel.ActualHeight;
        if (w < 1 || h < 1 || HdrPanel.XamlRoot == null) return;
        var dpi = (float)(HdrPanel.XamlRoot.RasterizationScale * 96);

        if (_swapChain == null)
        {
            _swapChain = new CanvasSwapChain(_canvasDevice, w, h, dpi, DirectXPixelFormat.R16G16B16A16Float, 2,
                                             CanvasAlphaMode.Premultiplied);
            HdrPanel.SwapChain = _swapChain;
        }
        else if (_swapChain.Size.Width != w || _swapChain.Size.Height != h || _swapChain.Dpi != dpi)
        {
            _swapChain.ResizeBuffers(w, h, dpi);
        }

        // Fit the photo inside the panel, applying its EXIF rotation.
        var bw = (float)_hdrBitmap.SizeInPixels.Width;
        var bh = (float)_hdrBitmap.SizeInPixels.Height;
        var angle = _hdrOrientation switch { 3 => MathF.PI, 6 => MathF.PI / 2, 8 => -MathF.PI / 2, _ => 0f };
        var (vw, vh) = _hdrOrientation is 6 or 8 ? (bh, bw) : (bw, bh);
        var fit = Math.Min(w / vw, h / vh);

        using (var ds = _swapChain.CreateDrawingSession(Colors.Transparent))
        {
            ds.Transform = Matrix3x2.CreateTranslation(-bw / 2, -bh / 2)
                         * Matrix3x2.CreateRotation(angle)
                         * Matrix3x2.CreateScale(fit)
                         * Matrix3x2.CreateTranslation(w / 2, h / 2);
            ds.DrawImage(_hdrBitmap, 0, 0);
        }
        _swapChain.Present();
    }

    private async void HdrPreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppSettings.HdrPreview = HdrPreviewToggle.IsOn;
        if (_current != null && _ready) await UpdateHdrPreviewAsync(_current, _selectionVersion);
    }

    private void UpdateUserDataUI(ImageItem item)
    {
        _updatingRating = true;
        RatingBox.Value = item.Rating > 0 ? item.Rating : -1;
        _updatingRating = false;

        // The accent colour reads well in both light and dark themes.
        var highlight = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
                        ?? new SolidColorBrush(Colors.DodgerBlue);
        foreach (var button in _labelButtons)
            button.BorderBrush = (string)button.Tag == item.Label ? highlight : new SolidColorBrush(Colors.Transparent);

        _tags.Clear();
        foreach (var tag in item.Tags) _tags.Add(tag);
    }

    private void ClearDetails()
    {
        _current = null;
        _selectionVersion++;
        _suggested.Clear();
        SuggestPanel.Visibility = Visibility.Collapsed;
        ShowCaptureDetails(null);
        Preview.Source = null;
        FileName.Text = "No image selected";
        BadgeBorder.Visibility = Visibility.Collapsed;
        DateTaken.Text = string.Empty;
        CameraName.Text = string.Empty;
        Exposure.Text = string.Empty;
        CameraImage.Source = null;
        CameraFallback.Visibility = Visibility.Visible;
        CameraCredit.Visibility = Visibility.Collapsed;
        SelectionNote.Visibility = Visibility.Collapsed;
        UpdateHdrCard(null);
        HdrDescription.Text = string.Empty;
        HideHdrPreview();
        _ = ShowVideoAsync(null);
        SharpChip.Visibility = Visibility.Collapsed;
        StackRow.Visibility = Visibility.Collapsed;
        HdrPreviewToggle.Visibility = Visibility.Collapsed;
        HdrPreviewNote.Text = string.Empty;
        _updatingRating = true;
        RatingBox.Value = -1;
        _updatingRating = false;
        foreach (var button in _labelButtons) button.BorderBrush = new SolidColorBrush(Colors.Transparent);
        _tags.Clear();
        TagStorageNote.Text = string.Empty;
    }

    // ================= Ratings, labels and tags =================

    /// <summary>Applies a change to every selected image, writes it to the files and the library.</summary>
    private Task ApplyToSelectionAsync(Action<ImageItem> change) => ApplyToItemsAsync(SelectedOrCurrent(), change);

    /// <summary>Changes rating, label or tags on the given photos and saves them to the files and library.</summary>
    public async Task ApplyToItemsAsync(List<ImageItem> targets, Action<ImageItem> change)
    {

        foreach (var item in targets)
        {
            if (item.IsCloudOnly)
            {
                ShowError($"{item.Name} is online-only", "Download it from OneDrive first (right-click > Always keep on this device), then try again.");
                continue;
            }
            try
            {
                await IndexItemAsync(item);
                change(item);
                await MetadataStore.WriteAsync(item);
                item.Modified = await Task.Run(() => FolderScanner.ModifiedOf(item));
                await _index.SaveAsync(item.ToRow());
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't save changes to {item.Name}", ex.Message);
            }
        }

        if (_current != null) UpdateUserDataUI(_current);
    }

    // Lightroom-style shortcuts: 0–5 set the rating, 6–9 toggle red/yellow/green/blue.
    private async void Thumbs_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Delete moves the selected photos to the Recycle Bin.
        if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            await TrashSelectionAsync();
            return;
        }

        // Lightroom-style flags: P pick, X reject, U clear. C opens compare.
        if (_current != null && (e.Key is VirtualKey.P or VirtualKey.X or VirtualKey.U))
        {
            e.Handled = true;
            var flag = e.Key == VirtualKey.P ? 1 : e.Key == VirtualKey.X ? -1 : 0;
            await SetFlagAsync(SelectedOrCurrent(), flag);
            return;
        }
        if (e.Key == VirtualKey.Enter && _current != null)
        {
            e.Handled = true;
            OpenViewer(_current);
            return;
        }
        if (e.Key == VirtualKey.E && _current != null)
        {
            e.Handled = true;
            OpenEditor(_current);
            return;
        }
        if (e.Key == VirtualKey.F2)
        {
            e.Handled = true;
            Rename_Click(sender, new RoutedEventArgs());
            return;
        }
        if (e.Key == VirtualKey.C)
        {
            e.Handled = true;
            OpenCompare(ItemsForCompare());
            return;
        }

        int? digit = e.Key switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => e.Key - VirtualKey.Number0,
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => e.Key - VirtualKey.NumberPad0,
            _ => null
        };
        if (digit is not int d || _current == null) return;
        e.Handled = true;

        if (d <= 5)
        {
            await ApplyToSelectionAsync(i => i.Rating = d);
        }
        else if (Labels.ForShortcut(d) is string label)
        {
            var newLabel = _current.Label == label ? null : label;
            await ApplyToSelectionAsync(i => i.Label = newLabel);
        }
    }

    private async void RatingBox_ValueChanged(RatingControl sender, object args)
    {
        if (_updatingRating || _current == null) return;
        var stars = sender.Value < 0 ? 0 : (int)Math.Round(sender.Value);
        await ApplyToSelectionAsync(i => i.Rating = stars);
    }

    private async void Label_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: string label }) return;
        var newLabel = _current.Label == label ? null : label;
        await ApplyToSelectionAsync(i => i.Label = newLabel);
    }

    private async void TagInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _current == null) return;
        e.Handled = true;

        var tag = TagInput.Text.Trim();
        TagInput.Text = string.Empty;
        if (tag.Length == 0) return;

        await ApplyToSelectionAsync(i =>
        {
            if (!i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) i.Tags.Add(tag);
        });
    }

    private async void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        await ApplyToSelectionAsync(i => i.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
    }

    // ================= Folder watching =================

    private void Watch(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            // FileName only: new, deleted and renamed files. Not LastWrite, so our own
            // metadata saves don't trigger a refresh.
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false
            };
            void OnChange(object? s, FileSystemEventArgs e)
            {
                if (SupportedFormats.IsSupported(e.FullPath))
                    DispatcherQueue.TryEnqueue(() => { _reloadTimer.Stop(); _reloadTimer.Start(); });
            }
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += (s, e) => OnChange(s, e);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            // Some network locations don't support change notifications.
        }
    }

    // ================= Opening a file or folder (File Explorer, command line) =================

    /// <summary>Shows a folder, or a file's folder with that file selected. Returns the selected file's item.</summary>
    public async Task<ImageItem?> OpenPathAsync(string path)
    {
        string folder;
        string? file = null;
        if (Directory.Exists(path)) folder = path;
        else if (File.Exists(path)) { folder = Path.GetDirectoryName(path)!; file = path; }
        else return null;

        await LoadFolderAsync(new FolderNode(folder), keepSelection: false);
        if (file == null) return null;

        var match = _images.FirstOrDefault(i => i.AllPaths.Any(p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase)));
        if (match != null)
        {
            Thumbs.SelectedItem = match;
            Thumbs.ScrollIntoView(match);
        }
        return match;
    }

    private List<ImageItem> SelectedOrCurrent()
    {
        var items = Thumbs.SelectedItems.OfType<ImageItem>().ToList();
        if (items.Count == 0 && _current != null) items.Add(_current);
        return items;
    }

    // ================= Settings window =================

    public IndexService Index => _index;

    public IReadOnlyList<string> RootFolders =>
        FolderTree.RootNodes.Select(n => ((FolderNode)n.Content).FullPath).ToList();

    public void AddFolder(string path) => AddRootFolder(path, save: true);

    public void RemoveFolder(string path)
    {
        var node = FolderTree.RootNodes.FirstOrDefault(n =>
            string.Equals(((FolderNode)n.Content).FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (node == null) return;
        FolderTree.RootNodes.Remove(node);
        AppSettings.Folders = RootFolders.ToList();
        _crawler.Start(RootFolders);
    }

    /// <summary>Changes the theme from Settings; the pill's handler saves and applies it.</summary>
    public void SetTheme(string theme)
    {
        ThemePicker.SelectedIndex = theme switch { "Light" => 0, "Dark" => 1, _ => 2 };
    }

    /// <summary>Re-reads the open folder, e.g. after the library cache was cleared.</summary>
    public async Task RefreshAsync()
    {
        if (_folder == null) return;
        foreach (var item in _all) item.Indexed = false;
        _crawler.Start(RootFolders);
        await LoadFolderAsync(_folder, keepSelection: true);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(this);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Activate();
    }

    // ================= Culling: flags, stacks, compare =================

    /// <summary>Pick (1), reject (-1) or clear (0). Flags live in Ocula's library.</summary>
    public async Task SetFlagAsync(IEnumerable<ImageItem> items, int flag)
    {
        foreach (var item in items.ToList())
        {
            await IndexItemAsync(item); // make sure the photo has a library row to hold the flag
            item.Flag = flag;
            await _index.SetFlagAsync(item.Path, flag);
        }
    }

    private void UpdateCullingUI(ImageItem item)
    {
        if (item.Sharpness is int score && score >= 0)
        {
            var soft = item.IsSoft;
            SharpText.Text = $"{(soft ? "Soft" : "Sharp")} · {score}";
            SharpChip.Background = (Brush)Application.Current.Resources[soft ? "SystemFillColorCautionBackgroundBrush" : "SystemFillColorSuccessBackgroundBrush"];
            SharpText.Foreground = (Brush)Application.Current.Resources[soft ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush"];
            SharpChip.Visibility = Visibility.Visible;
        }
        else
        {
            SharpChip.Visibility = Visibility.Collapsed;
        }

        if (_stackOf.TryGetValue(item, out var key))
        {
            StackInfo.Text = $"Stack of {_stacks[key].Count}";
            ExpandStackButton.Content = _expandedStacks.Contains(key) ? "Collapse" : "Show all";
            StackRow.Visibility = Visibility.Visible;
        }
        else
        {
            StackRow.Visibility = Visibility.Collapsed;
        }
    }

    private void ExpandStack_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || !_stackOf.TryGetValue(_current, out var key)) return;
        if (!_expandedStacks.Add(key)) _expandedStacks.Remove(key);
        ApplyFilter();
        UpdateCullingUI(_current);
    }

    private void CompareStack_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null && _stackOf.TryGetValue(_current, out var key)) OpenCompare(_stacks[key].ToList());
    }

    private void Compare_Click(object sender, RoutedEventArgs e) => OpenCompare(ItemsForCompare());

    /// <summary>The selection, or, when a single stack is selected, everything in that stack.</summary>
    private List<ImageItem> ItemsForCompare()
    {
        var selected = SelectedOrCurrent();
        return selected.Count == 1 && _stackOf.TryGetValue(selected[0], out var key) ? _stacks[key].ToList() : selected;
    }

    private void OpenCompare(List<ImageItem> items)
    {
        items = items.Where(i => !i.IsVideo).ToList();
        if (items.Count < 2)
        {
            ShowError("Choose photos to compare", "Select two or more photos (Ctrl+click), or a stack, then press Compare.");
            return;
        }
        _compare?.Close();
        _compare = new CompareWindow(this, items.Take(24));
        _compare.Closed += (_, _) =>
        {
            _compare = null;
            if (_ready) ApplyFilter(); // picks may change which photo covers each stack
        };
        _compare.Activate();
    }

    // ================= Background indexing and map =================

    public ElementTheme CurrentTheme => Root.ActualTheme;

    private void UpdateCrawlStatus(CrawlStatus status)
    {
        CrawlRing.IsActive = status.Running && !status.Paused;
        CrawlButton.Visibility = status.Running ? Visibility.Visible : Visibility.Collapsed;
        CrawlIcon.Glyph = status.Paused ? "\uE768" : "\uE769"; // play / pause
        ToolTipService.SetToolTip(CrawlButton, status.Paused ? "Resume indexing" : "Pause indexing");

        var where = status.Folder == null ? string.Empty : " · " + new FolderNode(status.Folder);
        _crawlBase = !status.Running ? $"Library up to date · {status.Checked:N0} photos"
                   : status.Paused ? $"Indexing paused · {status.Checked:N0} checked"
                   : $"Indexing · {status.Checked:N0} checked{where}";
        CrawlText.Text = _crawlBase + _smartNote + _faceNote;
    }

    private void CrawlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_crawler.IsPaused) _crawler.Resume(); else _crawler.Pause();
    }

    private void Map_Click(object sender, RoutedEventArgs e)
    {
        if (_map == null)
        {
            _map = new MapWindow(this);
            _map.Closed += (_, _) => _map = null;
        }
        _map.Activate();
    }

    // ================= Duplicates =================

    private void FindDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (_folder == null)
        {
            ShowError("Choose a folder first", "Pick a folder on the left, then look for duplicates in it.");
            return;
        }
        new DuplicatesWindow(_folder.FullPath, _index, Root.ActualTheme).Activate();
    }

    // ================= Export =================

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCts != null) return; // one export at a time
        var items = SelectedOrCurrent().Where(i => !i.IsVideo).ToList(); // export is for photos
        var skipped = items.Count(i => i.IsCloudOnly);
        items = items.Where(i => !i.IsCloudOnly).ToList();
        if (items.Count == 0)
        {
            if (skipped > 0) ShowError("Nothing to export", "The selected images are online-only. Download them from OneDrive first.");
            return;
        }

        var dialog = new ExportDialog(items.Count, WinRT.Interop.WindowNative.GetWindowHandle(this))
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Result is not { } preset) return;

        _exportCts = new CancellationTokenSource();
        _lastExportFolder = preset.Destination;
        _lastOutputFile = null;
        ExportBar.Severity = InfoBarSeverity.Informational;
        ExportBar.Title = $"Exporting {items.Count} images";
        ExportBar.Message = skipped > 0 ? $"{skipped} online-only images were skipped." : string.Empty;
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Maximum = items.Count;
        ExportProgress.Value = 0;
        ExportActionButton.Content = "Cancel";
        ExportBar.IsOpen = true;

        var progress = new Progress<int>(done =>
        {
            ExportProgress.Value = done;
            ExportBar.Title = $"Exporting {done} of {items.Count}";
        });

        try
        {
            var result = await ExportService.ExportAsync(items, preset, progress, _exportCts.Token);
            ExportBar.Severity = result.Failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            ExportBar.Title = $"Exported {result.Succeeded} of {items.Count} images";
            ExportBar.Message = result.Failed.Count == 0
                ? preset.Destination
                : "Couldn't export: " + string.Join("; ", result.Failed.Take(3)) + (result.Failed.Count > 3 ? "…" : "");
        }
        catch (OperationCanceledException)
        {
            ExportBar.Severity = InfoBarSeverity.Warning;
            ExportBar.Title = "Export cancelled";
            ExportBar.Message = "Images already exported were kept.";
        }
        catch (Exception ex)
        {
            ExportBar.Severity = InfoBarSeverity.Error;
            ExportBar.Title = "Export failed";
            ExportBar.Message = ex.Message;
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            ExportProgress.Visibility = Visibility.Collapsed;
            ExportActionButton.Content = "Open folder";
        }
    }

    private async void ExportAction_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCts != null)
        {
            _exportCts.Cancel();
        }
        else if (_lastOutputFile != null && File.Exists(_lastOutputFile))
        {
            await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(_lastOutputFile));
        }
        else if (_lastExportFolder != null && Directory.Exists(_lastExportFolder))
        {
            await Launcher.LaunchFolderPathAsync(_lastExportFolder);
        }
    }

    // ================= Share =================

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedOrCurrent().Where(i => !i.IsCloudOnly).ToList();
        if (items.Count == 0) return;

        try
        {
            // Share the JPEG of a RAW+JPEG pair; most apps that receive shares can't open RAW.
            var files = new List<IStorageItem>();
            foreach (var item in items) files.Add(await StorageFile.GetFileFromPathAsync(item.Path));

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var manager = DataTransferManagerInterop.GetForWindow(hwnd);
            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                args.Request.Data.Properties.Title = items.Count == 1 ? items[0].Name : $"{items.Count} photos";
                args.Request.Data.SetStorageItems(files);
                manager.DataRequested -= handler;
            };
            manager.DataRequested += handler;
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            ShowError("Couldn't share", ex.Message);
        }
    }

    // ================= Opening in other apps =================

    private async void OpenInPhotos_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try
        {
            if (!await PhotosLauncher.OpenInPhotosAsync(_current.Path))
                ShowError("Couldn't open Photos", "Windows didn't launch an app for this file.");
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open Photos", ex.Message);
        }
    }

    private async void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try
        {
            await PhotosLauncher.OpenWithAsync(_current.Path, WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open the file", ex.Message);
        }
    }

    private void ShowError(string title, string message)
    {
        Status.Severity = InfoBarSeverity.Error;
        Status.Title = title;
        Status.Message = message;
        Status.IsOpen = true;
    }
}
