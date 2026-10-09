using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using LibVLCSharp.Shared;

namespace Ocula.Services;

/// <summary>
/// Chooses how a video is played. Windows' own player is used for the formats it handles well
/// (hardware-accelerated, nothing extra to load). Everything else, such as MKV, WebM, AVI, FLV
/// and MPEG, goes to VLC's engine, which also provides multiple audio tracks and subtitles.
/// If Windows' player fails on a file, playback falls back to VLC automatically.
/// </summary>
public static class VideoEngine
{
    private static readonly HashSet<string> WindowsFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv"
    };

    public static bool PrefersWindows(string path) => WindowsFormats.Contains(Path.GetExtension(path));

    private static readonly object InitLock = new();
    private static bool _initialised;
    private static bool _available;

    /// <summary>
    /// True when VLC's native libraries are present. They ship for x64 only, so on ARM64 PCs (or
    /// a build without the VideoLAN.LibVLC.Windows package) Ocula plays with Windows' player only.
    /// </summary>
    public static bool VlcAvailable
    {
        get
        {
            lock (InitLock)
            {
                if (_initialised) return _available;
                _initialised = true;
                try
                {
                    Core.Initialize();
                    _available = true;
                }
                catch
                {
                    _available = false;
                }
                return _available;
            }
        }
    }
}

public sealed record VideoInfo(TimeSpan Duration, int Width, int Height);

/// <summary>
/// Reads length and frame size from any video, and grabs a frame for its thumbnail, using a
/// background VLC instance with no window. One file is handled at a time, because each VLC
/// player is fairly heavy.
/// </summary>
public static class VideoProbe
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static LibVLC? _vlc;

    private static LibVLC Vlc() => _vlc ??= new LibVLC("--no-audio", "--quiet", "--no-video-title-show");

    public static async Task<VideoInfo?> ProbeAsync(string path)
    {
        if (!VideoEngine.VlcAvailable) return null;
        await Gate.WaitAsync();
        try
        {
            return await ProbeCoreAsync(path);
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<VideoInfo?> ProbeCoreAsync(string path)
    {
        using var media = new Media(Vlc(), path, FromType.FromPath);
        if (await media.Parse(MediaParseOptions.ParseLocal, 10000) != MediaParsedStatus.Done) return null;

        var video = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        return new VideoInfo(TimeSpan.FromMilliseconds(Math.Max(0, media.Duration)),
                             (int)video.Data.Video.Width, (int)video.Data.Video.Height);
    }

    /// <summary>A JPEG still from about a tenth of the way into the video, sized to fit inside size × size.</summary>
    public static async Task<byte[]?> ThumbnailAsync(string path, uint size)
    {
        if (!VideoEngine.VlcAvailable) return null;
        await Gate.WaitAsync();
        try
        {
            var info = await ProbeCoreAsync(path);
            if (info == null || info.Width <= 0 || info.Height <= 0) return null;

            var scale = Math.Min(1.0, size / (double)Math.Max(info.Width, info.Height));
            var w = Math.Max(2, (int)(info.Width * scale) & ~1);
            var h = Math.Max(2, (int)(info.Height * scale) & ~1);
            var start = info.Duration.TotalSeconds > 3 ? Math.Min(info.Duration.TotalSeconds * 0.1, 60) : 0;

            var buffer = new byte[w * h * 4];
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var gotFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var frames = 0;

                using var media = new Media(Vlc(), path, FromType.FromPath, ":no-audio",
                                            ":start-time=" + start.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                using var player = new MediaPlayer(media);
                player.SetVideoFormat("RV32", (uint)w, (uint)h, (uint)(w * 4)); // B, G, R, unused
                player.SetVideoCallbacks(
                    (opaque, planes) => { Marshal.WriteIntPtr(planes, pin.AddrOfPinnedObject()); return IntPtr.Zero; },
                    (opaque, picture, planes) => { },
                    (opaque, picture) => { if (++frames == 3) gotFrame.TrySetResult(true); }); // skip the first frames, which can be blank

                player.Play();
                var done = await Task.WhenAny(gotFrame.Task, Task.Delay(8000));
                await Task.Run(() => player.Stop()); // after this, no more writes into the buffer
                if (done != gotFrame.Task) return null;
            }
            finally
            {
                pin.Free();
            }

            using var image = new MagickImage();
            image.ReadPixels(buffer, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
            image.Alpha(AlphaOption.Off); // VLC leaves the fourth byte undefined
            image.Format = MagickFormat.Jpeg;
            image.Quality = 85;
            return image.ToByteArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
