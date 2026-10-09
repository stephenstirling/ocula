using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Storage.Pickers;

namespace Ocula;

/// <summary>A day's worth of photos on the card, shown as a group in the import grid.</summary>
public sealed class ImportDay : List<ImportItem>
{
    public ImportDay(IEnumerable<ImportItem> items) : base(items) { }
    public string Title { get; init; } = string.Empty;
}

public sealed partial class ImportWindow : Window
{
    private readonly MainWindow _main;
    private List<CardInfo> _cards = new();
    private List<ImportItem> _items = new();
    private CancellationTokenSource? _cts;
    private bool _importing;
    private bool _ready;

    public ImportWindow(MainWindow main, CardInfo? card)
    {
        InitializeComponent();
        _main = main;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 800));
        Root.RequestedTheme = main.CurrentTheme;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;

        // Last-used options.
        DestinationBox.Text = AppSettings.ImportDestination;
        FolderPatternBox.Text = AppSettings.ImportFolderPattern;
        RenameToggle.IsOn = AppSettings.ImportRename;
        NamePatternBox.Text = AppSettings.ImportNamePattern;
        BackupBox.Text = AppSettings.ImportBackupFolder;
        BackupToggle.IsOn = AppSettings.ImportBackupFolder.Length > 0 && AppSettings.ImportBackupOn;
        EjectToggle.IsOn = AppSettings.ImportEject;
        _ready = true;

        LoadCards(card);
    }

    // ================= Cards and photos =================

    private void LoadCards(CardInfo? preferred)
    {
        _cards = ImportService.FindCards();
        CardBox.ItemsSource = _cards.Select(c => $"{c.Label} ({c.Root.TrimEnd('\\')})").ToList();
        if (_cards.Count == 0)
        {
            CardSummary.Text = string.Empty;
            EmptyText.Text = "No memory card found. Insert a camera card (with a DCIM folder) and it will appear here.";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        var index = preferred == null ? 0 : Math.Max(0, _cards.FindIndex(c => c.Root == preferred.Root));
        CardBox.SelectedIndex = index;
    }

    private async void CardBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardBox.SelectedIndex < 0 || _importing) return;
        await ScanAsync(_cards[CardBox.SelectedIndex]);
    }

    private async Task ScanAsync(CardInfo card)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        ScanRing.IsActive = true;
        EmptyText.Visibility = Visibility.Collapsed;
        Photos.ItemsSource = null;
        CardSummary.Text = "Reading the card…";
        try
        {
            var items = await Task.Run(() => ImportService.Scan(card, token), token);
            var imported = await _main.Index.GetImportedAsync(items.Select(i => i.Key));
            foreach (var item in items) item.AlreadyImported = imported.Contains(item.Key);
            _items = items;
            ShowItems();
        }
        catch (OperationCanceledException)
        {
            // Another card was chosen.
        }
        catch (Exception ex)
        {
            CardSummary.Text = "Couldn't read the card: " + ex.Message;
        }
        finally
        {
            ScanRing.IsActive = false;
        }
    }

    private void ShowItems()
    {
        var imported = _items.Count(i => i.AlreadyImported);
        var visible = ShowImportedBox.IsChecked == true ? _items : _items.Where(i => !i.AlreadyImported).ToList();

        var days = visible.GroupBy(i => i.Taken.Date)
            .Select(g => new ImportDay(g) { Title = $"{g.Key:dddd d MMMM yyyy} · {g.Count()}" })
            .ToList();
        Photos.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = days }.View;
        foreach (var item in visible.Where(i => !i.AlreadyImported)) Photos.SelectedItems.Add(item);

        var size = DuplicateCandidate.FormatSize(_items.Sum(i => i.Size));
        CardSummary.Text = imported == 0
            ? $"{_items.Count} photos and videos · {size}"
            : $"{_items.Count - imported} new · {imported} already imported{(ShowImportedBox.IsChecked == true ? string.Empty : ", hidden")} · {size}";

        EmptyText.Text = _items.Count == 0 ? "This card has no photos or videos." : "Everything on this card has already been imported.";
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFooter();
        UpdatePreview();
    }

    private void ShowImported_Click(object sender, RoutedEventArgs e) => ShowItems();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => Photos.SelectAll();
    private void Photos_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFooter();

    private async void Photos_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ImportItem item || item.ThumbnailRequested) return;
        item.ThumbnailRequested = true;
        var bytes = await ThumbnailService.GetBytesAsync(item.MainFile, 256);
        if (bytes != null) item.Thumbnail = await ThumbnailService.ToBitmapAsync(bytes);
    }

    private List<ImportItem> Selected() => Photos.SelectedItems.OfType<ImportItem>().ToList();

    private void UpdateFooter()
    {
        if (_importing) return;
        var selected = Selected();
        FooterText.Text = selected.Count == 0
            ? "Nothing selected."
            : $"{selected.Count} items · {DuplicateCandidate.FormatSize(selected.Sum(i => i.Size))}" +
              (BackupToggle.IsOn ? " · plus a backup copy" : string.Empty);
        ImportButton.IsEnabled = selected.Count > 0;
        ImportButton.Content = selected.Count > 0 ? $"Import {selected.Count}" : "Import";
    }

    // ================= Options =================

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        NamePatternBox.IsEnabled = RenameToggle.IsOn;
        BackupBox.IsEnabled = BackupToggle.IsOn;
        UpdatePreview();
        UpdateFooter();
    }

    private void UpdatePreview()
    {
        var sample = Selected().FirstOrDefault() ?? _items.FirstOrDefault();
        if (sample == null) { PreviewText.Text = string.Empty; return; }
        var context = new RenameContext(sample.Stem, sample.Taken, sample.Camera, EventBox.Text, 1);
        var folder = RenamePattern.Folder(FolderPatternBox.Text, context);
        var name = RenameToggle.IsOn ? RenamePattern.FileName(NamePatternBox.Text, context) : sample.Stem;
        PreviewText.Text = $"Example: {folder}\\{name}{Path.GetExtension(sample.Files[0])}";
    }

    private async void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } path) DestinationBox.Text = path;
    }

    private async void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } path) { BackupBox.Text = path; BackupToggle.IsOn = true; }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    // ================= Importing =================

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        var destination = DestinationBox.Text.Trim();
        if (selected.Count == 0 || CardBox.SelectedIndex < 0) return;
        if (!Path.IsPathFullyQualified(destination))
        {
            FooterText.Text = "Choose a folder to save the photos to.";
            return;
        }
        var backup = BackupToggle.IsOn ? BackupBox.Text.Trim() : null;
        if (backup != null && !Path.IsPathFullyQualified(backup))
        {
            FooterText.Text = "Choose a backup folder, or turn the backup off.";
            return;
        }

        // Remember these options for next time.
        AppSettings.ImportDestination = destination;
        AppSettings.ImportFolderPattern = FolderPatternBox.Text;
        AppSettings.ImportRename = RenameToggle.IsOn;
        AppSettings.ImportNamePattern = NamePatternBox.Text;
        AppSettings.ImportBackupFolder = BackupBox.Text.Trim();
        AppSettings.ImportBackupOn = BackupToggle.IsOn;
        AppSettings.ImportEject = EjectToggle.IsOn;

        var tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var options = new ImportOptions(destination, FolderPatternBox.Text, EventBox.Text.Trim(), RenameToggle.IsOn,
                                        NamePatternBox.Text, tags, backup, EjectToggle.IsOn);
        var card = _cards[CardBox.SelectedIndex];

        _importing = true;
        _cts = new CancellationTokenSource();
        ImportButton.IsEnabled = false;
        CardBox.IsEnabled = false;
        CancelButton.Content = "Stop";
        ImportProgress.Visibility = Visibility.Visible;
        ImportProgress.Maximum = selected.Count;
        ImportProgress.Value = 0;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            ImportProgress.Value = p.Done;
            FooterText.Text = $"Importing {p.Done} of {p.Total}…";
        });

        try
        {
            var result = await ImportService.ImportAsync(selected, options, _main.Index, progress, _cts.Token);
            foreach (var item in selected) item.AlreadyImported = true;

            var message = $"Imported {result.Imported} of {selected.Count}.";
            if (result.Failed.Count > 0) message += " Couldn't import: " + string.Join("; ", result.Failed.Take(3));
            else if (options.Eject) message += ImportService.Eject(card.Root) ? " The card can be removed." : " Couldn't eject the card; eject it from File Explorer.";

            _importing = false;
            ShowItems();
            FooterText.Text = message;
            if (result.Folders.Count > 0) await _main.OpenAfterImportAsync(destination, result.Folders[0]);
        }
        catch (OperationCanceledException)
        {
            _importing = false;
            ShowItems();
            FooterText.Text = "Import stopped. Photos copied so far are kept; the rest are still on the card.";
        }
        finally
        {
            _importing = false;
            CardBox.IsEnabled = true;
            CancelButton.Content = "Close";
            ImportProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_importing) _cts?.Cancel();
        else Close();
    }
}
