using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>One match in the "Look up on Wikidata" picker.</summary>
public sealed record WikidataCandidate(string Id, string Name, string? Description);

/// <summary>A spouse or partner as Wikidata lists them, with the years when it has them.</summary>
public sealed record Relation(string Name, string? Id, int? FromYear, int? ToYear)
{
    [JsonIgnore]
    public string Years => (FromYear, ToYear) switch
    {
        (int f, int t) => $"{f}–{t}",
        (int f, null) => $"{f}–",
        (null, int t) => $"until {t}",
        _ => string.Empty
    };

    [JsonIgnore]
    public string Display => Years.Length == 0 ? Name : $"{Name} ({Years})";
}

/// <summary>
/// What Wikidata says about one person. Every field is either something Wikidata listed or empty: nothing is guessed.
/// Wikidata is edited by anyone, so every text here has been cleaned (see <see cref="CaptureTextIndex.CleanLine"/>) before it
/// is shown or stored.
/// </summary>
public sealed class PersonFacts
{
    public string WikidataId { get; set; } = string.Empty;

    /// <summary>Wikidata's label for the item (the name people use).</summary>
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The "birth name" when Wikidata has one, which is where middle names usually are.</summary>
    public string? FullName { get; set; }

    /// <summary>"1977-02-02", "1977-02" or "1977", as precise as Wikidata is.</summary>
    public string? BirthDate { get; set; }
    public int? BirthYear { get; set; }
    public string? DeathDate { get; set; }

    /// <summary>Shown as "Austin, Texas, United States".</summary>
    public string? BirthPlace { get; set; }

    /// <summary>The place, then the regions it is in, then the country, so "born in Texas" can be matched.</summary>
    public List<string> BirthPlaceChain { get; set; } = new();

    public List<string> Occupations { get; set; } = new();

    /// <summary>Spouses with no end date on Wikidata.</summary>
    public List<Relation> Spouses { get; set; } = new();
    public List<Relation> FormerSpouses { get; set; } = new();

    /// <summary>Unmarried partners (Wikidata P451, which isn't gender specific).</summary>
    public List<Relation> Partners { get; set; } = new();

    /// <summary>A number only; children are never named.</summary>
    public int? Children { get; set; }

    /// <summary>True when the count is how many children Wikidata lists, not a number it states.</summary>
    public bool ChildrenFromList { get; set; }

    public double? HeightCm { get; set; }
    public List<string> Languages { get; set; } = new();

    public DateTimeOffset RetrievedAt { get; set; }

    /// <summary>Which source each field came from. Only "Wikidata" today; kept so another source can fill gaps later.</summary>
    public Dictionary<string, string> Sources { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);

    public static PersonFacts? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<PersonFacts>(json); }
        catch (JsonException) { return null; }
    }

    // ----- Text for the card -----

    [JsonIgnore]
    public string? BirthDateText => FormatDate(BirthDate);
    [JsonIgnore]
    public string? DeathDateText => FormatDate(DeathDate);

    /// <summary>For example 5′3″ (160 cm).</summary>
    [JsonIgnore]
    public string? HeightText
    {
        get
        {
            if (HeightCm is not double cm) return null;
            var totalInches = (int)Math.Round(cm / 2.54);
            return $"{totalInches / 12}′{totalInches % 12}″ ({cm:0.#} cm)";
        }
    }

    [JsonIgnore]
    public string? ChildrenText => Children is int n ? (ChildrenFromList ? $"{n} (as listed)" : n.ToString(CultureInfo.InvariantCulture)) : null;

    private static string? FormatDate(string? iso)
    {
        if (iso == null) return null;
        var parts = iso.Split('-', StringSplitOptions.RemoveEmptyEntries);
        // A leading minus (years before 1 CE) splits oddly; Wikidata people born then are not what this is for.
        if (iso.StartsWith('-') || parts.Length == 0 || !int.TryParse(parts[0], out var year)) return iso;
        if (parts.Length == 1) return year.ToString(CultureInfo.InvariantCulture);
        if (!int.TryParse(parts[1], out var month) || month is < 1 or > 12) return year.ToString(CultureInfo.InvariantCulture);
        var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month);
        if (parts.Length == 2) return $"{monthName} {year}";
        return int.TryParse(parts[2], out var day) ? $"{monthName} {day}, {year}" : $"{monthName} {year}";
    }
}

/// <summary>
/// "Look up on Wikidata": finds people by name and reads their facts. Wikidata's data is CC0. Only the name the user typed
/// (or, later, a Wikidata id) is sent; nothing about photos. Run only when the user asks.
/// </summary>
public sealed class PersonLookup
{
    private const string Api = "https://www.wikidata.org/w/api.php";
    private const string Human = "Q5";

    private readonly HttpClient _http;

    public PersonLookup(HttpClient http) => _http = http;

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Wikimedia asks every app to identify itself with a contact address.
        // TODO: same placeholder as CameraImageService; replace both with a real contact before release.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ocula/0.1 (contact: you@example.com)");
        return client;
    }

    // ---------- Search ----------

    /// <summary>Items called <paramref name="name"/> that are people, best match first. Many people share a name, so the user picks.</summary>
    public async Task<List<WikidataCandidate>> SearchPeopleAsync(string name, CancellationToken cancel = default)
    {
        name = name.Trim();
        if (name.Length == 0) return new List<WikidataCandidate>();

        var url = $"{Api}?action=wbsearchentities&format=json&language=en&uselang=en&type=item&limit=10&search={Uri.EscapeDataString(name)}";
        using var search = JsonDocument.Parse(await _http.GetStringAsync(url, cancel));
        if (!search.RootElement.TryGetProperty("search", out var results)) return new List<WikidataCandidate>();

        var found = new List<WikidataCandidate>();
        foreach (var r in results.EnumerateArray())
        {
            var id = Str(r, "id");
            if (!IsItemId(id)) continue;
            found.Add(new WikidataCandidate(id!, Clean(Str(r, "label")) ?? id!, Clean(Str(r, "description"))));
        }

        // The search can't be limited to people, so ask each match what it is (small requests, run together).
        var kinds = await Task.WhenAll(found.Select(c => IsHumanAsync(c.Id, cancel)));
        return found.Where((_, i) => kinds[i]).ToList();
    }

    private async Task<bool> IsHumanAsync(string id, CancellationToken cancel)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                $"{Api}?action=wbgetclaims&format=json&property=P31&entity={id}", cancel));
            return ItemValues(doc.RootElement, "P31", ranked: false).Contains(Human);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // ---------- Reading a person ----------

    // languagefallback=1 matters: many items now have their name only under Wikidata's "mul" (any language) label, which a plain
    // languages=en request leaves out, so the name would come back empty.

    public async Task<PersonFacts> GetFactsAsync(string id, DateTimeOffset now, CancellationToken cancel = default)
    {
        if (!IsItemId(id)) throw new ArgumentException("Not a Wikidata item id.", nameof(id));

        using var doc = JsonDocument.Parse(await _http.GetStringAsync(
            $"{Api}?action=wbgetentities&format=json&props=claims|labels|descriptions&languages=en&languagefallback=1&ids={id}", cancel));
        var entity = doc.RootElement.GetProperty("entities").GetProperty(id);

        // Where they were born, then the regions it is in (each lookup is small; the place's own entity can be huge).
        var chainIds = new List<string>();
        if (FirstItem(entity, "P19") is { } place)
        {
            chainIds.Add(place);
            for (var i = 0; i < 4; i++)
            {
                var parent = await FirstClaimItemAsync(chainIds[^1], "P131", cancel);
                if (parent == null || chainIds.Contains(parent)) break;
                chainIds.Add(parent);
            }
            var country = await FirstClaimItemAsync(place, "P17", cancel);
            if (country != null && !chainIds.Contains(country)) chainIds.Add(country);
        }

        var wanted = ReferencedItems(entity).Concat(chainIds).Distinct().ToList();
        var labels = await LabelsAsync(wanted, cancel);
        return Build(entity, labels, chainIds, now);
    }

    private async Task<string?> FirstClaimItemAsync(string entityId, string property, CancellationToken cancel)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                $"{Api}?action=wbgetclaims&format=json&property={property}&entity={entityId}", cancel));
            return ItemValues(doc.RootElement, property, ranked: true).FirstOrDefault();
        }
        catch (HttpRequestException)
        {
            return null; // a missing region just gives a shorter place
        }
    }

    private async Task<Dictionary<string, string>> LabelsAsync(IReadOnlyList<string> ids, CancellationToken cancel)
    {
        var labels = new Dictionary<string, string>();
        foreach (var batch in ids.Chunk(50))
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                $"{Api}?action=wbgetentities&format=json&props=labels&languages=en&languagefallback=1&ids={string.Join('|', batch)}", cancel));
            if (!doc.RootElement.TryGetProperty("entities", out var entities)) continue;
            foreach (var entity in entities.EnumerateObject())
            {
                if (entity.Value.TryGetProperty("labels", out var l) && l.TryGetProperty("en", out var en) &&
                    Clean(Str(en, "value")) is { } text)
                    labels[entity.Name] = text;
            }
        }
        return labels;
    }

    // ---------- Turning the answer into a card (no network below this line) ----------


    /// <summary>The items whose names are needed to write the card: occupations, spouses, partners, languages.</summary>
    public static IEnumerable<string> ReferencedItems(JsonElement entity)
    {
        foreach (var property in new[] { "P106", "P26", "P451", "P1412" })
            foreach (var id in ItemValues(entity, property, ranked: true))
                yield return id;
    }

    /// <summary>
    /// Builds the card from one Wikidata entity. <paramref name="labels"/> names the items it refers to;
    /// <paramref name="placeChain"/> is the birth place's item id, then the regions it is in, then the country.
    /// Anything Wikidata doesn't list stays empty.
    /// </summary>
    public static PersonFacts Build(JsonElement entity, IReadOnlyDictionary<string, string> labels,
                                    IReadOnlyList<string> placeChain, DateTimeOffset now)
    {
        string Label(string id) => labels.TryGetValue(id, out var l) ? l : string.Empty;

        var facts = new PersonFacts
        {
            WikidataId = Str(entity, "id") ?? string.Empty,
            RetrievedAt = now
        };
        facts.Name = Clean(EnglishText(entity, "labels")) ?? facts.WikidataId;
        facts.Description = Clean(EnglishText(entity, "descriptions"));
        facts.Sources["name"] = "Wikidata";

        // Full name: only the "birth name" (P1477), which is where middle names usually are.
        var birthNameValue = OrderedClaims(entity, "P1477").Select(MainValue).FirstOrDefault(v => v.ValueKind == JsonValueKind.Object);
        if (Clean(Str(birthNameValue, "text")) is { } birthName)
        {
            facts.FullName = birthName;
            facts.Sources["fullName"] = "Wikidata";
        }

        // Dates.
        if (BestTime(entity, "P569") is { } born)
        {
            facts.BirthDate = born.Text;
            facts.BirthYear = born.Year;
            facts.Sources["birthDate"] = "Wikidata";
        }
        if (BestTime(entity, "P570") is { } died)
        {
            facts.DeathDate = died.Text;
            facts.Sources["deathDate"] = "Wikidata";
        }

        // Place.
        var chain = placeChain.Select(Label).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (chain.Count > 0)
        {
            facts.BirthPlaceChain = chain;
            // "Austin, Texas, United States": the place, the region just below the country, and the country.
            var shown = chain.Count <= 3 ? chain : new List<string> { chain[0], chain[^2], chain[^1] };
            facts.BirthPlace = string.Join(", ", shown);
            facts.Sources["birthPlace"] = "Wikidata";
        }

        // Occupations and languages: the lists, best-ranked first.
        facts.Occupations = ItemValues(entity, "P106", ranked: true).Select(Label).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (facts.Occupations.Count > 0) facts.Sources["occupations"] = "Wikidata";

        facts.Languages = ItemValues(entity, "P1412", ranked: true).Select(Label).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (facts.Languages.Count > 0) facts.Sources["languages"] = "Wikidata";

        // Spouses: with an end date or an end cause they are "former"; without, current.
        foreach (var statement in Claims(entity, "P26"))
        {
            if (IsDeprecated(statement) || ItemId(MainValue(statement)) is not { } sid) continue;
            var name = Label(sid);
            if (name.Length == 0) continue;
            var relation = new Relation(name, sid, QualifierYear(statement, "P580"), QualifierYear(statement, "P582"));
            var ended = relation.ToYear != null || HasQualifier(statement, "P582") || HasQualifier(statement, "P1534");
            (ended ? facts.FormerSpouses : facts.Spouses).Add(relation);
        }
        facts.Spouses = facts.Spouses.DistinctBy(r => r.Id).OrderBy(r => r.FromYear ?? int.MaxValue).ToList();
        facts.FormerSpouses = facts.FormerSpouses.DistinctBy(r => (r.Id, r.FromYear)).OrderBy(r => r.FromYear ?? int.MaxValue).ToList();
        if (facts.Spouses.Count > 0) facts.Sources["spouse"] = "Wikidata";
        if (facts.FormerSpouses.Count > 0) facts.Sources["formerSpouses"] = "Wikidata";

        // Unmarried partners.
        foreach (var statement in Claims(entity, "P451"))
        {
            if (IsDeprecated(statement) || ItemId(MainValue(statement)) is not { } pid) continue;
            var name = Label(pid);
            if (name.Length == 0) continue;
            facts.Partners.Add(new Relation(name, pid, QualifierYear(statement, "P580"), QualifierYear(statement, "P582")));
        }
        facts.Partners = facts.Partners.DistinctBy(r => (r.Id, r.FromYear)).OrderBy(r => r.FromYear ?? int.MaxValue).ToList();
        if (facts.Partners.Count > 0) facts.Sources["partners"] = "Wikidata";

        // Children: the number Wikidata states (P1971); failing that, how many it lists (P40). Never named.
        if (BestQuantity(entity, "P1971") is { } stated && stated >= 0 && stated <= 60 && stated == Math.Floor(stated))
        {
            facts.Children = (int)stated;
            facts.Sources["children"] = "Wikidata";
        }
        else
        {
            var listed = Claims(entity, "P40").Count(c => !IsDeprecated(c));
            if (listed > 0)
            {
                facts.Children = listed;
                facts.ChildrenFromList = true;
                facts.Sources["children"] = "Wikidata";
            }
        }

        // Height, always in centimetres. A unit that isn't recognised is left out rather than guessed.
        foreach (var statement in OrderedClaims(entity, "P2048"))
        {
            if (HeightInCm(MainValue(statement)) is double cm && cm is >= 50 and <= 260)
            {
                facts.HeightCm = Math.Round(cm, 1);
                facts.Sources["height"] = "Wikidata";
                break;
            }
        }

        return facts;
    }

    // ---------- Reading Wikidata's JSON ----------

    private static bool IsItemId(string? id) => id != null && Regex.IsMatch(id, @"^Q[1-9]\d{0,12}$");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Clean(string? text) => CaptureTextIndex.CleanLine(text, 200);

    private static string? EnglishText(JsonElement entity, string section) =>
        entity.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("en", out var en)
            ? Str(en, "value") : null;

    /// <summary>The statements for a property, from either a full entity or a wbgetclaims answer.</summary>
    private static IEnumerable<JsonElement> Claims(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        var holder = root.TryGetProperty("claims", out var claims) ? claims : default;
        if (holder.ValueKind != JsonValueKind.Object || !holder.TryGetProperty(property, out var list) ||
            list.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var statement in list.EnumerateArray()) yield return statement;
    }

    private static bool IsDeprecated(JsonElement statement) => Str(statement, "rank") == "deprecated";

    /// <summary>Statements without the deprecated ones, preferred first (the order Wikidata gives is otherwise kept).</summary>
    private static IEnumerable<JsonElement> OrderedClaims(JsonElement root, string property) =>
        Claims(root, property).Where(c => !IsDeprecated(c)).OrderBy(c => Str(c, "rank") == "preferred" ? 0 : 1);

    private static JsonElement MainValue(JsonElement statement) =>
        statement.TryGetProperty("mainsnak", out var snak) && Str(snak, "snaktype") == "value" &&
        snak.TryGetProperty("datavalue", out var dv) && dv.TryGetProperty("value", out var v)
            ? v : default;

    private static string? ItemId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && Str(value, "id") is { } id && IsItemId(id) ? id : null;

    /// <summary>The item ids a property points to, deprecated statements dropped.</summary>
    private static List<string> ItemValues(JsonElement root, string property, bool ranked) =>
        (ranked ? OrderedClaims(root, property) : Claims(root, property))
            .Select(c => ItemId(MainValue(c))).OfType<string>().ToList();

    private static string? FirstItem(JsonElement root, string property) => ItemValues(root, property, ranked: true).FirstOrDefault();

    private static bool HasQualifier(JsonElement statement, string property) =>
        statement.TryGetProperty("qualifiers", out var q) && q.ValueKind == JsonValueKind.Object &&
        q.TryGetProperty(property, out var list) && list.GetArrayLength() > 0;

    private static readonly Regex TimePattern = new(@"^([+-])(\d{1,9})-(\d\d)-(\d\d)", RegexOptions.Compiled);

    /// <summary>A Wikidata time as "1977-02-02", "1977-02" or "1977", by its stated precision (year is 9, month 10, day 11).</summary>
    internal static (string Text, int Year)? ParseTime(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || Str(value, "time") is not { } time ||
            !value.TryGetProperty("precision", out var p) || p.ValueKind != JsonValueKind.Number)
            return null;
        var match = TimePattern.Match(time);
        if (!match.Success || match.Groups[1].Value != "+" || !int.TryParse(match.Groups[2].Value, out var year)) return null;
        var precision = p.GetInt32();
        if (precision < 9 || year < 1) return null; // a decade or a century isn't a birth date
        var month = match.Groups[3].Value;
        var day = match.Groups[4].Value;
        var y = year.ToString("0000", CultureInfo.InvariantCulture);
        if (precision == 9 || month == "00") return (y, year);
        if (precision == 10 || day == "00") return ($"{y}-{month}", year);
        return ($"{y}-{month}-{day}", year);
    }

    private static (string Text, int Year)? BestTime(JsonElement entity, string property)
    {
        foreach (var statement in OrderedClaims(entity, property))
            if (ParseTime(MainValue(statement)) is { } t) return t;
        return null;
    }

    private static int? QualifierYear(JsonElement statement, string property)
    {
        if (!statement.TryGetProperty("qualifiers", out var q) || q.ValueKind != JsonValueKind.Object ||
            !q.TryGetProperty(property, out var list))
            return null;
        foreach (var snak in list.EnumerateArray())
            if (Str(snak, "snaktype") == "value" && snak.TryGetProperty("datavalue", out var dv) &&
                dv.TryGetProperty("value", out var v) && ParseTime(v) is { } t)
                return t.Year;
        return null;
    }

    private static double? Amount(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && Str(value, "amount") is { } a &&
        double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static double? BestQuantity(JsonElement entity, string property)
    {
        foreach (var statement in OrderedClaims(entity, property))
            if (Amount(MainValue(statement)) is double d) return d;
        return null;
    }

    private static double? HeightInCm(JsonElement value)
    {
        if (Amount(value) is not double amount) return null;
        var unit = Str(value, "unit") ?? string.Empty;
        var factor = unit.Substring(unit.LastIndexOf('/') + 1) switch
        {
            "Q174728" => 1.0,        // centimetre
            "Q11573" => 100.0,       // metre
            "Q174789" => 0.1,        // millimetre
            "Q218593" => 2.54,       // inch
            "Q3710" => 30.48,        // foot
            _ => 0.0
        };
        return factor == 0 ? null : amount * factor;
    }
}
