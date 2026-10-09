using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using ImageMagick.Drawing;
using Ocula.Models;

namespace Ocula.Services;

/// <summary>A saved set of export settings, e.g. "Web" or "Full-size JPEG".</summary>
public sealed class ExportPreset
{
    public string Name { get; set; } = "Custom";

    /// <summary>Longest side in pixels; 0 keeps the original size.</summary>
    public uint LongEdge { get; set; }

    /// <summary>JPEG, PNG, WebP, AVIF or TIFF.</summary>
    public string Format { get; set; } = "JPEG";

    public uint Quality { get; set; } = 90;
    public bool RemoveLocation { get; set; } = true;
    public bool RemoveAllMetadata { get; set; }
    public bool UseRaw { get; set; }
    public string Watermark { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;

    public ExportPreset Clone() => (ExportPreset)MemberwiseClone();

    public override string ToString() => Name;
}

/// <summary>Stores presets in %LOCALAPPDATA%\Ocula\export-presets.json.</summary>
public static class ExportPresetStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocula", "export-presets.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<ExportPreset> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var presets = JsonSerializer.Deserialize<List<ExportPreset>>(File.ReadAllText(FilePath));
                if (presets is { Count: > 0 }) return presets;
            }
        }
        catch
        {
            // Corrupt file: fall back to the defaults.
        }
        return Defaults();
    }

    public static void Save(IEnumerable<ExportPreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(presets.ToList(), Json));
    }

    private static List<ExportPreset> Defaults() => new()
    {
        new() { Name = "Web", LongEdge = 2048, Format = "JPEG", Quality = 82, RemoveLocation = true, Suffix = "_web" },
        new() { Name = "Social", LongEdge = 1350, Format = "JPEG", Quality = 88, RemoveLocation = true, Suffix = "_social" },
        new() { Name = "Full-size JPEG", LongEdge = 0, Format = "JPEG", Quality = 95, RemoveLocation = false },
        new() { Name = "Archive TIFF (from RAW)", LongEdge = 0, Format = "TIFF", Quality = 100, RemoveLocation = false, UseRaw = true },
    };
}

public sealed record ExportResult(int Succeeded, IReadOnlyList<string> Failed);

/// <summary>
/// Converts images with Magick.NET: resize, convert to sRGB, strip location or all metadata,
/// add a text watermark, and save in the chosen format. Two images are processed at a time,
/// which keeps the app responsive while still using a multi-core CPU well.
///
/// Notes: the output is always standard range, so HDR gain maps aren't carried over.
/// RAW files are converted with LibRaw's neutral defaults, which can look flatter than the
/// camera's own JPEG; edits made in other apps (Lightroom etc.) aren't applied.
/// </summary>
public static class ExportService
{
    private static readonly object NameLock = new();

    private static readonly ExifTag[] LocationTags =
    {
        ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef, ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef,
        ExifTag.GPSAltitude, ExifTag.GPSAltitudeRef, ExifTag.GPSDestLatitude, ExifTag.GPSDestLongitude,
        ExifTag.GPSImgDirection, ExifTag.GPSAreaInformation
    };

    public static string Extension(string format) => format switch
    {
        "PNG" => ".png",
        "WebP" => ".webp",
        "AVIF" => ".avif",
        "TIFF" => ".tif",
        _ => ".jpg"
    };

    public static async Task<ExportResult> ExportAsync(IReadOnlyList<ImageItem> items, ExportPreset preset,
                                                       IProgress<int> progress, CancellationToken token)
    {
        Directory.CreateDirectory(preset.Destination);

        var done = 0;
        var succeeded = 0;
        var failed = new ConcurrentBag<string>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token };

        await Parallel.ForEachAsync(items, options, (item, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ExportOne(item, preset);
                Interlocked.Increment(ref succeeded);
            }
            catch (Exception ex)
            {
                failed.Add($"{item.Name}: {ex.Message}");
            }
            progress.Report(Interlocked.Increment(ref done));
            return ValueTask.CompletedTask;
        });

        return new ExportResult(succeeded, failed.ToList());
    }

    /// <summary>Exports one photo and returns the file it was saved as.</summary>
    public static string ExportOne(ImageItem item, ExportPreset preset)
    {
        var source = preset.UseRaw && item.RawPath != null ? item.RawPath : item.Path;
        var target = ReserveTarget(preset.Destination,
                                   Path.GetFileNameWithoutExtension(source) + preset.Suffix,
                                   Extension(preset.Format));
        try
        {
            using var image = new MagickImage(SupportedFormats.MagickPath(source));
            image.AutoOrient();

            if (preset.LongEdge > 0 && Math.Max(image.Width, image.Height) > preset.LongEdge)
                image.Resize(preset.LongEdge, preset.LongEdge); // keeps the aspect ratio

            // Browsers and phones assume sRGB, so convert wide-gamut photos (e.g. Display P3)
            // rather than letting colours look dull. TIFF archives keep their original profile.
            if (preset.Format != "TIFF" && image.GetColorProfile() != null)
                image.TransformColorSpace(ColorProfiles.SRGB);

            if (preset.RemoveAllMetadata) image.Strip();
            else if (preset.RemoveLocation) RemoveLocation(image);

            if (!string.IsNullOrWhiteSpace(preset.Watermark)) DrawWatermark(image, preset.Watermark.Trim());

            image.Format = preset.Format switch
            {
                "PNG" => MagickFormat.Png,
                "WebP" => MagickFormat.WebP,
                "AVIF" => MagickFormat.Avif,
                "TIFF" => MagickFormat.Tiff,
                _ => MagickFormat.Jpeg
            };
            image.Quality = preset.Quality;
            image.Write(target);
            return target;
        }
        catch
        {
            TryDelete(target); // don't leave an empty placeholder behind
            throw;
        }
    }

    private static void RemoveLocation(MagickImage image)
    {
        var exif = image.GetExifProfile();
        if (exif != null)
        {
            foreach (var tag in LocationTags) exif.RemoveValue(tag);
            image.SetProfile(exif);
        }
        // XMP can also hold a copy of the location, so it's removed too. Keywords stay in
        // the IPTC block and the EXIF data keeps camera details.
        image.RemoveProfile("xmp");
    }

    private static void DrawWatermark(MagickImage image, string text)
    {
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        var size = Math.Max(14.0, Math.Min(image.Width, image.Height) * 0.03);
        var margin = size;

        // A soft shadow first, so white text stays readable on bright areas.
        new Drawables()
            .Font(font).FontPointSize(size).Gravity(Gravity.Southeast)
            .FillColor(new MagickColor(0, 0, 0, 110)).Text(margin - 2, margin - 2, text)
            .FillColor(new MagickColor(255, 255, 255, 200)).Text(margin, margin, text)
            .Draw(image);
    }

    /// <summary>Picks a free file name ("photo_web.jpg", "photo_web (2).jpg"…) and reserves it.</summary>
    private static string ReserveTarget(string folder, string stem, string extension)
    {
        lock (NameLock)
        {
            var candidate = Path.Combine(folder, stem + extension);
            for (var n = 2; File.Exists(candidate); n++)
                candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            using (File.Create(candidate)) { }
            return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
