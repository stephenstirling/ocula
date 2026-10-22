using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Ocula.Services;

/// <summary>One named person as read from the library, before anything is chosen for the export.</summary>
public sealed record PeopleExportRow(string Uid, string Name, string? CardJson, int Photos, byte[]? Thumbnail);

/// <summary>
/// "Share people with Porta": a small file listing the people the user has named, so Porta can find their photos.
///
/// The file (version 1) is <c>people-index.json</c>:
/// <code>
/// { "version": 1, "updated": "2026-10-21T22:30:00Z",
///   "people": [ { "id": "...", "name": "...", "aliases": ["..."], "description": "...", "photoCount": 123, "thumbnail": "C:\...\x.png" } ] }
/// </code>
/// Only those fields are ever written, by construction: the file is written field by field below, not by serialising a card.
/// <c>description</c> and <c>thumbnail</c> are left out when there is none. People with no name, and hidden groups, are never included.
/// Nothing about a person other than a name, other names, a short description and a face picture can reach the file: no birth date,
/// height, spouse, children, birthplace, languages or occupations.
/// </summary>
public static class PeopleExport
{
    public const int Version = 1;
    public const string FileName = "people-index.json";
    public const string ThumbnailFolder = "people-thumbnails";
    public const int ThumbnailSize = 96;

    // ---------- Reading the library ----------

    /// <summary>Named, visible groups with their photo count and best face. Unnamed and hidden groups are not read at all.</summary>
    public static List<PeopleExportRow> ReadRows(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            SELECT p.uid, p.name, c.json,
                   (SELECT COUNT(DISTINCT path) FROM faces WHERE person_id = p.id),
                   (SELECT thumb FROM faces f WHERE f.person_id = p.id ORDER BY f.score DESC LIMIT 1)
            FROM people p LEFT JOIN person_cards c ON c.person_id = p.id
            WHERE p.name IS NOT NULL AND trim(p.name) <> '' AND p.hidden = 0 AND p.uid IS NOT NULL
            ORDER BY 4 DESC, p.name";
        var rows = new List<PeopleExportRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PeopleExportRow(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : (byte[])reader[4]));
        }
        return rows;
    }

    // ---------- Choosing what goes in ----------

    /// <summary>The other names Wikidata gives for the person (the full name and the label), apart from the name the user chose.</summary>
    internal static List<string> Aliases(string name, PersonFacts? card)
    {
        var aliases = new List<string>();
        if (card == null) return aliases;
        foreach (var candidate in new[] { card.FullName, card.Name })
        {
            var clean = CaptureTextIndex.CleanLine(candidate, 200);
            if (clean == null || clean.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!aliases.Contains(clean, StringComparer.OrdinalIgnoreCase)) aliases.Add(clean);
        }
        return aliases;
    }

    private static readonly Regex Parenthetical = new(@"\s*\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Year = new(@"\b\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex Personal = new(
        @"\b(born|died|married|wife|husband|spouse|partner|children|child|mother of|father of|height|tall)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A short description such as "American actress and singer". Wikidata descriptions sometimes carry a birth year ("singer-songwriter
    /// (born 1977)") or other personal detail, which must not be shared: a parenthetical is dropped, and a description that still has a
    /// year or one of a few personal words is left out altogether. Wrong-way errors are on purpose: too little is better than too much.
    /// </summary>
    internal static string? SafeDescription(string? description)
    {
        var clean = CaptureTextIndex.CleanLine(description, 200);
        if (clean == null) return null;
        clean = Parenthetical.Replace(clean, string.Empty).Trim();
        if (clean.Length == 0 || clean.Length > 120) return null;
        if (Year.IsMatch(clean) || Personal.IsMatch(clean)) return null;
        return clean;
    }

    /// <summary>A 96 px PNG of a face, or null if the stored picture can't be read.</summary>
    internal static byte[]? ToThumbnail(byte[] image)
    {
        try
        {
            using var source = Image.Load(image);
            source.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(ThumbnailSize, ThumbnailSize), Mode = ResizeMode.Crop }));
            using var output = new MemoryStream();
            source.SaveAsPng(output, new PngEncoder());
            return output.ToArray();
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return null;
        }
    }

    // ---------- Writing ----------

    /// <summary>
    /// Writes the people file and the face pictures into <paramref name="folder"/>. The file is written to a temporary name and renamed
    /// over the old one, so a reader never sees half a file. Pictures are named by person and a hash of the picture, so an unchanged
    /// picture is not written again and a changed one gets a new name; pictures no longer in the file are deleted last.
    /// </summary>
    public static void Write(string folder, IReadOnlyList<PeopleExportRow> rows, DateTimeOffset now)
    {
        Directory.CreateDirectory(folder);
        var thumbs = Path.Combine(folder, ThumbnailFolder);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>();

        foreach (var row in rows)
        {
            var name = CaptureTextIndex.CleanLine(row.Name, 200);
            if (name == null || row.Photos <= 0 || !IsId(row.Uid)) continue;
            var card = row.CardJson == null ? null : PersonFacts.FromJson(row.CardJson);

            string? thumbnailPath = null;
            if (row.Thumbnail is { Length: > 0 } source)
            {
                var file = $"{row.Uid}-{Convert.ToHexString(SHA256.HashData(source))[..8].ToLowerInvariant()}.png";
                var path = Path.Combine(thumbs, file);
                if (!File.Exists(path) && ToThumbnail(source) is { } png)
                {
                    Directory.CreateDirectory(thumbs);
                    var temporary = path + ".tmp";
                    File.WriteAllBytes(temporary, png);
                    File.Move(temporary, path, overwrite: true);
                }
                if (File.Exists(path))
                {
                    thumbnailPath = path;
                    wanted.Add(file);
                }
            }
            entries.Add(new Entry(row.Uid, name, Aliases(name, card), SafeDescription(card?.Description), row.Photos, thumbnailPath));
        }

        var json = BuildJson(entries, now);
        var target = Path.Combine(folder, FileName);
        var temp = target + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, target, overwrite: true);

        // Only now that the new file is in place: pictures nobody refers to any more.
        if (Directory.Exists(thumbs))
            foreach (var file in Directory.EnumerateFiles(thumbs))
                if (!wanted.Contains(Path.GetFileName(file)))
                    TryDelete(file);
    }

    /// <summary>Removes the people file and every face picture (switch turned off, or all face data deleted).</summary>
    public static void Delete(string folder)
    {
        TryDelete(Path.Combine(folder, FileName));
        TryDelete(Path.Combine(folder, FileName + ".tmp"));
        var thumbs = Path.Combine(folder, ThumbnailFolder);
        if (!Directory.Exists(thumbs)) return;
        try { Directory.Delete(thumbs, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Something has a picture open; delete what can be deleted.
            foreach (var file in Directory.EnumerateFiles(thumbs)) TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static bool IsId(string? id) => id != null && Regex.IsMatch(id, "^[0-9a-f]{8,32}$");

    // ---------- The file ----------

    internal sealed record Entry(string Id, string Name, IReadOnlyList<string> Aliases, string? Description, int PhotoCount, string? Thumbnail);

    /// <summary>The file's text. Written property by property, so no field can be added by accident.</summary>
    internal static string BuildJson(IReadOnlyList<Entry> people, DateTimeOffset updated)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteString("updated", updated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            w.WriteStartArray("people");
            foreach (var p in people)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("name", p.Name);
                w.WriteStartArray("aliases");
                foreach (var alias in p.Aliases) w.WriteStringValue(alias);
                w.WriteEndArray();
                if (p.Description != null) w.WriteString("description", p.Description);
                w.WriteNumber("photoCount", p.PhotoCount);
                if (p.Thumbnail != null) w.WriteString("thumbnail", p.Thumbnail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
