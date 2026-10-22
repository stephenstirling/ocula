using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ocula.Services;

public enum PersonClauseKind { Height, Age, BornYear, BornPlace, Married, Children, Occupation, Language }

/// <summary>
/// One thing a search asked about people ("taller than 5'3\"", "born in Texas"). It knows how to describe itself as a chip, which
/// words of the typed text it came from (so the chip can be removed), which card fields it needs, and the SQL that tests a saved card.
/// </summary>
public sealed class PersonClause
{
    internal PersonClause(PersonClauseKind kind, string label, int start, int length, string[] requires,
                          Func<Func<object, string>, DateOnly, string> sql, Func<PersonFacts, string?> detail)
    {
        Kind = kind;
        Label = label;
        Start = start;
        Length = length;
        Requires = requires;
        _sql = sql;
        _detail = detail;
    }

    private readonly Func<Func<object, string>, DateOnly, string> _sql;
    private readonly Func<PersonFacts, string?> _detail;

    public PersonClauseKind Kind { get; }

    /// <summary>The chip text, for example "Height over 5 ft 3 in (1.60 m)".</summary>
    public string Label { get; }

    /// <summary>Where the clause's words sit in the typed text.</summary>
    public int Start { get; }
    public int Length { get; }

    /// <summary>Columns of person_cards that must hold a value before this clause can be answered either way.</summary>
    public string[] Requires { get; }

    /// <summary>A SQL condition on the person_cards row aliased <c>c</c>. Values go through <paramref name="param"/> as parameters.</summary>
    public string ToSql(Func<object, string> param, DateOnly today) => _sql(param, today);

    /// <summary>What the card says about this question, for the "Who matched" list (null when the card is silent).</summary>
    public string? Detail(PersonFacts card) => _detail(card);

    /// <summary>The name shown for a missing field: "height", "birth date", ...</summary>
    public string MissingName => Requires.Length == 0 ? string.Empty : ColumnNames.GetValueOrDefault(Requires[0], Requires[0]);

    private static readonly Dictionary<string, string> ColumnNames = new()
    {
        ["height_cm"] = "height",
        ["born_min"] = "birth date",
        ["birth_place"] = "birthplace",
        ["children"] = "number of children",
        ["occupations"] = "occupation",
        ["languages"] = "languages",
        ["gender"] = "gender",
    };
}

/// <summary>
/// Reads person questions out of search text: height, age, birth year, birthplace, married, children, occupation and languages,
/// combinable ("actresses born in Texas"). What it recognises is blanked out and the rest is left for the normal search.
/// A word in quotes is never read as a question. An occupation word counts only if a saved card has that occupation (or it is
/// "actress"), so ordinary searches are not changed by this.
/// </summary>
public static class PersonQuery
{
    /// <summary>Words that carry no meaning of their own in "people who are taller than ...".</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "people", "person", "persons", "who", "are", "is", "that", "whose", "and", "the", "a", "all", "with"
    };

    /// <summary>Occupations saved on cards (lower case), refreshed by the library whenever a card is saved or removed.</summary>
    public static volatile IReadOnlyCollection<string> KnownOccupations = Array.Empty<string>();

    public sealed record Result(IReadOnlyList<PersonClause> Clauses, string Remaining);

    /// <summary>
    /// Splits <paramref name="text"/> into person clauses and the text left over. <paramref name="occupations"/> are the occupations
    /// saved on cards (lower case).
    /// </summary>
    public static Result Parse(string text, IReadOnlyCollection<string>? occupations = null)
    {
        var clauses = new List<PersonClause>();
        if (string.IsNullOrWhiteSpace(text)) return new Result(clauses, string.Empty);

        var work = new StringBuilder(text);

        // Takes the first match of a pattern, records its clause and blanks its words.
        bool Take(Regex pattern, Func<Match, PersonClause?> make, string? mask = null)
        {
            var current = work.ToString();
            var found = false;
            foreach (Match m in pattern.Matches(current))
            {
                if (mask != null && Masked(current, m.Index)) continue;
                if (make(m) is not { } clause) continue;
                clauses.Add(clause);
                for (var i = m.Index; i < m.Index + m.Length; i++) work[i] = ' ';
                found = true;
            }
            return found;
        }

        // Height first: 5'3" ends in a double quote, which must not be mistaken for a quoted phrase.
        Take(HeightPattern, m => HeightClause(m));

        // From here on, words inside quotes are literal text.
        bool Masked(string s, int index) => InsideQuotes(s, index);

        Take(AgeOverUnder, m => AgeClause(m), "q");
        Take(AgeExact, m => AgeExactClause(m), "q");
        Take(BornYear, m => BornYearClause(m), "q");
        Take(BornPlace, m => BornPlaceClause(m), "q");
        Take(Married, m => IsNegated(work.ToString(), m.Index) ? null : MarriedClause(m), "q");
        Take(HasChildren, m => ChildrenClause(m), "q");
        Take(Speaks, m => LanguageClause(m), "q");

        // Occupations: only words that are saved on some card.
        if (occupations is { Count: > 0 })
        {
            foreach (var occupation in occupations.OrderByDescending(o => o.Length))
            {
                var pattern = new Regex($@"\b{Regex.Escape(occupation)}(?:s|es)?\b", RegexOptions.IgnoreCase);
                Take(pattern, m => OccupationClause(m, occupation), "q");
            }
        }
        Take(Actress, m => ActressClause(m), "q");

        clauses.Sort((a, b) => a.Start.CompareTo(b.Start));

        if (clauses.Count == 0) return new Result(clauses, text);

        // What is left: drop the filler words, keep everything else (a name, a tag, a quoted phrase).
        var left = Regex.Replace(work.ToString(), @"\s+", " ").Trim();
        var words = left.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Fillers.Contains(w.Trim(',', '.')))
            .Select(w => w.Trim(',', '.'))
            .Where(w => w.Length > 0);
        return new Result(clauses, string.Join(' ', words));
    }

    /// <summary>
    /// The search text with one clause's words taken out (when its chip is removed). If no person question is left, the filler words that
    /// went with it ("people", "who") are dropped too.
    /// </summary>
    public static string Without(string text, PersonClause clause, IReadOnlyCollection<string>? occupations = null)
    {
        var cut = text[..clause.Start] + " " + text[(clause.Start + clause.Length)..];
        cut = Regex.Replace(cut, @"\s+", " ").Trim();
        var again = Parse(cut, occupations);
        if (again.Clauses.Count > 0) return cut;
        return string.Join(' ', cut.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Fillers.Contains(w.Trim(',', '.'))));
    }

    // ---------- SQL that tests every clause on one person ----------

    /// <summary>
    /// The condition for "this card satisfies every clause". The columns are on person_cards, aliased <c>c</c>.
    /// </summary>
    public static string ConditionFor(IEnumerable<PersonClause> clauses, Func<object, string> param, DateOnly today) =>
        string.Join(" AND ", clauses.Select(c => "(" + c.ToSql(param, today) + ")"));

    /// <summary>Photos (by path) of any visible named group whose card satisfies every clause.</summary>
    public static string PhotosSql(IEnumerable<PersonClause> clauses, Func<object, string> param, DateOnly today) =>
        "path IN (SELECT f.path FROM faces f JOIN people p ON p.id = f.person_id JOIN person_cards c ON c.person_id = p.id " +
        $"WHERE p.hidden = 0 AND {ConditionFor(clauses, param, today)})";

    /// <summary>Photos of a visible group whose name (or the card's full name) contains the word.</summary>
    public static string NameSql(string word, Func<object, string> param) =>
        "path IN (SELECT f.path FROM faces f JOIN people p ON p.id = f.person_id LEFT JOIN person_cards c ON c.person_id = p.id " +
        $"WHERE p.hidden = 0 AND (p.name LIKE {param("%" + CaptureTextIndex.EscapeLike(word) + "%")} ESCAPE '\\' " +
        $"OR c.full_name LIKE {param("%" + CaptureTextIndex.EscapeLike(word) + "%")} ESCAPE '\\'))";

    // ---------- Patterns ----------

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // taller than 5'3"   shorter than 5 ft 6   over 160 cm   under 1.8 m
    private static readonly Regex HeightPattern = new(
        @"\b(?<dir>taller|shorter|over|under|above|below)\s+(?:than\s+)?(?:(?<ft>\d)\s*(?:'|′|ft\.?(?!\p{L})|feet\b|foot\b)\s*(?:(?<in>\d{1,2})\s*(?:""|″|''|in(?!\p{L})\.?|inch\b|inches\b)?)?|(?<n>\d+(?:[.,]\d+)?)\s*(?<unit>cm|m|meters?|metres?)\b)",
        Opt);

    // over 40   under 30   older than 40   younger than 30
    private static readonly Regex AgeOverUnder = new(
        @"\b(?<dir>over|under|older\s+than|younger\s+than|above|below)\s+(?<n>\d{1,3})\b(?!\s*(?:cm|m\b|ft|'|""|feet|foot|inches))", Opt);

    // aged 30   age 30   30 years old
    private static readonly Regex AgeExact = new(@"\b(?:aged?\s+(?<n>\d{1,3})\b|(?<n2>\d{1,3})\s+years?\s+old\b)", Opt);

    // born before 1990   born after 1990   born in 1985
    private static readonly Regex BornYear = new(@"\bborn\s+(?<dir>before|after|in)\s+(?<y>(?:1[0-9]|20)\d\d)\b", Opt);

    // born in Texas   born in New York   (up to the next question word)
    private static readonly Regex BornPlace = new(
        @"\bborn\s+in\s+(?<place>[\p{L}][\p{L}.'\- ]*?)(?=\s+(?:and|who|with|speaks?|speaking|married|has|have|taller|shorter|over|under|older|younger|aged?|born)\b|[,;]|$)", Opt);

    private static readonly Regex Married = new(@"\bmarried\b", Opt);
    private static readonly Regex HasChildren = new(@"\b(?:has|have|with)\s+(?:any\s+)?(?:children|kids)\b", Opt);
    private static readonly Regex Speaks = new(@"\bspeak(?:s|ing)?\s+(?<lang>[\p{L}]+)\b", Opt);
    private static readonly Regex Actress = new(@"\bactress(?:es)?\b", Opt);

    // ---------- Clauses ----------

    private static bool InsideQuotes(string s, int index)
    {
        var quotes = 0;
        for (var i = 0; i < index; i++) if (s[i] == '"') quotes++;
        return quotes % 2 == 1;
    }

    private static bool IsNegated(string s, int index)
    {
        var before = s[..index].TrimEnd().ToLowerInvariant();
        return before.EndsWith(" not") || before == "not" || before.EndsWith("no longer");
    }

    private static double ParseDecimal(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static PersonClause? HeightClause(Match m)
    {
        double cm;
        if (m.Groups["ft"].Success)
        {
            var feet = int.Parse(m.Groups["ft"].Value, CultureInfo.InvariantCulture);
            var inches = m.Groups["in"].Success ? int.Parse(m.Groups["in"].Value, CultureInfo.InvariantCulture) : 0;
            if (inches >= 12) return null;
            cm = (feet * 12 + inches) * 2.54;
        }
        else
        {
            var n = ParseDecimal(m.Groups["n"].Value);
            cm = m.Groups["unit"].Value.Equals("cm", StringComparison.OrdinalIgnoreCase) ? n : n * 100;
        }
        if (cm is < 50 or > 260) return null;

        var taller = m.Groups["dir"].Value.ToLowerInvariant() is "taller" or "over" or "above";
        var label = $"Height {(taller ? "over" : "under")} {FeetInches(cm)} ({cm / 100:0.00} m)";
        return new PersonClause(PersonClauseKind.Height, label, m.Index, m.Length, new[] { "height_cm" },
            (param, _) => $"c.height_cm {(taller ? ">" : "<")} {param(Math.Round(cm, 2))}",
            card => card.HeightCm is null ? null : $"{card.HeightCm.Value / 100:0.00} m ({FeetInches(card.HeightCm.Value)})");
    }

    /// <summary>5 ft 3 in.</summary>
    public static string FeetInches(double cm)
    {
        var inches = (int)Math.Round(cm / 2.54);
        return inches % 12 == 0 ? $"{inches / 12} ft" : $"{inches / 12} ft {inches % 12} in";
    }

    private static string AgeText(PersonFacts card, DateOnly today)
    {
        if (card.BornRange is not { } range || card.DeathDate != null) return card.DeathDate != null ? "died " + card.DeathDateText : string.Empty;
        // A person whose date is only known to the year may be one of two ages.
        var min = AgeOn(DateOnly.ParseExact(range.Max, "yyyy-MM-dd", CultureInfo.InvariantCulture), today);
        var max = AgeOn(DateOnly.ParseExact(range.Min, "yyyy-MM-dd", CultureInfo.InvariantCulture), today);
        return min == max ? $"age {min}" : $"age {min}–{max}";
    }

    private static int AgeOn(DateOnly born, DateOnly today)
    {
        var age = today.Year - born.Year;
        if (born > today.AddYears(-age)) age--;
        return age;
    }

    private static PersonClause? AgeClause(Match m)
    {
        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (n is < 1 or > 120) return null;
        var over = m.Groups["dir"].Value.ToLowerInvariant() is "over" or "above" || m.Groups["dir"].Value.StartsWith("older", StringComparison.OrdinalIgnoreCase);
        // Over 40 means older than 40: born before the day 40 years ago. Only living people have an age.
        return new PersonClause(PersonClauseKind.Age, $"Age {(over ? "over" : "under")} {n}", m.Index, m.Length, new[] { "born_min" },
            (param, today) => over
                ? $"c.death_date IS NULL AND c.born_max < {param(today.AddYears(-n).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}"
                : $"c.death_date IS NULL AND c.born_min > {param(today.AddYears(-n).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
            card => AgeText(card, DateOnly.FromDateTime(DateTime.Today)));
    }

    private static PersonClause? AgeExactClause(Match m)
    {
        var raw = m.Groups["n"].Success ? m.Groups["n"].Value : m.Groups["n2"].Value;
        var n = int.Parse(raw, CultureInfo.InvariantCulture);
        if (n is < 1 or > 120) return null;
        return new PersonClause(PersonClauseKind.Age, $"Age {n}", m.Index, m.Length, new[] { "born_min" },
            (param, today) =>
                $"c.death_date IS NULL AND c.born_min > {param(today.AddYears(-n - 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))} " +
                $"AND c.born_max <= {param(today.AddYears(-n).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
            card => AgeText(card, DateOnly.FromDateTime(DateTime.Today)));
    }

    private static PersonClause? BornYearClause(Match m)
    {
        var year = m.Groups["y"].Value;
        var dir = m.Groups["dir"].Value.ToLowerInvariant();
        return new PersonClause(PersonClauseKind.BornYear, $"Born {dir} {year}", m.Index, m.Length, new[] { "born_min" },
            (param, _) => dir switch
            {
                "before" => $"c.born_max < {param(year + "-01-01")}",
                "after" => $"c.born_min > {param(year + "-12-31")}",
                _ => $"c.born_min >= {param(year + "-01-01")} AND c.born_max <= {param(year + "-12-31")}"
            },
            card => card.BirthDateText is { } d ? "born " + d : null);
    }

    private static PersonClause? BornPlaceClause(Match m)
    {
        var place = m.Groups["place"].Value.Trim();
        if (place.Length < 2 || place.Length > 60) return null;
        var shown = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(place.ToLowerInvariant());
        return new PersonClause(PersonClauseKind.BornPlace, $"Born in {shown}", m.Index, m.Length, new[] { "birth_place" },
            (param, _) => $"c.birth_place LIKE {param("%" + CaptureTextIndex.EscapeLike(place) + "%")} ESCAPE '\\'",
            card => card.BirthPlace is { } b ? "born in " + b : null);
    }

    private static PersonClause? MarriedClause(Match m) =>
        // Wikidata often has no spouse for a married person, and has none for an unmarried one either, so "married" can only
        // mean "has a current spouse on record"; nobody is counted as unknown.
        new PersonClause(PersonClauseKind.Married, "Married", m.Index, m.Length, Array.Empty<string>(),
            (_, _) => "c.spouse IS NOT NULL",
            card => card.Spouses.Count > 0 ? "married to " + string.Join(", ", card.Spouses.Select(s => s.Name)) : null);

    private static PersonClause? ChildrenClause(Match m) =>
        new PersonClause(PersonClauseKind.Children, "Has children", m.Index, m.Length, new[] { "children" },
            (_, _) => "c.children > 0",
            card => card.Children is int n ? (n == 1 ? "1 child" : $"{n} children") : null);

    private static PersonClause? LanguageClause(Match m)
    {
        var language = m.Groups["lang"].Value;
        var shown = char.ToUpperInvariant(language[0]) + language[1..].ToLowerInvariant();
        return new PersonClause(PersonClauseKind.Language, $"Speaks {shown}", m.Index, m.Length, new[] { "languages" },
            (param, _) => $"c.languages LIKE {param("%" + CaptureTextIndex.EscapeLike(language) + "%")} ESCAPE '\\'",
            card => card.Languages.Count > 0 ? "speaks " + string.Join(", ", card.Languages) : null);
    }

    private static PersonClause? OccupationClause(Match m, string occupation) =>
        new PersonClause(PersonClauseKind.Occupation, "Occupation: " + occupation, m.Index, m.Length, new[] { "occupations" },
            (param, _) => $"c.occupations LIKE {param("%" + CaptureTextIndex.EscapeLike(occupation) + "%")} ESCAPE '\\'",
            card => card.Occupations.Count > 0 ? string.Join(", ", card.Occupations.Take(3)) : null);

    // Wikidata files actresses under "actor"; the card's gender tells them apart.
    private static PersonClause? ActressClause(Match m) =>
        new PersonClause(PersonClauseKind.Occupation, "Actress", m.Index, m.Length, new[] { "occupations", "gender" },
            (_, _) => "c.occupations LIKE '%actor%' AND c.gender = 'female'",
            card => card.Occupations.Count > 0 ? string.Join(", ", card.Occupations.Take(3)) : null);
}
