using System;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ocula.Services;

/// <summary>
/// Search over what Capta recorded about a screenshot: its source app, window title and the text
/// found in the image (OCR). Backed by an SQLite FTS5 table that the library keeps up to date
/// with triggers, so every way a photo's row can change (indexing, rename, removal) is covered.
///
/// All of that text is untrusted: it is whatever was on a screen, or in a PNG someone sent.
/// It is only ever stored and compared as plain text. It never becomes markup, a file name, a
/// path or a command; what the user types is turned into an FTS5 query by quoting every word
/// (see <see cref="MatchExpression"/>), so FTS5's own operators can't be smuggled in either.
/// </summary>
public static class CaptureTextIndex
{
    /// <summary>The longest source-app or window-title text kept.</summary>
    public const int MaxFieldChars = 260;

    /// <summary>The longest text-in-the-image kept. A long scrolling capture is well under this.</summary>
    public const int MaxTextChars = 50_000;

    // ================= Cleaning untrusted text =================

    /// <summary>
    /// A single line of untrusted text (app name, window title): control characters and line breaks
    /// become spaces, bidirectional overrides are dropped, it is trimmed and capped. Null if nothing is left.
    /// </summary>
    public static string? CleanLine(string? text, int maxChars = MaxFieldChars) => Clean(text, maxChars, keepLineBreaks: false);

    /// <summary>
    /// Multi-line untrusted text (the text found in an image): line breaks are kept as "\n" (a CRLF is
    /// one break), other control characters become spaces, bidirectional overrides are dropped, and
    /// it is trimmed and capped. Null if nothing is left.
    /// </summary>
    public static string? CleanText(string? text, int maxChars = MaxTextChars) => Clean(text, maxChars, keepLineBreaks: true);

    private static string? Clean(string? text, int maxChars, bool keepLineBreaks)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var sb = new StringBuilder(Math.Min(text.Length, maxChars + 2));
        for (var i = 0; i < text.Length && sb.Length < maxChars; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++; // CRLF is one break
                sb.Append(keepLineBreaks ? '\n' : ' ');
            }
            else if (c == '\n')
            {
                sb.Append(keepLineBreaks ? '\n' : ' ');
            }
            else if (c == '\t')
            {
                sb.Append(' ');
            }
            else if (IsBidiControl(c))
            {
                // Dropped: these can make text read in a different order from how it is stored.
            }
            else if (char.IsControl(c))
            {
                sb.Append(' ');
            }
            else if (char.IsHighSurrogate(c))
            {
                // Keep a surrogate pair together, or drop a lone half.
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (sb.Length + 2 > maxChars) break;
                    sb.Append(c).Append(text[++i]);
                }
            }
            else if (!char.IsLowSurrogate(c))
            {
                sb.Append(c);
            }
        }

        var cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static bool IsBidiControl(char c) =>
        c is '‎' or '‏' or '؜' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');

    // ================= Building a query from what was typed =================

    /// <summary>
    /// An FTS5 MATCH expression for one word the user typed: every character is quoted, so none of
    /// FTS5's operators (AND, OR, NOT, NEAR, "*", ":", "^", brackets, quotes) can act as one, and the
    /// last token is a prefix ("netw" finds "network"). Null when the word has no letter or digit in it,
    /// which FTS5 would reject.
    /// </summary>
    /// <param name="includeText">Search the text found in the image as well as the app and window title.</param>
    public static string? MatchExpression(string word, bool includeText)
    {
        if (!word.Any(char.IsLetterOrDigit)) return null;
        var quoted = "\"" + word.Replace("\"", "\"\"") + "\"";
        var columns = includeText ? "{details body}" : "details";
        return $"{columns} : {quoted} *";
    }

    /// <summary>An FTS5 expression that looks only in the text found in the image (the <c>text:</c> filter).</summary>
    public static string? TextOnlyExpression(string word)
    {
        if (!word.Any(char.IsLetterOrDigit)) return null;
        return $"body : \"{word.Replace("\"", "\"\"")}\" *";
    }

    /// <summary>Escapes a word for a LIKE pattern used with ESCAPE '\'.</summary>
    public static string EscapeLike(string word) =>
        word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ================= The table =================

    /// <summary>
    /// Creates the FTS5 table and its triggers if they aren't there, and fills the table from the
    /// photos already in the library the first time. Returns whether it had to create the table.
    /// </summary>
    public static bool EnsureSchema(SqliteConnection db)
    {
        bool exists;
        using (var check = db.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'capture_fts'";
            exists = check.ExecuteScalar() != null;
        }

        Run(db, @"
            CREATE VIRTUAL TABLE IF NOT EXISTS capture_fts USING fts5(
                details, body, path UNINDEXED,
                tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3');

            CREATE TRIGGER IF NOT EXISTS capture_fts_insert AFTER INSERT ON images WHEN NEW.capture IS NOT NULL BEGIN
                INSERT INTO capture_fts (details, body, path) VALUES (NEW.capture, COALESCE(NEW.ocr, ''), NEW.path);
            END;

            CREATE TRIGGER IF NOT EXISTS capture_fts_delete AFTER DELETE ON images WHEN OLD.capture IS NOT NULL BEGIN
                DELETE FROM capture_fts WHERE path = OLD.path;
            END;

            CREATE TRIGGER IF NOT EXISTS capture_fts_update
                AFTER UPDATE OF capture, ocr, path ON images
                WHEN OLD.capture IS NOT NEW.capture OR OLD.ocr IS NOT NEW.ocr OR OLD.path IS NOT NEW.path
            BEGIN
                DELETE FROM capture_fts WHERE path = OLD.path;
                INSERT INTO capture_fts (details, body, path)
                    SELECT NEW.capture, COALESCE(NEW.ocr, ''), NEW.path WHERE NEW.capture IS NOT NULL;
            END;");

        if (!exists) Rebuild(db);
        return !exists;
    }

    /// <summary>Rebuilds the FTS5 table from the library's rows (after anything that bypassed the triggers).</summary>
    public static void Rebuild(SqliteConnection db) => Run(db, @"
        DELETE FROM capture_fts;
        INSERT INTO capture_fts (details, body, path)
            SELECT capture, COALESCE(ocr, ''), path FROM images WHERE capture IS NOT NULL;");

    /// <summary>Forgets every stored text-in-image (the triggers keep the FTS5 table in step). Returns how many were cleared.</summary>
    public static int ClearText(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE images SET ocr = NULL WHERE ocr IS NOT NULL";
        return cmd.ExecuteNonQuery();
    }

    private static void Run(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
