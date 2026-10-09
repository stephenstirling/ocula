using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Ocula.Models;
using Ocula.Services;
using Windows.Media.Core;
using Windows.Storage;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Ocula;

/// <summary>
/// Full-screen viewer: photos zoom and pan (mouse wheel, pinch, double-click), videos play,
/// and the controls fade away while you look. Esc returns to the library on the same photo.
/// </summary>
public sealed partial class ViewerWindow : Window
{
    private static readonly float[] ZoomSteps = { 1f, 1.5f, 2f, 3f, 4f, 6f, 8f };

    private readonly MainWindow _main;
    private readonly List<ImageItem> _items;
    private readonly DispatcherQueueTimer _hide;
    private int _index;
    private int _version;
    private BitmapImage? _bitmap;

    public ViewerWindow(MainWindow main, List<ImageItem> items, int index)
    {
        InitializeComponent();
        _main = main;
        _items = items;

        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
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
            if (_items.ElementAtOrDefault(_index) is { } current) _main.SelectItem(current);
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

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateZoomText();

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
}
