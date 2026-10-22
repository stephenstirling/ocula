using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Ocula.Library;

namespace Ocula.Views;

/// <summary>One tile in the Phun page's "Recently saved" grid. The picture arrives later, so it raises a change when it is ready.</summary>
public sealed class RecentImage : INotifyPropertyChanged
{
    public RecentImage(IndexedImage image) => Image = image;

    public IndexedImage Image { get; }
    public string Name => Image.Name;
    public string Badge => Image.Badge;

    private BitmapImage? _thumb;
    public BitmapImage? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What screen readers announce for a tile.</summary>
    public override string ToString() => $"{Name}, {Badge}";
}
