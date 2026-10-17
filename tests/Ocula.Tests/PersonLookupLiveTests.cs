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
        Assert.NotNull(card.Children);
        Assert.NotNull(card.HeightCm);
        Assert.NotNull(card.FullName);
        Assert.NotEmpty(card.Partners);
        // Left in the temp folder so the whole card can be looked at.
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ocula-live-card.json"), card.ToJson());
    }
}
