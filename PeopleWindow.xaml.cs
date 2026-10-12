using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using FontStyle = Windows.UI.Text.FontStyle;

namespace Ocula;

/// <summary>One group of faces in the People grid.</summary>
public sealed class PersonCard
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public int FaceCount { get; init; }
    public BitmapImage? Avatar { get; init; }
    /// <summary>The saved person card (JSON), if this person was looked up on Wikidata.</summary>
    public string? CardJson { get; init; }
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
    private readonly ObservableCollection<TagChip> _tagChips = new();

    // The person card shown on the right: the saved one, or the one just looked up.
    private PersonCardData? _card;
    // True once a lookup result (or "None of these") was chosen for the selected group; Save then stores that choice.
    private bool _cardChosen;
    private CancellationTokenSource? _lookup;
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
        TagChips.ItemsSource = _tagChips;

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
                    Avatar = person.Avatar == null ? null : await ThumbnailService.ToBitmapAsync(person.Avatar),
                    CardJson = person.Card,
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
        CancelLookup();
        _card = PersonCardData.FromJson(person.CardJson);
        _cardChosen = false;
        MatchList.Children.Clear();
        MatchBorder.Visibility = Visibility.Collapsed;
        ShowLookupNote(null);
        _tagChips.Clear();
        NameBox.Text = person.Name ?? string.Empty;
        UpdateNameUI();
        ShowCard();
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
        var renamed = !string.Equals(name, person.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var survivor = await _main.Library.NamePersonAsync(person.Id, name);
        var merged = survivor != person.Id;

        // The card goes with the name: a looked-up card (or "None of these") is saved; a new name
        // without a lookup drops the old card, since it was about someone else.
        if (_cardChosen || name.Length == 0)
            await _main.Library.SetPersonCardAsync(survivor, name.Length == 0 ? null : _card?.ToJson());
        else if (renamed && !merged)
            await _main.Library.SetPersonCardAsync(survivor, null);

        var tags = name.Length == 0 ? new List<string>() : _tagChips.Where(c => c.On).Select(c => c.Label).ToList();
        if (tags.Count > 0)
        {
            var paths = await _main.Library.GetPersonPhotoPathsAsync(survivor);
            var rows = await _main.Library.GetRowsAsync(paths);
            SaveNote.Text = $"Adding {string.Join(" and ", tags.Select(t => $"“{t}”"))} to {rows.Count:N0} photos…";
            foreach (var tag in tags) await _main.TagPhotosAsync(rows, tag);
        }

        SaveNote.Text = name.Length == 0 ? "Name removed."
                      : merged ? $"Joined the existing “{name}” group."
                      : "Saved.";
        await RefreshAsync(survivor);
    }

    // ================= Look up on Wikidata =================

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateNameUI();

    /// <summary>The lookup button and tag chips follow the name being typed.</summary>
    private void UpdateNameUI()
    {
        var name = NameBox.Text.Trim();
        LookupButton.Content = name.Length == 0 ? "Look up" : $"Look up “{name}”";
        LookupButton.IsEnabled = name.Length > 0;

        var ticked = _tagChips.Where(c => c.On).Select(c => c.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _tagChips.Clear();
        var names = PersonCardData.TagNames(name, _card);
        for (var i = 0; i < names.Count; i++)
            _tagChips.Add(new TagChip { Label = names[i], On = i == 0 || ticked.Contains(names[i]) });
        TagsPanel.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        var cancel = StartLookup();
        ShowLookupNote(null);
        MatchList.Children.Clear();
        MatchBorder.Visibility = Visibility.Collapsed;

        try
        {
            var matches = await PersonLookup.SearchAsync(name, cancel);
            if (cancel.IsCancellationRequested) return;

            foreach (var match in matches)
            {
                var text = new StackPanel { Spacing = 0 };
                text.Children.Add(new TextBlock { Text = match.Name, FontWeight = FontWeights.SemiBold });
                if (match.Description != null)
                    text.Children.Add(new TextBlock
                    {
                        Text = match.Description, TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    });
                AddMatchChoice(text, match);
            }
            AddMatchChoice(new TextBlock { Text = "None of these, just use the name", TextWrapping = TextWrapping.Wrap }, null);
            MatchBorder.Visibility = Visibility.Visible;
            if (matches.Count == 0) ShowLookupNote($"Wikidata has no person called “{name}”.");
        }
        catch (Exception ex)
        {
            // async void: nothing may escape, a cancelled lookup included.
            if (!cancel.IsCancellationRequested)
                ShowLookupNote($"Couldn't reach Wikidata ({ex.Message}). Check the internet connection and try again.");
        }
        finally
        {
            if (!cancel.IsCancellationRequested) LookupRing.IsActive = false;
        }
    }

    private void AddMatchChoice(UIElement content, PersonMatch? match)
    {
        var choice = new RadioButton { Content = content, GroupName = "PersonMatch", Tag = match, Padding = new Thickness(8, 6, 0, 6) };
        choice.Checked += MatchChoice_Checked;
        MatchList.Children.Add(choice);
    }

    /// <summary>Picking a person fetches their card; "None of these" keeps just the name.</summary>
    private async void MatchChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: var tag }) return;
        var cancel = StartLookup();
        ShowLookupNote(null);

        if (tag is not PersonMatch match)
        {
            LookupRing.IsActive = false;
            _card = null;
            _cardChosen = true;
            ShowCard();
            UpdateNameUI();
            return;
        }

        try
        {
            var card = await PersonLookup.GetCardAsync(match, cancel);
            if (cancel.IsCancellationRequested) return;
            _card = card;
            _cardChosen = true;
            ShowCard();
            UpdateNameUI();
        }
        catch (Exception ex)
        {
            if (!cancel.IsCancellationRequested)
                ShowLookupNote($"Couldn't get {match.Name}'s details from Wikidata ({ex.Message}).");
        }
        finally
        {
            if (!cancel.IsCancellationRequested) LookupRing.IsActive = false;
        }
    }

    /// <summary>Starts a lookup, cancelling any earlier one so a slow answer can't replace a newer one.</summary>
    private CancellationToken StartLookup()
    {
        CancelLookup();
        _lookup = new CancellationTokenSource();
        LookupRing.IsActive = true;
        return _lookup.Token;
    }

    private void CancelLookup()
    {
        _lookup?.Cancel();
        _lookup = null;
        LookupRing.IsActive = false;
    }

    private void ShowLookupNote(string? text)
    {
        LookupNote.Text = text ?? string.Empty;
        LookupNote.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowCard()
    {
        CardRows.Children.Clear();
        CardRows.RowDefinitions.Clear();
        if (_card == null)
        {
            CardBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var rows = _card.Rows();
        for (var i = 0; i < rows.Count; i++)
        {
            CardRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Foreground = secondary, TextWrapping = TextWrapping.Wrap };
            var value = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            CardRows.Children.Add(label);
            CardRows.Children.Add(value);
        }
        if (rows.Count == 0)
        {
            CardRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var none = new TextBlock { Text = $"Wikidata has no details for {_card.Name} yet.", TextWrapping = TextWrapping.Wrap };
            Grid.SetColumnSpan(none, 2);
            CardRows.Children.Add(none);
        }
        CardSource.Text = _card.SourceLine;
        CardBorder.Visibility = Visibility.Visible;
    }

    // ================= Group actions =================

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
