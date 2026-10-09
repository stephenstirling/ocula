using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Ocula.Models;
using Ocula.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ocula;

/// <summary>A set of photos that look the same.</summary>
public sealed class DuplicateGroup : INotifyPropertyChanged
{
    public ObservableCollection<DuplicateCandidate> Items { get; } = new();

    public string Summary =>
        $"{Items.Count} matching photos · {DuplicateCandidate.FormatSize(Items.Sum(i => i.Size))}";

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One photo in a group, with whether to keep it.</summary>
public sealed class DuplicateCandidate : INotifyPropertyChanged
{
    private bool _keep;
    private BitmapImage? _thumbnail;

    public DuplicateCandidate(HashedImage image, string rootFolder)
    {
        Image = image;
        var relative = Path.GetRelativePath(rootFolder, image.Item.Folder);
        FolderName = relative == "." ? "This folder" : relative;
        var parts = new List<string> { FormatSize(image.Size) };
        if (image.Item.Badge.Length > 0) parts.Add(image.Item.Badge);
        if (image.Item.Rating > 0) parts.Add(new string('★', image.Item.Rating));
        Details = string.Join(" · ", parts);
    }

    public HashedImage Image { get; }
    public ImageItem Item => Image.Item;
    public long Size => Image.Size;
    public string Name => Image.Item.Name;
    public string FolderName { get; }
    public string Details { get; }

    public bool Keep
    {
        get => _keep;
        set
        {
            if (_keep == value) return;
            _keep = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeepBrush));
        }
    }

    /// <summary>Accent border on photos being kept, red on photos to be removed.</summary>
    public Brush KeepBrush => (Brush)Application.Current.Resources[_keep ? "AccentFillColorDefaultBrush" : "SystemFillColorCriticalBrush"];

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class DuplicatesWindow : Window
{
    private readonly string _root;
    private readonly IndexService _index;
    private readonly ObservableCollection<DuplicateGroup> _groups = new();
    private bool _scanning;

    public DuplicatesWindow(string rootFolder, IndexService index, ElementTheme theme)
    {
        InitializeComponent();
        _root = rootFolder;
        _index = index;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        Root.RequestedTheme = theme;
        var dark = Root.ActualTheme == ElementTheme.Dark || theme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;

        FolderText.Text = new FolderNode(rootFolder).ToString();
        StatusText.Text = rootFolder;
        Groups.ItemsSource = _groups;
        UpdateSelectionText();
    }

    // ================= Scanning =================

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        _scanning = true;
        ScanButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        AutoSelectButton.IsEnabled = false;
        EmptyText.Visibility = Visibility.Collapsed;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        _groups.Clear();

        try
        {
            var folders = SubfoldersToggle.IsOn ? await Task.Run(() => AllFolders(_root)) : new List<string> { _root };
            var limits = DuplicateFinder.Limits(SensitivityBox.SelectedItem as string ?? "Very similar");
            var anyOrientation = RotatedToggle.IsOn;

            // 1. List every photo and look up what the library already knows.
            var pending = new List<(ImageItem Item, IndexRow? Row, PhotoFingerprint? Fingerprint)>();
            var skipped = 0;
            foreach (var folder in folders)
            {
                StatusText.Text = $"Reading {folder}";
                List<ImageItem> items;
                try { items = await Task.Run(() => FolderScanner.Scan(folder)); }
                catch { continue; } // no access, removed meanwhile, etc.

                var rows = await _index.GetFolderAsync(folder);
                var stored = await _index.GetFingerprintsAsync(folder);
                foreach (var item in items)
                {
                    if (item.IsCloudOnly) { skipped++; continue; }
                    IndexRow? row = rows.TryGetValue(item.Path, out var r) && r.Modified == item.Modified ? r : null;
                    if (row != null) item.ApplyIndex(row with { Thumb = null }); // rating etc., for "keep best"
                    PhotoFingerprint? fingerprint = stored.TryGetValue(item.Path, out var h) && h.Modified == item.Modified ? h.Fingerprint : null;
                    // Only hold on to the cached thumbnail if we still need to fingerprint it.
                    pending.Add((item, fingerprint == null ? row : null, fingerprint));
                }
            }

            // 2. Fingerprint each photo (reusing saved fingerprints), a few at a time.
            Progress.IsIndeterminate = false;
            Progress.Maximum = Math.Max(1, pending.Count);
            var hashed = new List<HashedImage>();
            for (var start = 0; start < pending.Count; start += 8)
            {
                var batch = pending.Skip(start).Take(8).Select(p => FingerprintAsync(p.Item, p.Row, p.Fingerprint));
                hashed.AddRange((await Task.WhenAll(batch)).OfType<HashedImage>());
                var done = Math.Min(start + 8, pending.Count);
                Progress.Value = done;
                StatusText.Text = $"Checking {done} of {pending.Count} photos";
            }

            // 3. Group matching fingerprints.
            var groups = await Task.Run(() => DuplicateFinder.Group(hashed, limits, anyOrientation));
            foreach (var group in groups)
            {
                var dg = new DuplicateGroup();
                var best = DuplicateFinder.Best(group);
                foreach (var image in group)
                {
                    var candidate = new DuplicateCandidate(image, _root) { Keep = image == best };
                    candidate.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(DuplicateCandidate.Keep)) UpdateSelectionText();
                    };
                    dg.Items.Add(candidate);
                }
                _groups.Add(dg);
            }

            var groupWord = _groups.Count == 1 ? "group" : "groups";
            StatusText.Text = _groups.Count == 0
                ? $"No duplicates among {hashed.Count} photos."
                : $"{_groups.Count} {groupWord} of matching photos among {hashed.Count} checked." +
                  (skipped > 0 ? $" {skipped} online-only files were skipped." : string.Empty);
            if (_groups.Count == 0)
            {
                EmptyText.Text = "No duplicates found. Try \"Similar\" to catch edited or cropped versions.";
                EmptyText.Visibility = Visibility.Visible;
            }

            UpdateSelectionText();
            await LoadThumbnailsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = true;
            AutoSelectButton.IsEnabled = _groups.Count > 0;
            _scanning = false;
        }
    }

    private async Task<HashedImage?> FingerprintAsync(ImageItem item, IndexRow? row, PhotoFingerprint? stored)
    {
        try
        {
            var size = await Task.Run(() => item.AllPaths.Sum(p => new FileInfo(p).Length));
            if (stored != null) return new HashedImage(item, stored, size);

            var bytes = row?.Thumb ?? await ThumbnailService.GetBytesAsync(item.Path, 384);
            if (bytes == null) return null;
            var fingerprint = await Task.Run(() => DuplicateFinder.MakeFingerprint(bytes));
            await _index.SetFingerprintAsync(item.Path, item.Modified, fingerprint);
            return new HashedImage(item, fingerprint, size);
        }
        catch
        {
            return null; // unreadable file: leave it out
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        foreach (var candidate in _groups.SelectMany(g => g.Items).ToList())
        {
            var bytes = await ThumbnailService.GetBytesAsync(candidate.Item.Path, 384);
            if (bytes != null) candidate.Thumbnail = await ThumbnailService.ToBitmapAsync(bytes);
        }
    }

    private static List<string> AllFolders(string root)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            result.Add(folder);
            try
            {
                foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories())
                {
                    if ((dir.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                        stack.Push(dir.FullName);
                }
            }
            catch
            {
                // Skip folders we can't read.
            }
        }
        return result;
    }

    // ================= Choosing and removing =================

    private void AutoSelect_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _groups)
        {
            var best = DuplicateFinder.Best(group.Items.Select(i => i.Image));
            foreach (var item in group.Items) item.Keep = item.Image == best;
        }
    }

    private List<DuplicateCandidate> ToRemove() =>
        _groups.SelectMany(g => g.Items).Where(i => !i.Keep).ToList();

    private void UpdateSelectionText()
    {
        var remove = ToRemove();
        var groupsWithNothingKept = _groups.Count(g => g.Items.All(i => !i.Keep));

        if (groupsWithNothingKept > 0)
        {
            SelectionText.Text = $"Keep at least one photo in every group ({groupsWithNothingKept} have none ticked).";
            RemoveButton.IsEnabled = false;
        }
        else if (remove.Count == 0)
        {
            SelectionText.Text = _groups.Count == 0 ? string.Empty : "Nothing selected to remove.";
            RemoveButton.IsEnabled = false;
        }
        else
        {
            SelectionText.Text = $"{remove.Count} photos to remove · frees {DuplicateCandidate.FormatSize(remove.Sum(i => i.Size))}";
            RemoveButton.IsEnabled = !_scanning;
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var remove = ToRemove();
        if (remove.Count == 0) return;

        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = $"Move {remove.Count} photos to the Recycle Bin?",
            Content = "RAW+JPEG pairs and their .xmp sidecars are moved together. You can restore them from the Recycle Bin.",
            PrimaryButtonText = "Move to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var files = remove.SelectMany(c => DuplicateFinder.FilesOf(c.Item)).ToList();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ok = await Task.Run(() => DuplicateFinder.MoveToRecycleBin(files, hwnd));

        // Drop whatever is actually gone, and groups that no longer have duplicates.
        foreach (var group in _groups.ToList())
        {
            foreach (var candidate in group.Items.Where(c => !File.Exists(c.Item.Path)).ToList())
                group.Items.Remove(candidate);
            if (group.Items.Count < 2) _groups.Remove(group);
            else group.Refresh();
        }

        StatusText.Text = ok
            ? $"Moved {remove.Count} photos to the Recycle Bin."
            : "Some files couldn't be moved. They may be open in another app.";
        UpdateSelectionText();
    }
}
