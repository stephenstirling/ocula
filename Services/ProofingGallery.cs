using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Models;

namespace Ocula.Services;

public sealed record ProofingOptions(string Title, string Client, string Email, string Destination, uint LongEdge, string Watermark);

/// <summary>
/// Makes a client proofing gallery: a folder with web-sized photos and one index.html.
/// It needs no server or database, so it can be copied to the NAS's web folder (Synology
/// Web Station), any web host, or a shared drive. Clients tap a heart on their favourites;
/// "Send my favourites" opens an email to you listing the file names, and Ocula can read that
/// list back to mark those photos as picked.
///
/// Anyone with the link can view a static gallery, so use a hard-to-guess folder name or the
/// web server's own password protection for private shoots.
/// </summary>
public static class ProofingGallery
{
    public static async Task<string> CreateAsync(IReadOnlyList<ImageItem> items, ProofingOptions o,
                                                 IProgress<int> progress, CancellationToken token)
    {
        // A random suffix keeps the gallery address hard to guess.
        var folderName = $"{Slug(o.Title)}-{Guid.NewGuid().ToString("N")[..6]}";
        var folder = Path.Combine(o.Destination, folderName);
        var images = Path.Combine(folder, "images");
        Directory.CreateDirectory(images);

        var preset = new ExportPreset
        {
            Name = "Proofing", Format = "JPEG", Quality = 84, LongEdge = o.LongEdge,
            RemoveLocation = true, Watermark = o.Watermark, Destination = images
        };

        var photos = new List<object>();
        var done = 0;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            var file = await Task.Run(() => ExportService.ExportOne(item, preset), token);
            photos.Add(new { file = "images/" + Path.GetFileName(file), name = Path.GetFileNameWithoutExtension(file) });
            progress.Report(++done);
        }

        var config = JsonSerializer.Serialize(new
        {
            id = folderName,
            title = o.Title,
            client = o.Client,
            email = o.Email,
            photographer = AppInfo.Publisher,
            copyright = AppInfo.Copyright,
            photos
        }); // the default encoder escapes <, > and &, so this is safe inside a <script> block

        var template = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Proofing", "gallery.html"), token);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), template.Replace("/*OCULA_CONFIG*/null", config), token);
        return folder;
    }

    /// <summary>Reads a list of file names (from the client's email) and finds the matching photos.</summary>
    public static (List<ImageItem> Matched, List<string> Missing) MatchPicks(string text, IEnumerable<ImageItem> candidates)
    {
        var byName = new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
        {
            byName.TryAdd(Path.GetFileNameWithoutExtension(item.Path), item);
            if (item.RawPath != null) byName.TryAdd(Path.GetFileNameWithoutExtension(item.RawPath), item);
        }

        var matched = new List<ImageItem>();
        var missing = new List<string>();
        foreach (var line in text.Split('\n').Select(l => l.Trim().TrimStart('-', '•', '*').Trim()).Where(l => l.Length > 0))
        {
            var name = Path.GetFileNameWithoutExtension(line);
            name = Regex.Replace(name, @" \(\d+\)$", string.Empty); // "IMG_0412 (2)" from a duplicate export name
            if (byName.TryGetValue(name, out var item)) { if (!matched.Contains(item)) matched.Add(item); }
            else missing.Add(line);
        }
        return (matched, missing);
    }

    private static string Slug(string text)
    {
        var slug = Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? slug : "gallery";
    }
}
