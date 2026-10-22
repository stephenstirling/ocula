// Ocula side of the scraper integration.
// NuGet: Microsoft.Data.Sqlite
//
// The scraper writes ocula_library.db into its downloads folder. Paths inside are RELATIVE
// (forward slashes), so Ocula joins them onto whatever root the user picks (Z:\..., \\NAS\...).
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Ocula.Library;

public record IndexedImage(
    long Id, string FullPath, string? Celebrity, string? Category, IReadOnlyList<string> Tags,
    string? SourceUrl, string? ThreadTitle, string? ThreadUrl, string? PostDate,
    int Width, int Height, long FileSize, bool Upgraded, DateTime? DownloadedAt);

public sealed class LibraryIndex
{
    private const string DbName = "ocula_library.db";
    private readonly string _root;
    private readonly string _localCopy;

    public LibraryIndex(string libraryRoot)
    {
        _root = libraryRoot;
        _localCopy = Path.Combine(Path.GetTempPath(), "ocula_library_cache.db");
    }

    public bool Exists => File.Exists(Path.Combine(_root, DbName));

    // SQLite over SMB can hit locking problems, and the scraper writes while Ocula reads,
    // so work from a local snapshot and refresh it only when the source file changed.
    private void RefreshSnapshot()
    {
        var src = Path.Combine(_root, DbName);
        if (!File.Exists(_localCopy) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(_localCopy))
            File.Copy(src, _localCopy, overwrite: true);
    }

    public IEnumerable<IndexedImage> Query(string? celebrity = null, string? category = null, string? tag = null)
    {
        if (!Exists) yield break;
        RefreshSnapshot();

        using var conn = new SqliteConnection($"Data Source={_localCopy};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.id, i.rel_path, i.celebrity, i.category, i.tags, i.source_url, i.thread_title,
                   i.thread_url, i.post_date, i.width, i.height, i.file_size, i.upgraded, i.downloaded_at
            FROM images i
            WHERE ($celeb IS NULL OR i.celebrity = $celeb)
              AND ($cat   IS NULL OR i.category  = $cat)
              AND ($tag   IS NULL OR EXISTS (SELECT 1 FROM image_tags t WHERE t.image_id = i.id AND t.tag = $tag))
            ORDER BY i.downloaded_at DESC";
        cmd.Parameters.AddWithValue("$celeb", (object?)celebrity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cat", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var tags = JsonSerializer.Deserialize<List<string>>(S(4) ?? "[]") ?? new();
            yield return new IndexedImage(
                r.GetInt64(0),
                Path.Combine(_root, r.GetString(1).Replace('/', Path.DirectorySeparatorChar)),
                S(2), S(3), tags, S(5), S(6), S(7), S(8),
                r.IsDBNull(9) ? 0 : r.GetInt32(9), r.IsDBNull(10) ? 0 : r.GetInt32(10),
                r.IsDBNull(11) ? 0 : r.GetInt64(11), !r.IsDBNull(12) && r.GetInt32(12) == 1,
                DateTime.TryParse(S(13), out var d) ? d : null);
        }
    }
}
