using System;
using System.Collections.Generic;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Ocula.Services;

public sealed record CameraInfo(string? Make, string? Model, string? Lens, string? Exposure, DateTime? Taken,
                                int? CustomRendered = null, bool FloatingPoint = false,
                                double? Latitude = null, double? Longitude = null);

/// <summary>
/// Reads camera details with the MetadataExtractor library, which understands JPEG, HEIC,
/// PNG, TIFF and RAW formats (CR2, CR3, NEF, ARW, DNG…) without needing any Windows codecs.
/// </summary>
public static class ExifService
{
    public static CameraInfo Read(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            var parts = new List<string>();
            void Add(int tag, string prefix = "")
            {
                var text = sub?.GetDescription(tag);
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(prefix + text);
            }
            Add(ExifDirectoryBase.TagFocalLength);
            Add(ExifDirectoryBase.TagFNumber);
            Add(ExifDirectoryBase.TagExposureTime);
            Add(ExifDirectoryBase.TagIsoEquivalent, "ISO ");

            DateTime? taken = null;
            if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                taken = dt;

            // CustomRendered: iPhones (and some others) write 2 or 3 for photos taken in HDR mode.
            int? customRendered = sub != null && sub.TryGetInt32(ExifDirectoryBase.TagCustomRendered, out var cr) ? cr : null;

            // SampleFormat 3 = floating-point pixels, as in 32-bit HDR merges saved as DNG or TIFF.
            const int TagSampleFormat = 0x0153;
            var floatingPoint = directories.OfType<ExifDirectoryBase>().Any(d =>
                d.GetObject(TagSampleFormat) switch
                {
                    Array values => values.Cast<object>().Any(IsFloat),
                    var single => IsFloat(single)
                });

            // Where the photo was taken, if the camera or phone recorded it.
            double? lat = null, lon = null;
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            if (gps != null && gps.TryGetGeoLocation(out var location) && !location.IsZero)
            {
                lat = location.Latitude;
                lon = location.Longitude;
            }

            return new CameraInfo(
                Make: ifd0?.GetDescription(ExifDirectoryBase.TagMake)?.Trim(),
                Model: ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim(),
                Lens: sub?.GetDescription(ExifDirectoryBase.TagLensModel)?.Trim(),
                Exposure: parts.Count > 0 ? string.Join(" · ", parts) : null,
                Taken: taken,
                CustomRendered: customRendered,
                FloatingPoint: floatingPoint,
                Latitude: lat,
                Longitude: lon);
        }
        catch
        {
            return new CameraInfo(null, null, null, null, null);
        }
    }

    private static bool IsFloat(object? value) =>
        value is IConvertible c && c.GetTypeCode() is not TypeCode.String and not TypeCode.Object && c.ToInt32(null) == 3;

    /// <summary>
    /// Builds a readable camera name. Cameras store make and model separately and
    /// inconsistently: Canon writes "Canon" + "Canon EOS R5", Nikon writes
    /// "NIKON CORPORATION" + "NIKON Z 6", Sony writes "SONY" + "ILCE-7M4".
    /// </summary>
    public static string? DisplayName(CameraInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Model)) return null;
        if (string.IsNullOrWhiteSpace(info.Make)) return info.Model;

        var brand = info.Make.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return info.Model.StartsWith(brand, StringComparison.OrdinalIgnoreCase)
            ? info.Model
            : $"{brand} {info.Model}";
    }
}
