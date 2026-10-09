using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Ocula.Services;
using Windows.Media.Core;
using Windows.Storage;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;
using WinPlayer = Windows.Media.Playback.MediaPlayer;

namespace Ocula;

/// <summary>
/// Plays one video at a time, with whichever engine suits the file: Windows' own player for
/// MP4, MOV, M4V and WMV, and VLC's engine for MKV, WebM, AVI and the rest (with audio-track,
/// subtitle and speed menus). Subtitle files with the same name as the video (movie.srt next to
/// movie.mkv) are picked up automatically.
/// </summary>
public sealed partial class MediaPlayerView : UserControl
{
    private WinPlayer? _native;
    private LibVLC? _vlc;
    private VlcPlayer? _player;
    private TaskCompletionSource<bool>? _vlcReady;
    private int _version;
    private bool _updating;
    private string? _path;

    public MediaPlayerView()
    {
        InitializeComponent();
        VolumeSlider.Value = AppSettings.VideoVolume;
        foreach (var speed in new[] { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f })
        {
            var item = new MenuFlyoutItem { Text = $"{speed:0.##}×" };
            item.Click += (_, _) => { _player?.SetRate(speed); SpeedButton.Content = $"{speed:0.##}×"; };
            SpeedMenu.Items.Add(item);
        }
    }

    // ================= Opening and closing =================

    /// <summary>Opens a video. With autoPlay off, the poster is shown until play is pressed.</summary>
    public async Task OpenAsync(string path, BitmapImage? poster, bool autoPlay)
    {
        var version = ++_version;
        _path = path;
        await StopCoreAsync();
        Message.Visibility = Visibility.Collapsed;

        if (VideoEngine.PrefersWindows(path) || !VideoEngine.VlcAvailable)
        {
            await OpenNativeAsync(path, autoPlay, version);
        }
        else
        {
            await OpenVlcAsync(path, poster, autoPlay, version);
        }
    }

    /// <summary>Stops playback and releases the file (the view stays ready for the next video).</summary>
    public void Stop()
    {
        _version++;
        _ = StopCoreAsync();
    }

    /// <summary>Releases everything. Call when the window closes.</summary>
    public void Unload()
    {
        _version++;
        var player = _player;
        var vlc = _vlc;
        _player = null;
        _vlc = null;
        NativePlayer.Source = null;
        _native?.Dispose();
        _native = null;
        if (player != null || vlc != null)
        {
            // Disposing blocks until playback stops, so keep it off the UI thread.
            Task.Run(() => { player?.Stop(); player?.Dispose(); vlc?.Dispose(); });
        }
    }

    private async Task StopCoreAsync()
    {
        if (_native != null) { _native.Pause(); NativePlayer.Source = null; }
        NativePlayer.Visibility = Visibility.Collapsed;
        VlcHost.Visibility = Visibility.Collapsed;

        var player = _player;
        if (player != null && player.IsPlaying) await Task.Run(() => player.Stop());
        else if (player != null) await Task.Run(() => player.Media = null);
        PlayIcon.Glyph = "\uE768";
    }

    // ================= Windows' engine =================

    private async Task OpenNativeAsync(string path, bool autoPlay, int version)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (version != _version) return;

            if (_native == null)
            {
                _native = new WinPlayer();
                _native.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(async () =>
                {
                    // Windows can't play this one (missing codec, odd container): try VLC.
                    if (_path != null && VideoEngine.VlcAvailable && version == _version)
                        await OpenVlcAsync(_path, null, autoPlay, version);
                    else ShowMessage("Windows couldn't play this video. It may need a codec from the Microsoft Store (for example HEVC Video Extensions).");
                });
                NativePlayer.SetMediaPlayer(_native);
            }
            _native.AutoPlay = autoPlay;
            _native.Source = MediaSource.CreateFromStorageFile(file);
            NativePlayer.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't open this video: " + ex.Message);
        }
    }

    // ================= VLC's engine =================

    private async Task OpenVlcAsync(string path, BitmapImage? poster, bool autoPlay, int version)
    {
        try
        {
            NativePlayer.Visibility = Visibility.Collapsed;
            _native?.Pause();
            Poster.Source = poster;
            Poster.Visibility = poster != null && !autoPlay ? Visibility.Visible : Visibility.Collapsed;
            VlcHost.Visibility = Visibility.Visible;

            await EnsureVlcAsync();
            if (version != _version || _player == null || _vlc == null) return;

            // Same-name subtitle files (.srt, .ass…) load automatically; the first audio track plays.
            using var media = new Media(_vlc, path, FromType.FromPath);
            _player.Media = media;
            _updating = true;
            SeekSlider.Value = 0;
            TimeText.Text = "0:00";
            LengthText.Text = "0:00";
            _updating = false;
            SpeedButton.Content = "1×";
            _player.SetRate(1f);

            if (autoPlay) _player.Play();
            PlayIcon.Glyph = autoPlay ? "\uE769" : "\uE768";
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't play this video: " + ex.Message);
        }
    }

    /// <summary>The VLC view has to be on screen before it can start; wait for that once, then reuse it.</summary>
    private async Task EnsureVlcAsync()
    {
        if (_player != null) return;
        _vlcReady ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.WhenAny(_vlcReady.Task, Task.Delay(5000));
    }

    private void VlcView_Initialized(object? sender, InitializedEventArgs e)
    {
        _vlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
        _player = new VlcPlayer(_vlc) { Volume = (int)VolumeSlider.Value };
        VlcView.MediaPlayer = _player;

        _player.TimeChanged += (_, args) => DispatcherQueue.TryEnqueue(() => ShowTime(args.Time));
        _player.LengthChanged += (_, args) => DispatcherQueue.TryEnqueue(() => LengthText.Text = Format(args.Length));
        _player.Playing += (_, _) => DispatcherQueue.TryEnqueue(() => { Poster.Visibility = Visibility.Collapsed; PlayIcon.Glyph = "\uE769"; });
        _player.Paused += (_, _) => DispatcherQueue.TryEnqueue(() => PlayIcon.Glyph = "\uE768");
        _player.Stopped += (_, _) => DispatcherQueue.TryEnqueue(() => PlayIcon.Glyph = "\uE768");
        _player.EndReached += (_, _) => DispatcherQueue.TryEnqueue(() => PlayIcon.Glyph = "\uE768");
        _player.EncounteredError += (_, _) => DispatcherQueue.TryEnqueue(() => ShowMessage("VLC couldn't play this video."));
        _vlcReady?.TrySetResult(true);
    }

    // ================= Transport controls =================

    private void ShowTime(long milliseconds)
    {
        TimeText.Text = Format(milliseconds);
        if (_player == null || _player.Length <= 0) return;
        _updating = true;
        SeekSlider.Value = Math.Clamp(milliseconds / (double)_player.Length * 1000, 0, 1000);
        _updating = false;
    }

    private static string Format(long milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void VlcHost_Tapped(object sender, TappedRoutedEventArgs e) => TogglePlay();

    private void Bar_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true; // clicks on the bar aren't clicks on the video

    public void TogglePlay()
    {
        if (_player == null) return;
        if (_player.IsPlaying) _player.Pause();
        else
        {
            Poster.Visibility = Visibility.Collapsed;
            if (_player.State == VLCState.Ended || _player.State == VLCState.Stopped) _player.Stop();
            _player.Play();
        }
    }

    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _player == null || _player.Length <= 0) return;
        _player.Position = (float)(e.NewValue / 1000);
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        AppSettings.VideoVolume = (int)e.NewValue;
        if (_player != null)
        {
            _player.Volume = (int)e.NewValue;
            _player.Mute = false;
        }
        MuteIcon.Glyph = e.NewValue == 0 ? "\uE74F" : "\uE767";
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;
        _player.Mute = !_player.Mute;
        MuteIcon.Glyph = _player.Mute ? "\uE74F" : "\uE767";
    }

    // ================= Audio and subtitle menus =================

    private void AudioMenu_Opening(object sender, object e) =>
        FillTrackMenu(AudioMenu, _player?.AudioTrackDescription, _player?.AudioTrack ?? -1, id => _player?.SetAudioTrack(id), "No audio tracks");

    private void SubtitleMenu_Opening(object sender, object e) =>
        FillTrackMenu(SubtitleMenu, _player?.SpuDescription, _player?.Spu ?? -1, id => _player?.SetSpu(id),
                      "No subtitles found. A .srt file with the same name next to the video loads automatically.");

    private static void FillTrackMenu(MenuFlyout menu, TrackDescription[]? tracks, int current, Action<int> choose, string emptyText)
    {
        menu.Items.Clear();
        if (tracks == null || tracks.Length == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = emptyText, IsEnabled = false });
            return;
        }
        foreach (var track in tracks)
        {
            var id = track.Id;
            var item = new ToggleMenuFlyoutItem { Text = track.Name ?? $"Track {id}", IsChecked = id == current };
            item.Click += (_, _) => choose(id);
            menu.Items.Add(item);
        }
    }

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
        Poster.Visibility = Visibility.Collapsed;
        if (NativePlayer.Visibility == Visibility.Collapsed) VlcHost.Visibility = Visibility.Visible;
    }
}
