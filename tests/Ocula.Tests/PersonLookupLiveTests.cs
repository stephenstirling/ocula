using System.Globalization;
using System.Text;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// Talks to the real Wikidata. Explicit, so a normal test run never touches the network: run it by hand with
/// <c>dotnet test -p:Platform=x64 -- --explicit only --filter-class Ocula.Tests.PersonLookupLiveTests</c>.
/// Wikidata is edited all the time, so these check shapes and a few long-settled facts, not exact card contents.
/// </summary>
public class PersonLookupLiveTests
{
    [Fact(Explicit = true)]
    public async Task A_famous_person_can_be_found_and_read()
    {
        var lookup = new PersonLookup(PersonLookup.CreateClient());

        var matches = await lookup.SearchPeopleAsync("Shakira");

        // The search also finds a given name, an album and a crater; only the person should be offered.
        var person = Assert.Single(matches, m => m.Description?.Contains("singer", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(matches, m => m.Description?.Contains("album") == true || m.Description?.Contains("given name") == true);

        var card = await lookup.GetFactsAsync(person.Id, DateTimeOffset.Now);

        Assert.Equal("Shakira", card.Name);                       // stored only under the "mul" label
        Assert.Equal("1977-02-02", card.BirthDate);
        Assert.Contains("Colombia", card.BirthPlace);
        Assert.Contains("singer", card.Occupations);
        Assert.Contains("Spanish", card.Languages);
        Assert.Equal("female", card.Gender);
        Assert.NotNull(card.Children);
        Assert.NotNull(card.HeightCm);
        Assert.NotNull(card.FullName);
        Assert.NotEmpty(card.Partners);
        // Left in the temp folder so the whole card can be looked at.
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ocula-live-card.json"), card.ToJson());
    }

    /// <summary>
    /// Reads a few real people from Wikidata and writes the SQL that would store their cards (the same columns as
    /// <c>IndexService.SavePersonCardAsync</c>) to the temp folder, for trying the person search on a real library by hand.
    /// Pass the group ids to use as <c>OCULA_LIVE_GROUPS=12,34,56</c> (one per name, in order: Shakira, Taylor Swift, Hilary Duff).
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Write_sql_for_a_few_real_cards()
    {
        var groups = (Environment.GetEnvironmentVariable("OCULA_LIVE_GROUPS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.SkipUnless(groups.Length == 3, "Set OCULA_LIVE_GROUPS to three people ids.");
        var names = new[] { ("Shakira", "singer"), ("Taylor Swift", "singer"), ("Hilary Duff", "actress") };
        var lookup = new PersonLookup(PersonLookup.CreateClient());
        var sql = new StringBuilder();

        string Q(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";
        string Join(IEnumerable<string> items) => items.Any() ? Q(string.Join(" | ", items)) : "NULL";

        for (var i = 0; i < names.Length; i++)
        {
            var (name, wanted) = names[i];
            var matches = await lookup.SearchPeopleAsync(name);
            var chosen = matches.First(m => m.Description?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true);
            var facts = await lookup.GetFactsAsync(chosen.Id, DateTimeOffset.Now);
            var range = facts.BornRange;
            sql.AppendLine($"UPDATE people SET name = {Q(name)} WHERE id = {groups[i]};");
            sql.AppendLine("INSERT OR REPLACE INTO person_cards (person_id, wikidata_id, birth_date, birth_year, birth_place, occupations, spouse, children, height_cm, languages, retrieved, json, full_name, gender, death_date, born_min, born_max) VALUES (" +
                $"{groups[i]}, {Q(facts.WikidataId)}, {Q(facts.BirthDate)}, {(facts.BirthYear?.ToString() ?? "NULL")}, {Join(facts.BirthPlaceChain)}, {Join(facts.Occupations)}, " +
                $"{Join(facts.Spouses.Select(s => s.Name))}, {(facts.Children?.ToString() ?? "NULL")}, {(facts.HeightCm?.ToString(CultureInfo.InvariantCulture) ?? "NULL")}, {Join(facts.Languages)}, " +
                $"{Q(facts.RetrievedAt.ToString("o", CultureInfo.InvariantCulture))}, {Q(facts.ToJson())}, {Q(facts.FullName)}, {Q(facts.Gender)}, {Q(facts.DeathDate)}, {Q(range?.Min)}, {Q(range?.Max)});");
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ocula-live-cards.sql"), sql.ToString());
    }
}
