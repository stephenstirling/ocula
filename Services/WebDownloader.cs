using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

public sealed record WebDownloadProgress(int Done, int Total, string Name);

public sealed record WebDownloadResult(List<string> Saved, int Duplicates, List<string> Failures);

/// <summary>Downloads the pictures the user ticked, a few at a time, into one folder.</summary>
public static class WebDownloader
{
    private const long MaxBytes = 200L * 1024 * 1024;

    public static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Ocula/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/*,text/html;q=0.8,*/*;q=0.5");
        return http;
    }

    public static async Task<WebDownloadResult> RunAsync(HttpClient http, IReadOnlyList<WebImage> items, string folder,
        Uri page, string? cookies, IProgress<WebDownloadProgress>? progress, CancellationToken cancel,
        Action<string>? prepare = null)
    {
        Directory.CreateDirectory(folder);
        var saved = new List<string>();
        var failures = new List<string>();
        var duplicates = 0;
        var done = 0;
        var nameLock = new object();
        var hashes = new Dictionary<string, HashSet<string>>(); // length -> hashes of files already in the folder
        using var gate = new SemaphoreSlim(3);

        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancel);
            try
            {
                var result = await DownloadOneAsync(http, item, folder, page, cookies, nameLock, hashes, prepare, cancel);
                lock (nameLock)
                {
                    if (result.Path != null) saved.Add(result.Path);
                    else if (result.Duplicate) duplicates++;
                    else failures.Add($"{item.FileName}: {result.Error}");
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (nameLock) failures.Add($"{item.FileName}: {ex.Message}");
            }
            finally
            {
                gate.Release();
                progress?.Report(new WebDownloadProgress(Interlocked.Increment(ref done), items.Count, item.FileName));
            }
        }));

        return new WebDownloadResult(saved, duplicates, failures);
    }

    private sealed record One(string? Path, bool Duplicate, string? Error);

    private static async Task<One> DownloadOneAsync(HttpClient http, WebImage item, string folder, Uri page, string? cookies,
        object nameLock, Dictionary<string, HashSet<string>> hashes, Action<string>? prepare, CancellationToken cancel)
    {
        var url = new Uri(item.FullUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = item.LinkUrl != null && Uri.TryCreate(item.LinkUrl, UriKind.Absolute, out var linkUri) ? linkUri : page;
        if (!string.IsNullOrEmpty(cookies) && string.Equals(url.Host, page.Host, StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("Cookie", cookies);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!response.IsSuccessStatusCode) return new One(null, false, $"the site answered {(int)response.StatusCode}");

        var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return new One(null, false, "it isn't an image file");
        if (response.Content.Headers.ContentLength > MaxBytes) return new One(null, false, "the file is very large");

        var name = FileNameFor(response, url, type);
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        string hash;
        long length;
        try
        {
            await using (var file = File.Create(temp))
            await using (var stream = await response.Content.ReadAsStreamAsync(cancel))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) throw new IOException("the file is very large");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                }
                length = total;
            }

            // Changes the picture before it gets its final name (the trim option). The file is judged as a duplicate, and
            // named, only after this, so only the changed version is ever kept and a second download matches it.
            if (prepare != null)
            {
                prepare(temp);
                length = new FileInfo(temp).Length;
            }

            await using (var check = File.OpenRead(temp))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancel));

            lock (nameLock)
            {
                // Skip a picture that is already in the folder (same bytes).
                var key = length.ToString();
                if (!hashes.TryGetValue(key, out var known))
                {
                    known = new HashSet<string>();
                    foreach (var existing in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length == length))
                    {
                        using var s = File.OpenRead(existing);
                        known.Add(Convert.ToHexString(SHA256.HashData(s)));
                    }
                    hashes[key] = known;
                }
                if (!known.Add(hash))
                {
                    File.Delete(temp);
                    return new One(null, true, null);
                }

                var finalPath = UniquePath(folder, name);
                File.Move(temp, finalPath);
                return new One(finalPath, false, null);
            }
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* left behind, harmless */ }
            throw;
        }
    }

    private static string FileNameFor(HttpResponseMessage response, Uri url, string contentType)
    {
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name)) name = "image";

        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (!Path.HasExtension(name))
        {
            var ext = contentType.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp",
                "image/avif" => ".avif", "image/bmp" => ".bmp", "image/tiff" => ".tif", _ => ".jpg"
            };
            name += ext;
        }
        return name.Length > 120 ? Path.GetFileNameWithoutExtension(name)[..100] + Path.GetExtension(name) : name;
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    /// <summary>Makes a page title safe to use as a folder name.</summary>
    public static string FolderNameFor(string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Web download" : title.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        return name.Length > 60 ? name[..60].TrimEnd() : name.Length == 0 ? "Web download" : name;
    }
}
