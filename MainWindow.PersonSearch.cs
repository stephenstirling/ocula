using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ocula.Models;
using Ocula.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ocula;

/// <summary>
/// Searching by person details ("taller than 5'3\"", "born in Texas"). Under the title the search shows how it read the question
/// (chips you can remove) and who matched, built only from the person cards saved on this PC. People with no card are counted, never guessed.
/// </summary>
public sealed partial class MainWindow
{
    private SearchQuery? _personQuery;

    private void HidePersonSearch()
    {
        _personQuery = null;
        PersonSearchPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows how the search was understood and who matched, when it asked about people.</summary>
    private async Task ShowPersonSearchAsync(SearchQuery query)
    {
        if (query.PersonClauses.Count == 0)
        {
            HidePersonSearch();
            return;
        }
        _personQuery = query;
        var report = await _index.GetPersonSearchReportAsync(query.PersonClauses, DateOnly.FromDateTime(DateTime.Today));
        if (!ReferenceEquals(_personQuery, query)) return;   // another search started while this was loading

        // "Understood as" chips: each one can be removed, which takes its words out of the search box and searches again.
        PersonChips.Children.Clear();
        PersonChips.Children.Add(Chip("People with a person card", null));
        foreach (var clause in query.PersonClauses)
            PersonChips.Children.Add(Chip(clause.Label, clause));

        // Who matched.
        PersonWhoRows.Children.Clear();
        var matches = report.People.Where(p => p.Matched).ToList();
        var others = report.People.Where(p => !p.Matched).ToList();
        PersonWho.Header = $"Who matched · {matches.Count:N0} {(matches.Count == 1 ? "person" : "people")}";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PersonWho, (string)PersonWho.Header);   // screen readers keep the old name otherwise
        foreach (var person in matches.Take(12)) PersonWhoRows.Children.Add(PersonRow(person));
        if (matches.Count > 12) PersonWhoRows.Children.Add(Note($"and {matches.Count - 12:N0} more"));
        foreach (var person in others.Take(4)) PersonWhoRows.Children.Add(PersonRow(person));
        if (others.Count > 4) PersonWhoRows.Children.Add(Note($"and {others.Count - 4:N0} more who don't match"));
        if (report.People.Count == 0) PersonWhoRows.Children.Add(Note("No saved person card could answer this."));

        // What could not be counted.
        var notes = new List<string>();
        foreach (var (field, count) in report.Missing.OrderBy(m => m.Key))
            notes.Add($"{count:N0} {(count == 1 ? "person has" : "people have")} no {field} on record.");
        if (report.NoCard > 0)
            notes.Add(report.NoCard == 1 ? "1 named group hasn't been looked up yet." : $"{report.NoCard:N0} named groups haven't been looked up yet.");
        if (report.NamedGroups == 0)
            notes.Add("No groups are named yet. Name people in People, then look them up on Wikidata.");
        PersonNotCountedRows.Children.Clear();
        foreach (var note in notes) PersonNotCountedRows.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
        PersonNotCounted.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PersonLookThemUp.Visibility = report.NoCard > 0 || report.Missing.Count > 0 || report.NamedGroups == 0 ? Visibility.Visible : Visibility.Collapsed;

        PersonSearchPanel.Visibility = Visibility.Visible;
    }

    private FrameworkElement Chip(string text, PersonClause? removable)
    {
        if (removable == null)
        {
            return new Border
            {
                Padding = new Thickness(10, 3, 10, 3),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
            };
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = row,
            Padding = new Thickness(10, 3, 8, 3),
            CornerRadius = new CornerRadius(14),
            Tag = removable
        };
        ToolTipService.SetToolTip(button, "Remove this from the search");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{text}. Remove from the search");
        button.Click += async (_, _) => await RemovePersonChipAsync(removable);
        return button;
    }

    private async Task RemovePersonChipAsync(PersonClause clause)
    {
        var text = SearchBox.Text;
        var cut = PersonQuery.Without(text, clause, PersonQuery.KnownOccupations);
        SearchBox.Text = cut;
        if (cut.Length == 0 && _folder == null)
        {
            // That was the last thing in the search: show no results rather than the old ones under an empty box.
            await OpenRowsAsync(new List<IndexRow>(), ViewKind.Search, 0, "Search results", string.Empty);
            return;
        }
        await RunLibrarySearchAsync(cut);
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        FontSize = 12
    };

    private UIElement PersonRow(IndexService.PersonMatch person)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 0 };
        var name = new HyperlinkButton { Content = person.Name, Padding = new Thickness(0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        ToolTipService.SetToolTip(name, "Show this person's photos");
        name.Click += async (_, _) =>
        {
            var paths = await _index.GetPersonPhotoPathsAsync(person.Id);
            await ShowPhotosAsync(person.Name, await _index.GetRowsAsync(paths));
        };
        text.Children.Add(name);
        var detail = string.Join(" · ", new[] { person.Detail, person.Photos == 1 ? "1 photo" : $"{person.Photos:N0} photos" }.Where(s => s.Length > 0));
        text.Children.Add(Note(detail));
        grid.Children.Add(text);

        var verdict = new TextBlock
        {
            Text = person.Matched ? "Match" : "Not a match",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = person.Matched
                ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetColumn(verdict, 1);
        grid.Children.Add(verdict);
        if (!person.Matched) grid.Opacity = 0.75;
        return grid;
    }

    private void PersonLookThemUp_Click(object sender, RoutedEventArgs e) => OpenPeople();
}
