using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Ocula.Models;
using Ocula.Services;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Ocula;

/// <summary>
/// Full-screen viewer: photos zoom and pan (mouse wheel, pinch, double-click), videos play,
/// and the controls fade away while you look. Esc returns to the library on the same photo.
///
/// As a photo window (double-click a thumbnail) it is an ordinary window instead, with a layout
/// chip and numbered spot buttons in its title bar; MainWindow.PhotoWindows.cs places it.
/// </summary>
public sealed partial class ViewerWindow : Window
{
    private static readonly float[] ZoomSteps = { 1f, 1.5f, 2f, 3f, 4f, 6f, 8f };

    private readonly MainWindow _main;
    private readonly List<ImageItem> _items;
    private readonly DispatcherQueueTimer _hide;
    private readonly bool _photoWindow;
    private int _placing;
    private bool _applyingLinkedView;
    private int _index;
    private int _version;
    private BitmapImage? _bitmap;

    public ViewerWindow(MainWindow main, List<ImageItem> items, int index, bool photoWindow = false)
    {
        InitializeComponent();
        _main = main;
        _items = items;
        _photoWindow = photoWindow;

        if (_photoWindow) SetUpPhotoWindow();
        else AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        Overlay.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(250) };
        _hide = DispatcherQueue.CreateTimer();
        _hide.Interval = TimeSpan.FromSeconds(2.5);
        _hide.IsRepeating = false;
        _hide.Tick += (_, _) => Overlay.Opacity = 0;

        Strip.ItemsSource = _items;
        Activated += (_, _) => Root.Focus(FocusState.Programmatic);
        Closed += (_, _) =>
        {
            _hide.Stop();
            Video.Unload();
            if (_photoWindow) _main.PhotoWindowClosed(this);
            else if (_items.ElementAtOrDefault(_index) is { } current) _main.SelectItem(current);
        };
        _ = ShowAsync(index);
        ShowOverlay();
    }

    private async Task ShowAsync(int index)
    {
        if (_items.Count == 0) return;
        _index = (index % _items.Count + _items.Count) % _items.Count;
        var item = _items[_index];
        var version = ++_version;

        NameText.Text = item.Name;
        if (_photoWindow)
        {
            TitleText.Text = item.Name;
            Title = $"{item.Name} – Ocula";
        }
        var info = new List<string> { $"{_index + 1} of {_items.Count}" };
        if (item.Taken is { } taken) info.Add(taken.ToString("d MMMM yyyy, HH:mm"));
        if (item.Camera != null) info.Add(item.Camera);
        if (item.Rating > 0) info.Add(new string('★', item.Rating));
        InfoText.Text = string.Join(" · ", info);
        Strip.SelectedIndex = _index;
        Strip.ScrollIntoView(item);

        if (item.IsVideo && !item.IsCloudOnly)
        {
            Scroller.Visibility = Visibility.Collapsed;
            Video.Visibility = Visibility.Visible;
            ZoomText.Text = string.Empty;
            await Video.OpenAsync(item.Path, item.Thumbnail, autoPlay: true);
            return;
        }

        Video.Stop();
        Video.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        Photo.Source = item.Thumbnail; // instant, then full detail
        Scroller.ChangeView(0, 0, 1, true);
        Busy.IsActive = true;

        // Decode at screen resolution or above, so 100% zoom stays sharp on most photos.
        var size = (uint)Math.Clamp(Math.Max(AppWindow.Size.Width, AppWindow.Size.Height) * 2, 2048, 8192);
        var bytes = await ThumbnailService.GetBytesAsync(item.Path, size, allowDecode: !item.IsCloudOnly,
                                                         highPriority: true, compress: false);
        if (version != _version) return;
        Busy.IsActive = false;
        if (bytes == null) return;
        _bitmap = await ThumbnailService.ToBitmapAsync(bytes);
        if (version != _version) return;
        Photo.Source = _bitmap;
        UpdateZoomText();

        // Have the neighbours' thumbnails ready for the strip.
        foreach (var neighbour in new[] { _index - 1, _index + 1 }.Select(i => _items.ElementAtOrDefault(i)).OfType<ImageItem>())
            if (neighbour.Thumbnail == null) await LoadThumbAsync(neighbour);
    }

    // ================= Zoom =================

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Photo.Width = e.NewSize.Width;  // zoom factor 1 = whole photo fitted
        Photo.Height = e.NewSize.Height;
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateZoomText();
        // Tell the other photo windows, for "Link zoom and pan" (centre as a fraction of the photo).
        if (_photoWindow && !_applyingLinkedView && Scroller.ExtentWidth > 0 && Scroller.ExtentHeight > 0)
            _main.PhotoWindowViewChanged(this, Scroller.ZoomFactor,
                (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) / Scroller.ExtentWidth,
                (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / Scroller.ExtentHeight);
    }

    /// <summary>Shows the same part of the photo at the same zoom as another photo window.</summary>
    public void ApplyLinkedView(float zoom, double centreX, double centreY)
    {
        if (Scroller.Visibility != Visibility.Visible || double.IsNaN(Photo.Width) || Photo.Width <= 0) return;
        _applyingLinkedView = true;
        var x = centreX * Photo.Width * zoom - Scroller.ViewportWidth / 2;
        var y = centreY * Photo.Height * zoom - Scroller.ViewportHeight / 2;
        Scroller.ChangeView(Math.Max(0, x), Math.Max(0, y), zoom, disableAnimation: true);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _applyingLinkedView = false);
    }

    private void UpdateZoomText() =>
        ZoomText.Text = Scroller.ZoomFactor <= 1.01 ? "Fit" : $"{ActualPercent():0}%";

    /// <summary>Zoom as a percentage of the photo's real pixels.</summary>
    private double ActualPercent()
    {
        if (_bitmap == null || Photo.ActualWidth < 1) return 100;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var fitted = Math.Min(Photo.ActualWidth / _bitmap.PixelWidth, Photo.ActualHeight / _bitmap.PixelHeight) * scale;
        return fitted * Scroller.ZoomFactor * 100;
    }

    private void ZoomTo(float factor, Windows.Foundation.Point? centre = null)
    {
        factor = Math.Clamp(factor, 1f, 8f);
        var c = centre ?? new Windows.Foundation.Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        var x = (Scroller.HorizontalOffset + c.X) / Scroller.ZoomFactor * factor - c.X;
        var y = (Scroller.VerticalOffset + c.Y) / Scroller.ZoomFactor * factor - c.Y;
        Scroller.ChangeView(Math.Max(0, x), Math.Max(0, y), factor);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomTo(ZoomSteps.FirstOrDefault(z => z > Scroller.ZoomFactor + 0.01f, 8f));

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomTo(ZoomSteps.LastOrDefault(z => z < Scroller.ZoomFactor - 0.01f, 1f));

    private void Fit_Click(object sender, RoutedEventArgs e) => ZoomTo(1f);

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        if (_bitmap == null) return;
        var percentAtFit = ActualPercent() / Scroller.ZoomFactor;
        ZoomTo((float)(100 / percentAtFit));
    }

    private void Scroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Scroller.ZoomFactor > 1.01f) ZoomTo(1f);
        else ZoomTo(2.5f, e.GetPosition(Scroller));
    }

    // ================= Navigation =================

    private async void Next_Click(object sender, RoutedEventArgs e) => await ShowAsync(_index + 1);
    private async void Previous_Click(object sender, RoutedEventArgs e) => await ShowAsync(_index - 1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var item = _items.ElementAtOrDefault(_index);
        if (item == null || item.IsVideo) return;
        Close();
        _main.OpenEditor(item);
    }

    private async void Strip_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ImageItem item) await ShowAsync(_items.IndexOf(item));
    }

    private async void Strip_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ImageItem { Thumbnail: null } item) await LoadThumbAsync(item);
    }

    private async Task LoadThumbAsync(ImageItem item)
    {
        var bytes = await _main.Index.GetThumbAsync(item.Path)
                    ?? await ThumbnailService.GetBytesAsync(item.Path, 384, allowDecode: !item.IsCloudOnly);
        if (bytes != null && item.Thumbnail == null) item.Thumbnail = await ThumbnailService.ToBitmapAsync(bytes);
    }

    private void ShowOverlay()
    {
        Overlay.Opacity = 1;
        _hide.Stop();
        _hide.Start();
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowOverlay();

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Alt+number belongs to the spot shortcuts (keyboard accelerators), not to zoom.
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)) return;

        switch (e.Key)
        {
            case VirtualKey.Escape: Close(); break;
            case VirtualKey.Right: case VirtualKey.PageDown: await ShowAsync(_index + 1); break;
            case VirtualKey.Left: case VirtualKey.PageUp: await ShowAsync(_index - 1); break;
            case VirtualKey.Home: await ShowAsync(0); break;
            case VirtualKey.End: await ShowAsync(_items.Count - 1); break;
            case VirtualKey.Add: case (VirtualKey)187: ZoomIn_Click(sender, e); break;      // + and =
            case VirtualKey.Subtract: case (VirtualKey)189: ZoomOut_Click(sender, e); break; // -
            case VirtualKey.Number0: case VirtualKey.NumberPad0: ZoomTo(1f); break;
            case VirtualKey.Number1: case VirtualKey.NumberPad1: Actual_Click(sender, e); break;
            case VirtualKey.E: Edit_Click(sender, e); break;
            case VirtualKey.Space when Video.Visibility == Visibility.Visible: Video.TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
        ShowOverlay();
    }

    // ================= Photo window (double-click a thumbnail) =================

    /// <summary>The monitor the window is on, for placing it among the others there.</summary>
    public ulong DisplayId => DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).DisplayId.Value;

    private void SetUpPhotoWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        var bar = AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = Colors.White;
        bar.ButtonHoverForegroundColor = Colors.White;
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        TitleRow.Visibility = Visibility.Visible;

        // Moving or resizing it yourself takes it out of its spot.
        AppWindow.Changed += (_, e) =>
        {
            if (_placing > 0 || !(e.DidPositionChange || e.DidSizeChange)) return;
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
            _main.PhotoWindowMovedByUser(this);
        };

        // Alt+1 to Alt+6 put the window in that spot; Alt+0 centres it.
        for (var n = 0; n <= 9; n++)
        {
            var number = n;
            foreach (var key in new[] { (VirtualKey)((int)VirtualKey.Number0 + n), (VirtualKey)((int)VirtualKey.NumberPad0 + n) })
            {
                var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Menu };
                accelerator.Invoked += (_, args) =>
                {
                    args.Handled = true;
                    if (number == 0) _main.CentrePhotoWindow(this);
                    else _main.SnapPhotoWindow(this, number - 1);
                };
                Root.KeyboardAccelerators.Add(accelerator);
            }
        }
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Shows the layout chip and spot buttons for the current settings and this window's spot.</summary>
    public void UpdateSnapBar(SnapLayout layout, int spots, int? spot)
    {
        if (!_photoWindow) return;
        LayoutChip.Content = PhotoWindowLayout.Name(layout, spots);
        LayoutMenu.Items.Clear();
        foreach (var option in PhotoWindowLayout.Layouts)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = PhotoWindowLayout.Name(option, spots),
                GroupName = "Layout",
                IsChecked = option == layout,
            };
            item.Click += (_, _) => _main.SetPhotoWindowLayout(option);
            LayoutMenu.Items.Add(item);
        }

        SpotButtons.Children.Clear();
        for (var i = 0; i < spots; i++)
        {
            var index = i;
            var button = new ToggleButton
            {
                Content = (i + 1).ToString(),
                IsChecked = spot == i,
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                FontSize = 12,
            };
            ToolTipService.SetToolTip(button, $"Spot {i + 1} (Alt+{i + 1})");
            AutomationProperties.SetName(button, $"Spot {i + 1}");
            button.Click += (_, _) => _main.SnapPhotoWindow(this, index);
            SpotButtons.Children.Add(button);
        }
        SpotText.Text = spot is int s ? $"Spot {s + 1} of {spots}" : string.Empty;

        // The compact menu: spots, then layouts, then moving the window.
        CompactChip.Content = spot is int c ? $"Spot {c + 1} of {spots}" : "Spots";
        CompactMenu.Items.Clear();
        for (var i = 0; i < spots; i++)
        {
            var index = i;
            var item = new RadioMenuFlyoutItem { Text = $"Spot {i + 1}", GroupName = "Spot", IsChecked = spot == i };
            item.KeyboardAcceleratorTextOverride = $"Alt+{i + 1}";
            item.Click += (_, _) => _main.SnapPhotoWindow(this, index);
            CompactMenu.Items.Add(item);
        }
        CompactMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var option in PhotoWindowLayout.Layouts)
        {
            var item = new RadioMenuFlyoutItem { Text = PhotoWindowLayout.Name(option, spots), GroupName = "Layout", IsChecked = option == layout };
            item.Click += (_, _) => _main.SetPhotoWindowLayout(option);
            CompactMenu.Items.Add(item);
        }
        CompactMenu.Items.Add(new MenuFlyoutSeparator());
        var centre = new MenuFlyoutItem { Text = "Centre", KeyboardAcceleratorTextOverride = "Alt+0" };
        centre.Click += (_, _) => _main.CentrePhotoWindow(this);
        CompactMenu.Items.Add(centre);
        var next = new MenuFlyoutItem { Text = "Send to the next display" };
        next.Click += (_, _) => _main.SendPhotoWindowToNextDisplay(this);
        CompactMenu.Items.Add(next);
        FitTitleRow(TitleRow.ActualWidth);
    }

    private void TitleRow_SizeChanged(object sender, SizeChangedEventArgs e) => FitTitleRow(e.NewSize.Width);

    /// <summary>
    /// The full title bar (chip, one button per spot, next display) needs about 280 px beside the
    /// window buttons; below that, one compact menu holds the same choices.
    /// </summary>
    private void FitTitleRow(double width)
    {
        if (!_photoWindow || width <= 0) return;
        var full = width >= 140 + 14 + 120 + SpotButtons.Children.Count * 32 + 48 + 80;
        LayoutChip.Visibility = SpotButtons.Visibility = NextDisplayButton.Visibility =
            full ? Visibility.Visible : Visibility.Collapsed;
        CompactChip.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Visibility = SpotText.Visibility = width >= 420 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Moves and resizes the window so its visible frame fills <paramref name="spot"/> (physical
    /// pixels). A second pass, once Windows has settled the move, corrects the size after a change
    /// of display scaling and hides the invisible resize borders.
    /// </summary>
    public void PlaceAt(SpotRect spot)
    {
        _placing++;
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored } presenter)
            presenter.Restore();
        AppWindow.MoveAndResize(ToRect(spot));
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var borders = WindowFrame.InvisibleBorders(WinRT.Interop.WindowNative.GetWindowHandle(this));
            AppWindow.MoveAndResize(ToRect(WindowFrame.Expand(spot, borders)));
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _placing--);
        });
    }

    private static RectInt32 ToRect(SpotRect r) => new(r.X, r.Y, r.Width, r.Height);

    private void NextDisplay_Click(object sender, RoutedEventArgs e) => _main.SendPhotoWindowToNextDisplay(this);
}
