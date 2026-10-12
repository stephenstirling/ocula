using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ocula.Models;
using Ocula.Services;

namespace Ocula;

/// <summary>
/// Photo windows: double-clicking a thumbnail opens the photo in its own window, which takes the
/// next free spot on the monitor (6 columns by default, so each window is a sixth of the screen's
/// width). The numbered buttons in each window's title bar, or Alt+1 to Alt+6, move it to another
/// spot: 1 is the far left, 6 the far right. Spots are fixed, so a window keeps its size however
/// many are open. The arithmetic is in Services/PhotoWindowLayout.cs; the settings are in
/// Settings > Photo windows.
/// </summary>
public sealed partial class MainWindow
{
    private sealed class PhotoWindowEntry
    {
        public required ViewerWindow Window { get; init; }
        public required long Order { get; init; }
        /// <summary>The spot it holds on its monitor, or null when it isn't snapped (moved by hand, centred, cascaded).</summary>
        public int? Spot { get; set; }
        public ulong Display { get; set; }
    }

    private readonly List<PhotoWindowEntry> _photoWindows = new();
    private long _photoWindowCount;

    /// <summary>Opens photos in their own windows, each in the next free spot on this window's monitor.</summary>
    public async Task OpenPhotoWindowsAsync(IReadOnlyList<ImageItem> photos)
    {
        var spots = AppSettings.PhotoWindowSpots;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).DisplayId.Value;
        var list = _images.ToList();
        var single = photos.Count == 1;

        foreach (var photo in photos.Take(spots))
        {
            var choice = await ChooseSpotAsync(display, spots, single);
            if (choice.Cancelled) return;

            var index = list.IndexOf(photo);
            var window = index >= 0 ? new ViewerWindow(this, list, index, photoWindow: true)
                                    : new ViewerWindow(this, new List<ImageItem> { photo }, 0, photoWindow: true);
            var entry = new PhotoWindowEntry
            {
                Window = window,
                Order = ++_photoWindowCount,
                Spot = choice.Spot,
                Display = display,
            };
            _photoWindows.Add(entry);

            // Placed before it's shown, so it doesn't appear somewhere else first.
            if (entry.Spot != null) ArrangeDisplay(display);
            else PlaceUnsnapped(entry);
            UpdateSnapBars();
            window.Activate();
        }

        if (photos.Count > spots)
        {
            Status.Severity = InfoBarSeverity.Informational;
            Status.Title = $"Opened the first {spots} photos";
            Status.Message = "One window per spot. Settings > Photo windows sets how many spots there are.";
            Status.IsOpen = true;
        }
    }

    private readonly record struct SpotChoice(int? Spot, bool Cancelled = false)
    {
        public static SpotChoice Cancel => new(null, true);
    }

    /// <summary>
    /// The spot for a new window: the one used last (if that setting is on, replacing its photo),
    /// else the lowest free one, else whatever "When every spot is taken" says.
    /// </summary>
    private async Task<SpotChoice> ChooseSpotAsync(ulong display, int spots, bool single)
    {
        var snapped = _photoWindows.Where(w => w.Spot != null && w.Display == display).ToList();

        if (single && AppSettings.PhotoWindowsReuseLastSpot)
        {
            var last = Math.Min(AppSettings.PhotoWindowsLastSpot, spots - 1);
            snapped.FirstOrDefault(w => w.Spot == last)?.Window.Close();
            return new SpotChoice(last);
        }

        if (PhotoWindowLayout.NextFreeSpot(snapped.Select(w => w.Spot!.Value), spots) is int free)
            return new SpotChoice(free);

        WhenSpotsFull? full = AppSettings.PhotoWindowsWhenFull;
        if (full == WhenSpotsFull.Ask)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"All {spots} spots have a photo",
                Content = "Replace the photo that has been open longest, or open this one on top of the others?",
                PrimaryButtonText = "Replace the oldest",
                SecondaryButtonText = "Open on top",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            full = await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => WhenSpotsFull.ReplaceOldest,
                ContentDialogResult.Secondary => WhenSpotsFull.Cascade,
                _ => null,
            };
            if (full == null) return SpotChoice.Cancel;
        }

        if (full == WhenSpotsFull.ReplaceOldest)
        {
            var oldest = snapped.MinBy(w => w.Order)!;
            var spot = oldest.Spot;
            oldest.Window.Close();
            return new SpotChoice(spot);
        }
        return new SpotChoice(null); // on top, cascaded
    }

    /// <summary>Puts a window that has no spot a step down and right of the other unsnapped ones.</summary>
    private void PlaceUnsnapped(PhotoWindowEntry entry)
    {
        var unsnapped = _photoWindows.Count(w => w.Spot == null && w.Display == entry.Display);
        var area = WorkArea(entry.Display);
        entry.Window.PlaceAt(PhotoWindowLayout.Spots(SnapLayout.Cascade, Math.Max(1, unsnapped), area)[^1]);
    }

    /// <summary>Lays out the snapped windows on one monitor (windows on other monitors are left alone).</summary>
    private void ArrangeDisplay(ulong display)
    {
        var layout = AppSettings.PhotoWindowLayoutKind;
        var snapped = _photoWindows.Where(w => w.Spot != null && w.Display == display).ToList();
        if (snapped.Count == 0) return;
        var rects = PhotoWindowLayout.Place(layout, AppSettings.PhotoWindowSpots, snapped.Select(w => w.Spot!.Value).ToList(), WorkArea(display));
        for (var i = 0; i < snapped.Count; i++) snapped[i].Window.PlaceAt(rects[i]);
    }

    private void ArrangeAll()
    {
        foreach (var display in _photoWindows.Select(w => w.Display).Distinct().ToList()) ArrangeDisplay(display);
    }

    private void UpdateSnapBars()
    {
        var layout = AppSettings.PhotoWindowLayoutKind;
        var spots = AppSettings.PhotoWindowSpots;
        foreach (var entry in _photoWindows) entry.Window.UpdateSnapBar(layout, spots, entry.Spot);
    }

    /// <summary>The usable part of a monitor (without the taskbar), in physical pixels.</summary>
    private static SpotRect WorkArea(ulong display)
    {
        var area = (DisplayArea.GetFromDisplayId(new DisplayId(display)) ?? DisplayArea.Primary).WorkArea;
        return new SpotRect(area.X, area.Y, area.Width, area.Height);
    }

    private PhotoWindowEntry? EntryFor(ViewerWindow window) => _photoWindows.FirstOrDefault(w => w.Window == window);

    // ================= Called by the photo windows =================

    /// <summary>A spot button or Alt+number: move the window there, swapping with the window that had it.</summary>
    public void SnapPhotoWindow(ViewerWindow window, int spot)
    {
        var spots = AppSettings.PhotoWindowSpots;
        if (EntryFor(window) is not { } entry || spot < 0 || spot >= spots) return;

        var oldDisplay = entry.Display;
        var oldSpot = oldDisplay == window.DisplayId ? entry.Spot : null;
        entry.Display = window.DisplayId;

        if (_photoWindows.FirstOrDefault(w => w != entry && w.Display == entry.Display && w.Spot == spot) is { } other)
            other.Spot = oldSpot ?? PhotoWindowLayout.NextFreeSpot(
                _photoWindows.Where(w => w != other && w != entry && w.Display == entry.Display && w.Spot != null)
                             .Select(w => w.Spot!.Value).Append(spot), spots);
        entry.Spot = spot;
        AppSettings.PhotoWindowsLastSpot = spot;

        ArrangeDisplay(entry.Display);
        UpdateSnapBars();
    }

    /// <summary>Alt+0: take the window out of its spot and centre it.</summary>
    public void CentrePhotoWindow(ViewerWindow window)
    {
        if (EntryFor(window) is not { } entry) return;
        entry.Display = window.DisplayId;
        Unsnap(entry);
        entry.Window.PlaceAt(PhotoWindowLayout.Centre(WorkArea(entry.Display)));
    }

    /// <summary>Moves the window to the next monitor, keeping its spot if that spot is free there.</summary>
    public void SendPhotoWindowToNextDisplay(ViewerWindow window)
    {
        if (EntryFor(window) is not { } entry) return;
        // Index, don't foreach: enumerating FindAll()'s result throws in some Windows App SDK versions.
        var all = DisplayArea.FindAll();
        var areas = new List<DisplayArea>();
        for (var i = 0; i < all.Count; i++) areas.Add(all[i]);
        var displays = areas.OrderBy(d => d.OuterBounds.X).ThenBy(d => d.OuterBounds.Y).Select(d => d.DisplayId.Value).ToList();
        if (displays.Count < 2) return;

        var from = window.DisplayId;
        var to = displays[(Math.Max(0, displays.IndexOf(from)) + 1) % displays.Count];
        var taken = _photoWindows.Where(w => w != entry && w.Display == to && w.Spot != null).Select(w => w.Spot!.Value).ToList();
        var spots = AppSettings.PhotoWindowSpots;

        entry.Display = to;
        if (entry.Spot is int spot && taken.Contains(spot))
            entry.Spot = PhotoWindowLayout.NextFreeSpot(taken, spots); // null: no room, so it goes on top

        if (entry.Spot != null) ArrangeDisplay(to);
        else entry.Window.PlaceAt(PhotoWindowLayout.Centre(WorkArea(to)));
        UpdateSnapBars();
    }

    public void SetPhotoWindowLayout(SnapLayout layout)
    {
        AppSettings.PhotoWindowLayoutKind = layout;
        ArrangeAll();
        UpdateSnapBars();
    }

    /// <summary>Dragged or resized by hand: it leaves its spot, which is then free for the next photo.</summary>
    public void PhotoWindowMovedByUser(ViewerWindow window)
    {
        if (EntryFor(window) is not { Spot: not null } entry) return;
        entry.Display = window.DisplayId;
        Unsnap(entry);
    }

    private void Unsnap(PhotoWindowEntry entry)
    {
        entry.Spot = null;
        UpdateSnapBars();
    }

    public void PhotoWindowClosed(ViewerWindow window)
    {
        if (EntryFor(window) is not { } entry) return;
        _photoWindows.Remove(entry);
        UpdateSnapBars();
    }

    /// <summary>Link zoom and pan: show the same part of every other photo at the same zoom.</summary>
    public void PhotoWindowViewChanged(ViewerWindow source, float zoom, double centreX, double centreY)
    {
        if (!AppSettings.PhotoWindowsLinkView) return;
        foreach (var entry in _photoWindows.Where(w => w.Window != source))
            entry.Window.ApplyLinkedView(zoom, centreX, centreY);
    }

    /// <summary>Settings > Photo windows changed: windows past the new spot count leave their spots.</summary>
    public void PhotoWindowSettingsChanged()
    {
        var spots = AppSettings.PhotoWindowSpots;
        foreach (var entry in _photoWindows.Where(w => w.Spot >= spots)) entry.Spot = null;
        ArrangeAll();
        UpdateSnapBars();
    }

    private void ClosePhotoWindows()
    {
        foreach (var entry in _photoWindows.ToList()) entry.Window.Close();
    }

    // ================= Thumbnails =================

    private async void OpenSeparate_Click(object sender, RoutedEventArgs e)
    {
        var photos = SelectedOrCurrent();
        if (photos.Count > 0) await OpenPhotoWindowsAsync(photos);
    }
}
