using Microsoft.Data.Sqlite;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// Text recorded by Capta is untrusted (whatever was on a screen, or in a PNG someone sent), and it is
/// searched with SQLite FTS5. These check that hostile text is cleaned, that nothing typed into the
/// search box can act as an FTS5 operator, and that the FTS5 table follows the library's rows.
/// </summary>
public class CaptureTextIndexTests
{
    // ---------- Cleaning ----------

    [Fact]
    public void Control_characters_become_spaces_and_line_breaks_are_kept_in_text_only()
    {
        Assert.Equal("a b c\nd\ne", CaptureTextIndex.CleanText("a\0b\u0007c\r\nd\re"));
        Assert.Equal("a b c d e", CaptureTextIndex.CleanLine("a\0b\u0007c\r\nd\re"));
    }

    [Fact]
    public void Bidirectional_overrides_are_dropped()
    {
        // A right-to-left override can make "gpj.exe" read as "exe.jpg".
        Assert.Equal("invoice exe.jpg", CaptureTextIndex.CleanLine("invoice \u202Eexe.jpg"));
        Assert.Equal("ab", CaptureTextIndex.CleanLine("a\u200Fb\u2066"));
    }

    [Fact]
    public void Nothing_left_is_null()
    {
        Assert.Null(CaptureTextIndex.CleanLine(null));
        Assert.Null(CaptureTextIndex.CleanLine(""));
        Assert.Null(CaptureTextIndex.CleanLine("  \0\u202E \r\n "));
        Assert.Null(CaptureTextIndex.CleanText("\0\0\0"));
    }

    [Fact]
    public void Long_text_is_cut_to_the_limit_without_splitting_an_emoji()
    {
        var text = new string('x', 10) + "😀😀😀";
        var cut = CaptureTextIndex.CleanText(text, 12)!;
        Assert.Equal(new string('x', 10) + "😀", cut); // 10 + one pair = 12; a second pair would not fit
        Assert.All(cut, _ => { }); // no exception: well-formed
        Assert.Equal(CaptureTextIndex.MaxTextChars, CaptureTextIndex.CleanText(new string('y', 500_000))!.Length);
        Assert.Equal(CaptureTextIndex.MaxFieldChars, CaptureTextIndex.CleanLine(new string('z', 5000))!.Length);
    }

    [Fact]
    public void A_lone_surrogate_half_is_dropped()
    {
        Assert.Equal("ab", CaptureTextIndex.CleanText("a\uD83Db"));
        Assert.Equal("ab", CaptureTextIndex.CleanText("a\uDE00b"));
    }

    [Fact]
    public void Markup_paths_and_commands_are_just_text()
    {
        // Nothing is interpreted; it is stored and compared as it is.
        const string hostile = "<script>alert(1)</script> ..\\..\\Windows\\System32 & del *.* %PATH% $(calc) {{x}}";
        Assert.Equal(hostile, CaptureTextIndex.CleanText(hostile));
    }

    // ---------- Query building ----------

    [Theory]
    [InlineData("network", "{details body} : \"network\" *")]
    [InlineData("Wi-Fi", "{details body} : \"Wi-Fi\" *")]
    [InlineData("a\"b", "{details body} : \"a\"\"b\" *")]
    public void A_word_is_quoted_whole_and_its_last_token_is_a_prefix(string word, string expected) =>
        Assert.Equal(expected, CaptureTextIndex.MatchExpression(word, includeText: true));

    [Fact]
    public void Without_the_text_switch_only_the_app_and_title_are_searched() =>
        Assert.Equal("details : \"network\" *", CaptureTextIndex.MatchExpression("network", includeText: false));

    [Theory]
    [InlineData("---")]
    [InlineData("*")]
    [InlineData("\"\"")]
    [InlineData("()")]
    [InlineData("")]
    public void A_word_with_no_letter_or_digit_is_not_sent_to_fts(string word) =>
        Assert.Null(CaptureTextIndex.MatchExpression(word, includeText: true));

    [Theory]
    [InlineData("100%_done", "100\\%\\_done")]
    [InlineData("a\\b", "a\\\\b")]
    public void Like_wildcards_are_escaped(string word, string expected) =>
        Assert.Equal(expected, CaptureTextIndex.EscapeLike(word));

    /// <summary>Every nasty thing someone could type must run as a plain search, never throw an FTS5 syntax error.</summary>
    [Theory]
    [InlineData("foo\" OR \"bar")]
    [InlineData("a OR b")]
    [InlineData("NOT")]
    [InlineData("NEAR(a b, 2)")]
    [InlineData("details:secret")]
    [InlineData("body:")]
    [InlineData("^start")]
    [InlineData("a*b*")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("\"")]
    [InlineData("'; DROP TABLE images; --")]
    [InlineData("{details}:x")]
    [InlineData("a:b:c")]
    [InlineData("-minus")]
    [InlineData("😀 emoji")]
    public void Hostile_search_words_run_without_error_and_match_literally(string word)
    {
        using var db = Library();
        Add(db, "C:\\c\\one.png", "Window\nchrome\nInbox", "plain text here");
        Add(db, "C:\\c\\two.png", "Window\nchrome\nOther", "OR NOT something");

        var expression = CaptureTextIndex.MatchExpression(word, includeText: true);
        if (expression == null) return; // nothing to search for: fine
        var found = Search(db, expression); // must not throw
        Assert.True(found.Count <= 2);
        // The table is still there.
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM capture_fts"));
    }

    [Fact]
    public void A_search_for_the_word_or_finds_the_word_not_an_operator()
    {
        using var db = Library();
        Add(db, "C:\\c\\one.png", "Window\nchrome\nInbox", "alpha beta");
        Add(db, "C:\\c\\two.png", "Window\nchrome\nOther", "OR NOT something");
        Assert.Equal(new[] { "C:\\c\\two.png" }, Search(db, CaptureTextIndex.MatchExpression("OR", true)!));
        Assert.Empty(Search(db, CaptureTextIndex.MatchExpression("alpha OR gamma", true)!)); // one phrase, not an OR
    }

    // ---------- The FTS5 table ----------

    [Fact]
    public void The_bundled_sqlite_has_fts5()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE VIRTUAL TABLE t USING fts5(a); INSERT INTO t(a) VALUES ('hello world'); SELECT COUNT(*) FROM t WHERE t MATCH 'hel*'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Finds_text_app_and_window_title_by_whole_word_or_start_of_word()
    {
        using var db = Library();
        Add(db, "C:\\c\\a.png", "Window\nSettings\nNetwork — Settings", "Wi-Fi Connected\r\nProxy server");
        Add(db, "C:\\c\\b.png", "Region\nchrome\nInvoice 4411 - Google Chrome", "Total due 120.00");

        Assert.Equal(new[] { "C:\\c\\a.png" }, Search(db, CaptureTextIndex.MatchExpression("proxy", true)!));      // text
        Assert.Equal(new[] { "C:\\c\\a.png" }, Search(db, CaptureTextIndex.MatchExpression("netw", true)!));       // window title, prefix
        Assert.Equal(new[] { "C:\\c\\b.png" }, Search(db, CaptureTextIndex.MatchExpression("chrome", true)!));     // source app
        Assert.Equal(new[] { "C:\\c\\a.png" }, Search(db, CaptureTextIndex.MatchExpression("Wi-Fi", true)!));      // punctuation
        Assert.Equal(new[] { "C:\\c\\a.png" }, Search(db, CaptureTextIndex.MatchExpression("connected", true)!));  // case
        Assert.Equal(new[] { "C:\\c\\b.png" }, Search(db, CaptureTextIndex.MatchExpression("120.00", true)!));
        Assert.Empty(Search(db, CaptureTextIndex.MatchExpression("nothinghere", true)!));
    }

    [Fact]
    public void Accents_do_not_get_in_the_way()
    {
        using var db = Library();
        Add(db, "C:\\c\\a.png", "Window\napp\nCafé", "Réservation confirmée");
        Assert.Single(Search(db, CaptureTextIndex.MatchExpression("reservation", true)!));
        Assert.Single(Search(db, CaptureTextIndex.MatchExpression("cafe", true)!));
    }

    [Fact]
    public void With_the_text_switch_off_the_text_is_not_searched_but_the_title_is()
    {
        using var db = Library();
        Add(db, "C:\\c\\a.png", "Window\nSettings\nNetwork — Settings", "secret proxy words");
        Assert.Empty(Search(db, CaptureTextIndex.MatchExpression("proxy", includeText: false)!));
        Assert.Single(Search(db, CaptureTextIndex.MatchExpression("network", includeText: false)!));
        Assert.Single(Search(db, CaptureTextIndex.TextOnlyExpression("proxy")!));
        Assert.Empty(Search(db, CaptureTextIndex.TextOnlyExpression("network")!));
    }

    [Fact]
    public void The_table_follows_changes_renames_and_removals_of_the_library_rows()
    {
        using var db = Library();
        Add(db, "C:\\c\\a.png", "Window\nSettings\nNetwork", "first text");
        Assert.Single(Search(db, CaptureTextIndex.MatchExpression("first", true)!));

        Exec(db, "UPDATE images SET ocr = 'second text' WHERE path = 'C:\\c\\a.png'");
        Assert.Empty(Search(db, CaptureTextIndex.MatchExpression("first", true)!));
        Assert.Single(Search(db, CaptureTextIndex.MatchExpression("second", true)!));

        Exec(db, "UPDATE images SET path = 'C:\\c\\renamed.png' WHERE path = 'C:\\c\\a.png'");
        Assert.Equal(new[] { "C:\\c\\renamed.png" }, Search(db, CaptureTextIndex.MatchExpression("second", true)!));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM capture_fts"));

        Exec(db, "DELETE FROM images WHERE path = 'C:\\c\\renamed.png'");
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM capture_fts"));
    }

    [Fact]
    public void An_upsert_that_changes_nothing_leaves_one_entry_and_a_photo_that_is_not_a_capture_has_none()
    {
        using var db = Library();
        for (var i = 0; i < 3; i++)
            Exec(db, @"INSERT INTO images (path, capture, ocr) VALUES ('C:\c\a.png', 'Window', 'text')
                       ON CONFLICT(path) DO UPDATE SET capture = excluded.capture, ocr = excluded.ocr");
        Exec(db, "INSERT INTO images (path, capture, ocr) VALUES ('C:\\c\\photo.jpg', NULL, NULL)");
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM capture_fts"));
    }

    [Fact]
    public void Existing_rows_are_added_when_the_table_is_first_created()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Exec(db, "CREATE TABLE images (path TEXT PRIMARY KEY COLLATE NOCASE, capture TEXT, ocr TEXT)");
        Exec(db, "INSERT INTO images VALUES ('C:\\c\\a.png', 'Window', 'old text'), ('C:\\c\\b.jpg', NULL, NULL)");
        Assert.True(CaptureTextIndex.EnsureSchema(db));
        Assert.Equal(new[] { "C:\\c\\a.png" }, Search(db, CaptureTextIndex.MatchExpression("old", true)!));
        Assert.False(CaptureTextIndex.EnsureSchema(db)); // second time: nothing to do
    }

    [Fact]
    public void Clearing_the_text_removes_it_from_both_the_column_and_the_search_but_keeps_the_app_and_title()
    {
        using var db = Library();
        Add(db, "C:\\c\\a.png", "Window\nSettings\nNetwork", "proxy words");
        Add(db, "C:\\c\\b.png", "Region\nchrome\nPage", null);

        Assert.Equal(1, CaptureTextIndex.ClearText(db));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM images WHERE ocr IS NOT NULL"));
        Assert.Empty(Search(db, CaptureTextIndex.MatchExpression("proxy", true)!));
        Assert.Single(Search(db, CaptureTextIndex.MatchExpression("network", true)!));
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM capture_fts"));
    }

    [Fact]
    public void Text_added_to_the_stored_row_after_clearing_is_a_separate_decision()
    {
        // ClearText only clears. Keeping it from coming back is the caller's job (the switch), tested in
        // the app; here: a cleared row can be given text again and is found again.
        using var db = Library();
        Add(db, "C:\\c\\a.png", "Window\nSettings\nNetwork", "proxy");
        CaptureTextIndex.ClearText(db);
        Exec(db, "UPDATE images SET ocr = 'proxy again' WHERE path = 'C:\\c\\a.png'");
        Assert.Single(Search(db, CaptureTextIndex.MatchExpression("again", true)!));
    }

    // ---------- Helpers ----------

    private static SqliteConnection Library()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Exec(db, "CREATE TABLE images (path TEXT PRIMARY KEY COLLATE NOCASE, capture TEXT, ocr TEXT)");
        CaptureTextIndex.EnsureSchema(db);
        return db;
    }

    private static void Add(SqliteConnection db, string path, string capture, string? ocr)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO images (path, capture, ocr) VALUES ($p, $c, $o)";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$c", capture);
        cmd.Parameters.AddWithValue("$o", (object?)ocr ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static List<string> Search(SqliteConnection db, string match)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path FROM images WHERE path IN (SELECT path FROM capture_fts WHERE capture_fts MATCH $m) ORDER BY path";
        cmd.Parameters.AddWithValue("$m", match);
        using var reader = cmd.ExecuteReader();
        var found = new List<string>();
        while (reader.Read()) found.Add(reader.GetString(0));
        return found;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
