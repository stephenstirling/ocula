using Microsoft.Data.Sqlite;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// Searching by person details ("taller than 5'3\"", "born in Texas", "actress"): what the parser reads out of the typed text, what it
/// leaves for the normal search, and the SQL it builds, run against a small in-memory library.
/// </summary>
public class PersonQueryTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);
    private static readonly string[] Occupations = { "singer", "actor", "film actor", "politician" };

    private static PersonQuery.Result Parse(string text) => PersonQuery.Parse(text, Occupations);

    private static string[] Labels(string text) => Parse(text).Clauses.Select(c => c.Label).ToArray();

    // ---------- Height ----------

    [Theory]
    [InlineData("people taller than 5'3\"", "Height over 5 ft 3 in (1.60 m)")]
    [InlineData("taller than 5 ft 3", "Height over 5 ft 3 in (1.60 m)")]
    [InlineData("taller than 5ft3in", "Height over 5 ft 3 in (1.60 m)")]
    [InlineData("shorter than 5 ft 6", "Height under 5 ft 6 in (1.68 m)")]
    [InlineData("over 160 cm", "Height over 5 ft 3 in (1.60 m)")]
    [InlineData("under 1.8 m", "Height under 5 ft 11 in (1.80 m)")]
    [InlineData("taller than 6'", "Height over 6 ft (1.83 m)")]
    public void Heights_are_read_in_feet_and_inches_or_metric(string text, string expected)
    {
        Assert.Equal(new[] { expected }, Labels(text));
    }

    [Theory]
    [InlineData("taller than 20 ft")]      // not a person
    [InlineData("taller than 5'14\"")]     // 14 inches
    [InlineData("taller than 10 cm")]
    public void An_impossible_height_is_not_a_question(string text)
    {
        Assert.Empty(Parse(text).Clauses);
    }

    // ---------- Age and birth ----------

    [Theory]
    [InlineData("over 40", "Age over 40")]
    [InlineData("under 30", "Age under 30")]
    [InlineData("older than 40", "Age over 40")]
    [InlineData("younger than 30", "Age under 30")]
    [InlineData("aged 30", "Age 30")]
    [InlineData("35 years old", "Age 35")]
    [InlineData("born before 1990", "Born before 1990")]
    [InlineData("born after 1990", "Born after 1990")]
    [InlineData("born in 1985", "Born in 1985")]
    public void Ages_and_birth_years_are_read(string text, string expected)
    {
        Assert.Equal(new[] { expected }, Labels(text));
    }

    [Fact]
    public void A_bare_number_after_over_is_an_age_not_a_height()
    {
        // 40 with no unit is an age, 160 cm is a height.
        Assert.Equal(new[] { "Age over 40" }, Labels("over 40"));
        Assert.Equal(new[] { "Height over 5 ft 3 in (1.60 m)" }, Labels("over 160cm"));
    }

    // ---------- Places, relationships, languages, occupations ----------

    [Fact]
    public void Birthplaces_can_be_several_words_and_stop_at_the_next_question()
    {
        Assert.Equal(new[] { "Born in Texas" }, Labels("born in Texas"));
        Assert.Equal(new[] { "Born in New York" }, Labels("born in new york"));
        Assert.Equal(new[] { "Born in Texas", "Married" }, Labels("born in Texas married"));
        Assert.Equal(new[] { "Born in Texas", "Speaks Spanish" }, Labels("born in Texas and speaks Spanish"));
    }

    [Theory]
    [InlineData("married", "Married")]
    [InlineData("has children", "Has children")]
    [InlineData("with kids", "Has children")]
    [InlineData("speaks Spanish", "Speaks Spanish")]
    [InlineData("speaking french", "Speaks French")]
    [InlineData("singer", "Occupation: singer")]
    [InlineData("singers", "Occupation: singer")]
    [InlineData("film actor", "Occupation: film actor")]
    [InlineData("actress", "Actress")]
    [InlineData("actresses", "Actress")]
    public void Plain_english_questions_are_read(string text, string expected)
    {
        Assert.Equal(new[] { expected }, Labels(text));
    }

    [Fact]
    public void Questions_combine_in_the_order_they_were_typed()
    {
        Assert.Equal(new[] { "Actress", "Born in Texas" }, Labels("actresses born in Texas"));
        Assert.Equal(new[] { "Height over 5 ft 3 in (1.60 m)", "Age over 40", "Married" },
                     Labels("people taller than 5'3\" over 40 married"));
    }

    [Theory]
    [InlineData("not married")]
    [InlineData("unmarried")]
    public void A_negated_married_is_not_read_as_married(string text)
    {
        Assert.Empty(Parse(text).Clauses);
    }

    // ---------- What is left for the normal search ----------

    [Fact]
    public void Filler_words_are_dropped_and_other_words_are_kept()
    {
        Assert.Equal(string.Empty, Parse("people who are taller than 5'3\"").Remaining);
        Assert.Equal("Hilary", Parse("Hilary born in Texas").Remaining);
        Assert.Equal("beach tag:wedding", Parse("beach married tag:wedding").Remaining);
    }

    [Fact]
    public void Text_with_no_question_is_returned_unchanged()
    {
        var result = Parse("camera:R5 sunset people");

        Assert.Empty(result.Clauses);
        Assert.Equal("camera:R5 sunset people", result.Remaining);
    }

    [Fact]
    public void An_occupation_word_is_a_question_only_if_a_card_has_it()
    {
        Assert.Empty(PersonQuery.Parse("singer", Array.Empty<string>()).Clauses);
        Assert.Empty(PersonQuery.Parse("singer", null).Clauses);
        Assert.Single(PersonQuery.Parse("singer", new[] { "singer" }).Clauses);
        Assert.Empty(Parse("dentist").Clauses);
    }

    [Fact]
    public void Words_in_quotes_are_never_questions()
    {
        var result = Parse("\"married\" \"born in Texas\"");

        Assert.Empty(result.Clauses);
        Assert.Equal("\"married\" \"born in Texas\"", result.Remaining);
    }

    [Fact]
    public void A_clause_remembers_where_it_was_typed_so_its_chip_can_be_removed()
    {
        var text = "people taller than 5'3\" born in Texas";
        var clauses = Parse(text).Clauses;

        Assert.Equal("taller than 5'3\"", text.Substring(clauses[0].Start, clauses[0].Length));
        Assert.Equal("born in Texas", PersonQuery.Without(text, clauses[0], Occupations).Replace("people ", ""));
        Assert.Equal(string.Empty, PersonQuery.Without("people who are married", Parse("people who are married").Clauses[0], Occupations));
        Assert.Equal("Hilary", PersonQuery.Without("Hilary married", Parse("Hilary married").Clauses[0], Occupations));
    }

    // ---------- The SQL, run on a small library ----------

    private sealed class Library : IDisposable
    {
        public readonly SqliteConnection Db = new("Data Source=:memory:");

        public Library()
        {
            Db.Open();
            Exec(@"
                CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT COLLATE NOCASE, hidden INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE faces (id INTEGER PRIMARY KEY, path TEXT NOT NULL, person_id INTEGER);
                CREATE TABLE person_cards (
                    person_id INTEGER PRIMARY KEY, wikidata_id TEXT, birth_place TEXT, occupations TEXT, spouse TEXT, children INTEGER,
                    height_cm REAL, languages TEXT, json TEXT, full_name TEXT, gender TEXT, death_date TEXT, born_min TEXT, born_max TEXT);");
        }

        public void Exec(string sql)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Person(long id, string? name, string[] photos, bool hidden = false)
        {
            Exec($"INSERT INTO people (id, name, hidden) VALUES ({id}, {(name == null ? "NULL" : "'" + name + "'")}, {(hidden ? 1 : 0)})");
            foreach (var photo in photos) Exec($"INSERT INTO faces (path, person_id) VALUES ('{photo}', {id})");
        }

        public void Card(long id, double? height = null, string? born = null, string? place = null, string? occupations = null,
                         string? spouse = null, int? children = null, string? languages = null, string? gender = null,
                         string? died = null, string? fullName = null)
        {
            var facts = new PersonFacts { BirthDate = born, DeathDate = died, HeightCm = height };
            var range = facts.BornRange;
            string Q(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";
            Exec($@"INSERT INTO person_cards (person_id, wikidata_id, birth_place, occupations, spouse, children, height_cm, languages, json,
                        full_name, gender, death_date, born_min, born_max)
                    VALUES ({id}, 'Q1', {Q(place)}, {Q(occupations)}, {Q(spouse)}, {(children?.ToString() ?? "NULL")}, {(height?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL")},
                        {Q(languages)}, '{{}}', {Q(fullName)}, {Q(gender)}, {Q(died)}, {Q(range?.Min)}, {Q(range?.Max)})");
        }

        public List<string> Photos(string where, Action<SqliteCommand> bind)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT path FROM faces WHERE " + where + " ORDER BY path";
            bind(cmd);
            using var reader = cmd.ExecuteReader();
            var found = new List<string>();
            while (reader.Read()) found.Add(reader.GetString(0));
            return found;
        }

        public List<string> Search(string text)
        {
            var clauses = Parse(text).Clauses;
            using var cmd = Db.CreateCommand();
            var n = 0;
            string Param(object v) { var name = "$p" + n++; cmd.Parameters.AddWithValue(name, v); return name; }
            cmd.CommandText = "SELECT DISTINCT path FROM faces WHERE " + PersonQuery.PhotosSql(clauses, Param, Today) + " ORDER BY path";
            using var reader = cmd.ExecuteReader();
            var found = new List<string>();
            while (reader.Read()) found.Add(reader.GetString(0));
            return found;
        }

        public void Dispose() => Db.Dispose();
    }

    private static Library Sample()
    {
        var lib = new Library();
        lib.Person(1, "Ann", new[] { "a1.jpg", "a2.jpg" });
        lib.Card(1, height: 170, born: "1980-03-04", place: "Austin | Travis County | Texas | United States", occupations: "actor | singer",
                 spouse: "Bo", children: 2, languages: "English | Spanish", gender: "female");
        lib.Person(2, "Bea", new[] { "b1.jpg" });
        lib.Card(2, height: 155, born: "1995", place: "Paris | France", occupations: "singer", children: 0, languages: "French", gender: "female");
        lib.Person(3, "Cal", new[] { "c1.jpg" });
        lib.Card(3, height: 185, born: "1960-01-01", place: "Houston | Texas | United States", occupations: "actor | politician",
                 languages: "English", gender: "male", died: "2020-05-05");
        lib.Person(4, "Dee", new[] { "d1.jpg" });                       // named, no card
        lib.Person(5, null, new[] { "u1.jpg" });                        // not named
        lib.Person(6, "Eve", new[] { "e1.jpg" }, hidden: true);         // hidden group
        lib.Card(6, height: 200, born: "1970-01-01", place: "Texas", occupations: "actor", gender: "female");
        return lib;
    }

    [Fact]
    public void Height_finds_only_people_with_a_card_and_a_taller_height()
    {
        using var lib = Sample();

        Assert.Equal(new[] { "a1.jpg", "a2.jpg", "c1.jpg" }, lib.Search("taller than 5'3\""));   // 160 cm: Ann 170, Cal 185; not Bea 155
        Assert.Equal(new[] { "b1.jpg" }, lib.Search("shorter than 5 ft 6"));                      // 168 cm
    }

    [Fact]
    public void Nobody_is_guessed_at_and_hidden_groups_are_left_out()
    {
        using var lib = Sample();

        var tall = lib.Search("taller than 5 ft");
        Assert.DoesNotContain("d1.jpg", tall);   // named, no card
        Assert.DoesNotContain("u1.jpg", tall);   // not named
        Assert.DoesNotContain("e1.jpg", tall);   // hidden group, though 200 cm
    }

    [Fact]
    public void Age_counts_only_living_people_and_uses_the_whole_date()
    {
        using var lib = Sample();

        // Today is 2026-10-17. Ann (1980-03-04) is 46; Bea (born 1995, year only) is 31 or 30; Cal is dead.
        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("over 40"));
        Assert.Equal(new[] { "b1.jpg" }, lib.Search("under 35"));
        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("aged 46"));
        Assert.Empty(lib.Search("aged 66"));   // Cal would be 66, but he has died
    }

    [Fact]
    public void An_age_that_depends_on_the_unknown_part_of_a_date_does_not_match_either_way()
    {
        using var lib = Sample();

        // Bea is only known to be born in 1995, so she is 30 or 31 today: she is not "aged 30" or "aged 31" for certain.
        Assert.DoesNotContain("b1.jpg", lib.Search("aged 30"));
        Assert.DoesNotContain("b1.jpg", lib.Search("aged 31"));
    }

    [Fact]
    public void Birth_years_use_the_precision_of_the_date()
    {
        using var lib = Sample();

        Assert.Equal(new[] { "a1.jpg", "a2.jpg", "c1.jpg" }, lib.Search("born before 1990"));
        Assert.Equal(new[] { "b1.jpg" }, lib.Search("born after 1990"));
        Assert.Equal(new[] { "b1.jpg" }, lib.Search("born in 1995"));
        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("born in 1980"));
    }

    [Fact]
    public void Birthplace_matches_any_level_of_the_place()
    {
        using var lib = Sample();

        Assert.Equal(new[] { "a1.jpg", "a2.jpg", "c1.jpg" }, lib.Search("born in Texas"));
        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("born in Travis County"));
        Assert.Equal(new[] { "b1.jpg" }, lib.Search("born in France"));
    }

    [Fact]
    public void Married_children_and_languages()
    {
        using var lib = Sample();

        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("married"));
        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("has children"));   // Bea has 0, Cal unknown
        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("speaks Spanish"));
        Assert.Equal(new[] { "b1.jpg" }, lib.Search("speaks French"));
    }

    [Fact]
    public void Actress_means_an_actor_whose_card_says_female_and_actors_are_everyone()
    {
        using var lib = Sample();

        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("actress"));                  // not Cal (male); not hidden Eve
        Assert.Equal(new[] { "a1.jpg", "a2.jpg", "c1.jpg" }, lib.Search("actor"));
        Assert.Equal(new[] { "a1.jpg", "a2.jpg", "b1.jpg" }, lib.Search("singer"));
    }

    [Fact]
    public void Every_question_must_hold_for_the_same_person()
    {
        using var lib = Sample();

        // Ann is the only actress born in Texas. Cal was born in Texas and is an actor, but he is male.
        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, lib.Search("actresses born in Texas"));
        // Taller than 5'3" and speaks French: Bea is French-speaking but 155 cm; Ann and Cal are tall but don't speak French.
        Assert.Empty(lib.Search("taller than 5'3\" speaks French"));
    }

    [Fact]
    public void Typed_text_cannot_inject_sql()
    {
        using var lib = Sample();

        Assert.Empty(lib.Search("born in x'; DROP TABLE people; --"));
        Assert.Equal(6, Convert.ToInt32(new SqliteCommand("SELECT count(*) FROM people", lib.Db).ExecuteScalar()));
        Assert.Empty(Parse("speaks %").Clauses);   // not a language, so not a question
    }

    // ---------- Searching by name ----------

    [Fact]
    public void A_name_matches_the_group_name_or_the_full_name_on_the_card()
    {
        using var lib = Sample();
        lib.Exec("UPDATE person_cards SET full_name = 'Ann Marie Example' WHERE person_id = 1");

        List<string> ByName(string word)
        {
            using var cmd = lib.Db.CreateCommand();
            var n = 0;
            string Param(object v) { var name = "$p" + n++; cmd.Parameters.AddWithValue(name, v); return name; }
            cmd.CommandText = "SELECT DISTINCT path FROM faces WHERE " + PersonQuery.NameSql(word, Param) + " ORDER BY path";
            using var reader = cmd.ExecuteReader();
            var found = new List<string>();
            while (reader.Read()) found.Add(reader.GetString(0));
            return found;
        }

        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, ByName("ann"));
        Assert.Equal(new[] { "a1.jpg", "a2.jpg" }, ByName("Marie"));    // from the full name
        Assert.Equal(new[] { "d1.jpg" }, ByName("dee"));                 // a name, no card needed
        Assert.Empty(ByName("eve"));                                     // hidden group
        Assert.Empty(ByName("%"));                                       // % is a letter here, not a wildcard
    }

    // ---------- What a card says about each question ----------

    [Fact]
    public void The_matched_list_shows_what_the_card_says()
    {
        var card = new PersonFacts { HeightCm = 157, BirthDate = "1977-02-02", Children = 2 };
        var clauses = Parse("taller than 5 ft born before 1990 has children").Clauses;

        Assert.Equal("1.57 m (5 ft 2 in)", clauses[0].Detail(card));
        Assert.StartsWith("born ", clauses[1].Detail(card));
        Assert.Equal("2 children", clauses[2].Detail(card));
        Assert.Null(clauses[0].Detail(new PersonFacts()));
    }

    [Fact]
    public void Missing_data_is_named_by_the_field_it_lacks()
    {
        Assert.Equal("height", Parse("taller than 5 ft").Clauses[0].MissingName);
        Assert.Equal("birth date", Parse("over 40").Clauses[0].MissingName);
        Assert.Equal("birthplace", Parse("born in Texas").Clauses[0].MissingName);
        Assert.Equal(new[] { "occupations", "gender" }, Parse("actress").Clauses[0].Requires);
        Assert.Empty(Parse("married").Clauses[0].Requires);
    }

    // ---------- Gender and born range come from the card ----------

    [Theory]
    [InlineData("1977-02-02", "1977-02-02", "1977-02-02")]
    [InlineData("1977-02", "1977-02-01", "1977-02-28")]
    [InlineData("1980-02", "1980-02-01", "1980-02-29")]
    [InlineData("1977", "1977-01-01", "1977-12-31")]
    public void A_birth_date_gives_the_earliest_and_latest_day_it_can_be(string born, string min, string max)
    {
        var range = new PersonFacts { BirthDate = born }.BornRange;

        Assert.Equal((min, max), range);
        Assert.Null(new PersonFacts().BornRange);
    }
}
