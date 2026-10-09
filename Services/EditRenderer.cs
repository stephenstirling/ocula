using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.DirectX;
using Ocula.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Ocula.Services;

/// <summary>Everything about an edit. Adjustments run from -100 to 100; 0 means unchanged.</summary>
public sealed class EditState
{
    public int Rotate90;            // extra quarter turns clockwise
    public bool FlipH, FlipV;
    public float Straighten;        // degrees, -45..45
    public Rect Crop = new(0, 0, 1, 1); // fraction of the rotated, straightened frame
    public float Exposure, Contrast, Highlights, Shadows, Warmth, Saturation;

    public bool HasGeometry => Rotate90 % 4 != 0 || FlipH || FlipV || Straighten != 0 || Crop != new Rect(0, 0, 1, 1);
    public bool HasAdjustments => Exposure != 0 || Contrast != 0 || Highlights != 0 || Shadows != 0 || Warmth != 0 || Saturation != 0;
    public bool IsEmpty => !HasGeometry && !HasAdjustments;
}

/// <summary>
/// Applies edits with Win2D effects. The same effect graph draws the live preview (on a
/// small copy) and the saved file (on the full-resolution photo), so what you see is what
/// you save. Geometry: EXIF orientation and quarter turns, flips, then straightening, which
/// zooms in just enough to hide the corners, then the crop.
/// </summary>
public static class EditRenderer
{
    public static ICanvasImage Adjust(ICanvasImage source, EditState s)
    {
        var image = source;
        if (s.Exposure != 0) image = new ExposureEffect { Source = image, Exposure = s.Exposure / 50f }; // ±2 stops
        if (s.Contrast != 0) image = new ContrastEffect { Source = image, Contrast = s.Contrast / 100f };
        if (s.Highlights != 0 || s.Shadows != 0)
            image = new HighlightsAndShadowsEffect { Source = image, Highlights = s.Highlights / 100f, Shadows = s.Shadows / 100f };
        if (s.Warmth != 0) image = new TemperatureAndTintEffect { Source = image, Temperature = s.Warmth / 100f };
        if (s.Saturation != 0) image = new SaturationEffect { Source = image, Saturation = 1 + s.Saturation / 100f };
        return image;
    }

    public static int QuarterTurns(int exifOrientation, EditState s)
    {
        var fromExif = exifOrientation switch { 6 => 1, 3 => 2, 8 => 3, _ => 0 };
        return ((fromExif + s.Rotate90) % 4 + 4) % 4;
    }

    /// <summary>Size of the frame the crop is measured in: the photo after rotation.</summary>
    public static Size FrameSize(float width, float height, int turns) =>
        turns % 2 == 1 ? new Size(height, width) : new Size(width, height);

    /// <summary>Maps source pixels into the rotated, flipped and straightened frame.</summary>
    public static Matrix3x2 FrameTransform(float width, float height, int exifOrientation, EditState s)
    {
        var turns = QuarterTurns(exifOrientation, s);
        var frame = FrameSize(width, height, turns);
        var angle = s.Straighten * MathF.PI / 180f;

        var m = Matrix3x2.CreateTranslation(-width / 2, -height / 2)
              * Matrix3x2.CreateRotation(turns * MathF.PI / 2)
              * Matrix3x2.CreateScale(s.FlipH ? -1 : 1, s.FlipV ? -1 : 1)
              * Matrix3x2.CreateRotation(angle)
              * Matrix3x2.CreateScale(CoverScale((float)frame.Width, (float)frame.Height, angle))
              * Matrix3x2.CreateTranslation((float)frame.Width / 2, (float)frame.Height / 2);
        return m;
    }

    /// <summary>How much a rotated photo must grow to still fill its frame with no empty corners.</summary>
    private static float CoverScale(float w, float h, float angle)
    {
        var c = MathF.Abs(MathF.Cos(angle));
        var s = MathF.Abs(MathF.Sin(angle));
        return MathF.Max(c + s * h / w, c + s * w / h);
    }

    public static Transform2DEffect Frame(ICanvasImage adjusted, float width, float height, int orientation, EditState s) => new()
    {
        Source = adjusted,
        TransformMatrix = FrameTransform(width, height, orientation, s),
        InterpolationMode = CanvasImageInterpolation.HighQualityCubic
    };

    // ================= Saving =================

    public static bool CanOverwrite(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff";

    /// <summary>
    /// Renders the edit at full resolution and writes it with the original's metadata
    /// (camera details, tags, rating, colour profile). Orientation is reset, since the pixels
    /// are now the right way up.
    /// </summary>
    public static async Task SaveAsync(CanvasDevice device, ImageItem item, EditState s, string target)
    {
        var (bitmap, orientation) = await LoadFullAsync(device, item);
        using (bitmap)
        {
            var w = (float)bitmap.SizeInPixels.Width;
            var h = (float)bitmap.SizeInPixels.Height;
            var frame = FrameSize(w, h, QuarterTurns(orientation, s));
            var cropW = Math.Max(1, (int)Math.Round(s.Crop.Width * frame.Width));
            var cropH = Math.Max(1, (int)Math.Round(s.Crop.Height * frame.Height));

            using var framed = Frame(Adjust(bitmap, s), w, h, orientation, s);
            using var output = new CanvasRenderTarget(device, cropW, cropH, 96);
            using (var ds = output.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Black);
                ds.DrawImage(framed, (float)(-s.Crop.X * frame.Width), (float)(-s.Crop.Y * frame.Height));
            }
            var pixels = output.GetPixelBytes(); // BGRA, opaque

            await Task.Run(() => WriteWithMetadata(pixels, cropW, cropH, item.Path, target));
        }
    }

    private static void WriteWithMetadata(byte[] bgra, int width, int height, string source, string target)
    {
        using var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        image.Alpha(AlphaOption.Off);

        try
        {
            using var original = new MagickImage();
            original.Ping(source);
            if (original.GetColorProfile() is { } icc) image.SetProfile(icc);
            if (original.GetExifProfile() is { } exif)
            {
                exif.SetValue(ExifTag.Orientation, (ushort)1);
                exif.RemoveThumbnail(); // the old embedded thumbnail would show the unedited photo
                image.SetProfile(exif);
            }
            if (original.GetXmpProfile() is { } xmp) image.SetProfile(xmp);
            if (original.GetIptcProfile() is { } iptc) image.SetProfile(iptc);
        }
        catch
        {
            // Metadata is a bonus; never fail the save over it.
        }

        image.Format = Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".png" => MagickFormat.Png,
            ".tif" or ".tiff" => MagickFormat.Tiff,
            _ => MagickFormat.Jpeg
        };
        image.Quality = 93;
        image.Write(target);
    }

    /// <summary>
    /// Loads the full-resolution photo. Windows decoders handle most formats (RAW too, with the
    /// Raw Image Extension); otherwise the largest preview Ocula can produce is used.
    /// </summary>
    private static async Task<(CanvasBitmap Bitmap, int Orientation)> LoadFullAsync(CanvasDevice device, ImageItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenReadAsync();
            var orientation = await ReadOrientationAsync(stream);
            stream.Seek(0);
            return (await CanvasBitmap.LoadAsync(device, stream), orientation);
        }
        catch
        {
            var bytes = await ThumbnailService.GetBytesAsync(item.Path, 8192, allowDecode: true, highPriority: true, compress: false)
                        ?? throw new InvalidOperationException("This photo couldn't be opened for editing.");
            using var memory = new InMemoryRandomAccessStream();
            await memory.WriteAsync(bytes.AsBuffer());
            memory.Seek(0);
            return (await CanvasBitmap.LoadAsync(device, memory), 1); // previews are already upright
        }
    }

    private static async Task<int> ReadOrientationAsync(IRandomAccessStream stream)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var props = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.Orientation" });
            return props.TryGetValue("System.Photo.Orientation", out var v) && v.Value is ushort o ? o : 1;
        }
        catch
        {
            return 1;
        }
    }
}
