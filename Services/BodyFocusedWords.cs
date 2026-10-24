using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ocula.Services;

/// <summary>
/// Body-focused words that Ocula never proposes as tags. CLAUDE.md: "Do not add body-focused smart labels (bikini, lingerie,
/// pregnant, etc.)". <see cref="TagSuggester"/> uses this so a headline such as "Pregnant … Shares …" doesn't produce a
/// ticked "Pregnant" chip. It only stops Ocula suggesting them: a tag the user types, or one that arrives in their own
/// ocula: link, is theirs and is never filtered.
/// The list is a starting point chosen without the owner's input. Add or remove words here (whole words, any case; the
/// plural is listed separately).
/// </summary>
public static class BodyFocusedWords
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "pregnant", "pregnancy", "bump",
        "bikini", "bikinis", "swimsuit", "swimsuits", "swimwear", "lingerie", "underwear", "panties", "thong", "bra", "bras",
        "topless", "nude", "nudes", "naked", "braless", "cleavage", "busty", "boobs", "breasts", "sexy",
    };

    private static readonly Regex Letters = new(@"\p{L}+", RegexOptions.Compiled);

    /// <summary>
    /// True when any whole word in the text is on the list. Hyphens, apostrophes, underscores and spaces separate words, so
    /// "Bikini-clad" and "baby_bump_01" match, while "Brazil", "Busy" and "Bumpkin" do not.
    /// </summary>
    public static bool IsBodyFocused(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (Match m in Letters.Matches(text))
            if (Words.Contains(m.Value)) return true;
        return false;
    }
}
