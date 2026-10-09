using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ocula.Models;
using Ocula.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;

namespace Ocula;

/// <summary>
/// Full-screen photos on any monitor. Two modes:
/// - Slideshow: advances by itself, with cross-fades, shuffle and adjustable timing.
/// - Second screen: follows whatever is selected in the main window, for showing clients
///   photos on a TV or projector while you browse on your own screen.
/// </summary>
public sealed partial class SlideshowWindow : Window
{
    private readonly List<ImageItem> _items;
    private readonly bool _follow;
    private readonly DispatcherQueueTimer _advance;
    private readonly DispatcherQueueTimer _hideControls;
    private readonly Random _random = new();
    private int _index;
    private bool _playing;
    private bool _showingA = true;
    private int _loadVersion;

    public SlideshowWindow(IEnumerable<ImageItem> items, int startIndex, bool follow, DisplayArea? display)
    {
        InitializeComponent();
        _items = items.Where(i => !i.IsVideo && !i.IsCloudOnly).ToList();
        _follow = follow;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, _items.Count - 1));

        // Fade between photos.
        ImageA.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(700) };
        ImageB.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(700) };
        Controls.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) };

        // Put the window on the chosen monitor, then go full screen there.
        if (display != null) AppWindow.Move(new PointInt32(display.WorkArea.X + 40, display.WorkArea.Y + 40));
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        _advance = DispatcherQueue.CreateTimer();
        _advance.Tick += async (_, _) => await StepAsync(1);
        SetInterval();

        _hideControls = DispatcherQueue.CreateTimer();
        _hideControls.Interval = TimeSpan.FromSeconds(3);
        _hideControls.IsRepeating = false;
        _hideControls.Tick += (_, _) => Controls.Opacity = 0;

        if (_follow)
        {
            // Second-screen mode: no slideshow controls, just the photo.
            Controls.Visibility = Visibility.Collapsed;
        }
        else
        {
            _playing = true;
            _advance.Start();
        }

        Activated += (_, _) => Root.Focus(FocusState.Programmatic);
        Closed += (_, _) => { _advance.Stop(); _hideControls.Stop(); };
        _ = ShowAsync(_index);
        ShowControls();
    }

    /// <summary>Second-screen mode: show the photo selected in the main window.</summary>
    public async Task FollowAsync(ImageItem item)
    {
        if (item.IsVideo || item.IsCloudOnly) return;
        var index = _items.IndexOf(item);
        if (index < 0) { _items.Add(item); index = _items.Count - 1; }
        await ShowAsync(index);
    }

    // ================= Showing photos =================

    private async Task ShowAsync(int index)
    {
        if (_items.Count == 0) return;
        _index = (index % _items.Count + _items.Count) % _items.Count;
        var item = _items[_index];
        var version = ++_loadVersion;

        // Decode at screen size: sharp on a 4K display without loading 50-megapixel originals.
        var size = (uint)Math.Clamp(Math.Max(AppWindow.Size.Width, AppWindow.Size.Height), 1280, 3840); // window size is in pixels
        var bytes = await ThumbnailService.GetBytesAsync(item.Path, size, allowDecode: true, highPriority: true, compress: false);
        if (bytes == null || version != _loadVersion) return;
        var bitmap = await ThumbnailService.ToBitmapAsync(bytes);
        if (version != _loadVersion) return;

        var (incoming, outgoing) = _showingA ? (ImageB, ImageA) : (ImageA, ImageB);
        incoming.Source = bitmap;
        incoming.Opacity = 1;
        outgoing.Opacity = 0;
        _showingA = !_showingA;

        CaptionText.Text = item.Taken is { } taken ? $"{item.Name} · {taken:d MMMM yyyy}" : item.Name;
        PositionText.Text = $"{_index + 1} / {_items.Count}";
    }

    private async Task StepAsync(int direction)
    {
        if (_items.Count == 0) return;
        var next = ShuffleToggle.IsChecked == true && _items.Count > 1 && direction > 0
            ? (_index + 1 + _random.Next(_items.Count - 1)) % _items.Count
            : _index + direction;
        await ShowAsync(next);
    }

    // ================= Controls =================

    private void SetInterval()
    {
        _advance.Interval = TimeSpan.FromSeconds(IntervalBox.SelectedIndex switch { 0 => 3, 2 => 8, 3 => 15, _ => 5 });
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_advance == null) return;
        SetInterval();
        if (_playing) { _advance.Stop(); _advance.Start(); }
    }

    private void TogglePlay()
    {
        _playing = !_playing;
        if (_playing) _advance.Start(); else _advance.Stop();
        PlayIcon.Glyph = _playing ? "\uE769" : "\uE768"; // pause / play
        ShowControls();
    }

    private void ShowControls()
    {
        if (_follow) return;
        Controls.Opacity = 1;
        _hideControls.Stop();
        _hideControls.Start();
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowControls();
    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private async void Next_Click(object sender, RoutedEventArgs e) { await StepAsync(1); RestartTimer(); }
    private async void Previous_Click(object sender, RoutedEventArgs e) { await StepAsync(-1); RestartTimer(); }
    private void Shuffle_Click(object sender, RoutedEventArgs e) => ShowControls();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Caption_Click(object sender, RoutedEventArgs e) =>
        CaptionText.Visibility = CaptionToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void RestartTimer()
    {
        if (!_playing) return;
        _advance.Stop();
        _advance.Start();
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape: Close(); break;
            case VirtualKey.Space when !_follow: TogglePlay(); break;
            case VirtualKey.Right when !_follow: await StepAsync(1); RestartTimer(); break;
            case VirtualKey.Left when !_follow: await StepAsync(-1); RestartTimer(); break;
            case VirtualKey.I:
                CaptionToggle.IsChecked = CaptionToggle.IsChecked != true;
                Caption_Click(sender, e);
                break;
            default: return;
        }
        e.Handled = true;
    }
}
