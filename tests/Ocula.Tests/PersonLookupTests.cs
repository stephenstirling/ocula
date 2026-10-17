using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// "Look up on Wikidata" and the person card. The JSON below is written by hand in the shape Wikidata's API returns
/// (wbsearchentities, wbgetclaims, wbgetentities). No real Wikidata answer was used, so these show the card is built
/// correctly from that shape, not that every real item looks like it.
/// </summary>
public class PersonLookupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    // ---------- Wikidata JSON, by hand ----------

    private static string Item(string id) => "{\"entity-type\":\"item\",\"id\":\"" + id + "\"}";

    private static string Statement(string valueJson, string rank = "normal", string qualifiers = "") =>
        "{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":{\"value\":" + valueJson + "}},\"rank\":\"" + rank + "\""
        + (qualifiers.Length > 0 ? ",\"qualifiers\":{" + qualifiers + "}" : "") + "}";

    private static string Time(string iso, int precision) =>
        "{\"time\":\"" + iso + "\",\"precision\":" + precision + ",\"calendarmodel\":\"http://www.wikidata.org/entity/Q1985727\"}";

    private static string TimeQualifier(string property, string iso, int precision) =>
        "\"" + property + "\":[{\"snaktype\":\"value\",\"datavalue\":{\"value\":" + Time(iso, precision) + "}}]";

    private static string Quantity(string amount, string unitId = "1") =>
        "{\"amount\":\"" + amount + "\",\"unit\":\"" + (unitId == "1" ? "1" : "http://www.wikidata.org/entity/" + unitId) + "\"}";

    private static string Claims(params (string Property, string[] Statements)[] properties) =>
        "{" + string.Join(",", properties.Select(p => $"\"{p.Property}\":[{string.Join(",", p.Statements)}]")) + "}";

    private static JsonElement Entity(string id, string label, string? description, string claimsJson)
    {
        var json = "{\"id\":\"" + id + "\",\"labels\":{\"en\":{\"language\":\"en\",\"value\":\"" + label + "\"}},"
                 + "\"descriptions\":{\"en\":{\"language\":\"en\",\"value\":\"" + description + "\"}},"
                 + "\"claims\":" + claimsJson + "}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["Q100"] = "Singer",
        ["Q101"] = "Actor",
        ["Q200"] = "Alex Rivera",
        ["Q201"] = "Sam Okafor",
        ["Q202"] = "Pat Lindqvist",
        ["Q203"] = "Jo Marsh",
        ["Q300"] = "English",
        ["Q301"] = "Spanish",
        ["Q400"] = "Austin",
        ["Q401"] = "Travis County",
        ["Q402"] = "Texas",
        ["Q403"] = "United States",
    };

    private static readonly string[] Chain = { "Q400", "Q401", "Q402", "Q403" };

    /// <summary>A person with every attribute the card shows.</summary>
    private static JsonElement FullPerson() => Entity("Q42", "Sam Q. Example", "American singer and actor", Claims(
        ("P1477", new[] { Statement("""{"text":"Samantha Quinn Example","language":"en"}""") }),
        ("P569", new[] { Statement(Time("+1977-02-02T00:00:00Z", 11)) }),
        ("P19", new[] { Statement(Item("Q400")) }),
        ("P106", new[] { Statement(Item("Q100")), Statement(Item("Q101")) }),
        ("P26", new[]
        {
            Statement(Item("Q200"), qualifiers: TimeQualifier("P580", "+2009-06-01T00:00:00Z", 11) + "," + TimeQualifier("P582", "+2014-03-01T00:00:00Z", 10)),
            Statement(Item("Q201"), qualifiers: TimeQualifier("P580", "+2016-01-01T00:00:00Z", 9)),
        }),
        ("P451", new[] { Statement(Item("Q202"), qualifiers: TimeQualifier("P580", "+2003-01-01T00:00:00Z", 9) + "," + TimeQualifier("P582", "+2006-01-01T00:00:00Z", 9)) }),
        ("P1971", new[] { Statement(Quantity("+2")) }),
        ("P2048", new[] { Statement(Quantity("+1.6", "Q11573")) }),
        ("P1412", new[] { Statement(Item("Q300")), Statement(Item("Q301")) })));

    // ---------- The whole card ----------

    [Fact]
    public void A_person_with_every_attribute_gets_a_full_card()
    {
        var card = PersonLookup.Build(FullPerson(), Labels, Chain, Now);

        Assert.Equal("Q42", card.WikidataId);
        Assert.Equal("Sam Q. Example", card.Name);
        Assert.Equal("American singer and actor", card.Description);
        Assert.Equal("Samantha Quinn Example", card.FullName);
        Assert.Equal("1977-02-02", card.BirthDate);
        Assert.Equal(1977, card.BirthYear);
        Assert.Equal("Austin, Texas, United States", card.BirthPlace);
        Assert.Equal(Chain.Select(c => Labels[c]), card.BirthPlaceChain);
        Assert.Equal(new[] { "Singer", "Actor" }, card.Occupations);
        Assert.Equal(new[] { "Sam Okafor" }, card.Spouses.Select(s => s.Name));
        Assert.Equal(new[] { "Alex Rivera" }, card.FormerSpouses.Select(s => s.Name));
        Assert.Equal("2009–2014", card.FormerSpouses[0].Years);
        Assert.Equal("Alex Rivera (2009–2014)", card.FormerSpouses[0].Display);
        Assert.Equal(new[] { "Pat Lindqvist" }, card.Partners.Select(s => s.Name));
        Assert.Equal(2, card.Children);
        Assert.False(card.ChildrenFromList);
        Assert.Equal(160.0, card.HeightCm);
        Assert.Equal(new[] { "English", "Spanish" }, card.Languages);
        Assert.Equal(Now, card.RetrievedAt);
        Assert.Null(card.DeathDate);
    }

    [Fact]
    public void Every_filled_field_records_its_source()
    {
        var card = PersonLookup.Build(FullPerson(), Labels, Chain, Now);

        foreach (var field in new[] { "name", "fullName", "birthDate", "birthPlace", "occupations", "spouse", "formerSpouses", "partners", "children", "height", "languages" })
            Assert.Equal("Wikidata", card.Sources[field]);
    }

    [Fact]
    public void The_card_survives_being_saved_and_read_back()
    {
        var card = PersonLookup.Build(FullPerson(), Labels, Chain, Now);

        var back = PersonFacts.FromJson(card.ToJson());

        Assert.NotNull(back);
        Assert.Equal(card.ToJson(), back!.ToJson());
        Assert.Equal("Alex Rivera (2009–2014)", back.FormerSpouses[0].Display);
        Assert.Equal(160.0, back.HeightCm);
        Assert.Null(PersonFacts.FromJson("{not json"));
    }

    [Fact]
    public void An_item_with_nothing_but_a_name_gives_a_card_with_nothing_guessed()
    {
        var card = PersonLookup.Build(Entity("Q7", "Nobody Much", null, "{}"), new Dictionary<string, string>(), Array.Empty<string>(), Now);

        Assert.Equal("Nobody Much", card.Name);
        Assert.Null(card.FullName);
        Assert.Null(card.BirthDate);
        Assert.Null(card.BirthPlace);
        Assert.Empty(card.Occupations);
        Assert.Empty(card.Spouses);
        Assert.Empty(card.FormerSpouses);
        Assert.Empty(card.Partners);
        Assert.Null(card.Children);
        Assert.Null(card.HeightCm);
        Assert.Empty(card.Languages);
        Assert.Equal(new[] { "name" }, card.Sources.Keys);
    }

    // ---------- Dates ----------

    [Theory]
    [InlineData("+1977-02-02T00:00:00Z", 11, "1977-02-02")]
    [InlineData("+1977-02-00T00:00:00Z", 10, "1977-02")]
    [InlineData("+1977-00-00T00:00:00Z", 9, "1977")]
    [InlineData("+1977-02-02T00:00:00Z", 9, "1977")]     // the stated precision wins over the digits
    [InlineData("+1977-02-02T00:00:00Z", 10, "1977-02")]
    [InlineData("+1970-01-01T00:00:00Z", 8, null)]       // "the 1970s" is not a birth date
    [InlineData("-0300-01-01T00:00:00Z", 11, null)]      // before the common era
    [InlineData("nonsense", 11, null)]
    public void Dates_are_kept_as_precisely_as_wikidata_states_them(string time, int precision, string? expected)
    {
        var result = PersonLookup.ParseTime(JsonDocument.Parse(Time(time, precision)).RootElement);

        Assert.Equal(expected, result?.Text);
    }

    [Fact]
    public void Dates_are_shown_in_words()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            Assert.Equal("February 2, 1977", new PersonFacts { BirthDate = "1977-02-02" }.BirthDateText);
            Assert.Equal("February 1977", new PersonFacts { BirthDate = "1977-02" }.BirthDateText);
            Assert.Equal("1977", new PersonFacts { BirthDate = "1977" }.BirthDateText);
            Assert.Null(new PersonFacts().BirthDateText);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_death_date_is_kept_so_a_current_spouse_isnt_taken_at_face_value()
    {
        var entity = Entity("Q9", "Late Person", null, Claims(
            ("P569", new[] { Statement(Time("+1920-05-05T00:00:00Z", 11)) }),
            ("P570", new[] { Statement(Time("+1999-12-31T00:00:00Z", 11)) })));

        var card = PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now);

        Assert.Equal("1999-12-31", card.DeathDate);
    }

    // ---------- Relationships ----------

    [Fact]
    public void A_spouse_with_no_end_is_current_and_one_with_an_end_date_or_end_cause_is_former()
    {
        var entity = Entity("Q9", "P", null, Claims(("P26", new[]
        {
            Statement(Item("Q200")),                                                          // current
            Statement(Item("Q201"), qualifiers: TimeQualifier("P582", "+2001-01-01T00:00:00Z", 9)), // ended
            Statement(Item("Q202"), qualifiers: "\"P1534\":[{\"snaktype\":\"value\",\"datavalue\":{\"value\":" + Item("Q99") + "}}]"), // ended, no date
        })));

        var card = PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now);

        Assert.Equal(new[] { "Alex Rivera" }, card.Spouses.Select(s => s.Name));
        Assert.Equal(new[] { "Sam Okafor", "Pat Lindqvist" }, card.FormerSpouses.Select(s => s.Name).OrderBy(n => n == "Sam Okafor" ? 0 : 1));
        Assert.Equal(2001, card.FormerSpouses.Single(s => s.Name == "Sam Okafor").ToYear);
    }

    [Fact]
    public void Deprecated_statements_are_ignored()
    {
        var entity = Entity("Q9", "P", null, Claims(
            ("P26", new[] { Statement(Item("Q200"), rank: "deprecated") }),
            ("P106", new[] { Statement(Item("Q100"), rank: "deprecated"), Statement(Item("Q101")) }),
            ("P2048", new[] { Statement(Quantity("+190", "Q174728"), rank: "deprecated") }),
            ("P569", new[] { Statement(Time("+1960-01-01T00:00:00Z", 11), rank: "deprecated") })));

        var card = PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now);

        Assert.Empty(card.Spouses);
        Assert.Equal(new[] { "Actor" }, card.Occupations);
        Assert.Null(card.HeightCm);
        Assert.Null(card.BirthDate);
    }

    [Fact]
    public void A_preferred_statement_comes_first()
    {
        var entity = Entity("Q9", "P", null, Claims(
            ("P569", new[] { Statement(Time("+1970-01-01T00:00:00Z", 11)), Statement(Time("+1971-02-03T00:00:00Z", 11), rank: "preferred") })));

        Assert.Equal("1971-02-03", PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now).BirthDate);
    }

    [Fact]
    public void A_spouse_with_no_name_available_is_left_out_rather_than_shown_as_an_id()
    {
        var entity = Entity("Q9", "P", null, Claims(("P26", new[] { Statement(Item("Q777")) })));

        var card = PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now);

        Assert.Empty(card.Spouses);
    }

    [Fact]
    public void A_statement_whose_value_is_unknown_or_missing_is_ignored()
    {
        var entity = JsonDocument.Parse("""
            {"id":"Q9","labels":{"en":{"value":"P"}},"claims":{
              "P26":[{"mainsnak":{"snaktype":"somevalue"},"rank":"normal"}],
              "P569":[{"mainsnak":{"snaktype":"novalue"},"rank":"normal"}],
              "P106":[{"rank":"normal"}]}}
            """).RootElement.Clone();

        var card = PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now);

        Assert.Empty(card.Spouses);
        Assert.Null(card.BirthDate);
        Assert.Empty(card.Occupations);
    }

    // ---------- Children ----------

    [Fact]
    public void Children_are_a_number_only_the_stated_one_first_then_the_listed_ones()
    {
        var stated = Entity("Q9", "P", null, Claims(
            ("P1971", new[] { Statement(Quantity("+3")) }),
            ("P40", new[] { Statement(Item("Q200")) })));
        var listed = Entity("Q9", "P", null, Claims(("P40", new[] { Statement(Item("Q200")), Statement(Item("Q201")) })));
        var none = Entity("Q9", "P", null, "{}");

        var a = PersonLookup.Build(stated, Labels, Array.Empty<string>(), Now);
        var b = PersonLookup.Build(listed, Labels, Array.Empty<string>(), Now);
        var c = PersonLookup.Build(none, Labels, Array.Empty<string>(), Now);

        Assert.Equal((3, false, "3"), (a.Children!.Value, a.ChildrenFromList, a.ChildrenText));
        Assert.Equal((2, true, "2 (as listed)"), (b.Children!.Value, b.ChildrenFromList, b.ChildrenText));
        Assert.Null(c.Children);
        Assert.Null(c.ChildrenText);
    }

    [Fact]
    public void An_absurd_number_of_children_is_ignored()
    {
        var entity = Entity("Q9", "P", null, Claims(("P1971", new[] { Statement(Quantity("+5000")) })));

        Assert.Null(PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now).Children);
    }

    // ---------- Height ----------

    [Theory]
    [InlineData("+160", "Q174728", 160.0)]   // centimetres
    [InlineData("+1.6", "Q11573", 160.0)]    // metres
    [InlineData("+1600", "Q174789", 160.0)]  // millimetres
    [InlineData("+63", "Q218593", 160.0)]    // inches
    [InlineData("+5.25", "Q3710", 160.0)]    // feet
    [InlineData("+160", "Q99999999", null)]  // a unit Ocula doesn't know is not guessed at
    [InlineData("+160", "1", null)]          // no unit at all
    [InlineData("+16", "Q174728", null)]     // 16 cm is not a person's height
    [InlineData("+3", "Q11573", null)]       // 3 m neither
    public void Height_is_turned_into_centimetres_or_left_out(string amount, string unit, double? expected)
    {
        var entity = Entity("Q9", "P", null, Claims(("P2048", new[] { Statement(Quantity(amount, unit)) })));

        Assert.Equal(expected, PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now).HeightCm);
    }

    [Fact]
    public void Height_is_shown_in_feet_and_inches_and_centimetres()
    {
        Assert.Equal("5′3″ (160 cm)", new PersonFacts { HeightCm = 160 }.HeightText);
        Assert.Equal("6′0″ (182.9 cm)", new PersonFacts { HeightCm = 182.9 }.HeightText);
        Assert.Null(new PersonFacts().HeightText);
    }

    // ---------- Birth place ----------

    [Fact]
    public void A_long_place_chain_is_shown_as_place_region_and_country_but_all_of_it_is_kept()
    {
        var chain = new[] { "Q400", "Q401", "Q402", "Q403" };
        var entity = Entity("Q9", "P", null, "{}");

        var card = PersonLookup.Build(entity, Labels, chain, Now);

        Assert.Equal("Austin, Texas, United States", card.BirthPlace);
        Assert.Contains("Travis County", card.BirthPlaceChain);
    }

    [Fact]
    public void A_short_place_chain_is_shown_whole_and_a_repeated_name_once()
    {
        var labels = new Dictionary<string, string> { ["Q1"] = "Monaco", ["Q2"] = "Monaco" };
        var entity = Entity("Q9", "P", null, "{}");

        Assert.Equal("Monaco", PersonLookup.Build(entity, labels, new[] { "Q1", "Q2" }, Now).BirthPlace);
    }

    // ---------- Untrusted text ----------

    [Fact]
    public void Text_from_wikidata_is_cleaned_because_anyone_can_edit_it()
    {
        var entity = Entity("Q9", "Evil\\u0000 Name \\u202Egpj.exe", "line one\\nline two", "{}");

        var card = PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now);

        Assert.DoesNotContain('\0', card.Name);
        Assert.DoesNotContain('‮', card.Name);
        Assert.DoesNotContain('\n', card.Description!);
    }

    [Fact]
    public void A_very_long_name_is_cut()
    {
        var entity = Entity("Q9", new string('x', 5000), null, "{}");

        Assert.True(PersonLookup.Build(entity, Labels, Array.Empty<string>(), Now).Name.Length <= 200);
    }

    // ---------- The network steps, with fake answers ----------

    private sealed class FakeWikidata : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public Func<Dictionary<string, string>, string?> Answer = _ => null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty));
            var body = Answer(query);
            return Task.FromResult(body == null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string ClaimsAnswer(string property, params string[] items) =>
        "{\"claims\":{\"" + property + "\":[" + string.Join(",", items.Select(i => Statement(Item(i)))) + "]}}";

    private static string EntityAnswer() => "{\"entities\":{\"Q42\":" + FullPerson().GetRawText() + "}}";

    private static string LabelsAnswer(string ids) =>
        "{\"entities\":{" + string.Join(",", ids.Split('|').Select(id =>
            "\"" + id + "\":{\"id\":\"" + id + "\",\"labels\":{\"en\":{\"value\":\"" + Labels.GetValueOrDefault(id, "?") + "\"}}}")) + "}}";

    [Fact]
    public async Task Search_lists_only_people_and_sends_only_the_name()
    {
        var fake = new FakeWikidata
        {
            Answer = q => q["action"] switch
            {
                "wbsearchentities" => """
                    {"search":[
                      {"id":"Q1","label":"Alex Rivera","description":"American actor"},
                      {"id":"Q2","label":"Alex","description":"male given name"},
                      {"id":"Q3","label":"Alex Rivera","description":"Canadian footballer"},
                      {"id":"not-an-id","label":"x"}]}
                    """,
                "wbgetclaims" => q["entity"] switch
                {
                    "Q1" or "Q3" => ClaimsAnswer("P31", "Q5"),
                    "Q2" => ClaimsAnswer("P31", "Q202444"),
                    _ => "{}"
                },
                _ => null
            }
        };
        var lookup = new PersonLookup(new HttpClient(fake));

        var found = await lookup.SearchPeopleAsync("  Alex Rivera ");

        Assert.Equal(new[] { "Q1", "Q3" }, found.Select(c => c.Id));
        Assert.Equal("American actor", found[0].Description);
        Assert.Contains("search=Alex%20Rivera", fake.Requests[0]);
        Assert.All(fake.Requests, r => Assert.StartsWith("https://www.wikidata.org/w/api.php?", r));
    }

    [Fact]
    public async Task Searching_for_nothing_makes_no_request()
    {
        var fake = new FakeWikidata();
        var lookup = new PersonLookup(new HttpClient(fake));

        Assert.Empty(await lookup.SearchPeopleAsync("   "));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task A_match_that_cannot_be_checked_is_not_offered()
    {
        var fake = new FakeWikidata
        {
            Answer = q => q["action"] == "wbsearchentities"
                ? """{"search":[{"id":"Q1","label":"Alex Rivera","description":"actor"}]}"""
                : null   // 503 for the check
        };
        var lookup = new PersonLookup(new HttpClient(fake));

        Assert.Empty(await lookup.SearchPeopleAsync("Alex Rivera"));
    }

    [Fact]
    public async Task Reading_a_person_builds_the_card_and_follows_the_birth_place_up_to_the_country()
    {
        var entityJson = EntityAnswer();
        var fake = new FakeWikidata
        {
            Answer = q =>
            {
                if (q["action"] == "wbgetentities" && q["ids"] == "Q42") return entityJson;
                if (q["action"] == "wbgetclaims")
                    return (q["entity"], q["property"]) switch
                    {
                        ("Q400", "P131") => ClaimsAnswer("P131", "Q401"),
                        ("Q401", "P131") => ClaimsAnswer("P131", "Q402"),
                        ("Q402", "P131") => ClaimsAnswer("P131", "Q403"),
                        ("Q403", "P131") => "{}",
                        ("Q400", "P17") => ClaimsAnswer("P17", "Q403"),
                        _ => "{}"
                    };
                if (q["action"] == "wbgetentities" && q["props"] == "labels")
                    return LabelsAnswer(q["ids"]);
                return null;
            }
        };
        var lookup = new PersonLookup(new HttpClient(fake));

        var card = await lookup.GetFactsAsync("Q42", Now);

        // Names stored only under Wikidata's "mul" label come back only when the fallback is asked for.
        Assert.All(fake.Requests.Where(r => r.Contains("action=wbgetentities")), r => Assert.Contains("languagefallback=1", r));
        Assert.Equal("Samantha Quinn Example", card.FullName);
        Assert.Equal("Austin, Texas, United States", card.BirthPlace);
        Assert.Equal(new[] { "Austin", "Travis County", "Texas", "United States" }, card.BirthPlaceChain);
        Assert.Equal(new[] { "English", "Spanish" }, card.Languages);
        Assert.Equal(new[] { "Sam Okafor" }, card.Spouses.Select(s => s.Name));
    }

    [Fact]
    public async Task A_region_that_cannot_be_read_gives_a_shorter_place_not_a_failure()
    {
        var entityJson = EntityAnswer();
        var fake = new FakeWikidata
        {
            Answer = q =>
            {
                if (q["action"] == "wbgetentities" && q["ids"] == "Q42") return entityJson;
                if (q["action"] == "wbgetclaims") return null; // 503
                if (q["props"] == "labels")
                    return LabelsAnswer(q["ids"]);
                return null;
            }
        };
        var lookup = new PersonLookup(new HttpClient(fake));

        var card = await lookup.GetFactsAsync("Q42", Now);

        Assert.Equal("Austin", card.BirthPlace);
    }

    [Fact]
    public async Task An_id_that_is_not_a_wikidata_item_is_refused_before_any_request()
    {
        var fake = new FakeWikidata();
        var lookup = new PersonLookup(new HttpClient(fake));

        await Assert.ThrowsAsync<ArgumentException>(() => lookup.GetFactsAsync("Q42&action=wbeditentity", Now));
        Assert.Empty(fake.Requests);
    }
}
