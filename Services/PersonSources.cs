using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>What DBpedia (CC BY-SA, derived from Wikipedia) says about a person. Heights are in centimetres.</summary>
public sealed record DbpediaFacts(double? HeightCm, string? BirthDate, string? BirthPlace, string? DeathDate, string? DeathPlace, string? DeathCause);

/// <summary>What MusicBrainz (CC0) says about a person who is an artist there.</summary>
public sealed record MusicBrainzFacts(string? BirthDate, string? BirthPlace, string? DeathDate, string? DeathPlace);

/// <summary>
/// Second sources for a person card, used only to fill what Wikidata left empty (see <see cref="Merge"/>). Both are asked about
/// the person the user picked on Wikidata, by an id that Wikidata gave (the English Wikipedia title, the MusicBrainz id): the name the user typed,
/// photos and faces are never sent. A failure of either is not an error; the card just has less.
/// </summary>
public sealed class PersonSources
{
    private readonly HttpClient _http;

    public PersonSources(HttpClient http) => _http = http;

    // ---------- DBpedia ----------

    /// <summary>The resource address for an English Wikipedia title. Characters that could end or change a SPARQL address are escaped.</summary>
    internal static string DbpediaIri(string title)
    {
        var sb = new StringBuilder("http://dbpedia.org/resource/");
        foreach (var c in title.Trim().Replace(' ', '_'))
        {
            if (c < 0x20 || "<>\"{}|^`\\%#?".IndexOf(c) >= 0)
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    internal static string DbpediaQuery(string title)
    {
        var p = "<" + DbpediaIri(title) + ">";
        return "SELECT ?height ?birthDate ?birthPlaceLabel ?deathDate ?deathPlaceLabel ?deathCauseLabel WHERE { " +
               $"OPTIONAL {{ {p} dbo:height ?height }} OPTIONAL {{ {p} dbo:birthDate ?birthDate }} " +
               $"OPTIONAL {{ {p} dbo:birthPlace ?bp . ?bp rdfs:label ?birthPlaceLabel . FILTER(lang(?birthPlaceLabel) = 'en') }} " +
               $"OPTIONAL {{ {p} dbo:deathDate ?deathDate }} " +
               $"OPTIONAL {{ {p} dbo:deathPlace ?dp . ?dp rdfs:label ?deathPlaceLabel . FILTER(lang(?deathPlaceLabel) = 'en') }} " +
               $"OPTIONAL {{ {p} dbo:deathCause ?dc . ?dc rdfs:label ?deathCauseLabel . FILTER(lang(?deathCauseLabel) = 'en') }} }} LIMIT 20";
    }

    public async Task<DbpediaFacts?> GetDbpediaAsync(string wikipediaTitle, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(wikipediaTitle) || wikipediaTitle.Length > 250) return null;
        var url = "https://dbpedia.org/sparql?format=" + Uri.EscapeDataString("application/sparql-results+json") +
                  "&query=" + Uri.EscapeDataString(DbpediaQuery(wikipediaTitle));
        return ParseDbpedia(await _http.GetStringAsync(url, cancel));
    }

    internal static DbpediaFacts? ParseDbpedia(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || !results.TryGetProperty("bindings", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                return null;

            string? First(string variable)
            {
                foreach (var row in rows.EnumerateArray())
                    if (row.TryGetProperty(variable, out var b) && b.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String &&
                        CaptureTextIndex.CleanLine(v.GetString(), 200) is { } text)
                        return text;
                return null;
            }

            // DBpedia gives metres; the value is checked for sanity when it is merged (a height of 0.0 occurs).
            double? height = First("height") is { } raw && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var metres)
                ? metres * 100 : null;

            return new DbpediaFacts(height, First("birthDate"), First("birthPlaceLabel"), First("deathDate"), First("deathPlaceLabel"),
                                    First("deathCauseLabel"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- Wikipedia (the height in the article's infobox) ----------

    /// <summary>
    /// The height in the infobox of the person's English Wikipedia article (Wikipedia text is CC BY-SA). Only the article's introduction
    /// section is read, where the infobox is. Many articles have no height (most actors and singers); athletes usually do.
    /// </summary>
    public async Task<double?> GetWikipediaHeightAsync(string wikipediaTitle, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(wikipediaTitle) || wikipediaTitle.Length > 250) return null;
        var url = "https://en.wikipedia.org/w/api.php?action=query&prop=revisions&rvprop=content&rvslots=main&rvsection=0&redirects=1" +
                  "&format=json&formatversion=2&titles=" + Uri.EscapeDataString(wikipediaTitle);
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, cancel));
            var text = doc.RootElement.GetProperty("query").GetProperty("pages")[0].GetProperty("revisions")[0]
                .GetProperty("slots").GetProperty("main").GetProperty("content").GetString();
            return text == null ? null : ParseInfoboxHeightCm(text);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;   // no such page, or an answer of another shape
        }
    }

    private static readonly Regex HeightField = new(@"(?im)^\s*\|\s*height\s*=\s*(?<v>.*)$", RegexOptions.Compiled);
    private static readonly Regex HeightTemplate = new(@"\{\{\s*height\s*\|(?<args>[^{}]*)\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ConvertTemplate = new(
        @"\{\{\s*(?:convert|cvt)\s*\|\s*(?<n>\d+(?:\.\d+)?)\s*\|\s*(?<unit>cm|m|ft|in)\b(?:\s*\|\s*(?<n2>\d+(?:\.\d+)?)\s*\|\s*(?<unit2>in)\b)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlainMetric = new(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<unit>cm|m)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlainImperial = new(@"(?<ft>\d)\s*(?:ft|feet|foot|')\s*(?:(?<in>\d{1,2}(?:\.\d+)?)\s*(?:in|inches|"")?)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The height in centimetres from the "height" line of an infobox: {{height|m=1.88}}, {{height|cm=188}}, {{height|ft=6|in=2}},
    /// {{convert|1.88|m|ftin}}, {{convert|6|ft|2|in}}, or plain text such as "1.88 m" or "6 ft 2 in". Null if there is none, or it is
    /// not a believable height (50 to 260 cm).
    /// </summary>
    internal static double? ParseInfoboxHeightCm(string wikitext)
    {
        var line = HeightField.Match(wikitext);
        if (!line.Success) return null;
        var value = line.Groups["v"].Value;
        double? cm = null;

        if (HeightTemplate.Match(value) is { Success: true } template)
        {
            var args = template.Groups["args"].Value.Split('|')
                .Select(a => a.Split('=', 2)).Where(a => a.Length == 2)
                .ToDictionary(a => a[0].Trim().ToLowerInvariant(), a => a[1].Trim());
            double Num(string key) => args.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            if (args.ContainsKey("cm")) cm = Num("cm");
            else if (args.ContainsKey("m")) cm = Num("m") * 100;
            else if (args.ContainsKey("ft")) cm = (Num("ft") * 12 + Num("in")) * 2.54;
        }
        else if (ConvertTemplate.Match(value) is { Success: true } convert)
        {
            var n = double.Parse(convert.Groups["n"].Value, CultureInfo.InvariantCulture);
            cm = convert.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "cm" => n,
                "m" => n * 100,
                "ft" => (n * 12 + (convert.Groups["n2"].Success ? double.Parse(convert.Groups["n2"].Value, CultureInfo.InvariantCulture) : 0)) * 2.54,
                "in" => n * 2.54,
                _ => null
            };
        }
        else
        {
            // Plain text, with any <ref> or template removed first so a number in a citation is never read.
            var plain = Regex.Replace(value, @"<ref[^>]*?/>|<ref.*?(</ref>|$)|\{\{.*?\}\}", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (PlainMetric.Match(plain) is { Success: true } metric)
            {
                var n = double.Parse(metric.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                cm = metric.Groups["unit"].Value.Equals("cm", StringComparison.OrdinalIgnoreCase) ? n : n * 100;
            }
            else if (PlainImperial.Match(plain) is { Success: true } imperial)
            {
                var inches = imperial.Groups["in"].Success ? double.Parse(imperial.Groups["in"].Value, CultureInfo.InvariantCulture) : 0;
                cm = (double.Parse(imperial.Groups["ft"].Value, CultureInfo.InvariantCulture) * 12 + inches) * 2.54;
            }
        }
        return cm is >= 50 and <= 260 ? Math.Round(cm.Value, 1) : null;
    }

    // ---------- MusicBrainz ----------

    public async Task<MusicBrainzFacts?> GetMusicBrainzAsync(string musicBrainzId, CancellationToken cancel = default)
    {
        if (!Guid.TryParse(musicBrainzId, out var id)) return null;
        return ParseMusicBrainz(await _http.GetStringAsync($"https://musicbrainz.org/ws/2/artist/{id:D}?fmt=json", cancel));
    }

    internal static MusicBrainzFacts? ParseMusicBrainz(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // A band or a character has no birth: only a person.
            if (root.ValueKind != JsonValueKind.Object || !(root.TryGetProperty("type", out var type) && type.GetString() == "Person")) return null;

            string? Str(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? CaptureTextIndex.CleanLine(v.GetString(), 200) : null;

            root.TryGetProperty("life-span", out var life);
            root.TryGetProperty("begin-area", out var beginArea);
            root.TryGetProperty("end-area", out var endArea);
            return new MusicBrainzFacts(Str(life, "begin"), Str(beginArea, "name"), Str(life, "end"), Str(endArea, "name"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- Whether to ask at all ----------

    /// <summary>DBpedia is asked only if the card is missing something DBpedia can give.</summary>
    public static bool WorthAskingDbpedia(PersonFacts card) =>
        card.HeightCm == null || card.BirthDate == null || card.BirthPlace == null ||
        (card.DeathDate != null && (card.DeathPlace == null || card.DeathCause == null));

    /// <summary>Wikipedia is asked only for a height, and only if the card has none.</summary>

    /// <summary>Wikipedia is asked only for a height, and only if the card has none.</summary>
    public static bool WorthAskingWikipedia(PersonFacts card) => card.HeightCm == null;

    /// <summary>MusicBrainz is asked only if the card is missing something it can give.</summary>
    public static bool WorthAskingMusicBrainz(PersonFacts card) =>
        card.BirthDate == null || card.BirthPlace == null || (card.DeathDate != null && card.DeathPlace == null);

    // ---------- Filling the card ----------

    private static readonly Regex PartialDate = new(@"^(?<y>\d{4})(?:-(?<m>\d\d)(?:-(?<d>\d\d))?)?$", RegexOptions.Compiled);

    /// <summary>A date as "1977", "1977-02" or "1977-02-02" if it is one of those and real; otherwise null.</summary>
    internal static string? CleanDate(string? date)
    {
        if (date == null) return null;
        // DBpedia can add a time zone to a date ("1963-08-09Z" or "+02:00").
        var text = Regex.Replace(date.Trim(), @"(Z|[+-]\d\d:\d\d)$", string.Empty);
        var m = PartialDate.Match(text);
        if (!m.Success || !int.TryParse(m.Groups["y"].Value, out var year) || year < 1 || year > DateTime.Now.Year) return null;
        if (m.Groups["m"].Success)
        {
            if (!int.TryParse(m.Groups["m"].Value, out var month) || month is < 1 or > 12) return null;
            if (m.Groups["d"].Success && (!int.TryParse(m.Groups["d"].Value, out var day) || day < 1 || day > DateTime.DaysInMonth(year, month))) return null;
        }
        return text;
    }

    /// <summary>
    /// Fills what Wikidata left empty on <paramref name="card"/> from the second sources, and says which source each fact came from. Nothing
    /// Wikidata has is changed, and a fact that doesn't check out (a height of 0, a date that isn't one) is dropped, not guessed at.
    /// DBpedia is asked first, then MusicBrainz, so a fact both have is credited to DBpedia.
    /// </summary>
    public static void Merge(PersonFacts card, DbpediaFacts? dbpedia, MusicBrainzFacts? musicBrainz, double? wikipediaHeightCm = null)
    {
        if (dbpedia != null) Fill(card, "DBpedia", dbpedia.HeightCm, dbpedia.BirthDate, dbpedia.BirthPlace, dbpedia.DeathDate, dbpedia.DeathPlace, dbpedia.DeathCause);
        if (wikipediaHeightCm != null) Fill(card, "Wikipedia", wikipediaHeightCm, null, null, null, null, null);
        if (musicBrainz != null) Fill(card, "MusicBrainz", null, musicBrainz.BirthDate, musicBrainz.BirthPlace, musicBrainz.DeathDate, musicBrainz.DeathPlace, null);
    }

    private static void Fill(PersonFacts card, string source, double? heightCm, string? birthDate, string? birthPlace,
                             string? deathDate, string? deathPlace, string? deathCause)
    {
        if (card.HeightCm == null && heightCm is >= 50 and <= 260)
        {
            card.HeightCm = Math.Round(heightCm.Value, 1);
            card.Sources["height"] = source;
        }

        if (card.BirthDate == null && CleanDate(birthDate) is { } born)
        {
            card.BirthDate = born;
            card.BirthYear = int.Parse(born[..4], CultureInfo.InvariantCulture);
            card.Sources["birthDate"] = source;
        }
        if (card.BirthPlace == null && birthPlace != null)
        {
            card.BirthPlace = birthPlace;
            card.BirthPlaceChain = new List<string> { birthPlace };
            card.Sources["birthPlace"] = source;
        }

        if (card.DeathDate == null && CleanDate(deathDate) is { } died && (card.BirthDate == null || string.CompareOrdinal(died, card.BirthDate) >= 0))
        {
            card.DeathDate = died;
            card.Sources["deathDate"] = source;
        }
        // Where and of what only go with a death date.
        if (card.DeathDate != null)
        {
            if (card.DeathPlace == null && deathPlace != null) { card.DeathPlace = deathPlace; card.Sources["deathPlace"] = source; }
            if (card.DeathCause == null && deathCause != null) { card.DeathCause = deathCause; card.Sources["deathCause"] = source; }
        }
    }
}
