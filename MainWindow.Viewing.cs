using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Ocula.Models;
using Ocula.Services;
using Windows.System;
using Windows.UI.Core;

namespace Ocula;

/// <summary>Viewing: full-screen viewer, editor, filmstrip, collapsible panels and empty/loading states.</summary>
public sealed partial class MainWindow
{
    private ViewerWindow? _viewer;
    private EditWindow? _editor;
    private bool _loadingFolder;
    private Action? _statePrimary;
    private Action? _stateSecondary;

    private void InitialiseViewing()
    {
        Filmstrip.ItemsSource = _images;

        // handledEventsToo: the grid's own selection handling marks these as handled.
        Thumbs.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Thumbs_PointerPressed), true);

        SidebarToggle.IsChecked = AppSettings.SidebarOpen;
        DetailsToggle.IsChecked = AppSettings.DetailsOpen;
        ApplyPanels();

        // Ctrl+[ and Ctrl+] show or hide the side panels.
        foreach (var (key, toggle) in new[] { ((VirtualKey)219, SidebarToggle), ((VirtualKey)221, DetailsToggle) })
        {
            var accelerator = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = key };
            accelerator.Invoked += (_, args) =>
            {
                toggle.IsChecked = toggle.IsChecked != true;
                ApplyPanels();
                args.Handled = true;
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        UpdateState();
    }

    // ================= Collapsible panels =================

    private void PanelToggle_Click(object sender, RoutedEventArgs e) => ApplyPanels();

    private void ApplyPanels()
    {
        var sidebar = SidebarToggle.IsChecked == true;
        var details = DetailsToggle.IsChecked == true;
        SidebarColumn.Width = new GridLength(sidebar ? 260 : 0);
        SidebarPanel.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = new GridLength(details ? 420 : 0);
        DetailsPanel.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        AppSettings.SidebarOpen = sidebar;
        AppSettings.DetailsOpen = details;
    }

    // ================= Filmstrip and selection =================

    private void SyncFilmstrip(ImageItem item)
    {
        if (!_images.Contains(item)) return;
        Filmstrip.SelectedItem = item;
        Filmstrip.ScrollIntoView(item);
    }

    private void Filmstrip_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ImageItem item) SelectItem(item);
    }

    /// <summary>Selects a photo in the grid (matching by path, so it works after a reload).</summary>
    public void SelectItem(ImageItem item)
    {
        var match = _images.FirstOrDefault(i => ReferenceEquals(i, item))
                    ?? _images.FirstOrDefault(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        if (match == null) return;
        Thumbs.SelectedItems.Clear();
        Thumbs.SelectedItem = match;
        Thumbs.ScrollIntoView(match);
    }

    // ================= Viewer and editor =================

    public void OpenViewer(ImageItem? item = null)
    {
        item ??= _current;
        if (item == null || _images.Count == 0) return;
        _viewer?.Close();
        var items = _images.ToList();
        _viewer = new ViewerWindow(this, items, Math.Max(0, items.IndexOf(item)));
        _viewer.Closed += (_, _) => _viewer = null;
        _viewer.Activate();
    }

    public void OpenEditor(ImageItem? item = null)
    {
        item ??= _current;
        if (item == null) return;
        if (item.IsVideo) { ShowError("Videos can't be edited here", "Use Edit in… to open the video in another app."); return; }
        if (item.IsCloudOnly) { ShowError($"{item.Name} is online-only", "Download it from OneDrive first."); return; }
        _editor?.Close();
        _editor = new EditWindow(this, item);
        _editor.Closed += (_, _) => _editor = null;
        _editor.Activate();
    }

    /// <summary>After saving an edit: show the folder with the saved photo selected.</summary>
    public async Task ShowAfterEditAsync(string path)
    {
        Activate();
        await OpenPathAsync(path);
        Status.Severity = InfoBarSeverity.Success;
        Status.Title = "Edit saved";
        Status.Message = System.IO.Path.GetFileName(path);
        Status.IsOpen = true;
    }

    private void PreviewFullScreen_Click(object sender, RoutedEventArgs e) => OpenViewer();
    private void PreviewEdit_Click(object sender, RoutedEventArgs e) => OpenEditor();

    /// <summary>
    /// A single click shows the photo here; a double-click opens it in its own photo window
    /// (MainWindow.PhotoWindows.cs), or full screen if that's switched off in Settings.
    /// </summary>
    private async void Thumbs_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _clickToken++; // the single click's pending preview is no longer wanted
        if (SelectionModifierHeld()) return; // Ctrl or Shift + click is for selecting several photos: open nothing
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ImageItem item) return;
        if (AppSettings.PhotoWindowsOnDoubleClick) await OpenPhotoWindowsAsync(new[] { item });
        else OpenViewer(item);
    }

    // ---------- Telling a single click from the start of a double-click ----------

    /// <summary>How long a mouse click waits before the side panel shows its photo, so that a
    /// double-click opening a window doesn't show the photo in the panel first.</summary>
    private const int SingleClickDelayMs = 200;

    private long _pointerSelectUntil;
    private int _clickToken;

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool SelectionModifierHeld() => IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift);

    private void Thumbs_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(Thumbs).Properties;
        // A mouse or pen click is about to change the selection (see Thumbs_SelectionChanged).
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
            _pointerSelectUntil = Environment.TickCount64 + 400;

        // Right-click on a photo that isn't selected selects it (and only it), like File Explorer,
        // so "Open in separate windows" acts on what was clicked rather than the old selection.
        if (properties.IsRightButtonPressed && !SelectionModifierHeld() &&
            (e.OriginalSource as FrameworkElement)?.DataContext is ImageItem item &&
            !Thumbs.SelectedItems.Contains(item))
        {
            Thumbs.SelectedItems.Clear();
            Thumbs.SelectedItems.Add(item);
        }
    }

    /// <summary>
    /// True when this selection change came from a plain mouse click and a double-click may follow,
    /// so showing the photo should wait a moment. Keyboard and Ctrl/Shift selections, and everything
    /// when double-click doesn't open windows, show it at once.
    /// </summary>
    private bool ShouldWaitForDoubleClick() =>
        AppSettings.PhotoWindowsOnDoubleClick && Environment.TickCount64 < _pointerSelectUntil && !SelectionModifierHeld();

    // ================= Empty, loading and no-results states =================

    /// <summary>Explains an empty grid: welcome, loading, empty folder, nothing found, or filtered out.</summary>
    private void UpdateState()
    {
        if (_loadingFolder)
        {
            ShowState("\uE895", "Opening folder…", _folder?.FullPath ?? string.Empty, loading: true);
            return;
        }
        if (_images.Count > 0)
        {
            StatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_folder == null && !_searchMode)
        {
            ShowState("\uE722", "Welcome to Ocula",
                "Choose a folder on the left, or import photos from a memory card.",
                ("Import photos", () => OpenImport(_newCard)), ("Add folder", () => AddFolder_Click(this, new RoutedEventArgs())));
        }
        else if (_all.Count > 0)
        {
            ShowState("\uE71C", "No photos match these filters",
                "Some photos here are hidden by the rating, label or soft-focus filters.",
                ("Clear filters", ClearFilters), null);
        }
        else if (_searchMode)
        {
            ShowState("\uE721", _view == ViewKind.Collection ? "This collection is empty" : "Nothing found",
                _view == ViewKind.Collection
                    ? "Add photos with More > Add to collection."
                    : "Try fewer words or a different filter. Folders that haven't been indexed yet are searched once indexing finishes.",
                _view == ViewKind.Collection ? null : ("Clear search", ClearSearch), null);
        }
        else
        {
            ShowState("\uE8B7", "No photos in this folder",
                "Its subfolders may have some, or you can import photos from a memory card.",
                ("Import photos", () => OpenImport(_newCard)), null);
        }
    }

    private void ShowState(string glyph, string title, string text, (string Label, Action Action)? primary = null,
                           (string Label, Action Action)? secondary = null, bool loading = false)
    {
        StatePanel.Visibility = Visibility.Visible;
        StateRing.IsActive = loading;
        StateRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        StateIconHost.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        StateIcon.Glyph = glyph;
        StateTitle.Text = title;
        StateText.Text = text;

        StatePrimary.Visibility = primary == null ? Visibility.Collapsed : Visibility.Visible;
        StatePrimary.Content = primary?.Label;
        _statePrimary = primary?.Action;
        StateSecondary.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
        StateSecondary.Content = secondary?.Label;
        _stateSecondary = secondary?.Action;
    }

    private void StatePrimary_Click(object sender, RoutedEventArgs e) => _statePrimary?.Invoke();
    private void StateSecondary_Click(object sender, RoutedEventArgs e) => _stateSecondary?.Invoke();

    private void ClearFilters()
    {
        RatingFilter.SelectedIndex = 0;
        LabelFilter.SelectedIndex = 0;
        HideSoftToggle.IsChecked = false;
        if (!_searchMode) SearchBox.Text = string.Empty;
        ApplyFilter();
    }

    private async void ClearSearch()
    {
        SearchBox.Text = string.Empty;
        if (_folder != null) await LoadFolderAsync(_folder, keepSelection: false);
        else
        {
            _searchMode = false;
            _all.Clear();
            ApplyFilter();
        }
    }
}
