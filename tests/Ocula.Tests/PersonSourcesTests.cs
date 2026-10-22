using System.Net;
using System.Text;
using System.Text.Json;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// DBpedia and MusicBrainz as second sources for a person card. They only fill what Wikidata left empty, never change what it has, and
/// a failure of either is not an error. The answers below are hand-written in the shape the real services returned when they were probed.
/// </summary>
public class PersonSourcesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    // ---------- Reading the answers ----------

    private const string DbpediaAnswer = """
        { "head": { "vars": ["height","birthDate","birthPlaceLabel","deathDate","deathPlaceLabel","deathCauseLabel"] },
          "results": { "bindings": [
            { "birthDate": { "type": "literal", "datatype": "http://www.w3.org/2001/XMLSchema#date", "value": "1963-08-09" },
              "birthPlaceLabel": { "type": "literal", "xml:lang": "en", "value": "Newark, New Jersey" } },
            { "height": { "type": "literal", "datatype": "http://www.w3.org/2001/XMLSchema#double", "value": "1.75" },
              "deathDate": { "type": "literal", "value": "2012-02-11" },
              "deathPlaceLabel": { "type": "literal", "xml:lang": "en", "value": "Beverly Hills, California" },
              "deathCauseLabel": { "type": "literal", "xml:lang": "en", "value": "Drowning" } } ] } }
        """;

    [Fact]
    public void A_dbpedia_answer_is_read_across_its_rows_and_the_height_goes_from_metres_to_centimetres()
    {
        var facts = PersonSources.ParseDbpedia(DbpediaAnswer)!;

        Assert.Equal(175, facts.HeightCm!.Value, 6);
        Assert.Equal("1963-08-09", facts.BirthDate);
        Assert.Equal("Newark, New Jersey", facts.BirthPlace);
        Assert.Equal("2012-02-11", facts.DeathDate);
        Assert.Equal("Beverly Hills, California", facts.DeathPlace);
        Assert.Equal("Drowning", facts.DeathCause);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"results":{"bindings":"no"}}""")]
    [InlineData("""{"results":{}}""")]
    public void A_dbpedia_answer_that_is_not_one_gives_nothing(string json)
    {
        Assert.Null(PersonSources.ParseDbpedia(json));
    }

    [Fact]
    public void A_dbpedia_answer_with_no_rows_gives_an_empty_result_not_a_failure()
    {
        var facts = PersonSources.ParseDbpedia("""{"results":{"bindings":[]}}""")!;

        Assert.Null(facts.HeightCm);
        Assert.Null(facts.BirthDate);
    }

    [Fact]
    public void A_musicbrainz_person_is_read_and_a_band_is_not()
    {
        var person = PersonSources.ParseMusicBrainz("""
            { "type": "Person", "name": "Shakira", "life-span": { "begin": "1977-02-02", "end": null, "ended": false },
              "begin-area": { "name": "Barranquilla" }, "end-area": null, "area": { "name": "Colombia" } }
            """)!;
        var died = PersonSources.ParseMusicBrainz("""
            { "type": "Person", "life-span": { "begin": "1963-08-09", "end": "2012-02-11" },
              "begin-area": { "name": "Newark" }, "end-area": { "name": "Beverly Hills" } }
            """)!;

        Assert.Equal(new("1977-02-02", "Barranquilla", null, null), person);
        Assert.Equal(new("1963-08-09", "Newark", "2012-02-11", "Beverly Hills"), died);
        Assert.Null(PersonSources.ParseMusicBrainz("""{ "type": "Group", "life-span": { "begin": "1990" } }"""));
        Assert.Null(PersonSources.ParseMusicBrainz("""{ "name": "no type" }"""));
        Assert.Null(PersonSources.ParseMusicBrainz("garbage"));
    }

    [Fact]
    public void Text_from_the_second_sources_is_cleaned_because_anyone_can_edit_the_sites_behind_them()
    {
        var facts = PersonSources.ParseDbpedia("""
            { "results": { "bindings": [ { "birthPlaceLabel": { "value": "Evil\u0000 ‮Town" } } ] } }
            """)!;

        Assert.DoesNotContain('\0', facts.BirthPlace!);
        Assert.DoesNotContain('‮', facts.BirthPlace!);
    }

    // ---------- Filling only what is empty ----------

    private static PersonFacts Wikidata(Action<PersonFacts>? change = null)
    {
        var card = new PersonFacts { WikidataId = "Q1", Name = "P", RetrievedAt = Now };
        card.Sources["name"] = "Wikidata";
        change?.Invoke(card);
        return card;
    }

    [Fact]
    public void Empty_fields_are_filled_and_credited_to_the_source_that_gave_them()
    {
        var card = Wikidata();

        PersonSources.Merge(card, PersonSources.ParseDbpedia(DbpediaAnswer), null);

        Assert.Equal(175, card.HeightCm);
        Assert.Equal("DBpedia", card.Sources["height"]);
        Assert.Equal("1963-08-09", card.BirthDate);
        Assert.Equal(1963, card.BirthYear);
        Assert.Equal("DBpedia", card.Sources["birthDate"]);
        Assert.Equal("Newark, New Jersey", card.BirthPlace);
        Assert.Equal(new[] { "Newark, New Jersey" }, card.BirthPlaceChain);
        Assert.Equal("2012-02-11", card.DeathDate);
        Assert.Equal("Beverly Hills, California", card.DeathPlace);
        Assert.Equal("Drowning", card.DeathCause);
        Assert.Equal(("1963-08-09", "1963-08-09"), card.BornRange);   // the search columns follow
    }

    [Fact]
    public void Nothing_wikidata_has_is_ever_changed()
    {
        var card = Wikidata(c =>
        {
            c.HeightCm = 160; c.Sources["height"] = "Wikidata";
            c.BirthDate = "1964-01-01"; c.BirthYear = 1964; c.Sources["birthDate"] = "Wikidata";
            c.BirthPlace = "Elsewhere"; c.Sources["birthPlace"] = "Wikidata";
            c.DeathDate = "2013-03-03"; c.DeathPlace = "Some Place"; c.DeathCause = "Some cause";
        });

        PersonSources.Merge(card, PersonSources.ParseDbpedia(DbpediaAnswer), new MusicBrainzFacts("1965", "MB Place", "2014", "MB Death Place"));

        Assert.Equal(160, card.HeightCm);
        Assert.Equal("1964-01-01", card.BirthDate);
        Assert.Equal("Elsewhere", card.BirthPlace);
        Assert.Equal("2013-03-03", card.DeathDate);
        Assert.Equal("Some Place", card.DeathPlace);
        Assert.Equal("Some cause", card.DeathCause);
        Assert.Equal(new[] { "Wikidata" }, card.SourceNames);
    }

    [Fact]
    public void Musicbrainz_fills_what_dbpedia_did_not_and_a_fact_both_have_is_credited_to_dbpedia()
    {
        var card = Wikidata();
        var dbpedia = new DbpediaFacts(null, "1977-02-02", null, null, null, null);
        var musicBrainz = new MusicBrainzFacts("1977-02-03", "Barranquilla", null, null);

        PersonSources.Merge(card, dbpedia, musicBrainz);

        Assert.Equal("1977-02-02", card.BirthDate);                   // DBpedia first
        Assert.Equal("DBpedia", card.Sources["birthDate"]);
        Assert.Equal("Barranquilla", card.BirthPlace);                // only MusicBrainz had it
        Assert.Equal("MusicBrainz", card.Sources["birthPlace"]);
        Assert.Equal(new[] { "Wikidata", "DBpedia", "MusicBrainz" }, card.SourceNames);
    }

    [Theory]
    [InlineData(0.0)]      // DBpedia really does return 0.0 as a height
    [InlineData(5.0)]
    [InlineData(400.0)]
    [InlineData(-170.0)]
    public void An_impossible_height_is_dropped(double cm)
    {
        var card = Wikidata();

        PersonSources.Merge(card, new DbpediaFacts(cm, null, null, null, null, null), null);

        Assert.Null(card.HeightCm);
        Assert.False(card.Sources.ContainsKey("height"));
    }

    [Theory]
    [InlineData("1963-08-09Z", "1963-08-09")]
    [InlineData("1963-08-09+02:00", "1963-08-09")]
    [InlineData("1963-08", "1963-08")]
    [InlineData("1963", "1963")]
    [InlineData("1963-13-01", null)]
    [InlineData("1963-02-30", null)]
    [InlineData("0000", null)]
    [InlineData("3000", null)]
    [InlineData("last Tuesday", null)]
    [InlineData(null, null)]
    public void A_date_must_be_a_real_date_to_be_used(string? date, string? expected)
    {
        Assert.Equal(expected, PersonSources.CleanDate(date));
    }

    [Fact]
    public void A_death_before_the_birth_is_dropped_and_where_and_of_what_need_a_death_date()
    {
        var card = Wikidata(c => { c.BirthDate = "1990-01-01"; c.Sources["birthDate"] = "Wikidata"; });

        PersonSources.Merge(card, new DbpediaFacts(null, null, null, "1980-01-01", "Place", "Cause"), null);

        Assert.Null(card.DeathDate);
        Assert.Null(card.DeathPlace);        // no death date, so no place or cause
        Assert.Null(card.DeathCause);
    }

    [Fact]
    public void The_height_names_its_source_once_there_are_several_and_the_credit_names_them_all()
    {
        var card = Wikidata();
        PersonSources.Merge(card, new DbpediaFacts(175, null, null, null, null, null), new MusicBrainzFacts("1977", null, null, null));

        Assert.Contains(card.Rows(), r => r.Label == "Height" && r.Value.EndsWith("· DBpedia"));
        Assert.Contains("From Wikidata (CC0), DBpedia (CC BY-SA), MusicBrainz (CC0), retrieved", card.CreditText);
        Assert.Contains("It is only what sources list, can be out of date", card.CreditText);
    }

    // ---------- Whether to ask ----------

    [Fact]
    public void A_source_is_asked_only_if_the_card_is_missing_something_it_can_give()
    {
        var complete = Wikidata(c =>
        {
            c.HeightCm = 160; c.BirthDate = "1990"; c.BirthPlace = "Somewhere";
        });
        var missingHeight = Wikidata(c => { c.BirthDate = "1990"; c.BirthPlace = "Somewhere"; });
        var deadWithoutPlace = Wikidata(c => { c.HeightCm = 160; c.BirthDate = "1920"; c.BirthPlace = "X"; c.DeathDate = "1999"; c.DeathCause = "Y"; });

        Assert.False(PersonSources.WorthAskingDbpedia(complete));
        Assert.False(PersonSources.WorthAskingMusicBrainz(complete));
        Assert.True(PersonSources.WorthAskingDbpedia(missingHeight));
        Assert.False(PersonSources.WorthAskingMusicBrainz(missingHeight));   // MusicBrainz has no heights
        Assert.True(PersonSources.WorthAskingMusicBrainz(deadWithoutPlace));
    }

    // ---------- The requests ----------

    [Theory]
    [InlineData("Maria Sharapova", "http://dbpedia.org/resource/Maria_Sharapova")]
    [InlineData("Gerard Piqué", "http://dbpedia.org/resource/Gerard_Piqué")]
    [InlineData("Harry Potter (film)", "http://dbpedia.org/resource/Harry_Potter_(film)")]
    [InlineData("A> } DROP <x", "http://dbpedia.org/resource/A%3E_%7D_DROP_%3Cx")]
    [InlineData("Has \"quote\" and \\back", "http://dbpedia.org/resource/Has_%22quote%22_and_%5Cback")]
    [InlineData("100%", "http://dbpedia.org/resource/100%25")]
    [InlineData("What?#", "http://dbpedia.org/resource/What%3F%23")]
    public void A_title_becomes_a_resource_address_that_cannot_end_or_change_the_query(string title, string expected)
    {
        Assert.Equal(expected, PersonSources.DbpediaIri(title));
    }

    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public Func<HttpRequestMessage, string?> Answer = _ => null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var body = Answer(request);
            return Task.FromResult(body == null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Dbpedia_is_asked_about_the_wikipedia_title_and_nothing_else()
    {
        var fake = new Fake { Answer = _ => DbpediaAnswer };
        var sources = new PersonSources(new HttpClient(fake));

        var facts = await sources.GetDbpediaAsync("Whitney Houston");

        Assert.NotNull(facts);
        var url = Uri.UnescapeDataString(Assert.Single(fake.Requests));
        Assert.StartsWith("https://dbpedia.org/sparql?", url);
        Assert.Contains("<http://dbpedia.org/resource/Whitney_Houston>", url);
        Assert.DoesNotContain("Shakira", url);
    }

    [Fact]
    public async Task Musicbrainz_is_asked_about_the_artist_id_and_a_bad_id_makes_no_request()
    {
        var fake = new Fake { Answer = _ => """{ "type": "Person", "life-span": { "begin": "1977-02-02" } }""" };
        var sources = new PersonSources(new HttpClient(fake));

        Assert.NotNull(await sources.GetMusicBrainzAsync("bf24ca37-25f4-4e34-9aec-460b94364cfc"));
        Assert.Equal("https://musicbrainz.org/ws/2/artist/bf24ca37-25f4-4e34-9aec-460b94364cfc?fmt=json", Assert.Single(fake.Requests));

        Assert.Null(await sources.GetMusicBrainzAsync("not-a-guid/../../x"));
        Assert.Single(fake.Requests);
    }

    // ---------- Inside a whole look-up ----------

    private static string EntityJson(string extraClaims = "", string sitelinks = "") =>
        "{\"entities\":{\"Q42\":{\"id\":\"Q42\",\"labels\":{\"en\":{\"value\":\"Sam Example\"}}," +
        "\"descriptions\":{\"en\":{\"value\":\"American singer\"}}," + (sitelinks.Length > 0 ? "\"sitelinks\":" + sitelinks + "," : "") +
        "\"claims\":{" + extraClaims + "}}}}";

    private static string MusicBrainzClaim(string id) =>
        "\"P434\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":{\"value\":\"" + id + "\",\"type\":\"string\"}},\"rank\":\"normal\"}]";

    private static string Answer(HttpRequestMessage request, string entity, Func<string, string?> other)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url.StartsWith("https://www.wikidata.org/"))
        {
            if (url.Contains("action=wbgetentities") && url.Contains("ids=Q42")) return entity;
            if (url.Contains("props=labels")) return "{\"entities\":{\"Q400\":{\"id\":\"Q400\",\"labels\":{\"en\":{\"value\":\"Austin\"}}}}}";
            return "{}";
        }
        return other(url)!;
    }

    [Fact]
    public async Task A_whole_lookup_fills_the_gaps_from_both_sources_by_the_ids_wikidata_gave()
    {
        var entity = EntityJson(MusicBrainzClaim("bf24ca37-25f4-4e34-9aec-460b94364cfc"), "{\"enwiki\":{\"site\":\"enwiki\",\"title\":\"Sam Example\"}}");
        var fake = new Fake
        {
            Answer = r => Answer(r, entity, url =>
                url.StartsWith("https://dbpedia.org/") ? DbpediaAnswer
                : url.StartsWith("https://musicbrainz.org/") ? """{ "type": "Person", "life-span": { "begin": "1963-08-09" }, "begin-area": { "name": "Newark" } }"""
                : null)
        };
        var lookup = new PersonLookup(new HttpClient(fake));

        var card = await lookup.GetFactsAsync("Q42", Now);

        Assert.Equal(175, card.HeightCm);
        Assert.Equal("DBpedia", card.Sources["height"]);
        Assert.Equal("1963-08-09", card.BirthDate);
        Assert.Equal("Newark, New Jersey", card.BirthPlace);                 // DBpedia was asked first
        Assert.Contains(fake.Requests, r => r.StartsWith("https://dbpedia.org/") && Uri.UnescapeDataString(r).Contains("<http://dbpedia.org/resource/Sam_Example>"));
        Assert.Contains(fake.Requests, r => r.Contains("musicbrainz.org/ws/2/artist/bf24ca37-25f4-4e34-9aec-460b94364cfc"));
        Assert.Contains(card.SourceNames, n => n == "DBpedia");
    }

    [Fact]
    public async Task A_second_source_that_fails_leaves_the_wikidata_card_as_it_was()
    {
        var entity = EntityJson(MusicBrainzClaim("bf24ca37-25f4-4e34-9aec-460b94364cfc"), "{\"enwiki\":{\"site\":\"enwiki\",\"title\":\"Sam Example\"}}");
        var fake = new Fake { Answer = r => Answer(r, entity, _ => null) };     // both second sources answer 503
        var lookup = new PersonLookup(new HttpClient(fake));

        var card = await lookup.GetFactsAsync("Q42", Now);

        Assert.Equal("Sam Example", card.Name);
        Assert.Null(card.HeightCm);
        Assert.Equal(new[] { "Wikidata" }, card.SourceNames);
    }

    [Fact]
    public async Task Without_a_wikipedia_title_or_a_musicbrainz_id_neither_source_is_asked()
    {
        var fake = new Fake { Answer = r => Answer(r, EntityJson(), _ => throw new InvalidOperationException("must not be asked")) };
        var lookup = new PersonLookup(new HttpClient(fake));

        await lookup.GetFactsAsync("Q42", Now);

        Assert.DoesNotContain(fake.Requests, r => !r.StartsWith("https://www.wikidata.org/"));
    }

    [Fact]
    public async Task A_complete_card_does_not_ask_the_second_sources()
    {
        var claims = "\"P569\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":{\"value\":{\"time\":\"+1990-01-01T00:00:00Z\",\"precision\":11}}},\"rank\":\"normal\"}]," +
                     "\"P2048\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":{\"value\":{\"amount\":\"+1.7\",\"unit\":\"http://www.wikidata.org/entity/Q11573\"}}},\"rank\":\"normal\"}]," +
                     "\"P19\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":{\"value\":{\"entity-type\":\"item\",\"id\":\"Q400\"}}},\"rank\":\"normal\"}]";
        var entity = EntityJson(claims + "," + MusicBrainzClaim("bf24ca37-25f4-4e34-9aec-460b94364cfc"), "{\"enwiki\":{\"site\":\"enwiki\",\"title\":\"Sam Example\"}}");
        var fake = new Fake { Answer = r => Answer(r, entity, _ => throw new InvalidOperationException("must not be asked")) };
        var lookup = new PersonLookup(new HttpClient(fake));

        var card = await lookup.GetFactsAsync("Q42", Now);

        Assert.Equal(170, card.HeightCm);
        Assert.DoesNotContain(fake.Requests, r => !r.StartsWith("https://www.wikidata.org/"));
    }

    [Fact]
    public void The_ids_for_the_second_sources_are_not_saved_with_the_card()
    {
        var card = new PersonFacts { Name = "P" };
        typeof(PersonFacts).GetProperty("WikipediaTitle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(card, "Secret Title");

        Assert.DoesNotContain("Secret Title", card.ToJson());
        Assert.DoesNotContain("WikipediaTitle", card.ToJson());
        Assert.DoesNotContain("MusicBrainz", card.ToJson().Replace("\"Sources\"", ""));
    }
}
