using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ocula.Services;

/// <summary>One image found on a web page, shown in the picker before anything is downloaded.</summary>
public sealed class WebImage : INotifyPropertyChanged
{
    /// <summary>The picture as it appears on the page (often a small preview).</summary>
    public string ThumbUrl { get; init; } = string.Empty;

    /// <summary>The page this picture links to, if it is wrapped in a link (for example a forum thumbnail).</summary>
    public string? LinkUrl { get; init; }

    public string Alt { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;

    /// <summary>Size of the picture as shown on the page, in pixels. 0 when unknown.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    private string _fullUrl = string.Empty;
    /// <summary>The address that will be downloaded. Starts as the picture itself, and is replaced by the larger version when one is found.</summary>
    public string FullUrl
    {
        get => string.IsNullOrEmpty(_fullUrl) ? ThumbUrl : _fullUrl;
        set => _fullUrl = value;
    }

    /// <summary>True when a larger version than the thumbnail was found.</summary>
    public bool HasLargerVersion => !string.IsNullOrEmpty(_fullUrl) && _fullUrl != ThumbUrl;

    /// <summary>Longest side in pixels if known; 0 if not (a linked full-size image is assumed to be large).</summary>
    public int LongSide => HasLargerVersion ? int.MaxValue : Math.Max(Width, Height);

    private string? _problem;
    /// <summary>Why the full-size version couldn't be found, or null.</summary>
    public string? Problem
    {
        get => _problem;
        set { _problem = value; Raise(); Raise(nameof(StatusText)); Raise(nameof(CanDownload)); }
    }

    public bool CanDownload => Problem == null;

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Raise(); Raise(nameof(CheckVisibility)); }
    }

    private bool _eligible = true;
    /// <summary>False when the size filter excludes it; shown faded and never selected.</summary>
    public bool Eligible
    {
        get => _eligible;
        set { _eligible = value; Raise(); Raise(nameof(Opacity)); }
    }

    public double Opacity => Eligible ? 1.0 : 0.35;

    public Microsoft.UI.Xaml.Visibility CheckVisibility =>
        Selected ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Raise(); }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; Raise(); Raise(nameof(StatusText)); }
    }

    public string StatusText => Problem ?? (Status.Length > 0 ? Status : DimensionText);

    public string DimensionText => Width > 0 && Height > 0
        ? $"{Width}×{Height}{(HasLargerVersion ? " preview, larger found" : string.Empty)}"
        : HasLargerVersion ? "larger version found" : string.Empty;

    /// <summary>Everything the page says about this picture (alt text, caption, file name), used for tag suggestions.</summary>
    public string FileName
    {
        get
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(new Uri(FullUrl).AbsolutePath); }
            catch { return string.Empty; }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A suggested tag, shown as a chip the user can tick.</summary>
public sealed class TagChip : INotifyPropertyChanged
{
    public string Label { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;

    private bool _on;
    public bool On
    {
        get => _on;
        set { if (_on == value) return; _on = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(On))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>What Ocula read from a page: its text and its images.</summary>
public sealed class PageSnapshot
{
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> Headings { get; init; } = new();
    public List<string> Breadcrumbs { get; init; } = new();
    public List<string> Keywords { get; init; } = new();
    public List<WebImage> Images { get; init; } = new();
}
