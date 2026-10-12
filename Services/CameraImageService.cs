using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ocula.Services;

public sealed record CameraImage(string LocalPath, string SourcePageUrl);

/// <summary>
/// Finds a real photo of a camera model online and caches it on disk.
///
/// Source: Wikidata (a free, open database) links many camera and phone models to a photo on
/// Wikimedia Commons. We search Wikidata for the model name, keep results described as a
/// camera or phone, and download that item's image (property P18) at a small size. New models
/// often have no photo there yet, so it also tries the Wikipedia article's freely licensed
/// photo and shorter names ("Pixel 10 Pro", then "Pixel 10"); see CameraText.SearchNames.
///
/// Commons images are freely licensed but usually require attribution, which is why the
/// UI shows an "Image: Wikimedia Commons" link to the image's page.
///
/// Results are cached in %LOCALAPPDATA%\Ocula\CameraCache, so each camera model is only looked
/// up once; "not found" is looked up again after 30 days.
/// </summary>
public sealed class CameraImageService
{
    private static HttpClient Http => Wikimedia.Http;

    private readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocula", "CameraCache");

    /// <summary>Written to the info file when nothing was found. Empty files are from before phones were looked up.</summary>
    private const string NotFound = "not found (lookup 2)";

    private readonly ConcurrentDictionary<string, Task<CameraImage?>> _lookups = new(StringComparer.OrdinalIgnoreCase);

    public Task<CameraImage?> GetAsync(string cameraName) =>
        AppSettings.OnlineCameraLookup
            ? _lookups.GetOrAdd(cameraName, LookupAsync)
            : Task.FromResult<CameraImage?>(null); // turned off in Settings > Privacy

    private async Task<CameraImage?> LookupAsync(string cameraName)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var key = Slug(cameraName);
            var imagePath = Path.Combine(_cacheDir, key + ".jpg");
            var infoPath = Path.Combine(_cacheDir, key + ".txt");

            if (File.Exists(infoPath))
            {
                var sourcePage = await File.ReadAllTextAsync(infoPath);
                if (sourcePage.StartsWith("https://", StringComparison.Ordinal) && File.Exists(imagePath))
                    return new CameraImage(imagePath, sourcePage);
                if (sourcePage == NotFound && File.GetLastWriteTimeUtc(infoPath) > DateTime.UtcNow.AddDays(-30))
                    return null;
            }

            var commonsFile = await FindCommonsFileAsync(cameraName);
            if (commonsFile == null)
            {
                await File.WriteAllTextAsync(infoPath, NotFound);
                return null;
            }

            var imageUrl = "https://commons.wikimedia.org/wiki/Special:FilePath/" + Uri.EscapeDataString(commonsFile) + "?width=480";
            var bytes = await Http.GetByteArrayAsync(imageUrl);
            await File.WriteAllBytesAsync(imagePath, bytes);

            var page = "https://commons.wikimedia.org/wiki/File:" + Uri.EscapeDataString(commonsFile.Replace(' ', '_'));
            await File.WriteAllTextAsync(infoPath, page);
            return new CameraImage(imagePath, page);
        }
        catch
        {
            // Offline or the service is down: don't cache, so it's retried next time the app runs.
            _lookups.TryRemove(cameraName, out _);
            return null;
        }
    }

    /// <summary>
    /// The Commons file name of a photo of the camera: Wikidata by the full name (with and
    /// without the brand), then the Wikipedia article, then Wikidata by shorter names.
    /// </summary>
    private static async Task<string?> FindCommonsFileAsync(string cameraName)
    {
        var names = CameraText.SearchNames(cameraName);
        var model = CameraText.ModelNumber(cameraName);
        for (var i = 0; i < names.Count; i++)
        {
            if (await FromWikidataAsync(names[i], model) is { } file) return file;
            if (i == Math.Min(1, names.Count - 1) && await FromWikipediaAsync(cameraName, model) is { } article) return article;
        }
        return null;
    }

    private static async Task<string?> FromWikidataAsync(string name, string? model)
    {
        var searchUrl = "https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&language=en&type=item&limit=7&search="
                        + Uri.EscapeDataString(name);
        using var search = JsonDocument.Parse(await Http.GetStringAsync(searchUrl));

        var candidates = search.RootElement.GetProperty("search").EnumerateArray()
            .Where(r => CameraText.IsDevice(Text(r, "description")) &&
                        (CameraText.Mentions(Text(r, "label"), model) ||
                         r.TryGetProperty("match", out var match) && CameraText.Mentions(Text(match, "text"), model)))
            .Select(r => Text(r, "id"))
            .OfType<string>()
            .Take(3)
            .ToList();

        foreach (var id in candidates)
        {
            var claimsUrl = $"https://www.wikidata.org/w/api.php?action=wbgetclaims&format=json&property=P18&entity={id}";
            using var claims = JsonDocument.Parse(await Http.GetStringAsync(claimsUrl));
            if (claims.RootElement.TryGetProperty("claims", out var all) &&
                all.TryGetProperty("P18", out var images) &&
                images.GetArrayLength() > 0)
            {
                return images[0].GetProperty("mainsnak").GetProperty("datavalue").GetProperty("value").GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// The photo at the top of the best-matching English Wikipedia article, if it's freely
    /// licensed (so it's on Commons). "Google Pixel 10 Pro XL" finds the "Pixel 10" article.
    /// </summary>
    private static async Task<string?> FromWikipediaAsync(string name, string? model)
    {
        var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrlimit=3"
                  + "&prop=pageimages|description&piprop=name&pilicense=free&gsrsearch=" + Uri.EscapeDataString(name);
        using var result = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!result.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages))
            return null;

        return pages.EnumerateArray()
            .OrderBy(p => p.TryGetProperty("index", out var index) ? index.GetInt32() : int.MaxValue)
            .Where(p => CameraText.IsDevice(Text(p, "description")) && CameraText.Mentions(Text(p, "title"), model))
            .Select(p => Text(p, "pageimage"))
            .FirstOrDefault(file => file != null);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Slug(string text) =>
        string.Concat(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
}
