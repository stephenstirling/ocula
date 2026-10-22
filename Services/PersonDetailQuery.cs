using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ocula.Services;

/// <summary>
/// Parses person-detail phrases from the search box into structured filters
/// against saved person_cards (height, birth, place, occupation, relationships, bra/cup).
/// Local only — never invents facts for people without a card.
/// </summary>
public sealed class PersonDetailQuery
{
    public double? HeightMinCm { get; init; }
    public double? HeightMaxCm { get; init; }
    public int? BirthYearMax { get; init; }      // born before / in-or-before
    public int? BirthYearMin { get; init; }      // born after
    public int? MinAge { get; init; }
    public int? MaxAge { get; init; }
    public string? BirthPlaceContains { get; init; }
    public string? OccupationContains { get; init; }
    public string? LanguageContains { get; init; }
    public bool? HasSpouse { get; init; }
    public bool? HasChildren { get; init; }
    public int? MinChildren { get; init; }

    /// <summary>Minimum cup rank (inclusive). A=1 … B=2 … C=3 …</summary>
    public int? CupMinRank { get; init; }
    public int? CupMaxRank { get; init; }
    public int? BraBandExact { get; init; }
    public string? BraSizeExact { get; init; }   // e.g. 32D

    /// <summary>Human-readable chips for "Understood as".</summary>
    public List<string> UnderstoodAs { get; } = new();

    /// <summary>Parts of the original query that were consumed.</summary>
    public List<string> ConsumedSpans { get; } = new();

    public bool HasPersonFilters =>
        HeightMinCm != null || HeightMaxCm != null ||
        BirthYearMin != null || BirthYearMax != null ||
        MinAge != null || MaxAge != null ||
        BirthPlaceContains != null || OccupationContains != null || LanguageContains != null ||
        HasSpouse != null || HasChildren != null || MinChildren != null ||
        CupMinRank != null || CupMaxRank != null || BraBandExact != null || BraSizeExact != null;

    public static PersonDetailQuery Parse(string raw)
    {
        var q = new PersonDetailQueryBuilder(raw ?? string.Empty);
        q.ParseAll();
        return q.Build();
    }

    /// <summary>US-style cup rank for comparison. DD and E treated as same rank.</summary>
    public static int? CupRank(string? cup) => BraSizeNormalizer.CupRank(cup);

    public static string RankToCup(int rank) => BraSizeNormalizer.RankToDisplay(rank);

    /// <summary>Parse height like 170, 170cm, 5'7", 5ft 7, 5'7.</summary>
    public static double? ParseHeightToCm(string text)
    {
        text = text.Trim();
        var cm = Regex.Match(text, @"^(\d+(?:\.\d+)?)\s*cm$", RegexOptions.IgnoreCase);
        if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
            return c;

        var plain = Regex.Match(text, @"^(\d{2,3}(?:\.\d+)?)$");
        if (plain.Success && double.TryParse(plain.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return p >= 100 && p <= 250 ? p : null; // treat bare 170 as cm

        var ft = Regex.Match(text,
            @"^(\d)\s*(?:'|ft|foot|feet)\s*(\d{1,2}(?:\.\d+)?)\s*(?:""|in|inches)?$",
            RegexOptions.IgnoreCase);
        if (ft.Success)
        {
            var feet = int.Parse(ft.Groups[1].Value, CultureInfo.InvariantCulture);
            var inches = double.Parse(ft.Groups[2].Value, CultureInfo.InvariantCulture);
            return (feet * 12 + inches) * 2.54;
        }

        var ftOnly = Regex.Match(text, @"^(\d)\s*(?:'|ft|foot|feet)$", RegexOptions.IgnoreCase);
        if (ftOnly.Success)
            return int.Parse(ftOnly.Groups[1].Value, CultureInfo.InvariantCulture) * 30.48;

        return null;
    }
}

internal sealed class PersonDetailQueryBuilder
{
    private string _rest;
    private readonly PersonDetailQuery _q = new();

    // mutable fields collected then frozen in Build
    private double? _hMin, _hMax;
    private int? _byMin, _byMax, _ageMin, _ageMax;
    private string? _place, _occ, _lang;
    private bool? _spouse, _children;
    private int? _minChildren;
    private int? _cupMin, _cupMax, _band;
    private string? _braExact;
    private readonly List<string> _chips = new();
    private readonly List<string> _consumed = new();

    public PersonDetailQueryBuilder(string raw) => _rest = raw;

    public void ParseAll()
    {
        // Order: more specific phrases first
        ConsumeBraExact();
        ConsumeCupComparisons();
        ConsumeHeightComparisons();
        ConsumeBorn();
        ConsumeAge();
        ConsumePlace();
        ConsumeOccupation();
        ConsumeLanguage();
        ConsumeSpouse();
        ConsumeChildren();
    }

    public PersonDetailQuery Build() => new PersonDetailQuery
    {
        HeightMinCm = _hMin,
        HeightMaxCm = _hMax,
        BirthYearMin = _byMin,
        BirthYearMax = _byMax,
        MinAge = _ageMin,
        MaxAge = _ageMax,
        BirthPlaceContains = _place,
        OccupationContains = _occ,
        LanguageContains = _lang,
        HasSpouse = _spouse,
        HasChildren = _children,
        MinChildren = _minChildren,
        CupMinRank = _cupMin,
        CupMaxRank = _cupMax,
        BraBandExact = _band,
        BraSizeExact = _braExact,
        UnderstoodAs = { },
        ConsumedSpans = { }
    }.Also(q =>
    {
        foreach (var c in _chips) q.UnderstoodAs.Add(c);
        foreach (var s in _consumed) q.ConsumedSpans.Add(s);
    });

    private void ConsumeBraExact()
    {
        // bra size 32D / 32d
        var m = Regex.Match(_rest, @"\bbra\s*size\s+(\d{2})\s*(AA|DDD|DD|A|B|C|D|E|F|G|H)\b", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(_rest, @"\b(\d{2})\s*(AA|DDD|DD|A|B|C|D|E|F|G|H)\s*bra\b", RegexOptions.IgnoreCase);
        if (!m.Success) return;
        _braExact = m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant();
        _chips.Add($"bra size {_braExact}");
        Eat(m);
    }

    private void ConsumeCupComparisons()
    {
        // Simple phrases → cup rank thresholds (US ladder via BraSizeNormalizer)
        // big / large  → >= D (rank 4)
        // huge / very big → >= DD/E (rank 5)
        // small → <= B (rank 2)
        // tiny → <= A (rank 1)
        if (TrySlangCups()) return;

        // boobs/bust/cup/bra bigger|larger than B [cup]
        // cup >= C, larger than B cup, bigger than b cup
        var patterns = new[]
        {
            @"\b(?:boobs?|bust|breasts?|cup|bra)\s+(?:bigger|larger|greater)\s+than\s+(?:a\s+)?(AA|DDD|DD|[A-K])\s*cups?\b",
            @"\b(?:bigger|larger|greater)\s+than\s+(?:a\s+)?(AA|DDD|DD|[A-K])\s*cups?\b",
            @"\bcup\s*(?:>=|>|at\s+least)\s*(AA|DDD|DD|[A-K])\b",
            @"\b(?:boobs?|bust|cup)\s*(?:>=|>)\s*(AA|DDD|DD|[A-K])\b",
            @"\b(?:boobs?|bust|breasts?|cup|bra)\s+(?:smaller|less)\s+than\s+(?:a\s+)?(AA|DDD|DD|[A-K])\s*cups?\b",
            @"\bcup\s*(?:<=|<|at\s+most)\s*(AA|DDD|DD|[A-K])\b",
        };

        foreach (var pat in patterns)
        {
            var m = Regex.Match(_rest, pat, RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var rank = PersonDetailQuery.CupRank(m.Groups[1].Value);
            if (rank == null) continue;

            var lower = m.Value.ToLowerInvariant();
            if (lower.Contains("smaller") || lower.Contains("less") || lower.Contains("<=") || Regex.IsMatch(lower, @"<\s*[a-k]"))
            {
                _cupMax = rank - (lower.Contains("than") ? 1 : 0);
                if (lower.Contains("than")) _cupMax = rank - 1;
                else _cupMax = rank;
                _chips.Add($"cup ≤ {PersonDetailQuery.RankToCup(_cupMax.Value)}");
            }
            else
            {
                // bigger than B → min rank C (rank B+1); >= B → min B
                if (lower.Contains("than"))
                    _cupMin = rank + 1;
                else
                    _cupMin = rank;
                _chips.Add($"cup ≥ {PersonDetailQuery.RankToCup(_cupMin.Value)}");
            }
            Eat(m);
            return; // one cup constraint per query is enough for v1
        }
    }


    /// <summary>
    /// Maps everyday phrases to cup ranks so search can be as simple as "big boobs".
    /// Thresholds are product defaults, not medical claims.
    /// </summary>
    private bool TrySlangCups()
    {
        // huge / very big / enormous → DD/E+
        var m = Regex.Match(_rest,
            @"\b(?:huge|enormous|massive|very\s+big|super\s+big)\s+(?:boobs?|bust|breasts?|tits?)\b",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(_rest,
                @"\b(?:boobs?|bust|breasts?|tits?)\s+(?:are\s+)?(?:huge|enormous|massive)\b",
                RegexOptions.IgnoreCase);
        if (m.Success)
        {
            _cupMin = 5;
            _chips.Add("cup ≥ DD/E (huge)");
            Eat(m);
            return true;
        }

        // big / large / busty → D+
        m = Regex.Match(_rest,
            @"\b(?:big|large|busty)\s+(?:boobs?|bust|breasts?|tits?)\b",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(_rest,
                @"\b(?:boobs?|bust|breasts?|tits?)\s+(?:are\s+)?(?:big|large)\b",
                RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(_rest, @"\bbusty\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            _cupMin = 4;
            _chips.Add("cup ≥ D (big)");
            Eat(m);
            return true;
        }

        // small → ≤ B
        m = Regex.Match(_rest,
            @"\bsmall\s+(?:boobs?|bust|breasts?|tits?)\b",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(_rest,
                @"\b(?:boobs?|bust|breasts?|tits?)\s+(?:are\s+)?small\b",
                RegexOptions.IgnoreCase);
        if (m.Success)
        {
            _cupMax = 2;
            _chips.Add("cup ≤ B (small)");
            Eat(m);
            return true;
        }

        // tiny / flat → ≤ A
        m = Regex.Match(_rest,
            @"\b(?:tiny|flat)\s+(?:boobs?|bust|breasts?|tits?|chested)?\b",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(_rest, @"\bflat[- ]?chested\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            _cupMax = 1;
            _chips.Add("cup ≤ A (tiny)");
            Eat(m);
            return true;
        }

        return false;
    }

    private void ConsumeHeightComparisons()
    {
        var m = Regex.Match(_rest,
            @"\b(?:people\s+)?(?:taller|higher)\s+than\s+(\d\s*(?:'|ft|foot|feet)\s*\d{1,2}(?:\.\d+)?\s*(?:""|in)?|\d+(?:\.\d+)?\s*cm|\d{2,3}(?:\.\d+)?))(?=\s|$|[.,;])",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var cm = PersonDetailQuery.ParseHeightToCm(m.Groups[1].Value);
            if (cm != null) { _hMin = cm + 0.01; _chips.Add($"height > {cm:0.#} cm"); Eat(m); }
        }

        m = Regex.Match(_rest,
            @"\b(?:people\s+)?(?:shorter|smaller)\s+than\s+(\d\s*(?:'|ft|foot|feet)\s*\d{1,2}(?:\.\d+)?\s*(?:""|in)?|\d+(?:\.\d+)?\s*cm|\d{2,3}(?:\.\d+)?))(?=\s|$|[.,;])",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var cm = PersonDetailQuery.ParseHeightToCm(m.Groups[1].Value);
            if (cm != null) { _hMax = cm - 0.01; _chips.Add($"height < {cm:0.#} cm"); Eat(m); }
        }

        m = Regex.Match(_rest, @"\bheight\s*(?:>=|>)\s*(\d+(?:\.\d+)?)\s*cm\b", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hMin))
        { _hMin = hMin; _chips.Add($"height ≥ {hMin:0.#} cm"); Eat(m); }
    }

    private void ConsumeBorn()
    {
        var m = Regex.Match(_rest, @"\bborn\s+before\s+(\d{4})\b", RegexOptions.IgnoreCase);
        if (m.Success) { _byMax = int.Parse(m.Groups[1].Value) - 1; _chips.Add($"born ≤ {_byMax}"); Eat(m); }

        m = Regex.Match(_rest, @"\bborn\s+after\s+(\d{4})\b", RegexOptions.IgnoreCase);
        if (m.Success) { _byMin = int.Parse(m.Groups[1].Value) + 1; _chips.Add($"born ≥ {_byMin}"); Eat(m); }

        m = Regex.Match(_rest, @"\bborn\s+in\s+(\d{4})\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var y = int.Parse(m.Groups[1].Value);
            _byMin = y; _byMax = y; _chips.Add($"born {y}"); Eat(m);
        }
    }

    private void ConsumeAge()
    {
        var m = Regex.Match(_rest, @"\bover\s+(\d{1,2})\b", RegexOptions.IgnoreCase);
        if (m.Success) { _ageMin = int.Parse(m.Groups[1].Value) + 1; _chips.Add($"age ≥ {_ageMin}"); Eat(m); }

        m = Regex.Match(_rest, @"\bunder\s+(\d{1,2})\b", RegexOptions.IgnoreCase);
        if (m.Success) { _ageMax = int.Parse(m.Groups[1].Value) - 1; _chips.Add($"age ≤ {_ageMax}"); Eat(m); }
    }

    private void ConsumePlace()
    {
        var m = Regex.Match(_rest, @"\bborn\s+in\s+([A-Za-z][A-Za-z\s\-]+?)(?=\s+(?:and|or|tag:|rating)|$)", RegexOptions.IgnoreCase);
        if (!m.Success) return;
        var place = m.Groups[1].Value.Trim();
        if (Regex.IsMatch(place, @"^\d{4}$")) return; // year handled elsewhere
        if (place.Length < 2) return;
        _place = place;
        _chips.Add($"born in {place}");
        Eat(m);
    }

    private void ConsumeOccupation()
    {
        foreach (var job in new[] { "actress", "actor", "singer", "musician", "model", "athlete", "writer", "director" })
        {
            var m = Regex.Match(_rest, $@"\b{job}s?\b", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            _occ = job;
            _chips.Add(job);
            Eat(m);
            return;
        }
    }

    private void ConsumeLanguage()
    {
        var m = Regex.Match(_rest, @"\bspeaks\s+([A-Za-z]+)\b", RegexOptions.IgnoreCase);
        if (!m.Success) return;
        _lang = m.Groups[1].Value;
        _chips.Add($"speaks {_lang}");
        Eat(m);
    }

    private void ConsumeSpouse()
    {
        if (Regex.IsMatch(_rest, @"\b(?:married|has\s+spouse|with\s+spouse)\b", RegexOptions.IgnoreCase))
        {
            var m = Regex.Match(_rest, @"\b(?:married|has\s+spouse|with\s+spouse)\b", RegexOptions.IgnoreCase);
            _spouse = true; _chips.Add("married"); Eat(m);
        }
    }

    private void ConsumeChildren()
    {
        var m = Regex.Match(_rest, @"\bhas\s+children\b", RegexOptions.IgnoreCase);
        if (m.Success) { _children = true; _chips.Add("has children"); Eat(m); }

        m = Regex.Match(_rest, @"\b(?:with\s+)?(\d+)\s+children\b", RegexOptions.IgnoreCase);
        if (m.Success) { _minChildren = int.Parse(m.Groups[1].Value); _chips.Add($"≥ {_minChildren} children"); Eat(m); }
    }

    private void Eat(Match m)
    {
        _consumed.Add(m.Value);
        _rest = _rest.Remove(m.Index, m.Length);
    }
}

file static class PersonDetailQueryExt
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
