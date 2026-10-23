using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ocula.Services;

/// <summary>
/// Builds stable, searchable file keywords from a saved person card. This is a pure
/// transformation: it never performs a lookup or guesses missing attributes.
/// </summary>
public static class PersonCardTagBuilder
{
    private const int RelationKeywordLimit = 20;

    public static IReadOnlyList<string> FromFacts(PersonFacts facts, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var tags = new List<string>();

        void Add(string prefix, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                tags.Add($"{prefix}:{value.Trim()}");
        }

        Add("person", string.IsNullOrWhiteSpace(displayName) ? facts.Name : displayName);
        Add("born", facts.BirthDate);
        Add("born-place", facts.BirthPlace);

        // A death place or cause is only meaningful when the saved card says the person is deceased.
        if (!string.IsNullOrWhiteSpace(facts.DeathDate))
        {
            Add("died", facts.DeathDate);
            Add("death-place", facts.DeathPlace);
            Add("death-cause", facts.DeathCause);
        }

        if (facts.HeightCm is double height)
            Add("height-cm", height.ToString("0.##", CultureInfo.InvariantCulture));

        // Evidence belongs on the card, not in the file keyword.
        Add("bra", facts.BraSize);

        foreach (var occupation in facts.Occupations)
            Add("occupation", occupation);

        foreach (var spouse in facts.Spouses)
            Add("spouse", spouse.Name);
        foreach (var spouse in facts.FormerSpouses)
            Add("former-spouse", spouse.Name);
        foreach (var partner in facts.Partners.Take(RelationKeywordLimit))
            Add("partner", partner.Name);
        foreach (var child in facts.ChildrenDetails.Take(RelationKeywordLimit))
            Add("child", child.Name);
        foreach (var language in facts.Languages)
            Add("language", language);

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Add non-empty keywords only when no case-insensitive equivalent exists.</summary>
    public static void MergeInto(IList<string> existingTags, IEnumerable<string> additions)
    {
        ArgumentNullException.ThrowIfNull(existingTags);
        ArgumentNullException.ThrowIfNull(additions);

        var seen = new HashSet<string>(
            existingTags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var addition in additions)
        {
            if (string.IsNullOrWhiteSpace(addition)) continue;
            var tag = addition.Trim();
            if (seen.Add(tag)) existingTags.Add(tag);
        }
    }
}
