using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ocula.Models;
using Ocula.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Ocula;

/// <summary>
/// Compares 2 or 4 photos side by side for picking the best of a burst. Zooming or panning
/// any photo moves all of them to the same spot, so focus and expressions can be checked
/// at 100% across the set. Picks, rejects and ratings save as you go.
/// </summary>
public sealed partial class CompareWindow : Window
{
    private sealed class Pane
    {
        public required Border Frame;
        public required ScrollViewer Scroller;
        public required Image Photo;
        public required TextBlock Name;
        public required TextBlock Score;
        public required ProgressBar Bar;
        public required Border BestTag;
        public required ToggleButton Pick;
        public required ToggleButton Reject;
        public required RatingControl Rating;
        public ImageItem? Item;
        public BitmapImage? Bitmap;
    }

    private readonly MainWindow _main;
    private readonly ObservableCollection<ImageItem> _items;
    private readonly List<Pane> _panes = new();
    private int _layout = 2;
    private int _active;
    private bool _syncing;
    private bool _updating;

    public CompareWindow(MainWindow main, IEnumerable<ImageItem> items)
    {
        InitializeComponent();
        _main = main;
        _items = new ObservableCollection<ImageItem>(items);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();

        TitleText.Text = $"Compare · {_items.Count} photos";
        Strip.ItemsSource = _items;
        if (_items.Count >= 4) SetLayout(4); else SetLayout(2);
        UpdateRejectButton();
    }

    // ================= Layout =================

    private void Layout_Click(object sender, RoutedEventArgs e) =>
        SetLayout(int.Parse((string)((FrameworkElement)sender).Tag));

    private void SetLayout(int count)
    {
        _layout = count;
        TwoUp.IsChecked = count == 2;
        FourUp.IsChecked = count == 4;

        var previous = _panes.Select(p => p.Item).ToList();
        _panes.Clear();
        PanesGrid.Children.Clear();
        PanesGrid.RowDefinitions.Clear();
        PanesGrid.ColumnDefinitions.Clear();

        var rows = count == 4 ? 2 : 1;
        for (var r = 0; r < rows; r++) PanesGrid.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < 2; c++) PanesGrid.ColumnDefinitions.Add(new ColumnDefinition());

        for (var i = 0; i < count; i++)
        {
            var pane = BuildPane(i);
            Grid.SetRow(pane.Frame, i / 2);
            Grid.SetColumn(pane.Frame, i % 2);
            PanesGrid.Children.Add(pane.Frame);
            _panes.Add(pane);

            // Keep what was already showing, then fill with the next photos in the set.
            var item = i < previous.Count && previous[i] != null ? previous[i]
                     : _items.FirstOrDefault(x => _panes.All(p => p.Item != x));
            if (item != null) _ = ShowAsync(pane, item);
        }
        _active = Math.Min(_active, count - 1);
        HighlightActive();
    }

    private Pane BuildPane(int index)
    {
        var name = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var bestTag = new Border
        {
            Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 0x1F, 0x4D, 0x33)), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "SHARPEST", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0x8F, 0xE0, 0xB0)) }
        };
        var bar = new ProgressBar { Width = 70, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
        var score = new TextBlock { Width = 26, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var header = new Grid { Padding = new Thickness(12, 8, 12, 8), ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = "Sharpness", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(name);
        AddAt(header, bestTag, 1); AddAt(header, label, 2); AddAt(header, bar, 3); AddAt(header, score, 4);

        var photo = new Image { Stretch = Stretch.Uniform };
        var scroller = new ScrollViewer
        {
            ZoomMode = ZoomMode.Enabled, MinZoomFactor = 1, MaxZoomFactor = 8,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = photo
        };
        // Zoom factor 1 = the whole photo fitted in the pane.
        scroller.SizeChanged += (_, e) => { photo.Width = e.NewSize.Width; photo.Height = e.NewSize.Height; };
        scroller.ViewChanged += (_, _) => Sync(scroller);

        var pick = new ToggleButton { Content = Labelled("\uE73E", "Pick") };
        var reject = new ToggleButton { Content = Labelled("\uE711", "Reject") };
        var rating = new RatingControl { IsClearEnabled = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Grid { Padding = new Thickness(12, 8, 12, 8), ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.Children.Add(pick); AddAt(footer, reject, 1); AddAt(footer, rating, 2);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(header);
        Grid.SetRow(scroller, 1); body.Children.Add(scroller);
        Grid.SetRow(footer, 2); body.Children.Add(footer);

        var frame = new Border
        {
            Child = body, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 0x17, 0x17, 0x1A))
        };

        var pane = new Pane
        {
            Frame = frame, Scroller = scroller, Photo = photo, Name = name, Score = score, Bar = bar,
            BestTag = bestTag, Pick = pick, Reject = reject, Rating = rating
        };
        frame.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _active = _panes.IndexOf(pane);
            HighlightActive();
        }), true);
        pick.Click += async (_, _) => await SetFlagAsync(pane, pick.IsChecked == true ? 1 : 0);
        reject.Click += async (_, _) => await SetFlagAsync(pane, reject.IsChecked == true ? -1 : 0);
        rating.ValueChanged += async (s, _) =>
        {
            if (_updating || pane.Item == null) return;
            var stars = s.Value < 0 ? 0 : (int)Math.Round(s.Value);
            await _main.ApplyToItemsAsync(new List<ImageItem> { pane.Item }, i => i.Rating = stars);
        };
        return pane;
    }

    private static void AddAt(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static StackPanel Labelled(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = text });
        return panel;
    }

    private void HighlightActive()
    {
        for (var i = 0; i < _panes.Count; i++)
        {
            _panes[i].Frame.BorderBrush = new SolidColorBrush(i == _active
                ? ColorHelper.FromArgb(255, 0x3B, 0x6F, 0xE0)
                : Colors.Transparent);
        }
    }

    // ================= Showing photos =================

    private async Task ShowAsync(Pane pane, ImageItem item)
    {
        pane.Item = item;
        pane.Name.Text = item.Name;
        pane.Photo.Source = item.Thumbnail; // instant, then the sharp version
        UpdatePane(pane);
        UpdateBest();

        var bytes = await ThumbnailService.GetBytesAsync(item.Path, 2400, allowDecode: !item.IsCloudOnly,
                                                         highPriority: true, compress: false);
        if (bytes == null || pane.Item != item) return;
        pane.Bitmap = await ThumbnailService.ToBitmapAsync(bytes);
        pane.Photo.Source = pane.Bitmap;
    }

    private void UpdatePane(Pane pane)
    {
        if (pane.Item is not { } item) return;
        _updating = true;
        pane.Pick.IsChecked = item.Flag > 0;
        pane.Reject.IsChecked = item.Flag < 0;
        pane.Rating.Value = item.Rating > 0 ? item.Rating : -1;
        _updating = false;

        pane.Score.Text = item.Sharpness?.ToString() ?? "–";
        pane.Bar.Value = item.Sharpness ?? 0;
        pane.Bar.Foreground = new SolidColorBrush(item.Sharpness switch
        {
            >= 70 => ColorHelper.FromArgb(255, 0x4C, 0xC3, 0x8A),
            >= SharpnessService.SoftThreshold => ColorHelper.FromArgb(255, 0xE8, 0xA3, 0x3D),
            _ => ColorHelper.FromArgb(255, 0xE5, 0x48, 0x4D)
        });
        pane.Scroller.Opacity = item.Flag < 0 ? 0.45 : 1;
    }

    private void UpdateBest()
    {
        var best = _panes.Where(p => p.Item?.Sharpness != null).MaxBy(p => p.Item!.Sharpness);
        foreach (var pane in _panes)
            pane.BestTag.Visibility = pane == best && _panes.Count(p => p.Item != null) > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Strip_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ImageItem item || _panes.Count == 0) return;
        var existing = _panes.FirstOrDefault(p => p.Item == item);
        if (existing != null) { _active = _panes.IndexOf(existing); HighlightActive(); return; }
        await ShowAsync(_panes[_active], item);
    }

    private async void Strip_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ImageItem item || item.Thumbnail != null) return;
        var bytes = await _main.Index.GetThumbAsync(item.Path)
                    ?? await ThumbnailService.GetBytesAsync(item.Path, 384, allowDecode: !item.IsCloudOnly);
        if (bytes != null) item.Thumbnail = await ThumbnailService.ToBitmapAsync(bytes);
    }

    // ================= Linked zoom =================

    private void Sync(ScrollViewer source)
    {
        if (_syncing) return;
        _syncing = true;
        foreach (var pane in _panes.Where(p => p.Scroller != source))
            pane.Scroller.ChangeView(source.HorizontalOffset, source.VerticalOffset, source.ZoomFactor, true);
        _syncing = false;
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        _panes.FirstOrDefault()?.Scroller.ChangeView(0, 0, 1);
    }

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        // 100% = one photo pixel per screen pixel, centred.
        var pane = _panes.ElementAtOrDefault(_active);
        if (pane?.Bitmap is not { } bitmap || pane.Photo.ActualWidth < 1) return;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var fitted = Math.Min(pane.Photo.ActualWidth / bitmap.PixelWidth, pane.Photo.ActualHeight / bitmap.PixelHeight);
        var zoom = (float)Math.Clamp(1 / (fitted * scale), 1, 8);
        var x = (pane.Photo.ActualWidth * zoom - pane.Scroller.ViewportWidth) / 2;
        var y = (pane.Photo.ActualHeight * zoom - pane.Scroller.ViewportHeight) / 2;
        pane.Scroller.ChangeView(x, y, zoom);
    }

    // ================= Pick, reject, rate =================

    private async Task SetFlagAsync(Pane pane, int flag)
    {
        if (_updating || pane.Item == null) return;
        await _main.SetFlagAsync(new[] { pane.Item }, flag);
        UpdatePane(pane);
        UpdateRejectButton();
    }

    private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var pane = _panes.ElementAtOrDefault(_active);
        if (pane?.Item == null) return;

        switch (e.Key)
        {
            case VirtualKey.P: e.Handled = true; await SetFlagAsync(pane, 1); break;
            case VirtualKey.X: e.Handled = true; await SetFlagAsync(pane, -1); break;
            case VirtualKey.U: e.Handled = true; await SetFlagAsync(pane, 0); break;
            case >= VirtualKey.Number0 and <= VirtualKey.Number5:
                e.Handled = true;
                var stars = e.Key - VirtualKey.Number0;
                await _main.ApplyToItemsAsync(new List<ImageItem> { pane.Item }, i => i.Rating = stars);
                UpdatePane(pane);
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                _active = (_active + 1) % _panes.Count;
                HighlightActive();
                break;
        }
    }

    private void UpdateRejectButton()
    {
        var rejects = _items.Count(i => i.Flag < 0);
        RemoveRejectsButton.IsEnabled = rejects > 0;
        RemoveRejectsText.Text = rejects > 0 ? $"Move {rejects} rejects to Recycle Bin" : "Move rejects to Recycle Bin";
    }

    private async void RemoveRejects_Click(object sender, RoutedEventArgs e)
    {
        var rejects = _items.Where(i => i.Flag < 0).ToList();
        if (rejects.Count == 0) return;

        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = $"Move {rejects.Count} rejected photos to the Recycle Bin?",
            Content = "RAW+JPEG pairs and their .xmp sidecars are moved together. You can restore them from the Recycle Bin.",
            PrimaryButtonText = "Move to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var files = rejects.SelectMany(DuplicateFinder.FilesOf).ToList();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        await Task.Run(() => DuplicateFinder.MoveToRecycleBin(files, hwnd));

        foreach (var item in rejects.Where(i => !File.Exists(i.Path)))
        {
            _items.Remove(item);
            foreach (var pane in _panes.Where(p => p.Item == item))
            {
                pane.Item = null;
                pane.Photo.Source = null;
                pane.Name.Text = "Removed";
                var next = _items.FirstOrDefault(x => _panes.All(p => p.Item != x));
                if (next != null) await ShowAsync(pane, next);
            }
        }
        TitleText.Text = $"Compare · {_items.Count} photos";
        UpdateRejectButton();
        UpdateBest();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
