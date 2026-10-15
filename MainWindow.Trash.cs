using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ocula.Models;
using Ocula.Services;

namespace Ocula;

/// <summary>
/// Delete: moves the selected photos to the Recycle Bin, so a mistake can be undone from there.
/// A RAW+JPEG pair goes together, with any .xmp sidecar, as in the duplicate finder and compare view.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Selections of this many photos or more ask first; fewer go straight to the Recycle Bin, like File Explorer.</summary>
    private const int ConfirmTrashAt = 10;

    private bool _trashing;

    private async void Trash_Click(object sender, RoutedEventArgs e) => await TrashSelectionAsync();

    private async Task TrashSelectionAsync()
    {
        if (_trashing) return; // a second Delete while the first is still working
        var items = SelectedOrCurrent();
        if (items.Count == 0) return;

        if (items.Count >= ConfirmTrashAt)
        {
            var confirm = Dialog($"Move {items.Count} photos to the Recycle Bin?",
                                 new TextBlock
                                 {
                                     Text = "RAW+JPEG pairs and their .xmp sidecars are moved together. You can restore them from the Recycle Bin.",
                                     TextWrapping = TextWrapping.Wrap
                                 },
                                 "Move to Recycle Bin");
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        _trashing = true;
        try
        {
            // The photo that takes the place of the first one removed gets selected next, so Delete can be pressed again.
            var firstIndex = items.Select(i => _images.IndexOf(i)).Where(i => i >= 0).DefaultIfEmpty(0).Min();

            var files = items.SelectMany(DuplicateFinder.FilesOf).ToList();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var moved = await Task.Run(() => DuplicateFinder.MoveToRecycleBin(files, hwnd));

            // Judge by what is actually gone: a locked file stays, and so does its photo.
            var gone = items.Where(i => i.AllPaths.All(p => !File.Exists(p))).ToList();
            if (gone.Count > 0)
            {
                await _index.RemovePhotosAsync(gone.Select(i => i.Path).ToList());
                _smart.NotifyChanged();
                foreach (var item in gone) _all.Remove(item);
                if (_current != null && gone.Contains(_current)) _current = null;

                ApplyFilter();
                if (_images.Count > 0) Thumbs.SelectedItem = _images[Math.Min(firstIndex, _images.Count - 1)];
                else ClearDetails();
                if (Thumbs.SelectedItem != null) Thumbs.ScrollIntoView(Thumbs.SelectedItem);
                _ = LoadSidebarAsync(); // collection and smart-album counts
            }

            if (gone.Count == items.Count && moved)
            {
                Status.Severity = InfoBarSeverity.Success;
                Status.Title = gone.Count == 1 ? $"Moved {gone[0].Name} to the Recycle Bin" : $"Moved {gone.Count} photos to the Recycle Bin";
                Status.Message = "Restore them from the Recycle Bin if you change your mind.";
                Status.IsOpen = true;
            }
            else
            {
                ShowError("Some photos couldn't be moved",
                          $"{items.Count - gone.Count} of {items.Count} are still there. They may be open in another app, or on a drive without a Recycle Bin.");
            }
        }
        finally
        {
            _trashing = false;
        }
    }
}
