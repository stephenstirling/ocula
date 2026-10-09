using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.UI.Xaml.Media.Imaging;
using Ocula.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Ocula.Services;

/// <summary>
/// Produces thumbnail and preview images as bytes (so they can be cached in the library).
/// 1. Ask Windows. Fast, and covers CR2 and most RAW formats once Microsoft's Raw Image
///    Extension is installed. For OneDrive online-only files, Windows asks OneDrive for a
///    cloud thumbnail instead of downloading the photo.
/// 2. If Windows only has a generic icon, decode the file with Magick.NET (LibRaw inside).
///    This is skipped for online-only files, because reading them would download them.
/// </summary>
public static class ThumbnailService
{
    private static readonly SemaphoreSlim Gate = new(4);

    public static async Task<byte[]?> GetBytesAsync(string path, uint size, bool allowDecode = true,
                                                    bool highPriority = false, bool compress = true)
    {
        if (!highPriority) await Gate.WaitAsync();
        try
        {
            var fromWindows = await TryWindowsThumbnailAsync(path, size);
            if (fromWindows != null)
                return compress ? await Task.Run(() => ToJpeg(fromWindows) ?? fromWindows) : fromWindows;

            if (!allowDecode) return null;
            // Videos Windows can't thumbnail (MKV, WebM, AVI…): grab a frame with VLC's engine.
            if (Models.SupportedFormats.IsVideo(path)) return await VideoProbe.ThumbnailAsync(path, size);
            return await Task.Run(() => DecodeWithMagick(path, size));
        }
        finally
        {
            if (!highPriority) Gate.Release();
        }
    }

    /// <summary>Must be called on the UI thread.</summary>
    public static async Task<BitmapImage> ToBitmapAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static async Task<byte[]?> TryWindowsThumbnailAsync(string path, uint size)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            // Type == Icon means Windows has no codec for this file and returned a file-type icon.
            if (thumb == null || thumb.Type == ThumbnailType.Icon || thumb.Size == 0) return null;

            var bytes = new byte[thumb.Size];
            using var reader = new DataReader(thumb);
            await reader.LoadAsync((uint)thumb.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    // Windows often hands back an uncompressed bitmap; re-encode so the library stays small.
    private static byte[]? ToJpeg(byte[] bytes)
    {
        try
        {
            using var image = new MagickImage(bytes);
            image.Format = MagickFormat.Jpeg;
            image.Quality = 82;
            return image.ToByteArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DecodeWithMagick(string path, uint size)
    {
        try
        {
            using var image = new MagickImage(SupportedFormats.MagickPath(path));
            image.AutoOrient();
            if (image.ColorSpace == ColorSpace.CMYK) image.TransformColorSpace(ColorProfiles.SRGB); // print-oriented PSDs
            if (image.HasAlpha) image.ColorAlpha(MagickColors.White);                              // JPEG has no transparency
            image.Thumbnail(size, size);
            image.Format = MagickFormat.Jpeg;
            image.Quality = 82;
            return image.ToByteArray();
        }
        catch
        {
            return null;
        }
    }
}
