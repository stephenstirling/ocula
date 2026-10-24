using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ocula.Services;

/// <summary>
/// Proposes tags from the words on a page: its title, headings, captions, alt text and file names.
/// It looks for names and topics that repeat, such as a name in the title that also appears in most
/// captions. This reads text only: no pictures are analysed and nothing leaves the PC. Nothing is
/// added until the user ticks it.
/// Body-focused words (BodyFocusedWords: pregnant, bikini, lingerie and so on) are never proposed, whatever the page says.
/// Forum-aware: strips common thread prefixes (e.g. APPEARANCE) and treats dashes as separators so
/// celebrity names in titles like "APPEARANCE - Katy Perry - Attends …" become suggested tags.
/// </summary>
public static class TagSuggester
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","of","in","on","at","to","for","with","from","by","is","are","was","were","this","that",
        "photo","photos","picture","pictures","pic","pics","image","images","img","gallery","galleries","album","albums",
        "page","home","forum","forums","thread","threads","post","posts","reply","share","download","menu","login","log",
        "sign","new","latest","best","more","view","click","here","full","size","thumb","thumbnail","small","large",
        "jpg","jpeg","png","gif","webp","dsc","dscn","img_","web","www","com","org","net","http","https","video","videos",
        "part","set","series","collection","all","top","next","previous","prev","back","read","about","contact","search",
        // Forum / event-title noise (celebrity photo boards)
        "appearance","discussion","update","news","photoshoot","editorial","magazine","cover","covers",
        "annual","awards","award","benefit","fund","performs","perform","attends","attend","during","tour",
        "concert","premiere","red","carpet","los","angeles","new","york","london","paris","milan","tokyo",
        "california","october","january","february","march","april","june","july","august","september","november","december",
        "oct","jan","feb","mar","apr","jun","jul","aug","sep","nov","dec","world","pop","style","instyle"
    };

    /// <summary>Prefixes stripped from forum thread titles before name extraction.</summary>
    private static readonly Regex ForumPrefix = new(
        @"^\s*(?:appearance|discussion|update|news|photos?|pics?|gif|video|request|ot|off[\s-]?topic)\s*[-–—:|]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Phrase
    {
        public string Key = string.Empty;
        public string Display = string.Empty;
        public bool InTitle;
        public int HeadingHits;
        public int ImageHits;
        public bool IsName;
        public double Score;
    }

    public static List<TagChip> Suggest(PageSnapshot page, int max = 6)
    {
        var images = page.Images;
        var total = Math.Max(1, images.Count);
        var phrases = new Dictionary<string, Phrase>();
        var siteWords = SiteWords(page.Url);

        var titleForNames = ForumPrefix.Replace(page.Title ?? string.Empty, "");
        foreach (var phrase in DistinctPhrases(titleForNames, names: true, siteWords)) Merge(phrases, phrase, title: true);
        // Also scan the raw title so nothing is lost if the prefix regex missed.
        if (!string.Equals(titleForNames, page.Title, StringComparison.Ordinal))
            foreach (var phrase in DistinctPhrases(page.Title, names: true, siteWords)) Merge(phrases, phrase, title: true);

        foreach (var heading in page.Headings.Concat(page.Breadcrumbs).Concat(page.Keywords))
            foreach (var phrase in DistinctPhrases(heading, names: true, siteWords)) Merge(phrases, phrase, heading: true);

        // Count how many pictures mention each phrase in their alt text, caption or file name.
        var imageText = images.Select(i => Normalise($"{i.Alt} {i.Caption} {i.FileName}")).ToList();
        foreach (var p in phrases.Values)
            p.ImageHits = imageText.Count(t => t.Contains(p.Key, StringComparison.Ordinal));

        // Topics: ordinary words that appear in many pictures' text (for example "beach", "sunset").
        var wordCounts = new Dictionary<string, int>();
        foreach (var text in imageText)
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct())
                if (word.Length >= 4 && !Stop.Contains(word) && !siteWords.Contains(word) && !word.Any(char.IsDigit) &&
                    !BodyFocusedWords.IsBodyFocused(word))
                    wordCounts[word] = wordCounts.GetValueOrDefault(word) + 1;
        foreach (var (word, count) in wordCounts)
        {
            if (count < 3 || count < total * 0.4) continue;
            if (phrases.Values.Any(p => p.Key.Split(' ').Contains(word))) continue; // already part of a name
            phrases[word] = new Phrase { Key = word, Display = word, ImageHits = count, IsName = false };
        }

        foreach (var p in phrases.Values)
        {
            var share = (double)p.ImageHits / total;
            // Prefer multi-word names that appear in the title (typical celebrity thread titles).
            var nameBonus = p.IsName && p.InTitle && p.Key.Contains(' ') ? 1.5 : 0;
            p.Score = (p.InTitle ? 3.0 : 0) + (p.HeadingHits > 0 ? 1.5 : 0) + share * 5
                      + (p.Key.Contains(' ') ? 0.5 : 0) + nameBonus;
        }

        // Keep phrases with real support, and prefer a longer phrase over the words inside it.
        var good = phrases.Values
            .Where(p => p.InTitle || p.ImageHits >= 2 || p.HeadingHits >= 2)
            .Where(p => p.Score >= 1.5)
            .Where(p => !BodyFocusedWords.IsBodyFocused(p.Key)) // a last check; such words are also kept out of the runs below
            .OrderByDescending(p => p.Score).ThenByDescending(p => p.Key.Length)
            .ToList();
        var chosen = new List<Phrase>();
        foreach (var p in good)
        {
            var covered = chosen.Any(c => c.Key.Contains(p.Key, StringComparison.Ordinal) && c.ImageHits >= p.ImageHits * 0.8);
            if (!covered) chosen.Add(p);
            if (chosen.Count == max) break;
        }

        return chosen.Select(p => new TagChip
        {
            Label = p.IsName ? Title(p.Display) : p.Display,
            Reason = Reason(p, total),
            // Auto-tick title names and phrases that dominate image text.
            On = p.InTitle || (double)p.ImageHits / total >= 0.6
        }).ToList();
    }

    private static string Reason(Phrase p, int total)
    {
        var parts = new List<string>();
        if (p.InTitle) parts.Add("the page title");
        else if (p.HeadingHits > 0) parts.Add("a heading");
        if (p.ImageHits > 0) parts.Add($"{p.ImageHits} of {total} captions or file names");
        return parts.Count == 0 ? "the page text" : "Found in " + string.Join(" and ", parts);
    }

    private static Phrase Merge(Dictionary<string, Phrase> all, Phrase from, bool title = false, bool heading = false)
    {
        if (!all.TryGetValue(from.Key, out var p)) all[from.Key] = p = from;
        p.IsName |= from.IsName;
        if (title) p.InTitle = true;
        if (heading) p.HeadingHits++;
        return p;
    }

    private static IEnumerable<Phrase> DistinctPhrases(string text, bool names, HashSet<string> siteWords)
    {
        var seen = new HashSet<string>();
        foreach (var run in CapitalisedRuns(text))
            for (var len = Math.Min(3, run.Count); len >= 1; len--)
                for (var start = 0; start + len <= run.Count; start++)
                {
                    var words = run.Skip(start).Take(len).ToList();
                    if (!Acceptable(words, siteWords)) continue;
                    var key = string.Join(' ', words).ToLowerInvariant();
                    if (seen.Add(key)) yield return new Phrase { Key = key, Display = string.Join(' ', words), IsName = names };
                }
    }

    private static readonly Regex Word = new(@"[\p{L}][\p{L}'’\-]*", RegexOptions.Compiled);

    /// <summary>Runs of consecutive capitalised words, e.g. "Anna Berg in Lisbon" gives [Anna, Berg] and [Lisbon].</summary>
    private static List<List<string>> CapitalisedRuns(string text)
    {
        var runs = new List<List<string>>();
        var current = new List<string>();
        var last = 0;
        foreach (Match m in Word.Matches(text))
        {
            var gap = text.Substring(last, m.Index - last);
            var capital = char.IsUpper(m.Value[0]);
            // A body-focused word ends the run and is left out, so "Pregnant Busy Philipps" still gives "Busy Philipps".
            var skip = BodyFocusedWords.IsBodyFocused(m.Value);
            // Treat ASCII hyphen and en/em dashes as separators so "Katy Perry - Attends" splits cleanly.
            if (!capital || skip || gap.Any(c => c is ',' or ';' or ':' or '|' or '(' or ')' or '/' or '-' or '–' or '—' or '.' or '!' or '?'))
            {
                if (current.Count > 0) runs.Add(current);
                current = new List<string>();
            }
            if (capital && !skip) current.Add(m.Value);
            last = m.Index + m.Length;
        }
        if (current.Count > 0) runs.Add(current);
        return runs;
    }

    private static bool Acceptable(List<string> words, HashSet<string> siteWords)
    {
        if (words.Any(w => Stop.Contains(w) || siteWords.Contains(w.ToLowerInvariant()))) return false;
        var joined = string.Join(' ', words);
        return joined.Length >= 3 && !joined.Any(char.IsDigit);
    }

    private static HashSet<string> SiteWords(string url)
    {
        var set = new HashSet<string>();
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            foreach (var part in host.Split('.')) if (part.Length > 2 && part != "www") set.Add(part);
        }
        catch { /* no site words */ }
        return set;
    }

    private static string Normalise(string text) =>
        Regex.Replace(text.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ').Replace('.', ' '), @"\s+", " ").Trim();

    private static string Title(string text) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
}
