using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Ocula.Models;

/// <summary>
/// One entry in the thumbnail grid. A RAW+JPEG pair (IMG_0001.CR2 + IMG_0001.JPG) is a single
/// item: Path is the JPEG (fast to preview), RawPath is the RAW file.
/// </summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private int _rating;
    private string? _label;
    private string? _hdr;
    private int? _sharpness;
    private int _flag;
    private int _stackCount;
    private readonly string _baseBadge;
    private TimeSpan? _duration;

    public ImageItem(string path, string? rawPath, bool isCloudOnly = false)
    {
        Path = path;
        RawPath = rawPath;
        IsCloudOnly = isCloudOnly;
        Folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        Name = rawPath != null ? System.IO.Path.GetFileNameWithoutExtension(path) : System.IO.Path.GetFileName(path);
        _baseBadge = rawPath != null ? "RAW+" + Ext(path)
              : isCloudOnly ? "ONLINE"
              : SupportedFormats.IsRaw(path) ? "RAW"
              : string.Empty;
    }

    public string Path { get; }
    public string? RawPath { get; }
    public string Folder { get; }
    public string Name { get; }
    public bool IsCloudOnly { get; }
    public bool IsVideo => SupportedFormats.IsVideo(Path);

    /// <summary>What screen readers announce for a tile (list items fall back to ToString()).</summary>
    public override string ToString() => Name;

    /// <summary>Length of a video clip (null for photos).</summary>
    public TimeSpan? Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(DurationVisibility));
        }
    }

    public string DurationText => _duration is { } d ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss")) : "▶";
    public Visibility DurationVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Short labels shown on the tile, e.g. "RAW+JPG", "HDR" or "RAW+JPG · HDR".</summary>
    public string Badge => _hdr == null ? _baseBadge : _baseBadge.Length == 0 ? "HDR" : _baseBadge + " · HDR";
    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The kind of HDR detected (see HdrDetector), or null for a standard image.</summary>
    public string? Hdr
    {
        get => _hdr;
        set
        {
            _hdr = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Badge));
            OnPropertyChanged(nameof(BadgeVisibility));
        }
    }

    public IEnumerable<string> AllPaths
    {
        get
        {
            yield return Path;
            if (RawPath != null) yield return RawPath;
        }
    }

    // Library (index) state
    public long Modified { get; set; }
    public bool Indexed { get; set; }
    public byte[]? CachedThumb { get; set; }
    public bool ThumbnailRequested { get; set; }

    // Metadata
    public string? Camera { get; set; }
    public string? Exposure { get; set; }
    public DateTime? Taken { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public List<string> Tags { get; set; } = new();

    /// <summary>For a Capta capture: mode, source app and window title (CaptaCapture.Summary), else null.</summary>
    public string? Capture { get; set; }

    /// <summary>For a Capta capture: the text Capta recognised in it, if any.</summary>
    public string? CaptureText { get; set; }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public int Rating
    {
        get => _rating;
        set
        {
            _rating = Math.Clamp(value, 0, 5);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StarsText));
        }
    }

    public string StarsText => _rating > 0 ? new string('★', _rating) : string.Empty;

    public string? Label
    {
        get => _label;
        set
        {
            _label = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LabelBrush));
            OnPropertyChanged(nameof(LabelVisibility));
        }
    }

    public Brush LabelBrush => new SolidColorBrush(Labels.ColorOf(_label));

    // ---------- Culling ----------

    /// <summary>Sharpness score 0–100 (see SharpnessService), or null if not measured yet.</summary>
    public int? Sharpness
    {
        get => _sharpness;
        set
        {
            _sharpness = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSoft));
            OnPropertyChanged(nameof(SoftVisibility));
        }
    }

    // -1 means "not applicable" (videos), so it never counts as soft.
    public bool IsSoft => _sharpness is int s && s >= 0 && s < Services.SharpnessService.SoftThreshold;
    public Visibility SoftVisibility => IsSoft ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>1 = picked, -1 = rejected, 0 = no flag. Stored in Ocula's library.</summary>
    public int Flag
    {
        get => _flag;
        set
        {
            _flag = Math.Clamp(value, -1, 1);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FlagText));
            OnPropertyChanged(nameof(FlagVisibility));
            OnPropertyChanged(nameof(TileOpacity));
        }
    }

    public string FlagText => _flag > 0 ? "✓" : _flag < 0 ? "✕" : string.Empty;
    public Visibility FlagVisibility => _flag != 0 ? Visibility.Visible : Visibility.Collapsed;
    public double TileOpacity => _flag < 0 ? 0.45 : 1.0;

    /// <summary>On the photo representing a collapsed stack: how many photos it holds.</summary>
    public int StackCount
    {
        get => _stackCount;
        set
        {
            _stackCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StackText));
            OnPropertyChanged(nameof(StackVisibility));
        }
    }

    public string StackText => _stackCount > 1 ? $"×{_stackCount}" : string.Empty;
    public Visibility StackVisibility => _stackCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LabelVisibility => _label != null ? Visibility.Visible : Visibility.Collapsed;

    public IndexRow ToRow() => new(Path, Folder, Modified, Camera, Exposure, Taken, Rating, Label,
                                   string.Join('\n', Tags), Hdr, CachedThumb, RawPath, Latitude, Longitude,
                                   Sharpness, Flag, Duration?.Ticks, Capture,
                                   Ocula.Services.AppSettings.CaptureTextSearch ? CaptureText : null); // never saved while the text switch is off

    public void ApplyIndex(IndexRow row)
    {
        Camera = row.Camera;
        Exposure = row.Exposure;
        Taken = row.Taken;
        Rating = row.Rating;
        Label = row.Label;
        Tags = row.Tags.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Hdr = row.Hdr;
        Latitude = row.Latitude;
        Longitude = row.Longitude;
        Sharpness = row.Sharpness;
        Flag = row.Flag;
        Duration = row.DurationTicks is long ticks ? TimeSpan.FromTicks(ticks) : null;
        Capture = row.Capture;
        CaptureText = row.CaptureText;
        CachedThumb = row.Thumb;
        Indexed = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string Ext(string path) => System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
}

/// <summary>One row of the local library database.</summary>
public sealed record IndexRow(
    string Path, string Folder, long Modified, string? Camera, string? Exposure, DateTime? Taken,
    int Rating, string? Label, string Tags, string? Hdr, byte[]? Thumb,
    string? RawPath = null, double? Latitude = null, double? Longitude = null,
    int? Sharpness = null, int Flag = 0, long? DurationTicks = null,
    string? Capture = null, string? CaptureText = null);

/// <summary>Colour labels, using the same names Lightroom and Bridge write to XMP.</summary>
public static class Labels
{
    public static readonly string[] Names = { "Red", "Yellow", "Green", "Blue", "Purple" };

    /// <summary>Lightroom's shortcuts: 6 = red, 7 = yellow, 8 = green, 9 = blue.</summary>
    public static string? ForShortcut(int digit) => digit switch
    {
        6 => "Red", 7 => "Yellow", 8 => "Green", 9 => "Blue", _ => null
    };

    public static Color ColorOf(string? name) => name switch
    {
        "Red" => ColorHelper.FromArgb(255, 0xD6, 0x45, 0x45),
        "Yellow" => ColorHelper.FromArgb(255, 0xE8, 0xB5, 0x3D),
        "Green" => ColorHelper.FromArgb(255, 0x2F, 0x8F, 0x5B),
        "Blue" => ColorHelper.FromArgb(255, 0x3B, 0x6F, 0xE0),
        "Purple" => ColorHelper.FromArgb(255, 0x8B, 0x5C, 0xF6),
        _ => Colors.Transparent
    };
}

/// <summary>A folder in the left-hand tree. ToString() is what the TreeView displays.</summary>
public sealed class FolderNode
{
    public FolderNode(string fullPath) => FullPath = fullPath;

    public string FullPath { get; }

    public override string ToString()
    {
        var name = System.IO.Path.GetFileName(FullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? FullPath : name; // drive roots like "D:\"
    }
}

/// <summary>Which files the app shows, and how they're grouped.</summary>
public static class SupportedFormats
{
    public static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng",
        ".orf", ".rw2", ".raf", ".pef", ".srw", ".x3f", ".3fr", ".iiq"
    };

    public static readonly HashSet<string> Standard = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp",
        ".heic", ".heif", ".avif", ".jxr", ".wdp", ".ico",
        ".psd", ".psb" // Photoshop: read-only, shown as the saved composite (see MagickPath)
    };

    /// <summary>Formats a camera writes alongside a RAW file in "RAW+JPEG" mode.</summary>
    private static readonly HashSet<string> Pairable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif"
    };

    /// <summary>Video formats Ocula lists. Windows' player or VLC's engine plays them (see VideoEngine).</summary>
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".wmv", ".mkv", ".webm", ".flv", ".3gp", ".mpg", ".mpeg", ".ogv", ".vob",
        ".mts", ".m2ts" // not .ts: it's more often a TypeScript file than a video
    };

    public static bool IsSupported(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return Standard.Contains(ext) || Raw.Contains(ext) || Video.Contains(ext);
    }

    public static bool IsVideo(string path) => Video.Contains(System.IO.Path.GetExtension(path));

    /// <summary>Photoshop documents: Ocula shows the flattened composite and never writes to them.</summary>
    public static bool IsPhotoshop(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".psd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".psb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What to hand to Magick.NET. "[0]" asks for the merged composite instead of every layer,
    /// which is much faster and uses far less memory on large files.
    /// </summary>
    public static string MagickPath(string path) => IsPhotoshop(path) ? path + "[0]" : path;

    public static bool IsRaw(string path) => Raw.Contains(System.IO.Path.GetExtension(path));
    public static bool IsPairable(string path) => Pairable.Contains(System.IO.Path.GetExtension(path));
}

/// <summary>Lists a folder's images, pairing RAW+JPEG files and spotting OneDrive online-only files.</summary>
public static class FolderScanner
{
    // Set on files that OneDrive (or another cloud provider) hasn't downloaded yet.
    // Reading their contents would trigger a download, so the app avoids it.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public static List<ImageItem> Scan(string folder)
    {
        var files = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => SupportedFormats.IsSupported(f.FullName))
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .ToList();

        var items = new List<ImageItem>();
        foreach (var group in files.GroupBy(f => System.IO.Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase))
        {
            var raw = group.FirstOrDefault(f => SupportedFormats.IsRaw(f.FullName));
            var partner = raw == null ? null : group.FirstOrDefault(f => SupportedFormats.IsPairable(f.FullName));

            foreach (var file in group)
            {
                if (file == raw && partner != null) continue; // represented by the pair
                if (file == partner)
                    items.Add(new ImageItem(partner.FullName, raw!.FullName, IsCloudOnly(partner) || IsCloudOnly(raw)));
                else
                    items.Add(new ImageItem(file.FullName, null, IsCloudOnly(file)));
            }
        }

        foreach (var item in items) item.Modified = ModifiedOf(item);
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static long ModifiedOf(ImageItem item) =>
        item.AllPaths.Max(p => File.GetLastWriteTimeUtc(p).Ticks);

    private static bool IsCloudOnly(FileInfo file) =>
        (file.Attributes & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0;
}
