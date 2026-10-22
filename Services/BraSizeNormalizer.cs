using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ocula.Services;

/// <summary>
/// Turns free-text bra sizes from CelebrityBraSizes, Boobpedia, CelebrityInside,
/// Babepedia, etc. into a single quantifiable form for storage and search.
///
/// Quantities:
///   BandInches  — underbust band in US inches when known (32, 34, …)
///   CupRank     — ordered cup volume index (AA=0 … K=11); comparable across sources
///   CupLetter   — normalized letter (DD not E when source said DD)
///   SizeKey     — canonical display "32D" or cup-only "D"
///   VolumeIndex — BandInches * 10 + CupRank when band known; else CupRank only
///                 (rough sort key, not a real cc measurement)
/// </summary>
public sealed class NormalizedBraSize
{
    public string Raw { get; init; } = "";
    public int? BandInches { get; init; }
    public string CupLetter { get; init; } = "";
    public int CupRank { get; init; } = -1;
    public string SizeKey { get; init; } = "";
    public int? VolumeIndex { get; init; }
    public bool IsCupOnly => BandInches == null && CupRank >= 0;
    public bool Ok => CupRank >= 0;

    public override string ToString() => SizeKey;
}

public static class BraSizeNormalizer
{
    // US-oriented cup ladder. DD and E share rank (common sister mapping);
    // we keep the letter the source used for display.
    private static readonly (string Letter, int Rank)[] Cups =
    {
        ("AA", 0), ("AAA", 0),
        ("A", 1),
        ("B", 2),
        ("C", 3),
        ("D", 4),
        ("DD", 5), ("E", 5),
        ("DDD", 6), ("F", 6),
        ("DDDD", 7), ("G", 7),
        ("H", 8),
        ("I", 9),
        ("J", 10),
        ("K", 11),
        ("L", 12),
        ("M", 13),
    };

    /// <summary>
    /// Parse anything like: 32D, 32 D, 34DD, 32F (70F), 70E, C, cup C, 34E (UK),
    /// Measurements 36DD-24-34, Bra/cup size: 32F (70F).
    /// </summary>
    public static NormalizedBraSize Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new NormalizedBraSize { Raw = raw ?? "" };

        var text = raw.Trim();

        // US band is typically 28–44 (even). EU underbust is 65–100 cm.
        // Match band+cup first, then classify by range so "70E" is EU not US 70.
        var bandCup = Regex.Match(text,
            @"\b(\d{2})\s*(AA|AAA|DDDD|DDD|DD|[A-M])\b",
            RegexOptions.IgnoreCase);
        if (bandCup.Success)
        {
            var n = int.Parse(bandCup.Groups[1].Value, CultureInfo.InvariantCulture);
            var cup = bandCup.Groups[2].Value;
            if (n >= 28 && n <= 48)
                return FromParts(text, n, cup);           // US inches
            if (n >= 60 && n <= 100)
                return FromParts(text, EuCmToUsBand(n), cup); // EU cm → US
            // Ambiguous odd values: still treat 28–48-like as band if even
            if (n >= 26 && n <= 50)
                return FromParts(text, n, cup);
        }

        // Cup only: "C", "cup size: DD", "Bra/cup size: C"
        var cupOnly = Regex.Match(text,
            @"(?:bra\s*/?\s*cup\s*size|cup\s*size|cup)\s*[:\-]?\s*(AA|AAA|DDDD|DDD|DD|[A-M])\b",
            RegexOptions.IgnoreCase);
        if (!cupOnly.Success)
            cupOnly = Regex.Match(text, @"^(AA|AAA|DDDD|DDD|DD|[A-M])$", RegexOptions.IgnoreCase);
        if (cupOnly.Success)
            return FromParts(text, null, cupOnly.Groups[1].Value);

        return new NormalizedBraSize { Raw = text };
    }

    public static int? CupRank(string? cupOrSize) => Parse(cupOrSize).Ok ? Parse(cupOrSize).CupRank : null;

    public static string RankToDisplay(int rank) => rank switch
    {
        0 => "AA",
        1 => "A",
        2 => "B",
        3 => "C",
        4 => "D",
        5 => "DD/E",
        6 => "DDD/F",
        7 => "G",
        8 => "H",
        9 => "I",
        10 => "J",
        11 => "K",
        12 => "L",
        13 => "M",
        _ => rank.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Compare two sizes for search: "bigger than B" uses cup rank only;
    /// when both have bands, VolumeIndex can sort sister sizes roughly.
    /// Returns negative if a &lt; b, 0 if equal rank, positive if a &gt; b.
    /// Cup-only vs band+cup compares on cup rank only.
    /// </summary>
    public static int Compare(NormalizedBraSize a, NormalizedBraSize b)
    {
        if (!a.Ok && !b.Ok) return 0;
        if (!a.Ok) return -1;
        if (!b.Ok) return 1;
        var cup = a.CupRank.CompareTo(b.CupRank);
        if (cup != 0) return cup;
        if (a.BandInches != null && b.BandInches != null)
            return a.BandInches.Value.CompareTo(b.BandInches.Value);
        return 0;
    }

    /// <summary>True if size meets "cup rank &gt;= min" (bigger than B → min rank C).</summary>
    public static bool CupAtLeast(NormalizedBraSize size, int minRank) =>
        size.Ok && size.CupRank >= minRank;

    private static NormalizedBraSize FromParts(string raw, int? band, string cupRaw)
    {
        var letter = cupRaw.ToUpperInvariant();
        var rank = -1;
        foreach (var (L, R) in Cups)
        {
            if (L == letter) { rank = R; break; }
        }
        if (rank < 0) return new NormalizedBraSize { Raw = raw };

        var key = band != null ? $"{band}{letter}" : letter;
        int? volume = band != null ? band * 10 + rank : rank;

        return new NormalizedBraSize
        {
            Raw = raw,
            BandInches = band,
            CupLetter = letter,
            CupRank = rank,
            SizeKey = key,
            VolumeIndex = volume
        };
    }

    /// <summary>EU underbust cm → approximate US band (even numbers).</summary>
    private static int EuCmToUsBand(int cm)
    {
        // Common chart: 70→32, 75→34, 80→36, 85→38, 65→30
        var approx = (int)Math.Round(cm / 2.54 / 2.0) * 2;
        if (approx < 28) approx = 28;
        if (approx > 46) approx = 46;
        return approx;
    }
}
