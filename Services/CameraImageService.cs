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
/// Source: Wikidata (a free, open database) links many camera models to a photo on
/// Wikimedia Commons. We search Wikidata for the model name, keep results described as a
/// camera, and download that item's image (property P18) at a small size.
///
/// Commons images are freely licensed but usually require attribution, which is why the
/// UI shows an "Image: Wikimedia Commons" link to the image's page.
///
/// Results (including "not found") are cached in %LOCALAPPDATA%\Ocula\CameraCache, so each
/// camera model is only looked up once.
/// </summary>
public sealed class CameraImageService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocula", "CameraCache");

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
                // An empty info file records "we looked and found nothing".
                return sourcePage.Length > 0 && File.Exists(imagePath) ? new CameraImage(imagePath, sourcePage) : null;
            }

            var commonsFile = await FindCommonsFileAsync(cameraName);
            if (commonsFile == null)
            {
                await File.WriteAllTextAsync(infoPath, string.Empty);
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

    private static async Task<string?> FindCommonsFileAsync(string cameraName)
    {
        var searchUrl = "https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&language=en&type=item&limit=7&search="
                        + Uri.EscapeDataString(cameraName);
        using var search = JsonDocument.Parse(await Http.GetStringAsync(searchUrl));

        var candidates = search.RootElement.GetProperty("search").EnumerateArray()
            .Where(r => r.TryGetProperty("description", out var d) &&
                        (d.GetString() ?? "").Contains("camera", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.GetProperty("id").GetString())
            .Where(id => id != null)
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Wikimedia asks every app to identify itself with a contact address.
        // TODO: replace with your own app URL or email before releasing.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ocula/0.1 (contact: you@example.com)");
        return client;
    }

    private static string Slug(string text) =>
        string.Concat(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
}
