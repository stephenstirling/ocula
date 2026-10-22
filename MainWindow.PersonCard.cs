using System;
using System.Linq;
using System.Threading.Tasks;
using Ocula.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ocula;

/// <summary>
/// The card over a person's photos. When the grid shows one person (People > "Show these photos", a Porta link, or "Who matched"), a card
/// above the photos shows what the saved Wikidata card says (born, occupation, and so on), or, if they haven't been looked up, says so and
/// opens People on them, where "Look up on Wikidata" is.
/// </summary>
public sealed partial class MainWindow
{
    private long? _headerPersonId;
    private string? _headerPersonName;

    private void HidePersonHeader()
    {
        _headerPersonId = null;
        _headerPersonName = null;
        PersonHeaderCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>Reloads the card if it is the one on show (the People window just changed it).</summary>
    public async Task RefreshPersonHeaderAsync(long personId)
    {
        if (_headerPersonId != personId) return;
        var name = _headerPersonName;
        // A person who is no longer named (or gone) has no card to show.
        if (name == null || await _index.FindPersonByIdAsync(personId) is not { } current) { HidePersonHeader(); return; }
        await ShowPersonHeaderAsync(personId, current);
    }

    private async Task ShowPersonHeaderAsync(long personId, string name)
    {
        _headerPersonId = personId;
        _headerPersonName = name;
        var card = await _index.GetPersonCardAsync(personId);
        var avatar = await _index.GetPersonAvatarAsync(personId);
        if (_headerPersonId != personId) return;   // another view opened while this was loading

        PersonHeaderPicture.ProfilePicture = avatar == null ? null : await ThumbnailService.ToBitmapAsync(avatar);
        PersonHeaderRows.Children.Clear();

        if (card == null)
        {
            PersonHeaderDescription.Text = string.Empty;
            PersonHeaderDescription.Visibility = Visibility.Collapsed;
            PersonHeaderNote.Text = FaceIndexer.Enabled && AppSettings.OnlinePersonLookup
                ? $"No person card for {name} yet. Look them up on Wikidata in People to see where and when they were born, what they do, and more. Only the name is sent."
                : $"No person card for {name}.";
            PersonHeaderButton.Content = "Look up on Wikidata";
            PersonHeaderButton.Visibility = FaceIndexer.Enabled && AppSettings.OnlinePersonLookup ? Visibility.Visible : Visibility.Collapsed;
            PersonHeaderLink.Visibility = Visibility.Collapsed;
        }
        else
        {
            PersonHeaderDescription.Text = card.Description ?? string.Empty;
            PersonHeaderDescription.Visibility = string.IsNullOrEmpty(card.Description) ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (label, value) in card.Rows())
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = label, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                PersonHeaderRows.Children.Add(row);
            }
            PersonHeaderNote.Text = $"From Wikidata (CC0), retrieved {card.RetrievedAt.LocalDateTime:MMMM d, yyyy}. Only what Wikidata lists; it can be out of date.";
            PersonHeaderButton.Content = "Open in People";
            PersonHeaderButton.Visibility = Visibility.Visible;
            PersonHeaderLink.NavigateUri = new Uri("https://www.wikidata.org/wiki/" + card.WikidataId);
            PersonHeaderLink.Visibility = Visibility.Visible;
        }
        PersonHeaderCard.Visibility = Visibility.Visible;
    }

    private void PersonHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_headerPersonId is not long id) return;
        OpenPeople();
        _people?.ShowPerson(id);
    }
}
