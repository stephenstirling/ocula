using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
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
    public string Title => Name ?? "Name this person";
    public FontStyle TitleStyle => Name == null ? FontStyle.Italic : FontStyle.Normal;
    public string CountText => FaceCount == 1 ? "1 photo" : $"{FaceCount:N0} photos";

    /// <summary>What screen readers announce for a card (list items fall back to ToString()).</summary>
    public override string ToString() => $"{Name ?? "Unnamed group"}, {CountText}";
}

/// <summary>One face crop inside a group.</summary>
public sealed class FaceCard
{
    public long Id { get; init; }
    public string Path { get; init; } = string.Empty;
    public BitmapImage? Image { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);

    public override string ToString() => $"Face in {FileName}";
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

    /// <summary>The group whose details are showing, so a refresh that re-selects it can leave them alone.</summary>
    private long? _detailId;

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

            var keep = select ?? _detailId ?? Selected?.Id;
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

            // The group in the details may have been merged away or deleted; otherwise show its current name.
            if (_detailId is long shown)
            {
                if (_cards.FirstOrDefault(c => c.Id == shown) is { } card) DetailTitle.Text = card.Name ?? "Unnamed group";
                else { Detail.Visibility = Visibility.Collapsed; _detailId = null; }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void Groups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var person = Selected;

        // While photos are being scanned the list is rebuilt every few seconds: the selection goes away
        // and comes back. That must not reset the details, or a name being typed would be wiped.
        if (_refreshing && (person == null || person.Id == _detailId)) return;

        if (person == null)
        {
            Detail.Visibility = Visibility.Collapsed;
            _detailId = null;
            return;
        }

        _detailId = person.Id;
        Detail.Visibility = Visibility.Visible;
        DetailTitle.Text = person.Name ?? "Unnamed group";
        NameBox.Text = person.Name ?? string.Empty;
        SaveNote.Text = string.Empty;
        _ = LoadCardAsync(person.Id, person.Name);

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
        await LoadCardAsync(survivor, name.Length == 0 ? null : name);   // a new name removes the old name's card
    }

    // ---------- Person card (Wikidata) ----------

    /// <summary>Shows the saved card for a group, or the hint and the look-up button when it has none.</summary>
    private async Task LoadCardAsync(long personId, string? name)
    {
        var facts = await _main.Library.GetPersonCardAsync(personId);
        if (_detailId != personId && Selected?.Id != personId) return;   // moved on while loading
        ShowCard(facts, name);
    }

    private void ShowCard(PersonFacts? facts, string? name)
    {
        CardRows.Children.Clear();
        CardNote.Text = string.Empty;

        if (facts == null)
        {
            CardHint.Text = name == null
                ? "Name this group first. Then you can look the person up on Wikidata."
                : "Optional. Ocula can look up facts about “" + name + "” on Wikidata and keep them here. Only the name is sent.";
            CardCredit.Visibility = Visibility.Collapsed;
            CardLink.Visibility = Visibility.Collapsed;
            RemoveCardButton.Visibility = Visibility.Collapsed;
            LookupButton.Content = "Look up on Wikidata";
            LookupButton.IsEnabled = name != null;
            return;
        }

        CardHint.Text = facts.Description is { Length: > 0 } d ? $"{facts.Name}: {d}" : facts.Name;

        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = new StackPanel { Spacing = 0 };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            CardRows.Children.Add(row);
        }

        Row("Full name", facts.FullName);
        Row("Born", Join(" · ", facts.BirthDateText, facts.BirthPlace));
        Row("Died", facts.DeathDateText);
        Row("Occupation", string.Join(", ", facts.Occupations));
        Row(facts.Spouses.Count > 1 ? "Spouses" : "Spouse", string.Join(", ", facts.Spouses.Select(s => s.Display)));
        Row("Former spouses", string.Join(", ", facts.FormerSpouses.Select(s => s.Display)));
        Row("Partners", string.Join(", ", facts.Partners.Select(s => s.Display)));
        Row("Children", facts.ChildrenText);
        Row("Height", facts.HeightText);
        Row("Languages", string.Join(", ", facts.Languages));
        if (CardRows.Children.Count == 0) Row("Wikidata has", "no facts about this person that Ocula shows.");

        CardCredit.Text = $"From Wikidata (CC0), retrieved {facts.RetrievedAt.LocalDateTime:MMMM d, yyyy}. It is only what Wikidata lists, " +
                          "can be out of date, and often leaves things out. A spouse is one with no end date there.";
        CardCredit.Visibility = Visibility.Visible;
        CardLink.NavigateUri = new Uri("https://www.wikidata.org/wiki/" + facts.WikidataId);
        CardLink.Visibility = Visibility.Visible;
        RemoveCardButton.Visibility = Visibility.Visible;
        LookupButton.Content = "Look up again";
        LookupButton.IsEnabled = name != null;
    }

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static readonly System.Net.Http.HttpClient WikidataClient = PersonLookup.CreateClient();

    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        var person = Selected;
        if (person?.Name is not { } name) return;
        if (!AppSettings.OnlinePersonLookup)
        {
            CardNote.Text = "Looking people up is turned off in Settings > Privacy.";
            return;
        }

        LookupButton.IsEnabled = false;
        try
        {
            var lookup = new PersonLookup(WikidataClient);
            CardNote.Text = "Searching Wikidata…";
            List<WikidataCandidate> matches;
            try
            {
                matches = await lookup.SearchPeopleAsync(name);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                DiagnosticLog.Write("Wikidata search failed: " + ex.GetType().Name);
                CardNote.Text = "Couldn't reach Wikidata. Check your connection and try again.";
                return;
            }
            if (Selected?.Id != person.Id) return;
            if (matches.Count == 0)
            {
                CardNote.Text = $"Wikidata has no person called “{name}”. Check the spelling of the name, or leave this group without a card.";
                return;
            }
            CardNote.Text = string.Empty;

            var chosen = await PickAsync(name, matches);
            if (chosen == null) return;

            CardNote.Text = "Reading Wikidata…";
            PersonFacts facts;
            try
            {
                facts = await lookup.GetFactsAsync(chosen.Id, DateTimeOffset.Now);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                           or KeyNotFoundException or InvalidOperationException)
            {
                DiagnosticLog.Write("Wikidata read failed: " + ex.GetType().Name);
                CardNote.Text = "Couldn't read that person from Wikidata. Try again in a moment.";
                return;
            }

            await _main.Library.SavePersonCardAsync(person.Id, facts);
            if (Selected?.Id == person.Id) ShowCard(facts, person.Name);
            CardNote.Text = "Saved on this PC.";
            await OfferNameTagsAsync(person, facts);
        }
        finally
        {
            LookupButton.IsEnabled = Selected?.Name != null;
        }
    }

    /// <summary>Lists the matches (many people share a name) and returns the one the user picks.</summary>
    private async Task<WikidataCandidate?> PickAsync(string name, List<WikidataCandidate> matches)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
        foreach (var match in matches)
        {
            var item = new StackPanel { Spacing = 0, Padding = new Thickness(0, 4, 0, 4), Tag = match };
            item.Children.Add(new TextBlock { Text = match.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            item.Children.Add(new TextBlock
            {
                Text = match.Description ?? "No description on Wikidata",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            list.Items.Add(item);
        }
        list.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Many people share a name. Pick the right one by its description. Only the name “" + name + "” was sent to Wikidata.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            Title = "Which one is this?",
            Content = panel,
            PrimaryButtonText = "Use this person",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem != null;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return (list.SelectedItem as StackPanel)?.Tag as WikidataCandidate;
    }

    /// <summary>
    /// Wikidata may know the person by a longer name than the group's. Offers those as tag chips, none ticked: only ticked ones are
    /// written into photos, and the group's own name is handled by "Add the name as a tag" above.
    /// </summary>
    private async Task OfferNameTagsAsync(PersonCard person, PersonFacts facts)
    {
        var names = new[] { facts.FullName, facts.Name }
            .Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, person.Name, StringComparison.OrdinalIgnoreCase))
            .Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return;

        var boxes = names.Select(n => new CheckBox { Content = n, IsChecked = false }).ToList();
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "Wikidata also knows this person by the names below. Tick any you want added as tags to this person's photos. " +
                   "Nothing is added unless you tick it.",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var box in boxes) panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "Add other names as tags?",
            Content = panel,
            PrimaryButtonText = "Add ticked names",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var ticked = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
        if (ticked.Count == 0) return;
        var paths = await _main.Library.GetPersonPhotoPathsAsync(person.Id);
        var rows = await _main.Library.GetRowsAsync(paths);
        CardNote.Text = $"Adding {ticked.Count} tag{(ticked.Count == 1 ? "" : "s")} to {rows.Count:N0} photos…";
        foreach (var tag in ticked) await _main.TagPhotosAsync(rows, tag);
        CardNote.Text = "Tags added.";
    }

    private async void RemoveCard_Click(object sender, RoutedEventArgs e)
    {
        var person = Selected;
        if (person == null) return;
        await _main.Library.DeletePersonCardAsync(person.Id);
        ShowCard(null, person.Name);
        CardNote.Text = "Card removed. Tags already added to photos are not touched.";
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
        _detailId = null;
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
        _detailId = null;
        await RefreshAsync();
    }
}
