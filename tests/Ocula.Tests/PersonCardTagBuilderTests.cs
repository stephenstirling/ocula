using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

public sealed class PersonCardTagBuilderTests
{
    [Fact]
    public void Builds_structured_keywords_from_saved_person_facts()
    {
        var facts = new PersonFacts
        {
            Name = "Katy Perry",
            BirthDate = "1984-10-25",
            BirthPlace = "Santa Barbara, California, United States",
            DeathDate = null,
            HeightCm = 170.8,
            BraSize = "32D",
            Occupations = new() { "singer", "songwriter" },
            Spouses = new() { new Relation("Russell Brand", null, 2010, 2012) },
            ChildrenDetails = new()
            {
                new ChildDetail("Child One", null, null),
                new ChildDetail("Child Two", null, null)
            },
            Languages = new() { "English" }
        };

        Assert.Equal(new[]
        {
            "person:Katy Perry",
            "born:1984-10-25",
            "born-place:Santa Barbara, California, United States",
            "height-cm:170.8",
            "bra:32D",
            "occupation:singer",
            "occupation:songwriter",
            "spouse:Russell Brand",
            "child:Child One",
            "child:Child Two",
            "language:English"
        }, PersonCardTagBuilder.FromFacts(facts));
    }

    [Fact]
    public void Empty_facts_produce_no_keywords()
    {
        Assert.Empty(PersonCardTagBuilder.FromFacts(new PersonFacts()));
    }

    [Fact]
    public void Merge_is_idempotent_and_case_insensitive()
    {
        var tags = new List<string> { "Katy Perry", "bra:32D" };
        var additions = new[] { "Katy Perry", "person:Katy Perry", "BRA:32d", "born:1984-10-25" };

        PersonCardTagBuilder.MergeInto(tags, additions);
        PersonCardTagBuilder.MergeInto(tags, additions);

        Assert.Equal(new[] { "Katy Perry", "bra:32D", "person:Katy Perry", "born:1984-10-25" }, tags);
    }
}
