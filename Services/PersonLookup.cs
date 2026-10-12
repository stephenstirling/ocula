using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>
/// "Look up on Wikidata" in People. Only runs when you press the button, and only the name you
/// typed is sent (then the Wikidata id of the person you pick). Nothing is looked up for groups
/// you haven't named, and no photo or face ever leaves the PC.
///
/// Wikidata first (CC0). A height Wikidata doesn't have is filled from DBpedia (CC BY-SA), which
/// the card then credits. Anything still missing stays blank; nothing is guessed.
/// </summary>
public static class PersonLookup
{
    private const string Api = "https://www.wikidata.org/w/api.php?format=json&";

    /// <summary>People (instances of "human", Q5) matching a name, best first, with a short description.</summary>
    public static async Task<IReadOnlyList<PersonMatch>> SearchAsync(string name, CancellationToken cancel)
    {
        using var search = await GetJsonAsync(Api + "action=query&formatversion=2&list=search&srnamespace=0&srlimit=8&srprop=&srsearch="
                                         + Uri.EscapeDataString(name + " haswbstatement:P31=Q5"), cancel);
        var ids = PersonCards.SearchIds(search.RootElement);
        if (ids.Count == 0) return Array.Empty<PersonMatch>();

        using var terms = await GetJsonAsync(Api + "action=wbgetentities&props=labels|descriptions&languages=en&languagefallback=1&ids="
                                             + string.Join('|', ids), cancel);
        return PersonCards.Matches(terms.RootElement, ids);
    }

    /// <summary>The person card for a chosen match.</summary>
    public static async Task<PersonCardData> GetCardAsync(PersonMatch match, CancellationToken cancel)
    {
        using var root = await GetJsonAsync(Api + "action=wbgetentities&props=labels|descriptions|claims&languages=en&languagefallback=1&ids="
                                            + match.Id, cancel);
        if (!root.RootElement.TryGetProperty("entities", out var entities) || !entities.TryGetProperty(match.Id, out var entity))
            throw new InvalidOperationException("Wikidata didn't return this person.");

        // The country of the birthplace, so the card reads "Barranquilla, Colombia".
        string? countryId = null;
        if (PersonCards.PlaceId(entity) is { } place)
        {
            using var claims = await GetJsonAsync(Api + $"action=wbgetclaims&property=P17&entity={place}", cancel);
            countryId = PersonCards.FirstClaimId(claims.RootElement, "P17");
        }

        var ids = PersonCards.ReferencedIds(entity).Append(countryId).OfType<string>().Distinct().ToList();
        var labels = new Dictionary<string, string>();
        foreach (var chunk in ids.Chunk(50))
        {
            using var names = await GetJsonAsync(Api + "action=wbgetentities&props=labels&languages=en&languagefallback=1&ids="
                                                 + string.Join('|', chunk), cancel);
            foreach (var (id, label) in PersonCards.Labels(names.RootElement)) labels[id] = label;
        }

        var card = PersonCards.Build(entity, labels, countryId, DateTime.Now);
        if (card.HeightCm == null) await FillFromDBpediaAsync(card, cancel);
        return card;
    }

    /// <summary>DBpedia (built from Wikipedia's infoboxes) often has the height Wikidata lacks.</summary>
    private static async Task FillFromDBpediaAsync(PersonCardData card, CancellationToken cancel)
    {
        try
        {
            var query = "SELECT ?h WHERE { ?p <http://www.w3.org/2002/07/owl#sameAs> <http://www.wikidata.org/entity/"
                        + card.WikidataId + "> ; <http://dbpedia.org/ontology/height> ?h } LIMIT 3";
            using var answer = await GetJsonAsync("https://dbpedia.org/sparql?format=json&query=" + Uri.EscapeDataString(query), cancel);
            if (PersonCards.DBpediaHeight(answer.RootElement) is double cm)
            {
                card.HeightCm = cm;
                card.Sources["Height"] = PersonCards.DBpedia;
            }
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested && ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // DBpedia is often slow or down; the card is complete without it.
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancel)
    {
        using var response = await Wikimedia.Http.GetAsync(url, cancel);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
    }
}
