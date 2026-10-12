using System.Text.Json;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// Reading Wikidata's answers into person cards (PersonCards). The person, places and ids are made
/// up; the JSON has the shape the Wikidata API returns.
/// </summary>
public class PersonCardTests
{
    private const string Entity = """
    {
      "id": "Q900001",
      "labels": { "en": { "language": "en", "value": "Ada Example" } },
      "descriptions": { "en": { "language": "en", "value": "fictional singer used in tests" } },
      "claims": {
        "P1477": [ { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "monolingualtext", "value": { "text": "Ada Maria Example", "language": "en" } } } } ],
        "P569": [
          { "rank": "deprecated", "mainsnak": { "snaktype": "value", "datavalue": { "type": "time", "value": { "time": "+1970-01-01T00:00:00Z", "precision": 11 } } } },
          { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "time", "value": { "time": "+1981-03-14T00:00:00Z", "precision": 11 } } } }
        ],
        "P19": [ { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900010" } } } } ],
        "P106": [
          { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900020" } } } },
          { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900021" } } } }
        ],
        "P1412": [ { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900030" } } } } ],
        "P26": [
          { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900040" } } },
            "qualifiers": { "P580": [ { "snaktype": "value", "datavalue": { "value": { "time": "+2003-00-00T00:00:00Z", "precision": 9 } } } ],
                            "P582": [ { "snaktype": "value", "datavalue": { "value": { "time": "+2008-06-01T00:00:00Z", "precision": 10 } } } ] } },
          { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900041" } } },
            "qualifiers": { "P580": [ { "snaktype": "value", "datavalue": { "value": { "time": "+2015-09-12T00:00:00Z", "precision": 11 } } } ] } }
        ],
        "P451": [ { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900042" } } },
            "qualifiers": { "P580": [ { "snaktype": "value", "datavalue": { "value": { "time": "+2010-00-00T00:00:00Z", "precision": 9 } } } ],
                            "P582": [ { "snaktype": "value", "datavalue": { "value": { "time": "+2013-00-00T00:00:00Z", "precision": 9 } } } ] } } ],
        "P40": [
          { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900050" } } } },
          { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "wikibase-entityid", "value": { "id": "Q900051" } } } },
          { "rank": "normal", "mainsnak": { "snaktype": "somevalue" } }
        ],
        "P2048": [ { "rank": "normal", "mainsnak": { "snaktype": "value", "datavalue": { "type": "quantity", "value": { "amount": "+1.63", "unit": "http://www.wikidata.org/entity/Q11573" } } } } ]
      }
    }
    """;

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["Q900010"] = "Springfield", ["Q900011"] = "Freedonia",
        ["Q900020"] = "singer", ["Q900021"] = "actor", ["Q900030"] = "English",
        ["Q900040"] = "Former Spouse", ["Q900041"] = "Current Spouse", ["Q900042"] = "Old Partner",
    };

    private static PersonCardData Card()
    {
        using var doc = JsonDocument.Parse(Entity);
        return PersonCards.Build(doc.RootElement, Labels, "Q900011", new DateTime(2026, 10, 12));
    }

    [Fact]
    public void The_card_has_every_field_Wikidata_gives()
    {
        var card = Card();

        Assert.Equal("Q900001", card.WikidataId);
        Assert.Equal("Ada Example", card.Name);
        Assert.Equal("Ada Maria Example", card.FullName);
        Assert.Equal("1981-03-14", card.Born);           // the deprecated 1970 date is ignored
        Assert.Equal("Springfield, Freedonia", card.Birthplace);
        Assert.Equal(new[] { "singer", "actor" }, card.Occupations);
        Assert.Equal(new[] { "English" }, card.Languages);
        Assert.Equal(new[] { new PersonRelation("Current Spouse", 2015, null) }, card.Spouses);
        Assert.Equal(new[] { new PersonRelation("Former Spouse", 2003, 2008) }, card.FormerSpouses);
        Assert.Equal(new[] { new PersonRelation("Old Partner", 2010, 2013) }, card.FormerPartners);
        Assert.Empty(card.Partners);
        Assert.Equal(2, card.Children);                  // two listed children; "unknown value" doesn't count
        Assert.Equal(163, card.HeightCm);
        Assert.All(card.Sources.Values, s => Assert.Equal(PersonCards.Wikidata, s));
    }

    [Fact]
    public void Rows_read_like_the_mockup_and_skip_empty_fields()
    {
        var rows = Card().Rows();

        Assert.Equal(new[] { "Full name", "Born", "Birthplace", "Occupations", "Language", "Spouse", "Former spouse",
                             "Former partner", "Children", "Height" }, rows.Select(r => r.Label));
        Assert.Equal("14 March 1981", rows.Single(r => r.Label == "Born").Value);
        Assert.Equal("Singer, actor", rows.Single(r => r.Label == "Occupations").Value);
        Assert.Equal("Current Spouse · since 2015", rows.Single(r => r.Label == "Spouse").Value);
        Assert.Equal("Former Spouse · 2003–2008", rows.Single(r => r.Label == "Former spouse").Value);
        Assert.Equal("1.63 m (5 ft 4 in)", rows.Single(r => r.Label == "Height").Value);
    }

    [Fact]
    public void Ids_to_name_cover_place_jobs_languages_and_relations_but_not_children()
    {
        using var doc = JsonDocument.Parse(Entity);
        var ids = PersonCards.ReferencedIds(doc.RootElement);

        Assert.Equal(new[] { "Q900010", "Q900020", "Q900021", "Q900030", "Q900040", "Q900041", "Q900042" }, ids);
        Assert.Equal("Q900010", PersonCards.PlaceId(doc.RootElement));
    }

    [Fact]
    public void A_card_survives_saving_to_the_library()
    {
        var card = Card();
        var loaded = PersonCardData.FromJson(card.ToJson())!;

        Assert.Equal(card.Rows(), loaded.Rows());
        Assert.Equal(card.SourceLine, loaded.SourceLine);
        Assert.Null(PersonCardData.FromJson("not json"));
        Assert.Null(PersonCardData.FromJson(null));
    }

    [Fact]
    public void The_credit_names_DBpedia_only_when_it_filled_a_field()
    {
        var card = Card();
        Assert.StartsWith("Source: Wikidata, retrieved 12 Oct 2026.", card.SourceLine);

        card.Sources["Height"] = PersonCards.DBpedia;
        Assert.StartsWith("Source: Wikidata and DBpedia (CC BY-SA), retrieved 12 Oct 2026.", card.SourceLine);
    }

    [Fact]
    public void Search_results_keep_their_order_and_skip_ids_without_a_name()
    {
        using var search = JsonDocument.Parse("""
            { "query": { "search": [ { "title": "Q900002" }, { "title": "Q900001" }, { "title": "Property:P31" }, { "title": "Q900003" } ] } }
            """);
        using var terms = JsonDocument.Parse("""
            { "entities": {
                "Q900001": { "labels": { "en": { "value": "Ada Example" } }, "descriptions": { "en": { "value": "fictional singer" } } },
                "Q900002": { "labels": { "en": { "value": "Ada Example" } }, "descriptions": { "en": { "value": "fictional rower" } } },
                "Q900003": { "labels": {} } } }
            """);

        var ids = PersonCards.SearchIds(search.RootElement);
        Assert.Equal(new[] { "Q900002", "Q900001", "Q900003" }, ids);

        var matches = PersonCards.Matches(terms.RootElement, ids);
        Assert.Equal(new[] { new PersonMatch("Q900002", "Ada Example", "fictional rower"),
                             new PersonMatch("Q900001", "Ada Example", "fictional singer") }, matches);
    }

    [Fact]
    public void A_label_in_another_language_is_used_when_there_is_no_English_one()
    {
        using var labels = JsonDocument.Parse("""{ "entities": { "Q900060": { "labels": { "es": { "value": "Ciudad Ejemplo" } } } } }""");
        Assert.Equal("Ciudad Ejemplo", PersonCards.Labels(labels.RootElement)["Q900060"]);
    }

    [Theory]
    [InlineData(1.57, "http://www.wikidata.org/entity/Q11573", 157.0)]
    [InlineData(157.0, "http://www.wikidata.org/entity/Q174728", 157.0)]
    [InlineData(62.0, "http://www.wikidata.org/entity/Q218593", 157.5)]
    [InlineData(5.5, "http://www.wikidata.org/entity/Q3710", 167.6)]
    [InlineData(1.57, "http://www.wikidata.org/entity/Q11570", null)] // kilograms: not a height
    [InlineData(15.7, "http://www.wikidata.org/entity/Q11573", null)] // 15.7 m: a data error
    public void Heights_convert_to_centimetres(double amount, string unit, double? cm) =>
        Assert.Equal(cm, PersonCards.HeightCm(amount, unit));

    [Theory]
    [InlineData(1.57, 157.0)]
    [InlineData(157.0, 157.0)]
    [InlineData(0.01, null)]
    public void DBpedia_heights_in_metres_or_centimetres(double value, double? cm) =>
        Assert.Equal(cm, PersonCards.DBpediaHeightCm(value));

    [Fact]
    public void DBpedia_answer_gives_the_height()
    {
        using var answer = JsonDocument.Parse("""{ "results": { "bindings": [ { "h": { "type": "literal", "value": "1.57" } } ] } }""");
        Assert.Equal(157, PersonCards.DBpediaHeight(answer.RootElement));
    }

    [Theory]
    [InlineData("1977-02-02", 11, "2 February 1977")]
    [InlineData("1977-02-01", 10, "February 1977")]
    [InlineData("1977-01-01", 9, "1977")]
    [InlineData(null, 11, null)]
    public void Dates_show_only_as_much_as_Wikidata_knows(string? date, int precision, string? text) =>
        Assert.Equal(text, PersonCards.FormatDate(date, precision));

    [Fact]
    public void Tags_offered_are_the_typed_name_then_the_Wikidata_and_full_names()
    {
        var card = Card();
        Assert.Equal(new[] { "Ada", "Ada Example", "Ada Maria Example" }, PersonCardData.TagNames("Ada", card));
        Assert.Equal(new[] { "ada example", "Ada Maria Example" }, PersonCardData.TagNames("ada example", card)); // your spelling wins
        Assert.Equal(new[] { "Ada" }, PersonCardData.TagNames(" Ada ", null));
    }
}
