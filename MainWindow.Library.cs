using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ocula.Models;
using Ocula.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Media.Core;
using Windows.Storage;
using Windows.UI.Text;

namespace Ocula;

/// <summary>An entry in the sidebar's library list.</summary>
public sealed class SidebarEntry
{
    public enum Kinds { Heading, Timeline, Collection, SmartAlbum, Action }

    public required Kinds Kind { get; init; }
    public required string Name { get; init; }
    public long Id { get; init; }
    public string Query { get; init; } = string.Empty;
    public string Glyph { get; init; } = string.Empty;
    public string CountText { get; init; } = string.Empty;

    public bool CanEdit => Kind is Kinds.Collection or Kinds.SmartAlbum;
    public Visibility IconVisibility => Kind == Kinds.Heading ? Visibility.Collapsed : Visibility.Visible;
    public double FontSize => Kind == Kinds.Heading ? 12 : 14;
    public FontWeight Weight => Kind == Kinds.Heading ? FontWeights.SemiBold : FontWeights.Normal;
    public double Opacity => Kind is Kinds.Heading or Kinds.Action ? 0.7 : 1;
    public Thickness Indent => Kind == Kinds.Heading ? new Thickness(0, 8, 0, 0) : new Thickness(0);

    /// <summary>What screen readers announce for the entry (list items fall back to ToString()).</summary>
    public override string ToString() => Name;
}

/// <summary>A month of photos in the timeline.</summary>
public sealed class PhotoGroup : List<ImageItem>
{
    public PhotoGroup(IEnumerable<ImageItem> items) : base(items) { }
    public string Title { get; init; } = string.Empty;
}

public sealed partial class MainWindow
{
    private enum ViewKind { Folder, Search, Timeline, Collection, SmartAlbum }

    private ViewKind _view = ViewKind.Folder;
    private long _viewId;
    private string _lastQuery = string.Empty;
    private SlideshowWindow? _slideshow;
    private SlideshowWindow? _presenter;

    // ================= Sidebar =================

    private async Task LoadSidebarAsync()
    {
        var collections = await _index.GetCollectionsAsync();
        var albums = await _index.GetSmartAlbumsAsync();

        var entries = new List<SidebarEntry>
        {
            new() { Kind = SidebarEntry.Kinds.Timeline, Name = "Timeline", Glyph = "\uE787" },
            new() { Kind = SidebarEntry.Kinds.Heading, Name = "COLLECTIONS" }
        };
        entries.AddRange(collections.Select(c => new SidebarEntry
        {
            Kind = SidebarEntry.Kinds.Collection, Name = c.Name, Id = c.Id, Glyph = "\uE8F1", CountText = c.Count.ToString("N0")
        }));
        entries.Add(new() { Kind = SidebarEntry.Kinds.Action, Name = "New collection…", Glyph = "\uE710" });
        entries.Add(new() { Kind = SidebarEntry.Kinds.Action, Name = "People…", Glyph = "\uE716" });
        if (AppSettings.PhunTabEnabled)
            entries.Add(new() { Kind = SidebarEntry.Kinds.Action, Name = "Phun scraper…", Glyph = "\uE896" });
        entries.Add(new() { Kind = SidebarEntry.Kinds.Heading, Name = "SMART ALBUMS" });
        entries.AddRange(albums.Select(a => new SidebarEntry
        {
            Kind = SidebarEntry.Kinds.SmartAlbum, Name = a.Name, Id = a.Id, Query = a.Query, Glyph = "\uE734"
        }));
        if (albums.Count == 0)
            entries.Add(new() { Kind = SidebarEntry.Kinds.Action, Name = "Search, then \"Save as smart album\"", Glyph = "\uE946" });

        LibraryList.ItemsSource = entries;
    }

    private async void LibraryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SidebarEntry entry) return;
        switch (entry.Kind)
        {
            case SidebarEntry.Kinds.Timeline:
                await OpenRowsAsync(await _index.GetAllAsync(), ViewKind.Timeline, 0, "Timeline", "everything, newest first");
                break;
            case SidebarEntry.Kinds.Collection:
                await OpenRowsAsync(await _index.GetCollectionRowsAsync(entry.Id), ViewKind.Collection, entry.Id, entry.Name, "collection");
                break;
            case SidebarEntry.Kinds.SmartAlbum:
                var query = new SearchQuery(entry.Query);
                await OpenRowsAsync(await _index.SearchAsync(query), ViewKind.SmartAlbum, entry.Id, entry.Name, query.Describe());
                break;
            case SidebarEntry.Kinds.Action when entry.Name.StartsWith("Phun"):
                OpenPhun();
                break;
            case SidebarEntry.Kinds.Action when entry.Name.StartsWith("People"):
                OpenPeople();
                break;
            case SidebarEntry.Kinds.Action when entry.Name.StartsWith("New collection"):
                await NewCollectionAsync(SelectedOrCurrent());
                break;
        }
    }

    /// <summary>Shows library rows (search, timeline, collection or smart album) in the grid.</summary>
    private async Task OpenRowsAsync(List<IndexRow> rows, ViewKind view, long id, string title, string description, int? limit = null)
    {
        _loadCts?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        _searchMode = true;
        _view = view;
        _viewId = id;
        HidePersonSearch();
        HidePersonHeader();

        _all.Clear();
        foreach (var row in rows)
        {
            var item = new ImageItem(row.Path, row.RawPath) { Modified = row.Modified };
            item.ApplyIndex(row);
            _all.Add(item);
        }
        ApplyFilter();
        ClearDetails();

        FolderTitle.Text = title;
        var notes = new List<string> { $"{_images.Count:N0} items", description };
        if (limit != null && rows.Count >= limit) notes.Add($"showing the newest {limit:N0}");
        if (_crawler.IsRunning && view != ViewKind.Collection) notes.Add("still indexing, so more may appear");
        FolderCount.Text = string.Join(" · ", notes.Where(n => n.Length > 0));
        SaveSearchButton.Visibility = view == ViewKind.Search ? Visibility.Visible : Visibility.Collapsed;
        await Task.CompletedTask;
    }

    /// <summary>Called when a folder opens: back to the plain, ungrouped grid.</summary>
    private void ResetLibraryView()
    {
        _view = ViewKind.Folder;
        HidePersonSearch();
        HidePersonHeader();
        SaveSearchButton.Visibility = Visibility.Collapsed;
        LibraryList.SelectedItem = null;
        if (!ReferenceEquals(Thumbs.ItemsSource, _images)) Thumbs.ItemsSource = _images;
    }

    /// <summary>Groups the timeline by month; other views use the plain list.</summary>
    private void ShowInGrid(List<ImageItem> visible)
    {
        if (_view != ViewKind.Timeline)
        {
            if (!ReferenceEquals(Thumbs.ItemsSource, _images)) Thumbs.ItemsSource = _images;
            return;
        }
        var groups = visible
            .GroupBy(i => i.Taken is { } t ? new DateTime(t.Year, t.Month, 1) : DateTime.MinValue)
            .OrderByDescending(g => g.Key)
            .Select(g => new PhotoGroup(g.OrderByDescending(i => i.Taken))
            {
                Title = g.Key == DateTime.MinValue ? $"Undated · {g.Count()}" : $"{g.Key:MMMM yyyy} · {g.Count()}"
            })
            .ToList();
        Thumbs.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
    }

    private async void SaveSearch_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Name", Text = new SearchQuery(_lastQuery).Describe() };
        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = $"Search: {_lastQuery}", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "Smart albums update by themselves: new photos that match appear automatically.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap
        });
        if (await Dialog("Save as smart album", panel, "Save").ShowAsync() != ContentDialogResult.Primary) return;
        if (name.Text.Trim().Length == 0) return;
        await _index.SaveSmartAlbumAsync(name.Text.Trim(), _lastQuery);
        await LoadSidebarAsync();
    }

    // ================= Collections =================

    private void MoreMenu_Opening(object sender, object e)
    {
        AddToCollectionMenu.Items.Clear();
        var add = new MenuFlyoutItem { Text = "New collection…" };
        add.Click += async (_, _) => await NewCollectionAsync(SelectedOrCurrent());
        AddToCollectionMenu.Items.Add(add);

        if (LibraryList.ItemsSource is List<SidebarEntry> entries)
        {
            var collections = entries.Where(x => x.Kind == SidebarEntry.Kinds.Collection).ToList();
            if (collections.Count > 0) AddToCollectionMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (var collection in collections)
            {
                var item = new MenuFlyoutItem { Text = collection.Name };
                item.Click += async (_, _) => await AddToCollectionAsync(collection.Id, collection.Name, SelectedOrCurrent());
                AddToCollectionMenu.Items.Add(item);
            }
        }
        RemoveFromCollectionItem.Visibility = _view == ViewKind.Collection ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task NewCollectionAsync(List<ImageItem> items)
    {
        var name = new TextBox { Header = "Collection name", PlaceholderText = "For example Portfolio" };
        var hint = items.Count > 0 ? $"The {items.Count} selected photos will be added." : "Add photos later from More > Add to collection.";
        var panel = new StackPanel { Spacing = 8, Width = 360 };
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = hint, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        if (await Dialog("New collection", panel, "Create").ShowAsync() != ContentDialogResult.Primary) return;
        if (name.Text.Trim().Length == 0) return;

        var id = await _index.CreateCollectionAsync(name.Text.Trim());
        if (items.Count > 0) await AddToCollectionAsync(id, name.Text.Trim(), items);
        else await LoadSidebarAsync();
    }

    private async Task AddToCollectionAsync(long id, string name, List<ImageItem> items)
    {
        if (items.Count == 0) { ShowError("Nothing selected", "Select photos to add first."); return; }
        foreach (var item in items) await IndexItemAsync(item); // collections hold library photos
        await _index.AddToCollectionAsync(id, items.Select(i => i.Path));
        await LoadSidebarAsync();
        Status.Severity = InfoBarSeverity.Success;
        Status.Title = $"Added {items.Count} to \"{name}\"";
        Status.Message = "Collections don't move or copy files, so a photo can be in several.";
        Status.IsOpen = true;
    }

    private async void RemoveFromCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_view != ViewKind.Collection) return;
        var items = SelectedOrCurrent();
        await _index.RemoveFromCollectionAsync(_viewId, items.Select(i => i.Path));
        foreach (var item in items) _all.Remove(item);
        ApplyFilter();
        await LoadSidebarAsync();
    }

    private async void SidebarRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarEntry entry || !entry.CanEdit) return;
        var name = new TextBox { Header = "Name", Text = entry.Name };
        if (await Dialog("Rename", name, "Rename").ShowAsync() != ContentDialogResult.Primary || name.Text.Trim().Length == 0) return;
        if (entry.Kind == SidebarEntry.Kinds.Collection) await _index.RenameCollectionAsync(entry.Id, name.Text.Trim());
        else await _index.RenameSmartAlbumAsync(entry.Id, name.Text.Trim());
        await LoadSidebarAsync();
    }

    private async void SidebarDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarEntry entry || !entry.CanEdit) return;
        var what = entry.Kind == SidebarEntry.Kinds.Collection ? "collection" : "smart album";
        var message = new TextBlock { Text = "Only the " + what + " is deleted. The photos stay where they are.", TextWrapping = TextWrapping.Wrap };
        if (await Dialog($"Delete the {what} \"{entry.Name}\"?", message, "Delete").ShowAsync() != ContentDialogResult.Primary) return;
        if (entry.Kind == SidebarEntry.Kinds.Collection) await _index.DeleteCollectionAsync(entry.Id);
        else await _index.DeleteSmartAlbumAsync(entry.Id);
        await LoadSidebarAsync();
        if ((_view is ViewKind.Collection or ViewKind.SmartAlbum) && _viewId == entry.Id && _folder != null)
            await LoadFolderAsync(_folder, keepSelection: false);
    }

    // ================= Slideshow and second screen =================

    private async void Slideshow_Click(object sender, RoutedEventArgs e)
    {
        var photos = _images.Where(i => !i.IsVideo).ToList();
        if (photos.Count == 0) { ShowError("Nothing to show", "Open a folder with photos first."); return; }
        var selected = SelectedOrCurrent().Where(i => !i.IsVideo).ToList();
        if (selected.Count > 1) photos = selected; // a selection plays just those photos

        var display = await ChooseDisplayAsync("Slideshow", "Start slideshow");
        if (display.Cancelled) return;
        _slideshow?.Close();
        var start = _current != null ? Math.Max(0, photos.IndexOf(_current)) : 0;
        _slideshow = new SlideshowWindow(photos, start, follow: false, display.Area);
        _slideshow.Closed += (_, _) => _slideshow = null;
        _slideshow.Activate();
    }

    private async void Presenter_Click(object sender, RoutedEventArgs e)
    {
        if (_presenter != null) { _presenter.Close(); return; }
        var display = await ChooseDisplayAsync("Show on second screen",
            "Show photos there",
            "The chosen screen shows whatever you select here, full screen. Choose this option again to stop.");
        if (display.Cancelled) return;
        _presenter = new SlideshowWindow(_images.Where(i => !i.IsVideo), 0, follow: true, display.Area);
        _presenter.Closed += (_, _) => _presenter = null;
        _presenter.Activate();
        Activate(); // keep browsing here
        if (_current != null) await _presenter.FollowAsync(_current);
    }

    private async Task<(bool Cancelled, DisplayArea? Area)> ChooseDisplayAsync(string title, string action, string? note = null)
    {
        var displays = DisplayArea.FindAll();
        var areas = Enumerable.Range(0, displays.Count).Select(i => displays[i]).ToList();
        var current = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var labels = areas.Select((d, i) =>
        {
            var tags = new List<string>();
            if (d.DisplayId.Value == DisplayArea.Primary.DisplayId.Value) tags.Add("main display");
            if (d.DisplayId.Value == current.DisplayId.Value) tags.Add("this window");
            return $"Display {i + 1} · {d.OuterBounds.Width}×{d.OuterBounds.Height}" + (tags.Count > 0 ? $" ({string.Join(", ", tags)})" : string.Empty);
        }).ToList();

        // Default to another screen when there is one: that's usually the point.
        var other = areas.FindIndex(d => d.DisplayId.Value != current.DisplayId.Value);
        var box = new ComboBox { Header = "Show on", ItemsSource = labels, SelectedIndex = Math.Max(0, other), Width = 360 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        if (note != null) panel.Children.Add(new TextBlock { Text = note, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Width = 360 });
        panel.Children.Add(new TextBlock { Text = "Press Esc to close it.", Opacity = 0.7 });

        if (await Dialog(title, panel, action).ShowAsync() != ContentDialogResult.Primary) return (true, null);
        return (false, areas.ElementAtOrDefault(box.SelectedIndex));
    }

    // ================= Video =================

    /// <summary>Shows the video player for a clip, or puts the photo preview back.</summary>
    private async Task ShowVideoAsync(ImageItem? item)
    {
        if (item is not { IsVideo: true } || item.IsCloudOnly)
        {
            if (VideoPlayer.Visibility == Visibility.Visible)
            {
                VideoPlayer.Stop();
                VideoPlayer.Visibility = Visibility.Collapsed;
                if (HdrPanel.Visibility != Visibility.Visible) Preview.Visibility = Visibility.Visible;
            }
            return;
        }

        try
        {
            VideoPlayer.Visibility = Visibility.Visible;
            Preview.Visibility = Visibility.Collapsed;
            await VideoPlayer.OpenAsync(item.Path, item.Thumbnail, autoPlay: false);
        }
        catch (Exception ex)
        {
            ShowError("Couldn't play this video", ex.Message + " It may need a codec from the Microsoft Store (e.g. HEVC Video Extensions).");
        }
    }

    // ================= Colour management =================

    /// <summary>
    /// Shows a wide-gamut photo with correct colours: the embedded profile is converted to scRGB
    /// with Win2D's colour management and drawn on the same floating-point surface as the HDR
    /// preview. Windows then maps scRGB to the monitor's colours (fully so when HDR or
    /// Automatic Colour Management is on).
    /// </summary>
    private async Task<bool> TryColourManagedPreviewAsync(ImageItem item, int version)
    {
        if (!AppSettings.ColourManaged) return false;
        var profile = await Task.Run(() => ColourService.ReadWideGamutProfile(item));
        if (profile == null || version != _selectionVersion) return false;

        try
        {
            var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            var maxSize = (int)Math.Clamp(Math.Max(PreviewHost.ActualWidth, PreviewHost.ActualHeight) * scale * 1.5, 512, 2048);
            var (pixels, w, h, orientation) = await Task.Run(() => HdrRenderer.DecodePreviewAsync(item.Path, maxSize));
            if (version != _selectionVersion) return false;

            using var source = CanvasBitmap.CreateFromBytes(_canvasDevice, pixels, w, h, DirectXPixelFormat.R8G8B8A8UIntNormalized);
            using var convert = new ColorManagementEffect
            {
                Source = source,
                SourceColorProfile = ColorManagementProfile.CreateCustom(profile.Icc),
                OutputColorProfile = new ColorManagementProfile(CanvasColorSpace.ScRgb),
                Quality = ColorManagementEffect.IsBestQualitySupported(_canvasDevice)
                    ? ColorManagementEffectQuality.Best : ColorManagementEffectQuality.Normal
            };

            // On an HDR display, scRGB 1.0 is only 80 nits; lift white to Windows' SDR white level.
            ICanvasImage output = convert;
            using var brighten = new ColorMatrixEffect { Source = convert };
            if (_displayHdr.IsHdr)
            {
                var s = _displayHdr.SdrWhiteNits / 80f;
                brighten.ColorMatrix = new Matrix5x4 { M11 = s, M22 = s, M33 = s, M44 = 1 };
                output = brighten;
            }

            var target = new CanvasRenderTarget(_canvasDevice, w, h, 96, DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                ds.DrawImage(output);
            }
            if (version != _selectionVersion) { target.Dispose(); return false; }

            ShowAdvancedPreview(target, orientation, ShortName(profile.Name));
            HdrPreviewNote.Text = $"Colour-managed preview · {profile.Name}";
            return true;
        }
        catch
        {
            return false; // fall back to the normal preview
        }
    }

    private static string ShortName(string profile) =>
        profile.Contains("P3", StringComparison.OrdinalIgnoreCase) ? "P3"
        : profile.Contains("Adobe", StringComparison.OrdinalIgnoreCase) ? "ADOBE RGB"
        : profile.Contains("ProPhoto", StringComparison.OrdinalIgnoreCase) ? "PROPHOTO"
        : "WIDE GAMUT";
}
