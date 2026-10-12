using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Ocula.Services;

/// <summary>One "Look up on Wikidata" result: a person and the short description that tells namesakes apart.</summary>
public sealed record PersonMatch(string Id, string Name, string? Description);

/// <summary>A spouse or partner, with the years Wikidata gives (often none).</summary>
public sealed record PersonRelation(string Name, int? From, int? To)
{
    public string Text => (From, To) switch
    {
        (int from, int to) => $"{Name} · {from}–{to}",
        (int from, null) => $"{Name} · since {from}",
        (null, int to) => $"{Name} · until {to}",
        _ => Name,
    };
}

/// <summary>
/// A person card: what Wikidata (and DBpedia, for gaps) says about someone you named in People.
/// Saved as JSON in the library (people.card) and never written into photos. Fields are kept as
/// data, not text, so the search box can filter on them later. Children are a count only.
/// </summary>
public sealed class PersonCardData
{
    public string WikidataId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? FullName { get; set; }
    /// <summary>yyyy-MM-dd; with year or month precision the rest is "01".</summary>
    public string? Born { get; set; }
    /// <summary>Wikidata's precision: 9 year, 10 month, 11 day.</summary>
    public int BornPrecision { get; set; }
    public string? Died { get; set; }
    public int DiedPrecision { get; set; }
    public string? Birthplace { get; set; }
    public List<string> Occupations { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public List<PersonRelation> Spouses { get; set; } = new();
    public List<PersonRelation> FormerSpouses { get; set; } = new();
    public List<PersonRelation> Partners { get; set; } = new();
    public List<PersonRelation> FormerPartners { get; set; } = new();
    public int? Children { get; set; }
    public double? HeightCm { get; set; }
    public DateTime Retrieved { get; set; }
    /// <summary>Where each field came from ("Wikidata" or "DBpedia"); not shown, kept for checking.</summary>
    public Dictionary<string, string> Sources { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PersonCardData? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PersonCardData>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The card's rows, in the mockup's order. Fields with no data are left out.</summary>
    public IReadOnlyList<(string Label, string Value)> Rows()
    {
        var rows = new List<(string, string)>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value));
        }
        static string Lines(IEnumerable<PersonRelation> relations) => string.Join("\n", relations.Select(r => r.Text));

        Add("Full name", FullName);
        Add("Born", PersonCards.FormatDate(Born, BornPrecision));
        Add("Died", PersonCards.FormatDate(Died, DiedPrecision));
        Add("Birthplace", Birthplace);
        Add(Occupations.Count == 1 ? "Occupation" : "Occupations", PersonCards.Sentence(Occupations));
        Add(Languages.Count == 1 ? "Language" : "Languages", string.Join(", ", Languages));
        Add(Spouses.Count > 1 ? "Spouses" : "Spouse", Lines(Spouses));
        Add(FormerSpouses.Count == 1 ? "Former spouse" : "Former spouses", Lines(FormerSpouses));
        Add(Partners.Count == 1 ? "Partner" : "Partners", Lines(Partners));
        Add(FormerPartners.Count == 1 ? "Former partner" : "Former partners", Lines(FormerPartners));
        Add("Children", Children?.ToString(CultureInfo.InvariantCulture));
        Add("Height", PersonCards.FormatHeight(HeightCm));
        return rows;
    }

    /// <summary>The credit line under the card (DBpedia's licence asks for it when its data is used).</summary>
    public string SourceLine
    {
        get
        {
            var source = Sources.ContainsValue(PersonCards.DBpedia) ? "Wikidata and DBpedia (CC BY-SA)" : "Wikidata";
            return $"Source: {source}, retrieved {Retrieved.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}. " +
                   "Fields with no data are left out. May be out of date. Children as a count only. Stays on this PC.";
        }
    }

    /// <summary>The names offered as tags: what you typed (ticked), then the Wikidata name and full name.</summary>
    public static IReadOnlyList<string> TagNames(string typed, PersonCardData? card)
    {
        var names = new List<string> { typed.Trim() };
        if (card != null)
        {
            names.Add(card.Name);
            if (card.FullName != null) names.Add(card.FullName);
        }
        return names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Reads Wikidata's JSON into person cards. Knows nothing about the network, so it is
/// unit-tested with saved answers (tests/Ocula.Tests/PersonCardTests.cs).
/// </summary>
public static class PersonCards
{
    public const string Wikidata = "Wikidata";
    public const string DBpedia = "DBpedia";

    // Wikidata properties used on the card.
    private const string BirthName = "P1477", DateOfBirth = "P569", DateOfDeath = "P570", PlaceOfBirth = "P19",
                         Occupation = "P106", LanguagesSpoken = "P1412", Spouse = "P26", UnmarriedPartner = "P451",
                         NumberOfChildren = "P1971", Child = "P40", Height = "P2048", StartTime = "P580", EndTime = "P582";

    /// <summary>The Q-ids from a search (action=query&amp;list=search), best match first.</summary>
    public static IReadOnlyList<string> SearchIds(JsonElement root) =>
        root.TryGetProperty("query", out var query) && query.TryGetProperty("search", out var search)
            ? search.EnumerateArray().Select(r => Text(r, "title")).OfType<string>().Where(IsItemId).ToList()
            : Array.Empty<string>();

    /// <summary>Names and descriptions (wbgetentities, props=labels|descriptions) in the search's order.</summary>
    public static IReadOnlyList<PersonMatch> Matches(JsonElement root, IReadOnlyList<string> ids)
    {
        if (!root.TryGetProperty("entities", out var entities)) return Array.Empty<PersonMatch>();
        var matches = new List<PersonMatch>();
        foreach (var id in ids)
        {
            if (!entities.TryGetProperty(id, out var entity) || Term(entity, "labels") is not { } name) continue;
            matches.Add(new PersonMatch(id, name, Term(entity, "descriptions")));
        }
        return matches;
    }

    /// <summary>The place of birth, whose country is looked up separately.</summary>
    public static string? PlaceId(JsonElement entity) => Best(entity, PlaceOfBirth).Select(ItemId).FirstOrDefault(id => id != null);

    /// <summary>Every item the card needs a name for: place, occupations, languages, spouses and partners.</summary>
    public static IReadOnlyList<string> ReferencedIds(JsonElement entity) =>
        new[] { PlaceOfBirth, Occupation, LanguagesSpoken, Spouse, UnmarriedPartner }
            .SelectMany(p => Statements(entity, p))
            .Select(ItemId).OfType<string>().Distinct().ToList();

    /// <summary>The first item value of a property in wbgetclaims' answer (the country of a place, P17).</summary>
    public static string? FirstClaimId(JsonElement root, string property) =>
        root.TryGetProperty("claims", out var claims) && claims.TryGetProperty(property, out var list)
            ? list.EnumerateArray().Where(s => Rank(s) != "deprecated").OrderBy(s => Rank(s) == "preferred" ? 0 : 1)
                  .Select(ItemId).FirstOrDefault(id => id != null)
            : null;

    /// <summary>Names (labels) from wbgetentities, props=labels.</summary>
    public static Dictionary<string, string> Labels(JsonElement root)
    {
        var labels = new Dictionary<string, string>();
        if (!root.TryGetProperty("entities", out var entities)) return labels;
        foreach (var entity in entities.EnumerateObject())
            if (Term(entity.Value, "labels") is { } label) labels[entity.Name] = label;
        return labels;
    }

    /// <summary>Builds the card from the person's entity (wbgetentities, props=labels|descriptions|claims).</summary>
    public static PersonCardData Build(JsonElement entity, IReadOnlyDictionary<string, string> labels, string? countryId, DateTime retrieved)
    {
        string? Label(string? id) => id != null && labels.TryGetValue(id, out var label) ? label : null;

        var card = new PersonCardData
        {
            WikidataId = Text(entity, "id") ?? string.Empty,
            Name = Term(entity, "labels") ?? string.Empty,
            Description = Term(entity, "descriptions"),
            Retrieved = retrieved,
        };
        void From(string field, bool has) { if (has) card.Sources[field] = Wikidata; }

        card.FullName = Best(entity, BirthName).Select(s => Value(s) is { ValueKind: JsonValueKind.Object } v ? Text(v, "text") : null)
                                               .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        From("FullName", card.FullName != null);

        (card.Born, card.BornPrecision) = Best(entity, DateOfBirth).Select(Date).FirstOrDefault(d => d.Date != null);
        From("Born", card.Born != null);
        (card.Died, card.DiedPrecision) = Best(entity, DateOfDeath).Select(Date).FirstOrDefault(d => d.Date != null);
        From("Died", card.Died != null);

        if (Label(PlaceId(entity)) is { } place)
        {
            var country = Label(countryId);
            card.Birthplace = country != null && !string.Equals(country, place, StringComparison.OrdinalIgnoreCase)
                ? $"{place}, {country}" : place;
        }
        From("Birthplace", card.Birthplace != null);

        card.Occupations = Statements(entity, Occupation).Select(ItemId).Select(Label).OfType<string>()
                                                         .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        From("Occupations", card.Occupations.Count > 0);
        card.Languages = Statements(entity, LanguagesSpoken).Select(ItemId).Select(Label).OfType<string>()
                                                            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        From("Languages", card.Languages.Count > 0);

        // A spouse or partner with no end date is current; one with an end date is former.
        foreach (var (property, current, former) in new[]
                 {
                     (Spouse, card.Spouses, card.FormerSpouses),
                     (UnmarriedPartner, card.Partners, card.FormerPartners),
                 })
        {
            foreach (var statement in Statements(entity, property))
            {
                if (Label(ItemId(statement)) is not { } name) continue;
                var relation = new PersonRelation(name, QualifierYear(statement, StartTime), QualifierYear(statement, EndTime));
                (relation.To == null ? current : former).Add(relation);
            }
            current.Sort((a, b) => (a.From ?? 0).CompareTo(b.From ?? 0));
            former.Sort((a, b) => (a.From ?? a.To ?? 0).CompareTo(b.From ?? b.To ?? 0));
        }
        From("Spouses", card.Spouses.Count + card.FormerSpouses.Count > 0);
        From("Partners", card.Partners.Count + card.FormerPartners.Count > 0);

        // "Number of children" when Wikidata has it; otherwise how many children it lists (a count, never names).
        var count = Best(entity, NumberOfChildren).Select(Quantity).FirstOrDefault(q => q.Amount != null).Amount;
        var listed = Statements(entity, Child).Count(s => Snak(s) == "value");
        card.Children = count is double n && n >= 0 ? (int)Math.Round(n) : listed > 0 ? listed : null;
        From("Children", card.Children != null);

        card.HeightCm = Best(entity, Height).Select(Quantity).Select(q => HeightCm(q.Amount, q.Unit)).FirstOrDefault(h => h != null);
        From("Height", card.HeightCm != null);

        return card;
    }

    /// <summary>A height in centimetres from a Wikidata quantity (metres, centimetres, feet or inches).</summary>
    public static double? HeightCm(double? amount, string? unit)
    {
        if (amount is not double value) return null;
        var id = unit?[(unit.LastIndexOf('/') + 1)..];
        double? cm = id switch
        {
            "Q11573" => value * 100,   // metre
            "Q174728" => value,        // centimetre
            "Q3710" => value * 30.48,  // foot
            "Q218593" => value * 2.54, // inch
            _ => null,
        };
        return cm is >= 50 and <= 260 ? Math.Round(cm.Value, 1) : null;
    }

    /// <summary>A DBpedia height (dbo:height), which is in metres for most people and centimetres for some.</summary>
    public static double? DBpediaHeightCm(double value) => value switch
    {
        >= 0.5 and <= 2.6 => Math.Round(value * 100, 1),
        >= 50 and <= 260 => Math.Round(value, 1),
        _ => null,
    };

    /// <summary>The height from DBpedia's SPARQL answer (format=json), if it has one.</summary>
    public static double? DBpediaHeight(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || !results.TryGetProperty("bindings", out var bindings)) return null;
        foreach (var binding in bindings.EnumerateArray())
            if (binding.TryGetProperty("h", out var h) && Text(h, "value") is { } text &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                DBpediaHeightCm(value) is double cm)
                return cm;
        return null;
    }

    /// <summary>"1.57 m (5 ft 2 in)".</summary>
    public static string? FormatHeight(double? cm)
    {
        if (cm is not double value) return null;
        var inches = (int)Math.Round(value / 2.54);
        return $"{(value / 100).ToString("0.00", CultureInfo.InvariantCulture)} m ({inches / 12} ft {inches % 12} in)";
    }

    /// <summary>"2 February 1977", "February 1977" or "1977", depending on how precise Wikidata is.</summary>
    public static string? FormatDate(string? date, int precision)
    {
        if (date == null || !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return null;
        return precision >= 11 ? d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
             : precision == 10 ? d.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
             : d.Year.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"Singer, songwriter, dancer": Wikidata's lower-case names, first letter capitalised.</summary>
    public static string Sentence(IReadOnlyList<string> words)
    {
        var text = string.Join(", ", words);
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ---------- Wikidata JSON ----------

    private static bool IsItemId(string id) => id.Length > 1 && id[0] == 'Q' && id[1..].All(char.IsDigit);

    /// <summary>A property's statements, without deprecated ones.</summary>
    private static IEnumerable<JsonElement> Statements(JsonElement entity, string property) =>
        entity.TryGetProperty("claims", out var claims) && claims.TryGetProperty(property, out var list)
            ? list.EnumerateArray().Where(s => Rank(s) != "deprecated")
            : Enumerable.Empty<JsonElement>();

    /// <summary>For one-value facts (birth date, height): the preferred statements if any, else all of them.</summary>
    private static IEnumerable<JsonElement> Best(JsonElement entity, string property)
    {
        var all = Statements(entity, property).ToList();
        var preferred = all.Where(s => Rank(s) == "preferred").ToList();
        return preferred.Count > 0 ? preferred : all;
    }

    private static string? Rank(JsonElement statement) => Text(statement, "rank");

    private static string? Snak(JsonElement statement) =>
        statement.TryGetProperty("mainsnak", out var snak) ? Text(snak, "snaktype") : null;

    private static JsonElement? Value(JsonElement statement) =>
        statement.TryGetProperty("mainsnak", out var snak) && Text(snak, "snaktype") == "value" &&
        snak.TryGetProperty("datavalue", out var data) && data.TryGetProperty("value", out var value)
            ? value : null;

    private static string? ItemId(JsonElement statement) =>
        Value(statement) is { ValueKind: JsonValueKind.Object } value ? Text(value, "id") : null;

    private static (string? Date, int Precision) Date(JsonElement statement) =>
        Value(statement) is { ValueKind: JsonValueKind.Object } value ? Time(value) : (null, 0);

    /// <summary>"+1977-02-02T00:00:00Z" with its precision; year or month precision stores "00" for the rest.</summary>
    private static (string? Date, int Precision) Time(JsonElement value)
    {
        var time = Text(value, "time");
        var precision = value.TryGetProperty("precision", out var p) && p.TryGetInt32(out var n) ? n : 0;
        if (time == null || !time.StartsWith('+') || time.Length < 11 || precision < 9) return (null, 0);
        var year = time[1..5];
        var month = precision >= 10 && time[6..8] != "00" ? time[6..8] : "01";
        var day = precision >= 11 && time[9..11] != "00" ? time[9..11] : "01";
        return ($"{year}-{month}-{day}", Math.Min(precision, 11));
    }

    private static int? QualifierYear(JsonElement statement, string qualifier)
    {
        if (!statement.TryGetProperty("qualifiers", out var qualifiers) || !qualifiers.TryGetProperty(qualifier, out var list))
            return null;
        foreach (var snak in list.EnumerateArray())
            if (Text(snak, "snaktype") == "value" && snak.TryGetProperty("datavalue", out var data) &&
                data.TryGetProperty("value", out var value) && Time(value) is { Date: { } date })
                return int.Parse(date[..4], CultureInfo.InvariantCulture);
        return null;
    }

    private static (double? Amount, string? Unit) Quantity(JsonElement statement)
    {
        if (Value(statement) is not { ValueKind: JsonValueKind.Object } value) return (null, null);
        var amount = Text(value, "amount");
        return (amount != null && double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null,
                Text(value, "unit"));
    }

    /// <summary>An English label or description (or whatever language Wikidata fell back to).</summary>
    private static string? Term(JsonElement entity, string kind)
    {
        if (!entity.TryGetProperty(kind, out var terms) || terms.ValueKind != JsonValueKind.Object) return null;
        if (terms.TryGetProperty("en", out var en) && Text(en, "value") is { } english) return english;
        return terms.EnumerateObject().Select(t => Text(t.Value, "value")).FirstOrDefault(v => v != null);
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
