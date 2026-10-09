using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>
/// Reads a web page the user has opened in Ocula's browser pane and works out which images
/// are on it. For a thumbnail that links to another page (a gallery or forum post), it opens
/// that page once and looks for the full-size picture. It never goes further than one link,
/// never fills in forms, and does not try to get past ad screens, countdowns, paywalls or logins.
/// </summary>
public static class PageScanner
{
    /// <summary>JavaScript run inside the page (so logged-in and script-built pages work). Returns JSON text.</summary>
    public const string GrabScript = """
(() => {
  const abs = u => { try { return new URL(u, document.baseURI).href; } catch (e) { return null; } };
  const clean = s => (s || '').replace(/\s+/g, ' ').trim().slice(0, 300);
  const text = e => clean(e && (e.innerText || e.textContent));
  const meta = n => { const m = document.querySelector('meta[property="' + n + '"],meta[name="' + n + '"]'); return m ? clean(m.content) : ''; };
  const bestSrc = img => {
    let u = img.currentSrc || img.getAttribute('src') || '';
    const lazy = img.dataset.src || img.dataset.original || img.dataset.lazySrc || img.dataset.full || img.dataset.url || '';
    if ((!u || u.startsWith('data:')) && lazy) u = lazy;
    const ss = img.getAttribute('srcset') || img.dataset.srcset || '';
    if (ss) {
      let best = null, bw = -1;
      ss.split(',').forEach(p => {
        const parts = p.trim().split(/\s+/);
        const w = parseFloat(parts[1]) || 1;
        if (parts[0] && w >= bw) { bw = w; best = parts[0]; }
      });
      if (best) u = best;
    }
    return abs(u);
  };
  const imgExt = /\.(jpe?g|png|gif|webp|avif|bmp|tiff?)(\?|#|$)/i;
  const seen = new Set();
  const images = [];
  for (const img of document.images) {
    const src = bestSrc(img);
    if (!src || src.startsWith('data:') || seen.has(src)) continue;
    const w = img.naturalWidth || img.width || 0, h = img.naturalHeight || img.height || 0;
    if (w && h && (w < 80 || h < 80)) continue;               // icons, spacers, avatars
    seen.add(src);
    const a = img.closest('a[href]');
    const fig = img.closest('figure');
    images.push({
      src, w, h,
      alt: clean(img.alt || img.title),
      caption: fig ? text(fig.querySelector('figcaption')) : '',
      link: a ? abs(a.getAttribute('href')) : null
    });
  }
  for (const a of document.querySelectorAll('a[href]')) {      // plain links to image files
    const href = abs(a.getAttribute('href'));
    if (!href || seen.has(href) || !imgExt.test(href) || a.querySelector('img')) continue;
    seen.add(href);
    images.push({ src: href, w: 0, h: 0, alt: text(a), caption: '', link: null });
  }
  const crumbs = [...document.querySelectorAll('[aria-label*="readcrumb" i] a, .breadcrumb a, .breadcrumbs a, nav ol li a')]
    .map(text).filter(Boolean).slice(0, 8);
  const headings = [...document.querySelectorAll('h1, h2')].map(text).filter(Boolean).slice(0, 12);
  const keywords = meta('keywords').split(',').map(clean).filter(Boolean).slice(0, 12);
  return JSON.stringify({
    url: location.href,
    title: meta('og:title') || clean(document.title),
    description: meta('og:description') || meta('description'),
    headings, crumbs, keywords, images
  });
})()
""";

    /// <summary>Turns the script's JSON into a page snapshot.</summary>
    public static PageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        static int Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
        static List<string> List(JsonElement e, string name)
        {
            var list = new List<string>();
            if (e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) list.Add(s);
            return list;
        }

        var snapshot = new PageSnapshot
        {
            Url = Str(root, "url"),
            Title = Str(root, "title"),
            Description = Str(root, "description"),
            Headings = List(root, "headings"),
            Breadcrumbs = List(root, "crumbs"),
            Keywords = List(root, "keywords")
        };

        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in images.EnumerateArray())
            {
                var src = Str(item, "src");
                if (!IsWeb(src)) continue;
                var link = Str(item, "link");
                snapshot.Images.Add(new WebImage
                {
                    ThumbUrl = src,
                    LinkUrl = IsWeb(link) && link != src ? link : null,
                    Alt = Str(item, "alt"),
                    Caption = Str(item, "caption"),
                    Width = Num(item, "w"),
                    Height = Num(item, "h")
                });
            }
        }
        return snapshot;
    }

    // ================= Thumbnail > linked page > full-size picture =================

    private static readonly Regex ImageExtension =
        new(@"\.(jpe?g|png|gif|webp|avif|bmp|tiff?)(\?|#|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsImageUrl(string url) => ImageExtension.IsMatch(url);

    private static bool IsWeb(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// For every image that links somewhere, finds the full-size picture. Images that link straight to
    /// an image file use that file; links to pages are opened once and read. If nothing larger is found
    /// the image is marked with a problem and left out of the download.
    /// </summary>
    public static async Task ResolveAsync(HttpClient http, IReadOnlyList<WebImage> images, Uri page,
        string? pageCookies, IProgress<int>? progress, CancellationToken cancel)
    {
        var linked = images.Where(i => i.LinkUrl != null).ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(4);

        await Task.WhenAll(linked.Select(async image =>
        {
            await gate.WaitAsync(cancel);
            try
            {
                await ResolveOneAsync(http, image, page, pageCookies, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                image.Problem = "Couldn't find the full-size version.";
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        }));
    }

    private static async Task ResolveOneAsync(HttpClient http, WebImage image, Uri page, string? cookies, CancellationToken cancel)
    {
        var link = image.LinkUrl!;
        if (IsImageUrl(link))
        {
            image.FullUrl = link;
            return;
        }

        var html = await GetTextAsync(http, new Uri(link), page, cookies, cancel);
        if (html == null)
        {
            image.Problem = "Couldn't open the linked page.";
            return;
        }

        var baseUri = new Uri(link);
        var candidates = FindCandidates(html, baseUri)
            .Where(c => c != image.ThumbUrl)
            .Take(6)
            .ToList();

        string? best = null;
        long bestSize = 0;
        foreach (var candidate in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            var size = await ImageSizeAsync(http, new Uri(candidate), baseUri, cookies, cancel);
            if (size > bestSize) { bestSize = size; best = candidate; }
        }

        // Anything under about 30 KB is an icon, ad or placeholder, not the photo.
        if (best != null && bestSize >= 30_000) image.FullUrl = best;
        else image.Problem = "Couldn't find the full-size version.";
    }

    private static readonly Regex MetaTag = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LinkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImgTag = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value
                                                              : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }

    /// <summary>Likely full-size pictures on a page, best guesses first: declared main image, then large &lt;img&gt; tags.</summary>
    internal static IEnumerable<string> FindCandidates(string html, Uri baseUri)
    {
        var found = new List<string>();
        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(baseUri, url.Trim(), out var abs) || !IsWeb(abs.AbsoluteUri)) return;
            if (!found.Contains(abs.AbsoluteUri)) found.Add(abs.AbsoluteUri);
        }

        foreach (Match m in MetaTag.Matches(html))
        {
            var key = (Attr(m.Value, "property") ?? Attr(m.Value, "name") ?? string.Empty).ToLowerInvariant();
            if (key is "og:image" or "og:image:url" or "og:image:secure_url" or "twitter:image" or "twitter:image:src")
                Add(Attr(m.Value, "content"));
        }
        foreach (Match m in LinkTag.Matches(html))
            if ((Attr(m.Value, "rel") ?? string.Empty).Equals("image_src", StringComparison.OrdinalIgnoreCase))
                Add(Attr(m.Value, "href"));

        // Pictures in the page body: bigger declared size first; names that sound like the photo next.
        var tags = ImgTag.Matches(html).Select(m => m.Value).Select(tag =>
        {
            var src = Attr(tag, "data-src") ?? Attr(tag, "data-original") ?? Attr(tag, "src");
            _ = int.TryParse(Attr(tag, "width"), out var w);
            _ = int.TryParse(Attr(tag, "height"), out var h);
            var hint = ((Attr(tag, "id") ?? "") + " " + (Attr(tag, "class") ?? "")).ToLowerInvariant();
            var named = hint.Contains("full") || hint.Contains("main") || hint.Contains("photo") || hint.Contains("picture") || hint.Contains("image");
            return (src, area: w * h, named);
        })
        .Where(t => t.src != null)
        .OrderByDescending(t => t.named).ThenByDescending(t => t.area);
        foreach (var t in tags) Add(t.src);

        return found;
    }

    private static HttpRequestMessage Request(Uri uri, Uri referer, string? cookies)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = referer;
        // The page's own cookies are only sent back to the same site, never to another host.
        if (!string.IsNullOrEmpty(cookies) && string.Equals(uri.Host, referer.Host, StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        return request;
    }

    private static async Task<string?> GetTextAsync(HttpClient http, Uri uri, Uri referer, string? cookies, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(Request(uri, referer, cookies), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) return null;
        var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

        // Read at most about 2 MB of the page.
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[2 * 1024 * 1024];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token)) > 0) total += read;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>Size in bytes of an image address, or 0 if it isn't an image.</summary>
    private static async Task<long> ImageSizeAsync(HttpClient http, Uri uri, Uri referer, string? cookies, CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.SendAsync(Request(uri, referer, cookies), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return 0;
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return 0;
            return response.Content.Headers.ContentLength ?? 0;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch { return 0; }
    }
}
