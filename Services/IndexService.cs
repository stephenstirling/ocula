using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Models;
using Microsoft.Data.Sqlite;

namespace Ocula.Services;

public sealed record GeoPhoto(string Path, string Name, double Latitude, double Longitude, DateTime? Taken);

/// <summary>
/// Ocula's local library: one SQLite file at %LOCALAPPDATA%\Ocula\library.db holding each
/// image's metadata, rating, label, tags, location and a small JPEG thumbnail.
///
/// A row is trusted only while the file's "last modified" time matches, so if another app
/// edits a photo it's re-read automatically. The files themselves stay the source of truth
/// for tags and ratings; the library just makes everything fast and searchable.
/// </summary>
public sealed class IndexService : IDisposable
{
    private const string Columns =
        "path, folder, modified, camera, exposure, taken, rating, label, tags, hdr, thumb, raw, lat, lon, sharp, flag, duration, capture, ocr";

    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _dbPath;

    public IndexService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocula");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "library.db");
        _db = new SqliteConnection($"Data Source={_dbPath}");
        _db.Open();

        Execute(@"
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS images (
                path     TEXT PRIMARY KEY COLLATE NOCASE,
                folder   TEXT NOT NULL COLLATE NOCASE,
                modified INTEGER NOT NULL,
                camera   TEXT,
                exposure TEXT,
                taken    TEXT,
                rating   INTEGER NOT NULL DEFAULT 0,
                label    TEXT,
                tags     TEXT NOT NULL DEFAULT '',
                thumb    BLOB
            );
            CREATE INDEX IF NOT EXISTS ix_images_folder ON images(folder);
            CREATE INDEX IF NOT EXISTS ix_images_camera ON images(camera);
            CREATE INDEX IF NOT EXISTS ix_images_taken  ON images(taken);");

        // Upgrades for libraries created by earlier versions. Columns that need every photo
        // re-read (HDR, location) mark all rows out of date so they refresh as Ocula indexes.
        AddColumn("hdr TEXT", rereadAll: true);
        AddColumn("phash INTEGER", rereadAll: false);          // older single-hash fingerprint, no longer used
        AddColumn("fp BLOB", rereadAll: false);                   // duplicate-finder fingerprint (hashes and a tiny grayscale picture)
        AddColumn("raw TEXT", rereadAll: true);
        AddColumn("lat REAL", rereadAll: false);
        AddColumn("lon REAL", rereadAll: false);
        AddColumn("sharp INTEGER", rereadAll: false);              // measured by the crawler as it goes
        AddColumn("flag INTEGER NOT NULL DEFAULT 0", rereadAll: false); // pick / reject
        AddColumn("duration INTEGER", rereadAll: false);           // video length in ticks
        // Capta captures: mode, source app and window title, and the text in the image. Only
        // PNGs can hold them, so only those are read again.
        if (AddColumn("capture TEXT", rereadAll: false) | AddColumn("ocr TEXT", rereadAll: false))
            Execute("UPDATE images SET modified = 0 WHERE path LIKE '%.png';");
        // Full-text search over those details (SQLite FTS5), kept in step with the rows by triggers.
        CaptureTextIndex.EnsureSchema(_db);
        Execute(@"
            CREATE TABLE IF NOT EXISTS collections (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS collection_items (
                collection_id INTEGER NOT NULL, path TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (collection_id, path));
            CREATE TABLE IF NOT EXISTS smart_albums (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, query TEXT NOT NULL);");
        Execute("CREATE INDEX IF NOT EXISTS ix_images_location ON images(lat) WHERE lat IS NOT NULL;");
        Execute("CREATE TABLE IF NOT EXISTS imports (key TEXT PRIMARY KEY, at INTEGER NOT NULL);");
        // People: faces found in photos and the groups they belong to. Face data lives only here,
        // never in photo files. A person with no name is just a group Ocula found.
        Execute(@"
            CREATE TABLE IF NOT EXISTS people (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT COLLATE NOCASE,
                centroid   BLOB,
                face_count INTEGER NOT NULL DEFAULT 0,
                hidden     INTEGER NOT NULL DEFAULT 0,
                card       TEXT);
            CREATE TABLE IF NOT EXISTS faces (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                path      TEXT NOT NULL COLLATE NOCASE,
                modified  INTEGER NOT NULL,
                x INTEGER, y INTEGER, w INTEGER, h INTEGER,
                score     REAL,
                emb       BLOB NOT NULL,
                thumb     BLOB,
                person_id INTEGER,
                locked    INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS face_scans (
                path TEXT PRIMARY KEY COLLATE NOCASE,
                modified INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_faces_person ON faces(person_id);
            CREATE INDEX IF NOT EXISTS ix_faces_path ON faces(path);");
        // Person cards: what Wikidata said about a named group, kept as plain fields (so they can be searched later) plus the
        // whole card as JSON. A card belongs to its group and goes with it: the trigger covers merges, deleted groups and
        // "Delete all face data".
        Execute(@"
            CREATE TABLE IF NOT EXISTS person_cards (
                person_id   INTEGER PRIMARY KEY,
                wikidata_id TEXT NOT NULL,
                birth_date  TEXT,
                birth_year  INTEGER,
                birth_place TEXT,
                occupations TEXT,
                spouse      TEXT,
                children    INTEGER,
                height_cm   REAL,
                languages   TEXT,
                retrieved   TEXT NOT NULL,
                json        TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS trg_people_delete_card AFTER DELETE ON people
            BEGIN
                DELETE FROM person_cards WHERE person_id = old.id;
            END;");
        // Smart recognition: one embedding per photo and model. An empty blob means "tried, nothing to read".
        Execute(@"
            CREATE TABLE IF NOT EXISTS embeddings (
                path     TEXT NOT NULL COLLATE NOCASE,
                model    TEXT NOT NULL,
                modified INTEGER NOT NULL,
                vec      BLOB NOT NULL,
                PRIMARY KEY (path, model));");
    }
            // Person-detail search columns (idempotent on older libraries).
        foreach (var column in new[] { "bra_size TEXT", "cup_rank INTEGER" })
        {
            try { Execute($"ALTER TABLE person_cards ADD COLUMN {column};");
}
            catch (SqliteException) { /* already there */ }
        }

    // ================= Reading =================

    public Task<Dictionary<string, IndexRow>> GetFolderAsync(string folder) => RunAsync(() =>
    {
        var rows = new Dictionary<string, IndexRow>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM images WHERE folder = $folder";
        cmd.Parameters.AddWithValue("$folder", folder);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = ReadRow(reader);
            rows[row.Path] = row;
        }
        return rows;
    });

    /// <summary>A light check for the crawler: each photo's modified time and whether it has a thumbnail.</summary>
    public Task<Dictionary<string, (long Modified, bool Complete)>> GetFolderStateAsync(string folder) => RunAsync(() =>
    {
        var state = new Dictionary<string, (long, bool)>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, modified, thumb IS NOT NULL AND sharp IS NOT NULL FROM images WHERE folder = $folder";
        cmd.Parameters.AddWithValue("$folder", folder);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) state[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2) != 0);
        return state;
    });

    /// <summary>Pick (1), reject (-1) or clear (0). Kept separately so re-indexing never resets it.</summary>
    public Task SetFlagAsync(string path, int flag) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE images SET flag = $flag WHERE path = $path";
        cmd.Parameters.AddWithValue("$flag", flag);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
        return true;
    });

    public Task<byte[]?> GetThumbAsync(string path) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT thumb FROM images WHERE path = $path";
        cmd.Parameters.AddWithValue("$path", path);
        return cmd.ExecuteScalar() as byte[];
    });

    /// <summary>Searches the whole library. Thumbnails aren't included; load them per tile.</summary>
    public Task<List<IndexRow>> SearchAsync(SearchQuery query, int limit = 3000) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        var where = query.ToSql(cmd);
        cmd.CommandText =
            $"SELECT path, folder, modified, camera, exposure, taken, rating, label, tags, hdr, NULL, raw, lat, lon, sharp, flag, duration, capture, ocr " +
            $"FROM images WHERE {where} ORDER BY taken DESC, path LIMIT {limit}";
        var rows = new List<IndexRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    });

    public Task<List<GeoPhoto>> GetGeotaggedAsync() => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, lat, lon, taken FROM images WHERE lat IS NOT NULL AND lon IS NOT NULL";
        var photos = new List<GeoPhoto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            photos.Add(new GeoPhoto(path, Path.GetFileName(path), reader.GetDouble(1), reader.GetDouble(2),
                reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3))));
        }
        return photos;
    });

    /// <summary>Camera models and tags in the library, for search suggestions.</summary>
    public Task<(List<string> Cameras, List<string> Tags)> GetSuggestionsAsync() => RunAsync(() =>
    {
        var cameras = new List<string>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT camera FROM images WHERE camera IS NOT NULL GROUP BY camera ORDER BY COUNT(*) DESC LIMIT 30";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) cameras.Add(reader.GetString(0));
        }

        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT tags FROM images WHERE tags <> ''";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                foreach (var tag in reader.GetString(0).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
        }
        return (cameras, tagCounts.OrderByDescending(t => t.Value).Take(50).Select(t => t.Key).ToList());
    });

    // ================= Writing =================

    /// <summary>
    /// Inserts or updates a row. A null thumbnail keeps the stored one, unless clearThumb is
    /// set (used when the file has changed and the old thumbnail is out of date).
    /// </summary>
    public Task SaveAsync(IndexRow row, bool clearThumb = false) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO images (path, folder, modified, camera, exposure, taken, rating, label, tags, hdr, thumb, raw, lat, lon, sharp, flag, duration, capture, ocr)
            VALUES ($path, $folder, $modified, $camera, $exposure, $taken, $rating, $label, $tags, $hdr, $thumb, $raw, $lat, $lon, $sharp, $flag, $duration, $capture, $ocr)
            ON CONFLICT(path) DO UPDATE SET
                folder = excluded.folder, modified = excluded.modified, camera = excluded.camera,
                exposure = excluded.exposure, taken = excluded.taken, rating = excluded.rating,
                label = excluded.label, tags = excluded.tags, hdr = excluded.hdr, raw = excluded.raw,
                lat = excluded.lat, lon = excluded.lon, duration = excluded.duration,
                capture = excluded.capture, ocr = excluded.ocr,
                sharp = CASE WHEN $clear THEN excluded.sharp ELSE COALESCE(excluded.sharp, images.sharp) END,
                thumb = CASE WHEN $clear THEN excluded.thumb ELSE COALESCE(excluded.thumb, images.thumb) END,
                phash = CASE WHEN $clear THEN NULL ELSE images.phash END,
                fp = CASE WHEN $clear THEN NULL ELSE images.fp END;";
        cmd.Parameters.AddWithValue("$path", row.Path);
        cmd.Parameters.AddWithValue("$folder", row.Folder);
        cmd.Parameters.AddWithValue("$modified", row.Modified);
        cmd.Parameters.AddWithValue("$camera", (object?)row.Camera ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exposure", (object?)row.Exposure ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$taken", row.Taken is { } t ? t.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$rating", row.Rating);
        cmd.Parameters.AddWithValue("$label", (object?)row.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", row.Tags);
        cmd.Parameters.AddWithValue("$hdr", (object?)row.Hdr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", (object?)row.Thumb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$raw", (object?)row.RawPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lat", (object?)row.Latitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon", (object?)row.Longitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sharp", (object?)row.Sharpness ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$flag", row.Flag); // only used for a brand-new row; updates keep the stored flag
        cmd.Parameters.AddWithValue("$duration", (object?)row.DurationTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$capture", (object?)row.Capture ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ocr", (object?)row.CaptureText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$clear", clearThumb ? 1 : 0);
        cmd.ExecuteNonQuery();
        return true;
    });

    // ================= Import history and renames =================

    public Task<HashSet<string>> GetImportedAsync(IEnumerable<string> keys) => RunAsync(() =>
    {
        var found = new HashSet<string>();
        foreach (var key in keys)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM imports WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            if (cmd.ExecuteScalar() != null) found.Add(key);
        }
        return found;
    });

    public Task MarkImportedAsync(string key) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO imports (key, at) VALUES ($key, $at)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
        return true;
    });

    /// <summary>
    /// Forgets photos whose files are gone (moved to the Recycle Bin): the library row, Smart recognition
    /// and face data, and collection entries. A face group left with no faces is dropped, and the others
    /// have their average face rebuilt.
    /// </summary>
    public Task RemovePhotosAsync(IReadOnlyList<string> paths) => RunAsync(() =>
    {
        var groups = new HashSet<long>();
        using (var tx = _db.BeginTransaction())
        {
            void Run(string sql, string path)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$path", path);
                cmd.ExecuteNonQuery();
            }

            foreach (var path in paths)
            {
                using (var find = _db.CreateCommand())
                {
                    find.Transaction = tx;
                    find.CommandText = "SELECT DISTINCT person_id FROM faces WHERE path = $path AND person_id IS NOT NULL";
                    find.Parameters.AddWithValue("$path", path);
                    using var reader = find.ExecuteReader();
                    while (reader.Read()) groups.Add(reader.GetInt64(0));
                }
                Run("DELETE FROM faces WHERE path = $path", path);
                Run("DELETE FROM face_scans WHERE path = $path", path);
                Run("DELETE FROM embeddings WHERE path = $path", path);
                Run("DELETE FROM collection_items WHERE path = $path", path);
                Run("DELETE FROM images WHERE path = $path", path);
            }
            tx.Commit();
        }
        foreach (var id in groups) RecomputeGroup(id);
        return true;
    });

    /// <summary>Moves a photo's library row to its new name, keeping flags, sharpness and the thumbnail.</summary>
    public Task RenameAsync(string oldPath, string newPath, string? newRawPath) => RunAsync(() =>
    {
        using (var collections = _db.CreateCommand())
        {
            collections.CommandText = "UPDATE collection_items SET path = $new WHERE path = $old";
            collections.Parameters.AddWithValue("$new", newPath);
            collections.Parameters.AddWithValue("$old", oldPath);
            collections.ExecuteNonQuery();
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE images SET path = $new, raw = $raw, folder = $folder WHERE path = $old";
        cmd.Parameters.AddWithValue("$new", newPath);
        cmd.Parameters.AddWithValue("$raw", (object?)newRawPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folder", Path.GetDirectoryName(newPath) ?? string.Empty);
        cmd.Parameters.AddWithValue("$old", oldPath);
        cmd.ExecuteNonQuery();
        return true;
    });

    // ================= Text found in screenshots =================

    /// <summary>
    /// Forgets the text found in every screenshot, in the library only: the PNG files are untouched, and
    /// the source app and window title stay. Returns how many screenshots had text.
    /// </summary>
    public Task<int> ClearCaptureTextAsync() => RunAsync(() => CaptureTextIndex.ClearText(_db));

    /// <summary>Marks screenshots out of date so the next crawl reads their text again (after the switch is turned back on).</summary>
    public Task MarkCapturesForRereadAsync() => RunAsync(() =>
    {
        Execute("UPDATE images SET modified = 0 WHERE capture IS NOT NULL;");
        return true;
    });

    /// <summary>How many Capta screenshots are in the library, and how many of those have text saved.</summary>
    public Task<(int Captures, int WithText)> CountCapturesAsync() => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(ocr IS NOT NULL AND ocr <> ''), 0) FROM images WHERE capture IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    });

    // ================= Timeline, collections and smart albums =================

    /// <summary>Every indexed photo, newest first, for the timeline.</summary>
    public Task<List<IndexRow>> GetAllAsync(int limit = 50000) => SearchAsync(new SearchQuery(string.Empty), limit);

    public Task<List<(long Id, string Name, int Count)>> GetCollectionsAsync() => RunAsync(() =>
    {
        var list = new List<(long, string, int)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"SELECT c.id, c.name, COUNT(i.path) FROM collections c
                            LEFT JOIN collection_items i ON i.collection_id = c.id
                            GROUP BY c.id ORDER BY c.name COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return list;
    });

    public Task<long> CreateCollectionAsync(string name) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO collections (name) VALUES ($name); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$name", name);
        return (long)cmd.ExecuteScalar()!;
    });

    public Task RenameCollectionAsync(long id, string name) =>
        ExecuteAsync("UPDATE collections SET name = $a WHERE id = $b", name, id);

    public Task DeleteCollectionAsync(long id) =>
        ExecuteAsync("DELETE FROM collection_items WHERE collection_id = $a; DELETE FROM collections WHERE id = $a;", id);

    public Task AddToCollectionAsync(long id, IEnumerable<string> paths) => RunAsync(() =>
    {
        using var transaction = _db.BeginTransaction();
        foreach (var path in paths)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO collection_items (collection_id, path) VALUES ($id, $path)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    });

    public Task RemoveFromCollectionAsync(long id, IEnumerable<string> paths) => RunAsync(() =>
    {
        foreach (var path in paths)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM collection_items WHERE collection_id = $id AND path = $path";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
        return true;
    });

    /// <summary>A collection's photos. Photos that were deleted or moved outside Ocula drop out.</summary>
    public Task<List<IndexRow>> GetCollectionRowsAsync(long id) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"SELECT i.path, i.folder, i.modified, i.camera, i.exposure, i.taken, i.rating, i.label, i.tags,
                                   i.hdr, NULL, i.raw, i.lat, i.lon, i.sharp, i.flag, i.duration, i.capture, i.ocr
                            FROM collection_items c JOIN images i ON i.path = c.path
                            WHERE c.collection_id = $id ORDER BY i.taken, i.path";
        cmd.Parameters.AddWithValue("$id", id);
        var rows = new List<IndexRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    });

    public Task<List<(long Id, string Name, string Query)>> GetSmartAlbumsAsync() => RunAsync(() =>
    {
        var list = new List<(long, string, string)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, query FROM smart_albums ORDER BY name COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return list;
    });

    public Task SaveSmartAlbumAsync(string name, string query) =>
        ExecuteAsync("INSERT INTO smart_albums (name, query) VALUES ($a, $b)", name, query);

    public Task RenameSmartAlbumAsync(long id, string name) =>
        ExecuteAsync("UPDATE smart_albums SET name = $a WHERE id = $b", name, id);

    public Task DeleteSmartAlbumAsync(long id) => ExecuteAsync("DELETE FROM smart_albums WHERE id = $a", id);

    private Task ExecuteAsync(string sql, object a, object? b = null) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$a", a);
        if (b != null) cmd.Parameters.AddWithValue("$b", b);
        cmd.ExecuteNonQuery();
        return true;
    });

    // ================= Duplicate fingerprints =================

    /// <summary>Stored fingerprints for a folder: path → (modified time it was made for, fingerprint).</summary>
    public Task<Dictionary<string, (long Modified, PhotoFingerprint Fingerprint)>> GetFingerprintsAsync(string folder) => RunAsync(() =>
    {
        var result = new Dictionary<string, (long, PhotoFingerprint)>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, modified, fp FROM images WHERE folder = $folder AND fp IS NOT NULL";
        cmd.Parameters.AddWithValue("$folder", folder);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var fingerprint = PhotoFingerprint.FromBytes((byte[])reader.GetValue(2));
            if (fingerprint != null) result[reader.GetString(0)] = (reader.GetInt64(1), fingerprint);
        }
        return result;
    });

    /// <summary>Saves a fingerprint, but only onto a library row that's still current for that file.</summary>
    public Task SetFingerprintAsync(string path, long modified, PhotoFingerprint fingerprint) => RunAsync(() =>
    {
        var bytes = fingerprint.ToBytes();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE images SET fp = $fp WHERE path = $path AND modified = $modified";
        cmd.Parameters.AddWithValue("$fp", bytes);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$modified", modified);
        cmd.ExecuteNonQuery();
        return true;
    });

    // ================= Smart recognition =================

    private const string NotVideo =
        "NOT (path LIKE '%.mp4' OR path LIKE '%.mov' OR path LIKE '%.m4v' OR path LIKE '%.avi' OR path LIKE '%.wmv' OR " +
        "path LIKE '%.mkv' OR path LIKE '%.webm' OR path LIKE '%.flv' OR path LIKE '%.3gp' OR path LIKE '%.mpg' OR " +
        "path LIKE '%.mpeg' OR path LIKE '%.ogv' OR path LIKE '%.vob' OR path LIKE '%.mts' OR path LIKE '%.m2ts')";

    /// <summary>Photos that have no up-to-date embedding for this model yet (newest first).</summary>
    public Task<List<(string Path, long Modified)>> GetUnanalysedAsync(string model, int limit) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT i.path, i.modified FROM images i
            LEFT JOIN embeddings e ON e.path = i.path AND e.model = $model AND e.modified = i.modified
            WHERE e.path IS NULL AND i.thumb IS NOT NULL AND {NotVideo.Replace("path", "i.path")}
            ORDER BY i.taken DESC LIMIT {limit}";
        cmd.Parameters.AddWithValue("$model", model);
        var list = new List<(string, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetString(0), reader.GetInt64(1)));
        return list;
    });

    /// <summary>How many photos have been analysed, out of all photos (videos excluded).</summary>
    public Task<(int Done, int Total)> GetSmartProgressAsync(string model) => RunAsync(() =>
    {
        int Count(string sql, string? modelParam = null)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            if (modelParam != null) cmd.Parameters.AddWithValue("$model", modelParam);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        var total = Count($"SELECT COUNT(*) FROM images WHERE thumb IS NOT NULL AND {NotVideo}");
        var done = Count(@"SELECT COUNT(*) FROM images i JOIN embeddings e
                           ON e.path = i.path AND e.model = $model AND e.modified = i.modified", model);
        return (done, total);
    });

    public Task SaveEmbeddingAsync(string path, long modified, string model, float[]? vector) => RunAsync(() =>
    {
        var blob = vector == null ? Array.Empty<byte>() : new byte[vector.Length * sizeof(float)];
        if (vector != null) Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO embeddings (path, model, modified, vec) VALUES ($path, $model, $modified, $vec)
            ON CONFLICT(path, model) DO UPDATE SET modified = excluded.modified, vec = excluded.vec;";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$modified", modified);
        cmd.Parameters.AddWithValue("$vec", blob);
        cmd.ExecuteNonQuery();
        return true;
    });

    public Task<float[]?> GetEmbeddingAsync(string path, string model) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT vec FROM embeddings WHERE path = $path AND model = $model";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$model", model);
        return cmd.ExecuteScalar() is byte[] { Length: > 0 } blob ? ToVector(blob) : null;
    });

    /// <summary>Every stored embedding for the model, skipping photos that couldn't be read.</summary>
    public Task<List<(string Path, float[] Vector)>> GetEmbeddingsAsync(string model) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, vec FROM embeddings WHERE model = $model AND length(vec) > 0";
        cmd.Parameters.AddWithValue("$model", model);
        var list = new List<(string, float[])>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetString(0), ToVector((byte[])reader[1])));
        return list;
    });

    /// <summary>Library rows for these paths, in the order given (used to show ranked search results).</summary>
    public Task<List<IndexRow>> GetRowsAsync(IReadOnlyList<string> paths) => RunAsync(() =>
    {
        var byPath = new Dictionary<string, IndexRow>(StringComparer.OrdinalIgnoreCase);
        const int chunk = 400; // SQLite's default limit is 999 parameters
        for (var start = 0; start < paths.Count; start += chunk)
        {
            var part = paths.Skip(start).Take(chunk).ToList();
            using var cmd = _db.CreateCommand();
            var names = part.Select((p, i) => "$p" + i).ToList();
            for (var i = 0; i < part.Count; i++) cmd.Parameters.AddWithValue(names[i], part[i]);
            cmd.CommandText =
                "SELECT path, folder, modified, camera, exposure, taken, rating, label, tags, hdr, NULL, raw, lat, lon, sharp, flag, duration, capture, ocr " +
                $"FROM images WHERE path IN ({string.Join(',', names)})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = ReadRow(reader);
                byPath[row.Path] = row;
            }
        }
        return paths.Where(byPath.ContainsKey).Select(p => byPath[p]).ToList();
    });

    public Task DeleteEmbeddingsAsync() => RunAsync(() =>
    {
        Execute("DELETE FROM embeddings;");
        return true;
    });

    private static float[] ToVector(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }

    // ================= People =================

    public sealed record PersonSummary(long Id, string? Name, int FaceCount, byte[]? Avatar);
    public sealed record FaceEntry(long Id, string Path, byte[]? Thumb);

    /// <summary>Photos not yet scanned for faces (or changed since), newest first.</summary>
    public Task<List<(string Path, long Modified)>> GetUnscannedAsync(int limit) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT i.path, i.modified FROM images i
            LEFT JOIN face_scans s ON s.path = i.path AND s.modified = i.modified
            WHERE s.path IS NULL AND i.thumb IS NOT NULL AND {NotVideo.Replace("path", "i.path")}
            ORDER BY i.taken DESC LIMIT {limit}";
        var list = new List<(string, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetString(0), reader.GetInt64(1)));
        return list;
    });

    public Task<(int Done, int Total)> GetFaceProgressAsync() => RunAsync(() =>
    {
        int Count(string sql)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        var total = Count($"SELECT COUNT(*) FROM images WHERE thumb IS NOT NULL AND {NotVideo}");
        var done = Count("SELECT COUNT(*) FROM images i JOIN face_scans s ON s.path = i.path AND s.modified = i.modified");
        return (done, total);
    });

    /// <summary>The current groups with their centroids, used to place each new face.</summary>
    public Task<List<(long Id, float[] Centroid, int Count)>> GetCentroidsAsync() => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, centroid, face_count FROM people WHERE centroid IS NOT NULL AND length(centroid) > 0";
        var list = new List<(long, float[], int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetInt64(0), ToVector((byte[])reader[1]), reader.GetInt32(2)));
        return list;
    });

    public Task<long> CreatePersonAsync(float[] centroid) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO people (centroid, face_count) VALUES ($c, 0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$c", ToBytes(centroid));
        return Convert.ToInt64(cmd.ExecuteScalar());
    });

    /// <summary>Replaces a photo's faces with the ones just found and records that it has been scanned.</summary>
    public Task SaveFacesAsync(string path, long modified, IReadOnlyList<(FaceResult Face, long PersonId)> faces,
                               IReadOnlyDictionary<long, (float[] Centroid, int Count)> updatedGroups) => RunAsync(() =>
    {
        using var tx = _db.BeginTransaction();

        void Run(string sql, params (string Name, object Value)[] args)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }

        Run("DELETE FROM faces WHERE path = $path", ("$path", path));
        foreach (var (face, personId) in faces)
        {
            Run(@"INSERT INTO faces (path, modified, x, y, w, h, score, emb, thumb, person_id)
                  VALUES ($path, $modified, $x, $y, $w, $h, $score, $emb, $thumb, $person)",
                ("$path", path), ("$modified", modified), ("$x", face.X), ("$y", face.Y), ("$w", face.Width),
                ("$h", face.Height), ("$score", face.Score), ("$emb", ToBytes(face.Embedding)),
                ("$thumb", face.Thumbnail), ("$person", personId));
        }
        foreach (var (id, group) in updatedGroups)
            Run("UPDATE people SET centroid = $c, face_count = $n WHERE id = $id",
                ("$c", ToBytes(group.Centroid)), ("$n", group.Count), ("$id", id));
        Run("INSERT INTO face_scans (path, modified) VALUES ($path, $modified) ON CONFLICT(path) DO UPDATE SET modified = excluded.modified",
            ("$path", path), ("$modified", modified));
        tx.Commit();
        return true;
    });

    /// <summary>Groups to show: named ones first, then by size. Each comes with its clearest face as an avatar.</summary>
    public Task<List<PersonSummary>> GetPeopleAsync(bool includeHidden = false) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT p.id, p.name, p.face_count,
                   (SELECT thumb FROM faces f WHERE f.person_id = p.id ORDER BY f.score DESC LIMIT 1)
            FROM people p
            WHERE p.face_count > 0 {(includeHidden ? "" : "AND p.hidden = 0")}
            ORDER BY (p.name IS NULL), p.face_count DESC";
        var list = new List<PersonSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new PersonSummary(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                                       reader.GetInt32(2), reader.IsDBNull(3) ? null : (byte[])reader[3]));
        return list;
    });

    public Task<List<FaceEntry>> GetFacesAsync(long personId, int limit = 200) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, path, thumb FROM faces WHERE person_id = $p ORDER BY score DESC LIMIT " + limit;
        cmd.Parameters.AddWithValue("$p", personId);
        var list = new List<FaceEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new FaceEntry(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : (byte[])reader[2]));
        return list;
    });

    public Task<List<string>> GetPersonPhotoPathsAsync(long personId) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT path FROM faces WHERE person_id = $p";
        cmd.Parameters.AddWithValue("$p", personId);
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    });

    /// <summary>
    /// Names a group. If another group already has that name, the two are merged and the id of
    /// the surviving group is returned. An empty name makes the group unnamed again.
    /// </summary>
    public Task<long> NamePersonAsync(long personId, string? name) => RunAsync(() =>
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        long survivor = personId;

        if (name != null)
        {
            using var find = _db.CreateCommand();
            find.CommandText = "SELECT id FROM people WHERE name = $n AND id <> $id LIMIT 1";
            find.Parameters.AddWithValue("$n", name);
            find.Parameters.AddWithValue("$id", personId);
            if (find.ExecuteScalar() is long other)
            {
                survivor = other;
                // A card for this name moves to the group that keeps it, unless that group already has one.
                Execute($"UPDATE OR IGNORE person_cards SET person_id = {other} WHERE person_id = {personId};");
                Execute($"UPDATE faces SET person_id = {other} WHERE person_id = {personId};");
                Execute($"DELETE FROM people WHERE id = {personId};");
                RecomputeGroup(other);
                return survivor;
            }
        }

        // A card describes one named person, so a different name makes it stale: remove it.
        using (var stale = _db.CreateCommand())
        {
            stale.CommandText = @"DELETE FROM person_cards WHERE person_id = $id
                                  AND (SELECT name FROM people WHERE id = $id) IS NOT $n";
            stale.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
            stale.Parameters.AddWithValue("$id", personId);
            stale.ExecuteNonQuery();
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE people SET name = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", personId);
        cmd.ExecuteNonQuery();
        return survivor;
    });

// ----- Person cards (Wikidata) -----

/// <summary>Saves (or replaces) the card for a named group. Kept in the library only, never written into photos.</summary>
public Task SavePersonCardAsync(long personId, PersonFacts facts) => RunAsync(() =>
{
    using var cmd = _db.CreateCommand();
    cmd.CommandText = @"INSERT OR REPLACE INTO person_cards
              (person_id, wikidata_id, birth_date, birth_year, birth_place, occupations, spouse, children, height_cm, languages, retrieved, json, bra_size, cup_rank)
            VALUES ($id, $qid, $bd, $by, $bp, $occ, $sp, $ch, $h, $lang, $at, $json, $bra, $cup)";
    cmd.Parameters.AddWithValue("$id", personId);
    cmd.Parameters.AddWithValue("$qid", facts.WikidataId);
    cmd.Parameters.AddWithValue("$bd", (object?)facts.BirthDate ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$by", (object?)facts.BirthYear ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$bp", facts.BirthPlaceChain.Count == 0 ? DBNull.Value : string.Join(" | ", facts.BirthPlaceChain));
    cmd.Parameters.AddWithValue("$occ", facts.Occupations.Count == 0 ? DBNull.Value : string.Join(" | ", facts.Occupations));
    cmd.Parameters.AddWithValue("$sp", facts.Spouses.Count == 0 ? DBNull.Value : string.Join(" | ", facts.Spouses.Select(s => s.Name)));
    cmd.Parameters.AddWithValue("$ch", (object?)facts.Children ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$h", (object?)facts.HeightCm ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$lang", facts.Languages.Count == 0 ? DBNull.Value : string.Join(" | ", facts.Languages));
    cmd.Parameters.AddWithValue("$at", facts.RetrievedAt.ToString("o", CultureInfo.InvariantCulture));
    cmd.Parameters.AddWithValue("$json", facts.ToJson());
    cmd.Parameters.AddWithValue("$bra", (object?)facts.BraSize ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$cup", (object?)facts.BraCupRank ?? DBNull.Value);
    cmd.ExecuteNonQuery();
    return true;
});

public Task<PersonFacts?> GetPersonCardAsync(long personId) => RunAsync(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT json FROM person_cards WHERE person_id = $id";
        cmd.Parameters.AddWithValue("$id", personId);
        return cmd.ExecuteScalar() is string json ? PersonFacts.FromJson(json) : null;
    });

/// <summary>
/// Saved person cards whose group name matches any of <paramref name="names"/>
/// (case-insensitive), or whose card JSON Name/FullName matches.
/// </summary>
public Task<List<ResolvedPersonCard>> GetPersonCardsMatchingNamesAsync(IReadOnlyList<string> names) => RunAsync(() =>
{
    var result = new List<ResolvedPersonCard>();
    if (names.Count == 0) return result;

    using (var cmd = _db.CreateCommand())
    {
        cmd.CommandText = @"
                SELECT p.id, p.name, c.json
                FROM people p
                INNER JOIN person_cards c ON c.person_id = p.id
                WHERE p.name IS NOT NULL AND length(trim(p.name)) > 0";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var groupName = reader.GetString(1);
            var json = reader.GetString(2);
            var facts = PersonFacts.FromJson(json);
            if (facts == null) continue;

            foreach (var tag in names)
            {
                if (PersonCardResolver.NamesMatch(tag, groupName) ||
                    PersonCardResolver.NamesMatch(tag, facts.Name) ||
                    PersonCardResolver.NamesMatch(tag, facts.FullName))
                {
                    if (result.Any(r => r.PersonId == id)) break;
                    result.Add(new ResolvedPersonCard
                    {
                        PersonId = id,
                        GroupName = groupName,
                        Facts = facts,
                        MatchedTag = tag
                    });
                    break;
                }
            }
        }
    }

    return result;
});

/// <summary>Person ids whose saved cards match structured person-detail filters.</summary>
public Task<List<long>> FindPersonIdsMatchingDetailAsync(PersonDetailQuery d) => RunAsync(() =>
{
    using var cmd = _db.CreateCommand();
    var clauses = new List<string> { "1=1" };

    void Bind(string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    if (d.HeightMinCm is double hMin) { clauses.Add("height_cm > $hMin"); Bind("$hMin", hMin); }
    if (d.HeightMaxCm is double hMax) { clauses.Add("height_cm < $hMax"); Bind("$hMax", hMax); }
    if (d.CupMinRank is int cMin) { clauses.Add("cup_rank >= $cMin"); Bind("$cMin", cMin); }
    if (d.CupMaxRank is int cMax) { clauses.Add("cup_rank <= $cMax"); Bind("$cMax", cMax); }
    if (d.BraSizeExact is string be) { clauses.Add("upper(trim(bra_size)) = upper($bra)"); Bind("$bra", be); }
    if (d.BirthPlaceContains is string pl) { clauses.Add("birth_place LIKE $place"); Bind("$place", "%" + pl + "%"); }
    if (d.OccupationContains is string oc) { clauses.Add("occupations LIKE $occ"); Bind("$occ", "%" + oc + "%"); }
    if (d.HasSpouse == true) clauses.Add("spouse IS NOT NULL AND length(spouse) > 0");
    if (d.HasChildren == true) clauses.Add("children IS NOT NULL AND children >= 1");
    if (d.MinChildren is int mc) { clauses.Add("children >= $mc"); Bind("$mc", mc); }
    if (d.BirthYearMin is int y0) { clauses.Add("birth_year >= $y0"); Bind("$y0", y0); }
    if (d.BirthYearMax is int y1) { clauses.Add("birth_year <= $y1"); Bind("$y1", y1); }

    cmd.CommandText = "SELECT person_id FROM person_cards WHERE " + string.Join(" AND ", clauses);
    var ids = new List<long>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) ids.Add(reader.GetInt64(0));
    return ids;
});
public Task DeletePersonCardAsync(long personId) => RunAsync(() =>
    {
        Execute($"DELETE FROM person_cards WHERE person_id = {personId};");
        return true;
    });

    /// <summary>"Not this person": moves one face into a new group of its own and keeps it there.</summary>
    public Task MoveFaceToNewGroupAsync(long faceId) => RunAsync(() =>
    {
        long oldId;
        byte[] emb;
        using (var get = _db.CreateCommand())
        {
            get.CommandText = "SELECT person_id, emb FROM faces WHERE id = $f";
            get.Parameters.AddWithValue("$f", faceId);
            using var reader = get.ExecuteReader();
            if (!reader.Read()) return false;
            oldId = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            emb = (byte[])reader[1];
        }

        long newId;
        using (var insert = _db.CreateCommand())
        {
            insert.CommandText = "INSERT INTO people (centroid, face_count) VALUES ($c, 1); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$c", emb);
            newId = Convert.ToInt64(insert.ExecuteScalar());
        }
        Execute($"UPDATE faces SET person_id = {newId}, locked = 1 WHERE id = {faceId};");
        if (oldId != 0) RecomputeGroup(oldId);
        return true;
    });

    /// <summary>Hides a group from the People view (for strangers in the background, say). Photos are untouched.</summary>
    public Task HidePersonAsync(long personId) => RunAsync(() =>
    {
        Execute($"UPDATE people SET hidden = 1 WHERE id = {personId};");
        return true;
    });

    /// <summary>Removes every face, group and name. Photos and their tags are not touched.</summary>
    public Task DeleteFaceDataAsync() => RunAsync(() =>
    {
        Execute("DELETE FROM faces; DELETE FROM people; DELETE FROM face_scans;");
        return true;
    });

    // Rebuilds a group's average face and count from its faces; drops the group if it's now empty.
    private void RecomputeGroup(long personId)
    {
        var sum = (float[]?)null;
        var count = 0;
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT emb FROM faces WHERE person_id = $p";
            cmd.Parameters.AddWithValue("$p", personId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var v = ToVector((byte[])reader[0]);
                sum ??= new float[v.Length];
                for (var i = 0; i < v.Length; i++) sum[i] += v[i];
                count++;
            }
        }

        if (count == 0 || sum == null)
        {
            Execute($"DELETE FROM people WHERE id = {personId};");
            return;
        }
        var norm = MathF.Sqrt(sum.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < sum.Length; i++) sum[i] /= norm;

        using var update = _db.CreateCommand();
        update.CommandText = "UPDATE people SET centroid = $c, face_count = $n WHERE id = $id";
        update.Parameters.AddWithValue("$c", ToBytes(sum));
        update.Parameters.AddWithValue("$n", count);
        update.Parameters.AddWithValue("$id", personId);
        update.ExecuteNonQuery();
    }

    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    // ================= Maintenance =================

    /// <summary>Size of the library on disk, including SQLite's write-ahead log.</summary>
    public long SizeOnDisk =>
        new[] { _dbPath, _dbPath + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);

    /// <summary>Empties the library and gives the disk space back.</summary>
    public Task ClearAsync() => RunAsync(() =>
    {
        Execute("DELETE FROM images;");
        Execute("DELETE FROM embeddings;");
        Execute("DELETE FROM faces; DELETE FROM people; DELETE FROM face_scans;");
        Execute("PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");
        return true;
    });

    public void Dispose() => _db.Dispose();

    // ================= Helpers =================

    private static IndexRow ReadRow(SqliteDataReader r) => new(
        Path: r.GetString(0),
        Folder: r.GetString(1),
        Modified: r.GetInt64(2),
        Camera: r.IsDBNull(3) ? null : r.GetString(3),
        Exposure: r.IsDBNull(4) ? null : r.GetString(4),
        Taken: r.IsDBNull(5) ? null : ParseDate(r.GetString(5)),
        Rating: r.GetInt32(6),
        Label: r.IsDBNull(7) ? null : r.GetString(7),
        Tags: r.GetString(8),
        Hdr: r.IsDBNull(9) ? null : r.GetString(9),
        Thumb: r.IsDBNull(10) ? null : (byte[])r[10],
        RawPath: r.IsDBNull(11) ? null : r.GetString(11),
        Latitude: r.IsDBNull(12) ? null : r.GetDouble(12),
        Longitude: r.IsDBNull(13) ? null : r.GetDouble(13),
        Sharpness: r.IsDBNull(14) ? null : r.GetInt32(14),
        Flag: r.IsDBNull(15) ? 0 : r.GetInt32(15),
        DurationTicks: r.IsDBNull(16) ? null : r.GetInt64(16),
        Capture: r.FieldCount > 17 && !r.IsDBNull(17) ? r.GetString(17) : null,
        CaptureText: r.FieldCount > 18 && !r.IsDBNull(18) ? r.GetString(18) : null);

    private static DateTime ParseDate(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Adds a column to an older library; true if it was added (false if it existed).</summary>
    private bool AddColumn(string definition, bool rereadAll)
    {
        try
        {
            Execute($"ALTER TABLE images ADD COLUMN {definition};");
            if (rereadAll) Execute("UPDATE images SET modified = 0;");
            return true;
        }
        catch (SqliteException)
        {
            // Column already exists.
            return false;
        }
    }

    private async Task<T> RunAsync<T>(Func<T> work)
    {
        await _lock.WaitAsync();
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            _lock.Release();
        }
    }
}
