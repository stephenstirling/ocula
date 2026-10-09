using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ocula.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Ocula;

/// <summary>One group of faces in the People grid.</summary>
public sealed class PersonCard
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public int FaceCount { get; init; }
    public BitmapImage? Avatar { get; init; }
    public string Title => Name ?? "Name this person";
    public FontStyle TitleStyle => Name == null ? FontStyle.Italic : FontStyle.Normal;
    public string CountText => FaceCount == 1 ? "1 photo" : $"{FaceCount:N0} photos";
}

/// <summary>One face crop inside a group.</summary>
public sealed class FaceCard
{
    public long Id { get; init; }
    public string Path { get; init; } = string.Empty;
    public BitmapImage? Image { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// People: the groups of faces Ocula found, where you give each a name. Nothing here
/// identifies anyone; a group is just "faces that look alike", and the name is whatever you type.
/// </summary>
public sealed partial class PeopleWindow : Window
{
    private readonly MainWindow _main;
    private readonly ObservableCollection<PersonCard> _cards = new();
    private readonly ObservableCollection<FaceCard> _faces = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshTimer;
    private bool _refreshing;

    public PeopleWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 820));
        Root.RequestedTheme = _main.CurrentTheme;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;

        Groups.ItemsSource = _cards;
        FaceGrid.ItemsSource = _faces;

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(3);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        _ = RefreshAsync();
    }

    /// <summary>Called while photos are still being scanned, so new groups appear without constant redraws.</summary>
    public void RefreshSoon()
    {
        if (!_refreshTimer.IsRunning) _refreshTimer.Start();
    }

    private PersonCard? Selected => Groups.SelectedItem as PersonCard;

    private async Task RefreshAsync(long? select = null)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (!FaceIndexer.Enabled)
            {
                _cards.Clear();
                Detail.Visibility = Visibility.Collapsed;
                Banner.Severity = InfoBarSeverity.Warning;
                Banner.Title = "People is off.";
                Banner.Message = "Turn it on in Settings > People. Ocula downloads two small face models once, then works offline.";
                CountText.Text = string.Empty;
                return;
            }
            Banner.Severity = InfoBarSeverity.Informational;
            Banner.Title = "People is on.";
            Banner.Message = "Faces are matched on this PC only and nothing is shared. You choose every name.";

            var keep = select ?? Selected?.Id;
            var people = await _main.Library.GetPeopleAsync();

            _cards.Clear();
            foreach (var person in people)
            {
                _cards.Add(new PersonCard
                {
                    Id = person.Id,
                    Name = person.Name,
                    FaceCount = person.FaceCount,
                    Avatar = person.Avatar == null ? null : await ThumbnailService.ToBitmapAsync(person.Avatar)
                });
            }
            var unnamed = people.Count(p => p.Name == null);
            CountText.Text = people.Count == 0 ? "no faces found yet"
                           : $"{people.Count:N0} groups · {unnamed:N0} still need a name";

            if (keep != null) Groups.SelectedItem = _cards.FirstOrDefault(c => c.Id == keep);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void Groups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var person = Selected;
        if (person == null)
        {
            Detail.Visibility = Visibility.Collapsed;
            return;
        }

        Detail.Visibility = Visibility.Visible;
        DetailTitle.Text = person.Name ?? "Unnamed group";
        NameBox.Text = person.Name ?? string.Empty;
        SaveNote.Text = string.Empty;

        var faces = await _main.Library.GetFacesAsync(person.Id);
        if (Selected?.Id != person.Id) return; // moved on while loading
        _faces.Clear();
        foreach (var face in faces)
        {
            _faces.Add(new FaceCard
            {
                Id = face.Id,
                Path = face.Path,
                Image = face.Thumb == null ? null : await ThumbnailService.ToBitmapAsync(face.Thumb)
            });
        }
    }

    private async void NameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SaveNameAsync();
    }

    private async void SaveName_Click(object sender, RoutedEventArgs e) => await SaveNameAsync();

    private async Task SaveNameAsync()
    {
        var person = Selected;
        if (person == null) return;

        var name = NameBox.Text.Trim();
        var survivor = await _main.Library.NamePersonAsync(person.Id, name);
        var merged = survivor != person.Id;

        if (name.Length > 0 && TagToggle.IsOn)
        {
            var paths = await _main.Library.GetPersonPhotoPathsAsync(survivor);
            var rows = await _main.Library.GetRowsAsync(paths);
            SaveNote.Text = $"Adding “{name}” to {rows.Count:N0} photos…";
            await _main.TagPhotosAsync(rows, name);
        }

        SaveNote.Text = name.Length == 0 ? "Name removed."
                      : merged ? $"Joined the existing “{name}” group."
                      : "Saved.";
        await RefreshAsync(survivor);
    }

    private async void ShowPhotos_Click(object sender, RoutedEventArgs e)
    {
        var person = Selected;
        if (person == null) return;
        var paths = await _main.Library.GetPersonPhotoPathsAsync(person.Id);
        var rows = await _main.Library.GetRowsAsync(paths);
        await _main.ShowPhotosAsync(person.Name ?? "Unnamed group", rows);
    }

    private async void HideGroup_Click(object sender, RoutedEventArgs e)
    {
        var person = Selected;
        if (person == null) return;
        await _main.Library.HidePersonAsync(person.Id);
        Detail.Visibility = Visibility.Collapsed;
        await RefreshAsync();
    }

    private async void NotThisPerson_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FaceCard face }) return;
        var currentId = Selected?.Id;
        await _main.Library.MoveFaceToNewGroupAsync(face.Id);
        await RefreshAsync(currentId);
        if (Selected?.Id == currentId) Groups_SelectionChanged(Groups, null!);
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete all face data?",
            Content = "This removes every face group and name from Ocula, and Ocula will stop finding faces until you turn People on again. " +
                      "Your photos and any tags already added to them are not touched.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await _main.DeleteFaceDataAsync();
        Detail.Visibility = Visibility.Collapsed;
        await RefreshAsync();
    }
}
