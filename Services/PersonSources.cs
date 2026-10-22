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
    public static void Merge(PersonFacts card, DbpediaFacts? dbpedia, MusicBrainzFacts? musicBrainz)
    {
        if (dbpedia != null) Fill(card, "DBpedia", dbpedia.HeightCm, dbpedia.BirthDate, dbpedia.BirthPlace, dbpedia.DeathDate, dbpedia.DeathPlace, dbpedia.DeathCause);
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
