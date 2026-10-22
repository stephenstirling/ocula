using Microsoft.Data.Sqlite;
using Ocula.Library;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// The Phun page's reader for the scraper's ocula_library.db. The tables below are the ones the scraper creates (LIBRARY_SCHEMA in
/// tools/phun-scraper/phun_scraper.py); the scraper stores relative paths with forward slashes and UTC "...Z" times.
/// </summary>
public class LibraryIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ocula-libindex-test-" + Guid.NewGuid().ToString("N"));

    public LibraryIndexTests()
    {
        Directory.CreateDirectory(_root);
        using var db = new SqliteConnection($"Data Source={Path.Combine(_root, "ocula_library.db")}");
        db.Open();
        Exec(db, @"
            CREATE TABLE images (id INTEGER PRIMARY KEY AUTOINCREMENT, rel_path TEXT NOT NULL UNIQUE, celebrity TEXT, category TEXT, tags TEXT,
                source_url TEXT, thread_title TEXT, thread_url TEXT, post_date TEXT, width INTEGER, height INTEGER, file_size INTEGER, phash TEXT,
                upgraded INTEGER NOT NULL DEFAULT 0, downloaded_at TEXT);
            CREATE TABLE image_tags (image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE, tag TEXT NOT NULL, PRIMARY KEY (image_id, tag));
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static void Exec(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private void Add(string rel, string celebrity, string[] tags, string when, bool upgraded = false, long size = 3_200_000, int w = 4000, int h = 3000,
                     string? thread = "A thread", string? category = null)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_root, "ocula_library.db")}");
        db.Open();
        Exec(db, @"INSERT INTO images (rel_path, celebrity, category, tags, thread_title, width, height, file_size, upgraded, downloaded_at)
                   VALUES ($p, $c, $cat, $t, $th, $w, $h, $s, $u, $d)",
            ("$p", rel), ("$c", celebrity), ("$cat", (object?)category ?? DBNull.Value), ("$t", System.Text.Json.JsonSerializer.Serialize(tags)),
            ("$th", (object?)thread ?? DBNull.Value), ("$w", w), ("$h", h), ("$s", size), ("$u", upgraded ? 1 : 0), ("$d", when));
        using var id = db.CreateCommand();
        id.CommandText = "SELECT id FROM images WHERE rel_path = $p";
        id.Parameters.AddWithValue("$p", rel);
        var imageId = (long)id.ExecuteScalar()!;
        foreach (var tag in tags) Exec(db, "INSERT OR IGNORE INTO image_tags (image_id, tag) VALUES ($i, $t)", ("$i", imageId), ("$t", tag));
    }

    [Fact]
    public void Rows_come_back_newest_first_with_full_paths_and_their_tags()
    {
        Add("Name A/old.jpg", "Name A", new[] { "Name A" }, "2026-10-21T10:00:00Z");
        Add("Name B/Beach/new.jpg", "Name B", new[] { "Name B", "Beach" }, "2026-10-22T10:00:00Z", upgraded: true);

        var rows = new LibraryIndex(_root).Query().ToList();

        Assert.Equal(new[] { "new.jpg", "old.jpg" }, rows.Select(r => r.Name));
        Assert.Equal(Path.Combine(_root, "Name B", "Beach", "new.jpg"), rows[0].FullPath);   // forward slashes became this PC's separator
        Assert.Equal(new[] { "Name B", "Beach" }, rows[0].Tags);
        Assert.Equal("Upgraded", rows[0].Badge);
        Assert.Equal("Saved", rows[1].Badge);
        Assert.Equal(DateTimeKind.Utc, rows[0].DownloadedAt!.Value.Kind);
    }

    [Fact]
    public void A_limit_keeps_the_newest_and_no_limit_keeps_all()
    {
        for (var i = 0; i < 30; i++) Add($"n/{i:00}.jpg", "n", new[] { "n" }, $"2026-10-22T10:{i:00}:00Z");

        var newest = new LibraryIndex(_root).Query(limit: 5).Select(r => r.Name).ToList();

        Assert.Equal(new[] { "29.jpg", "28.jpg", "27.jpg", "26.jpg", "25.jpg" }, newest);
        Assert.Equal(30, new LibraryIndex(_root).Query().Count());
    }

    [Fact]
    public void Filters_by_celebrity_category_and_tag()
    {
        Add("a/1.jpg", "A", new[] { "A", "Dress" }, "2026-10-22T10:00:00Z", category: "Bikini");
        Add("b/2.jpg", "B", new[] { "B", "Dress" }, "2026-10-22T11:00:00Z");

        var index = new LibraryIndex(_root);

        Assert.Equal(new[] { "1.jpg" }, index.Query(celebrity: "A").Select(r => r.Name));
        Assert.Equal(new[] { "1.jpg" }, index.Query(category: "Bikini").Select(r => r.Name));
        Assert.Equal(new[] { "2.jpg", "1.jpg" }, index.Query(tag: "Dress").Select(r => r.Name));
        Assert.Empty(index.Query(tag: "Nope"));
    }

    [Fact]
    public void A_row_whose_path_leaves_the_library_folder_is_ignored()
    {
        Add("../outside.jpg", "x", new[] { "x" }, "2026-10-22T10:00:00Z");
        Add("a/../../also-outside.jpg", "x", new[] { "x" }, "2026-10-22T10:01:00Z");
        Add("inside/ok.jpg", "x", new[] { "x" }, "2026-10-22T10:02:00Z");

        Assert.Equal(new[] { "ok.jpg" }, new LibraryIndex(_root).Query().Select(r => r.Name));
    }

    [Fact]
    public void No_library_file_means_no_rows_and_no_error()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.False(new LibraryIndex(empty).Exists);
        Assert.Empty(new LibraryIndex(empty).Query());
    }

    [Fact]
    public void Each_library_folder_gets_its_own_snapshot_file()
    {
        // Two libraries must not share one temporary copy: reading one would show the other's pictures.
        var other = Path.Combine(Path.GetTempPath(), "ocula-libindex-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            File.Copy(Path.Combine(_root, "ocula_library.db"), Path.Combine(other, "ocula_library.db"));
            Add("a/mine.jpg", "A", new[] { "A" }, "2026-10-22T10:00:00Z");

            Assert.Equal(new[] { "mine.jpg" }, new LibraryIndex(_root).Query().Select(r => r.Name));
            Assert.Empty(new LibraryIndex(other).Query());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(other, recursive: true);
        }
    }

    [Theory]
    [InlineData(3_200_000L, 4000, 3000, "3.2 MB · 4000 × 3000")]
    [InlineData(410_000L, 1200, 800, "410 KB · 1200 × 800")]
    [InlineData(900L, 0, 0, "900 bytes")]
    [InlineData(0L, 640, 480, "640 × 480")]
    [InlineData(0L, 0, 0, "")]
    public void The_size_line_reads_naturally(long size, int w, int h, string expected)
    {
        var image = new IndexedImage(1, "x/y.jpg", null, null, Array.Empty<string>(), null, null, null, null, w, h, size, false, null);

        Assert.Equal(expected, image.SizeText);
    }

    [Fact]
    public void Tags_the_scraper_meant_to_write_are_checked_against_the_file_without_regard_to_case()
    {
        var image = new IndexedImage(1, "x/y.jpg", "A", null, new[] { "Celebrity Name", "Red Carpet", "Dress" }, null, null, null, null, 1, 1, 1, false, null);

        Assert.Empty(image.MissingFrom(new[] { "celebrity name", "RED CARPET", "dress", "extra" }));
        Assert.Equal(new[] { "Red Carpet", "Dress" }, image.MissingFrom(new[] { "Celebrity Name" }));
        Assert.Equal(3, image.MissingFrom(Array.Empty<string>()).Count);
    }
}
