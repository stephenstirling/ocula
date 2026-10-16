using System;
using System.Threading.Tasks;
using Ocula.Models;

namespace Ocula.Services;

/// <summary>
/// Reads everything Ocula stores about a photo (camera details, location, HDR, tags, rating,
/// label) and saves it to the library. Used for the open folder and by the background crawler.
/// </summary>
public static class Indexer
{
    /// <summary>Videos: length, recording date and location from Windows, tags and rating from an .xmp sidecar.</summary>
    private static async Task ReadVideoAsync(ImageItem item)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Path);
        try
        {
            var video = await file.Properties.GetVideoPropertiesAsync();
            item.Duration = video.Duration > TimeSpan.Zero ? video.Duration : null;
            item.Latitude = video.Latitude;
            item.Longitude = video.Longitude;
        }
        catch
        {
            // Windows can't read this container (e.g. some MKV files); VLC's engine is asked below.
        }
        if (item.Duration == null && await VideoProbe.ProbeAsync(item.Path) is { } probed && probed.Duration > TimeSpan.Zero)
            item.Duration = probed.Duration;

        var props = await file.Properties.RetrievePropertiesAsync(new[] { "System.Media.DateEncoded" });
        item.Taken = props.TryGetValue("System.Media.DateEncoded", out var encoded) && encoded is DateTimeOffset recorded
            ? recorded.LocalDateTime
            : (await file.GetBasicPropertiesAsync()).DateModified.LocalDateTime;

        var user = await MetadataStore.ReadAsync(item);
        item.Tags = user.Tags;
        item.Rating = user.Rating;
        item.Label = user.Label;
        item.Sharpness = -1; // not applicable
        item.CachedThumb ??= await ThumbnailService.GetBytesAsync(item.Path, 384);
    }

    public static async Task IndexAsync(ImageItem item, IndexService index, bool withThumbnail = false)
    {
        if (item.Indexed) return;
        try
        {
            if (item.IsVideo && !item.IsCloudOnly)
            {
                await ReadVideoAsync(item);
            }
            else if (!item.IsCloudOnly)
            {
                var info = await Task.Run(() => ExifService.Read(item.Path));
                var user = await MetadataStore.ReadAsync(item);
                var hdr = await Task.Run(() => HdrDetector.Detect(item.Path, info));
                var capture = await Task.Run(() => CaptaCapture.Read(item.Path));
                if (item.Indexed) return; // indexed another way meanwhile

                item.Camera = ExifService.DisplayName(info);
                item.Exposure = info.Exposure;
                item.Taken = info.Taken;
                item.Latitude = info.Latitude;
                item.Longitude = info.Longitude;
                item.Hdr = hdr;
                item.Tags = user.Tags;
                item.Rating = user.Rating;
                item.Label = user.Label;
                item.Capture = capture?.Summary;
                item.CaptureText = AppSettings.CaptureTextSearch ? capture?.Text : null;
                // Screenshots have no camera date; when Capta took it is the closest thing.
                if (item.Taken == null && capture?.CapturedAt is { } capturedAt) item.Taken = capturedAt.LocalDateTime;

                if (withThumbnail && item.CachedThumb == null)
                    item.CachedThumb = await ThumbnailService.GetBytesAsync(item.Path, 384);
                item.Sharpness = await SharpnessService.MeasureAsync(item.Path, item.CachedThumb);
            }
            item.Indexed = true;
            await index.SaveAsync(item.ToRow(), clearThumb: true);
            if (item.Thumbnail != null || withThumbnail) item.CachedThumb = null; // saved; free the bytes
        }
        catch
        {
            // A single unreadable file shouldn't stop the rest.
        }
    }
}
