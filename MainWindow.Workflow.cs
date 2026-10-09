using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Models;
using Ocula.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;

namespace Ocula;

/// <summary>Getting photos in and out: card import, batch rename, "Edit in…", contact sheets, proofing.</summary>
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _cardTimer;
    private HashSet<string> _knownCards = new(StringComparer.OrdinalIgnoreCase);
    private CardInfo? _newCard;
    private ImportWindow? _import;
    private List<(string From, string To)>? _undoFiles;
    private List<(string From, string To, string? Raw)>? _undoRows;
    private string? _lastOutputFile;

    // ================= Memory cards =================

    /// <summary>Checks for camera cards every few seconds and offers to import new ones.</summary>
    private void InitialiseCardWatch()
    {
        _knownCards = ImportService.FindCards().Select(c => c.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _cardTimer = DispatcherQueue.CreateTimer();
        _cardTimer.Interval = TimeSpan.FromSeconds(3);
        _cardTimer.Tick += async (_, _) =>
        {
            var cards = await Task.Run(ImportService.FindCards);
            var added = cards.FirstOrDefault(c => !_knownCards.Contains(c.Root));
            _knownCards = cards.Select(c => c.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (added != null)
            {
                _newCard = added;
                CardBar.Title = $"Memory card \"{added.Label}\" inserted";
                CardBar.Message = "Import its new photos into your library?";
                CardBar.IsOpen = true;
            }
            else if (_newCard != null && !_knownCards.Contains(_newCard.Root))
            {
                CardBar.IsOpen = false; // card removed
                _newCard = null;
            }
        };
        _cardTimer.Start();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e) => OpenImport(_newCard);

    private void CardBarImport_Click(object sender, RoutedEventArgs e)
    {
        CardBar.IsOpen = false;
        OpenImport(_newCard);
    }

    private void OpenImport(CardInfo? card)
    {
        if (_import == null)
        {
            _import = new ImportWindow(this, card);
            _import.Closed += (_, _) => _import = null;
        }
        _import.Activate();
    }

    /// <summary>Shows freshly imported photos, adding the destination to the folder tree if needed.</summary>
    public async Task OpenAfterImportAsync(string destinationRoot, string firstFolder)
    {
        var inTree = RootFolders.Any(root =>
            destinationRoot.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(destinationRoot.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (!inTree) AddFolder(destinationRoot);
        else _crawler.Start(RootFolders);

        Activate();
        await OpenPathAsync(firstFolder);
    }

    // ================= Edit in… =================

    private Editor? DefaultEditor()
    {
        var editors = EditorLauncher.Available();
        return editors.FirstOrDefault(e => e.Name == AppSettings.LastEditor) ?? editors.FirstOrDefault();
    }

    private void UpdateEditButton()
    {
        var editor = DefaultEditor();
        EditButtonText.Text = editor == null ? "Edit in…" : $"Edit in {editor.Name}";
    }

    private void EditButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (DefaultEditor() is { } editor) OpenInEditor(editor);
        else sender.Flyout.ShowAt(sender);
    }

    private void EditMenu_Opening(object sender, object e)
    {
        EditMenu.Items.Clear();
        foreach (var editor in EditorLauncher.Available())
        {
            var item = new MenuFlyoutItem { Text = editor.Name };
            item.Click += (_, _) => OpenInEditor(editor);
            EditMenu.Items.Add(item);
        }
        if (EditMenu.Items.Count == 0)
            EditMenu.Items.Add(new MenuFlyoutItem { Text = "No editors found", IsEnabled = false });

        EditMenu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "Add an editor…" };
        add.Click += async (_, _) => await AddEditorAsync();
        EditMenu.Items.Add(add);
    }

    private void OpenInEditor(Editor editor)
    {
        var items = SelectedOrCurrent().Where(i => !i.IsCloudOnly).ToList();
        if (items.Count == 0) { ShowError("Nothing to edit", "Select a photo first."); return; }
        try
        {
            EditorLauncher.Open(editor, items);
            AppSettings.LastEditor = editor.Name;
            UpdateEditButton();
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't open {editor.Name}", ex.Message);
        }
    }

    private async Task AddEditorAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var name = new TextBox { Header = "Name", Text = Path.GetFileNameWithoutExtension(file.Path) };
        var raw = new CheckBox { Content = "It can open RAW files (send the RAW of RAW+JPEG pairs)", IsChecked = true };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(name);
        panel.Children.Add(raw);
        if (await Dialog("Add an editor", panel, "Add").ShowAsync() != ContentDialogResult.Primary) return;

        var editors = AppSettings.CustomEditors;
        editors.RemoveAll(e => string.Equals(e.ExePath, file.Path, StringComparison.OrdinalIgnoreCase));
        editors.Add(new Editor(name.Text.Trim().Length > 0 ? name.Text.Trim() : file.DisplayName, file.Path, raw.IsChecked == true));
        AppSettings.CustomEditors = editors;
        AppSettings.LastEditor = editors[^1].Name;
        UpdateEditButton();
    }

    // ================= Batch rename =================

    private sealed record RenamePlan(ImageItem Item, string NewStem);

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedOrCurrent().Where(i => !i.IsCloudOnly)
                                       .OrderBy(i => i.Taken ?? DateTime.MaxValue).ThenBy(i => i.Name).ToList();
        if (items.Count == 0) { ShowError("Nothing to rename", "Select the photos to rename first."); return; }

        var pattern = new TextBox { Header = "Name pattern", Text = AppSettings.RenamePatternSetting };
        var eventName = new TextBox { Header = "Event name (for {event})" };
        var start = new NumberBox { Header = "Start numbering at", Value = 1, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var help = new TextBlock
        {
            Text = "Tokens: {date} {time} {event} {camera} {seq} {name} {year} {month} {day}",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12
        };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        var panel = new StackPanel { Spacing = 12, Width = 460 };
        foreach (var child in new UIElement[] { pattern, eventName, start, help, preview }) panel.Children.Add(child);

        var dialog = Dialog($"Rename {items.Count} photos", panel, "Rename");
        List<RenamePlan> plan = new();
        void Refresh()
        {
            plan = BuildRenamePlan(items, pattern.Text, eventName.Text, double.IsNaN(start.Value) ? 1 : (int)start.Value, out var problem);
            var lines = plan.Take(8).Select(p => $"{p.Item.Name}  →  {p.NewStem}{(p.Item.RawPath != null ? " (+RAW)" : Path.GetExtension(p.Item.Path))}");
            preview.Text = string.Join('\n', lines) + (plan.Count > 8 ? $"\n…and {plan.Count - 8} more" : string.Empty) +
                           (problem != null ? $"\n\n{problem}" : string.Empty);
            dialog.IsPrimaryButtonEnabled = problem == null;
        }
        pattern.TextChanged += (_, _) => Refresh();
        eventName.TextChanged += (_, _) => Refresh();
        start.ValueChanged += (_, _) => Refresh();
        Refresh();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        AppSettings.RenamePatternSetting = pattern.Text;
        await ApplyRenameAsync(plan);
    }

    private static List<RenamePlan> BuildRenamePlan(List<ImageItem> items, string pattern, string eventName, int start, out string? problem)
    {
        problem = null;
        var plan = new List<RenamePlan>();
        var sources = items.SelectMany(i => i.AllPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var stem = RenamePattern.FileName(pattern, new RenameContext(Path.GetFileNameWithoutExtension(item.Path),
                                                                          item.Taken, item.Camera, eventName, start + i));
            foreach (var path in item.AllPaths)
            {
                var target = Path.Combine(Path.GetDirectoryName(path)!, stem + Path.GetExtension(path));
                if (!targets.Add(target))
                    problem ??= "Some photos would get the same name. Add {seq} or {time} to the pattern.";
                else if (File.Exists(target) && !sources.Contains(target))
                    problem ??= $"{Path.GetFileName(target)} already exists in that folder.";
            }
            plan.Add(new RenamePlan(item, stem));
        }
        return plan;
    }

    /// <summary>
    /// Renames in two steps (to temporary names, then to the final ones) so names can be
    /// swapped within a batch without clashing. Pairs and .xmp sidecars move together, and
    /// library rows move with them so flags, sharpness and thumbnails are kept.
    /// </summary>
    private async Task ApplyRenameAsync(List<RenamePlan> plan)
    {
        var moves = new List<(string From, string Temp, string To)>();
        var rows = new List<(string From, string To, string? Raw)>();
        foreach (var step in plan)
        {
            string NewPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, step.NewStem + Path.GetExtension(path));
            foreach (var path in DuplicateFinder.FilesOf(step.Item))
            {
                var to = NewPath(path);
                if (!string.Equals(path, to, StringComparison.Ordinal))
                    moves.Add((path, Path.Combine(Path.GetDirectoryName(path)!, $".ocula-{Guid.NewGuid():N}{Path.GetExtension(path)}"), to));
            }
            rows.Add((step.Item.Path, NewPath(step.Item.Path), step.Item.RawPath == null ? null : NewPath(step.Item.RawPath)));
        }

        var ok = await Task.Run(() => MoveInTwoSteps(moves.Select(m => (m.From, m.Temp, m.To)).ToList()));
        if (!ok)
        {
            ShowError("Rename failed", "A file may be open in another app. Nothing was renamed.");
            return;
        }
        foreach (var (from, to, raw) in rows) await _index.RenameAsync(from, to, raw);

        _undoFiles = moves.Select(m => (m.To, m.From)).ToList();
        _undoRows = rows.Select(r => (r.To, r.From, r.Raw == null ? null : plan.First(p => p.Item.Path == r.From).Item.RawPath)).ToList();
        UndoBar.Title = $"Renamed {plan.Count} photos";
        UndoBar.IsOpen = true;
    }

    private async void UndoRename_Click(object sender, RoutedEventArgs e)
    {
        if (_undoFiles == null || _undoRows == null) return;
        var moves = _undoFiles.Select(m => (m.From, Path.Combine(Path.GetDirectoryName(m.From)!, $".ocula-{Guid.NewGuid():N}{Path.GetExtension(m.From)}"), m.To)).ToList();
        if (await Task.Run(() => MoveInTwoSteps(moves)))
        {
            foreach (var (from, to, raw) in _undoRows) await _index.RenameAsync(from, to, raw);
            UndoBar.IsOpen = false;
            _undoFiles = null;
            _undoRows = null;
        }
        else
        {
            ShowError("Couldn't undo the rename", "A file may be open in another app.");
        }
    }

    private static bool MoveInTwoSteps(List<(string From, string Temp, string To)> moves)
    {
        var done = new List<(string From, string Temp)>();
        try
        {
            foreach (var m in moves) { File.Move(m.From, m.Temp); done.Add((m.From, m.Temp)); }
            foreach (var m in moves) File.Move(m.Temp, m.To);
            return true;
        }
        catch
        {
            // Put back whatever was moved to a temporary name.
            foreach (var (from, temp) in done)
            {
                try { if (File.Exists(temp)) File.Move(temp, from); } catch { }
            }
            return false;
        }
    }

    // ================= Contact sheet =================

    private async void ContactSheet_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedOrCurrent().Count > 1 ? SelectedOrCurrent() : _images.ToList();
        if (items.Count == 0) { ShowError("Nothing to print", "Open a folder or select some photos first."); return; }

        var title = new TextBox { Header = "Title", Text = _searchMode ? "Search results" : _folder?.ToString() ?? "Contact sheet" };
        var columns = new ComboBox { Header = "Photos per row", ItemsSource = new[] { 3, 4, 5, 6 }, SelectedItem = 4 };
        var ratings = new ToggleSwitch { Header = "Show star ratings", IsOn = true };
        var usLetter = RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "CA";
        var paper = new ComboBox { Header = "Paper", ItemsSource = new[] { "A4", "US Letter" }, SelectedIndex = usLetter ? 1 : 0 };
        var panel = new StackPanel { Spacing = 12, Width = 360 };
        foreach (var child in new UIElement[] { title, columns, paper, ratings }) panel.Children.Add(child);
        panel.Children.Add(new TextBlock { Text = $"{items.Count} photos", Opacity = 0.7 });
        if (await Dialog("Contact sheet", panel, "Save PDF…").ShowAsync() != ContentDialogResult.Primary) return;

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = title.Text };
        picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var options = new ContactSheetOptions(title.Text, (int)columns.SelectedItem, ratings.IsOn, paper.SelectedIndex == 1);
        await RunJobAsync($"Making contact sheet", items.Count, file.Path,
            (progress, token) => ContactSheet.CreateAsync(items, options, file.Path, _index, progress, token),
            $"Contact sheet saved: {file.Name}");
    }

    // ================= Client proofing =================

    private async void Proofing_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedOrCurrent().Count > 1 ? SelectedOrCurrent() : _images.ToList();
        items = items.Where(i => !i.IsCloudOnly && !i.IsVideo).ToList();
        if (items.Count == 0) { ShowError("Nothing to share", "Select the photos for the gallery first."); return; }

        var title = new TextBox { Header = "Gallery title", Text = _folder?.ToString() ?? "Proofs" };
        var client = new TextBox { Header = "Client name (optional)" };
        var email = new TextBox { Header = "Your email (favourites are sent here)", Text = AppSettings.ProofingEmail };
        var destination = new TextBox { Header = "Save to (e.g. the NAS web folder)", Text = AppSettings.ProofingDestination };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is { } folder) destination.Text = folder.Path;
        };
        var size = new ComboBox { Header = "Photo size", ItemsSource = new[] { "1600 px", "2048 px", "2560 px" }, SelectedIndex = 1 };
        var watermark = new TextBox { Header = "Watermark", Text = AppInfo.Copyright };
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12,
            Text = "Photos are resized, location data is removed, and the gallery gets a hard-to-guess folder name. " +
                   "Anyone with the link can view it, so use your web server's password protection for private shoots."
        };
        var panel = new StackPanel { Spacing = 12, Width = 440 };
        foreach (var child in new UIElement[] { title, client, email, destination, browse, size, watermark, note }) panel.Children.Add(child);
        var dialog = Dialog($"Client proofing gallery · {items.Count} photos", panel, "Create gallery");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!Path.IsPathFullyQualified(destination.Text.Trim()) || !email.Text.Contains('@'))
            {
                args.Cancel = true;
                note.Text = "Enter your email address and choose a folder to save the gallery to.";
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        AppSettings.ProofingEmail = email.Text.Trim();
        AppSettings.ProofingDestination = destination.Text.Trim();
        var longEdge = size.SelectedIndex switch { 0 => 1600u, 2 => 2560u, _ => 2048u };
        var options = new ProofingOptions(title.Text.Trim(), client.Text.Trim(), email.Text.Trim(), destination.Text.Trim(), longEdge, watermark.Text);

        string? galleryFolder = null;
        await RunJobAsync("Creating proofing gallery", items.Count, null, async (progress, token) =>
        {
            galleryFolder = await ProofingGallery.CreateAsync(items, options, progress, token);
            _lastExportFolder = galleryFolder;
        }, "Gallery ready. Upload or copy this folder, then send the client its index.html link.");
    }

    private async void ImportPicks_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 220, Width = 420,
            PlaceholderText = "Paste the list from the client's email, one file name per line"
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Open the folder the gallery was made from, then paste the client's favourites. " +
                   "Matching photos are marked as picked and tagged \"client pick\".",
            TextWrapping = TextWrapping.Wrap, Width = 420
        });
        panel.Children.Add(box);
        if (await Dialog("Import client favourites", panel, "Mark as picked").ShowAsync() != ContentDialogResult.Primary) return;

        var (matched, missing) = ProofingGallery.MatchPicks(box.Text, _all);
        if (matched.Count > 0)
        {
            await SetFlagAsync(matched, 1);
            await ApplyToItemsAsync(matched, i => { if (!i.Tags.Contains("client pick")) i.Tags.Add("client pick"); });
        }
        Status.Severity = missing.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        Status.Title = $"Marked {matched.Count} favourites as picked";
        Status.Message = missing.Count == 0 ? "Search \"tag:client pick\" to see them across your library."
                       : $"Not found in this folder: {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? "…" : string.Empty)}";
        Status.IsOpen = true;
    }

    // ================= Shared helpers =================

    private ContentDialog Dialog(string title, UIElement content, string primary) => new()
    {
        XamlRoot = Root.XamlRoot,
        RequestedTheme = Root.ActualTheme,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary
    };

    /// <summary>Runs a long job with the progress bar and Cancel button under the grid.</summary>
    private async Task RunJobAsync(string title, int total, string? outputFile,
                                   Func<IProgress<int>, CancellationToken, Task> job, string doneMessage)
    {
        if (_exportCts != null) { ShowError("Please wait", "Another export is still running."); return; }
        _exportCts = new CancellationTokenSource();
        _lastOutputFile = outputFile;
        if (outputFile != null) _lastExportFolder = Path.GetDirectoryName(outputFile);

        ExportBar.Severity = InfoBarSeverity.Informational;
        ExportBar.Title = title;
        ExportBar.Message = string.Empty;
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Maximum = Math.Max(1, total);
        ExportProgress.Value = 0;
        ExportActionButton.Content = "Cancel";
        ExportBar.IsOpen = true;

        try
        {
            await job(new Progress<int>(done => { ExportProgress.Value = done; ExportBar.Title = $"{title} · {done} of {total}"; }), _exportCts.Token);
            ExportBar.Severity = InfoBarSeverity.Success;
            ExportBar.Title = doneMessage;
            ExportBar.Message = _lastOutputFile ?? _lastExportFolder ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            ExportBar.Severity = InfoBarSeverity.Warning;
            ExportBar.Title = "Cancelled";
        }
        catch (Exception ex)
        {
            ExportBar.Severity = InfoBarSeverity.Error;
            ExportBar.Title = "Something went wrong";
            ExportBar.Message = ex.Message;
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            ExportProgress.Visibility = Visibility.Collapsed;
            ExportActionButton.Content = _lastOutputFile != null ? "Open" : "Open folder";
        }
    }
}
