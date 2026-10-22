using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>
/// Resolves photo tags (and selection) to saved People cards so the library
/// can show the same facts as the People window without another look-up.
/// Matching is local only: tag text ↔ named person group ↔ person_cards JSON.
/// </summary>
public static class PersonCardResolver
{
    /// <summary>Normalize for comparison: trim, collapse spaces, case-insensitive.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var parts = name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    public static bool NamesMatch(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Given tags on one or more photos, return every distinct saved person card
    /// whose group name (or Wikidata display/full name) matches a tag.
    /// </summary>
    public static async Task<List<ResolvedPersonCard>> ResolveFromTagsAsync(
        IndexService library, IEnumerable<string> tags)
    {
        var wanted = tags
            .Select(Normalize)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0) return new List<ResolvedPersonCard>();

        return await library.GetPersonCardsMatchingNamesAsync(wanted);
    }

    /// <summary>
    /// Tags on a single photo row (comma/semicolon separated, as Ocula stores them).
    /// </summary>
    public static IEnumerable<string> SplitTags(string? tagsField)
    {
        if (string.IsNullOrWhiteSpace(tagsField)) yield break;
        foreach (var part in tagsField.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.Length > 0) yield return t;
        }
    }
}

/// <summary>A saved card found via tag ↔ name match.</summary>
public sealed class ResolvedPersonCard
{
    public long PersonId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public PersonFacts Facts { get; init; } = null!;
    /// <summary>Which tag matched this card.</summary>
    public string MatchedTag { get; init; } = string.Empty;
}
