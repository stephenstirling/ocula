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
/// Forum-aware: prefers images inside posts, ignores avatars/signatures/logos, and reports
/// other pages of the current thread when present.
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

  // Forum / post content: XenForo, vBulletin, phpBB, Discourse, generic message bodies.
  const postSel = '.message-body, .bbWrapper, .messageContent, .message-content, .postbody, .post-body, .post_message, .postcontent, .post-content, .bbCodeImage, article .content, .topic-body, .cooked, .post';
  const ignoreSel = 'aside, nav, header, footer, .signature, .message-signature, .userBanner, .avatar, .memberHeader, .p-nav, .p-header, .p-footer, .sidebar, .sideNav, #navigation, #header, #footer, .ad, .ads, .advert, [class*="avatar"], [class*="signature"], [id*="signature"]';

  const inPost = el => !!(el.closest(postSel) && !el.closest(ignoreSel));
  const isIgnored = el => !!el.closest(ignoreSel);

  const bestSrc = img => {
    // Prefer explicit original/full-size attributes over currentSrc: browsers often select
    // a thumbnail from srcset even when the page also exposes the original in a data-* field.
    const preferred = img.dataset.full || img.dataset.original || img.dataset.originalSrc ||
      img.dataset.large || img.dataset.zoomImage || img.dataset.image ||
      img.dataset.url || img.dataset.lazySrc || img.dataset.src || '';
    let u = preferred || img.currentSrc || img.getAttribute('src') || '';
    if (!u || u.startsWith('data:')) {
      u = img.dataset.original || img.dataset.originalSrc || img.dataset.full ||
        img.dataset.large || img.dataset.lazySrc || img.dataset.src || img.getAttribute('src') || '';
    }

    // Only use srcset when no explicit original/full-size URL was supplied.
    const ss = !preferred
      ? (img.getAttribute('srcset') || img.dataset.srcset || img.dataset.lazySrcset || img.dataset.originalSrcset || '')
      : '';
    if (ss) {
      let best = null, bw = -1;
      ss.split(',').forEach(p => {
        const parts = p.trim().split(/\\s+/);
        const descriptor = parts[1] || '';
        const w = descriptor.endsWith('w') ? parseFloat(descriptor) : (descriptor.endsWith('x') ? parseFloat(descriptor) * 1000 : 1);
        if (parts[0] && Number.isFinite(w) && w >= bw) { bw = w; best = parts[0]; }
      });
      if (best) u = best;
    }

    // Some forum themes put the useful URL on a <source> inside <picture>.
    if (!preferred && (!u || u.startsWith('data:'))) {
      const source = img.closest('picture')?.querySelector('source[srcset],source[data-srcset]');
      const sourceSet = source?.getAttribute('srcset') || source?.getAttribute('data-srcset') || '';
      if (sourceSet) u = sourceSet.split(',').map(p => p.trim().split(/\\s+/)[0]).filter(Boolean).pop() || u;
    }
    return abs(u);
  };

  const imgExt = /\.(jpe?g|png|gif|webp|avif|bmp|tiff?)(\?|#|$)/i;
  const seen = new Set();
  const images = [];

  for (const img of document.images) {
    if (isIgnored(img)) continue;
    const src = bestSrc(img);
    if (!src || src.startsWith('data:') || seen.has(src)) continue;
    const w = img.naturalWidth || img.width || 0, h = img.naturalHeight || img.height || 0;
    const post = inPost(img);
    // Tighter size filter for non-post images (avatars, icons, chrome).
    const minSide = post ? 60 : 120;
    if (w && h && (w < minSide || h < minSide)) continue;
    // Skip obvious avatar / icon paths even if size is unknown.
    if (!post && /avatar|icon|logo|smiley|emoticon|sprite/i.test(src)) continue;
    seen.add(src);
    const a = img.closest('a[href]');
    const fig = img.closest('figure');
    images.push({
      src, w, h,
      alt: clean(img.alt || img.title),
      caption: fig ? text(fig.querySelector('figcaption')) : '',
      link: a ? abs(a.getAttribute('href')) : null,
      inPost: post
    });
  }

  for (const a of document.querySelectorAll('a[href]')) {
    if (isIgnored(a) || a.querySelector('img')) continue;
    const href = abs(a.getAttribute('href'));
    if (!href || seen.has(href) || !imgExt.test(href)) continue;
    const post = inPost(a);
    seen.add(href);
    images.push({ src: href, w: 0, h: 0, alt: text(a), caption: '', link: null, inPost: post });
  }

  // Prefer post images when the page has a clear post structure.
  const postCount = images.filter(i => i.inPost).length;
  const filtered = postCount >= 2 ? images.filter(i => i.inPost) : images;

  const crumbs = [...document.querySelectorAll('[aria-label*="readcrumb" i] a, .breadcrumb a, .breadcrumbs a, nav ol li a')]
    .map(text).filter(Boolean).slice(0, 8);
  const headings = [...document.querySelectorAll('h1, h2')].map(text).filter(Boolean).slice(0, 12);
  const keywords = meta('keywords').split(',').map(clean).filter(Boolean).slice(0, 12);

  // Other pages of this thread (XenForo, vBulletin, phpBB, Discourse, generic).
  const pageUrls = [];
  const pageSeen = new Set([location.href.split('#')[0]]);
  const addPage = href => {
    const u = abs(href);
    if (!u || pageSeen.has(u.split('#')[0])) return;
    pageSeen.add(u.split('#')[0]);
    pageUrls.push(u.split('#')[0]);
  };
  document.querySelectorAll(
    '.pageNav a[href], .pagination a[href], .PageNav a[href], nav[aria-label*="page" i] a[href], .pagelinks a[href], .pages a[href]'
  ).forEach(a => {
    const t = (a.textContent || '').trim();
    const rel = (a.getAttribute('rel') || '').toLowerCase();
    if (rel === 'next' || rel === 'prev' || /^\d+$/.test(t) || /next|prev|previous|>|»|«/i.test(t))
      addPage(a.getAttribute('href'));
  });

  return JSON.stringify({
    url: location.href,
    title: meta('og:title') || clean(document.title),
    description: meta('og:description') || meta('description'),
    headings, crumbs, keywords,
    images: filtered,
    pageUrls: pageUrls.slice(0, 40)
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
        static bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
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
            Keywords = List(root, "keywords"),
            PageUrls = List(root, "pageUrls")
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
                    Height = Num(item, "h"),
                    InPost = Flag(item, "inPost")
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
    /// an image file use that file; links to pages are opened once and read. Host-specific logic is
    /// used for common image hosts (ImageTwist, ImageBam, etc.). If nothing larger is found the image
    /// is marked with a problem and left out of the download.
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

        // An address that ends in an image extension is the picture itself, except on the image hosts below, whose landing pages
        // are addressed like pictures (pixhost.to/show/1234/ID_name.jpg) but are web pages that hold the real picture.
        if (IsImageUrl(link) && !IsHostLandingPage(link))
        {
            image.FullUrl = link;
            return;
        }

        var html = await GetTextAsync(http, new Uri(link), page, cookies, cancel);
        if (html == null)
        {
            // Not a page we can read. A host landing address that is really a picture (not HTML) is still a usable picture.
            if (IsImageUrl(link) && await ImageSizeAsync(http, new Uri(link), page, cookies, cancel) > 0)
                image.FullUrl = link;
            else
                image.Problem = "Couldn't open the linked page.";
            return;
        }

        var baseUri = new Uri(link);

        // Host-specific extraction from the landing page HTML.
        if (TryHostFromHtml(link, html, baseUri, out var fromHost))
        {
            image.FullUrl = fromHost;
            return;
        }

        var candidates = FindCandidates(html, baseUri)
            .Where(c => c != image.ThumbUrl)
            .Take(8)
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

    // ================= Host-specific full-size helpers =================

    /// <summary>
    /// True for an address on a known image host that is a landing PAGE even though it may end in .jpg, so it has to be opened and read,
    /// not downloaded as a picture. Pixhost: pixhost.to/show/NNNN/ID_name.jpg (the pictures are on imgN.pixhost.to/images and
    /// tN.pixhost.to/thumbs). ImageTwist and ImageHaha: imagetwist.com/CODE/name.jpg, or the same under another subdomain such as
    /// phun.imagetwist.com (the pictures are on imgN.imagetwist.com, under /i/ or /th/). ImageBam: www.imagebam.com/view/CODE.
    /// TurboImageHost: every page on turboimagehost.com (the pictures are on turboimg.net). The host is matched whole (the name itself or
    /// a subdomain of it), so a different site that merely contains the name isn't taken for it.
    /// </summary>
    internal static bool IsHostLandingPage(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        if (HostIs(host, "pixhost.to") || HostIs(host, "pixhost.org"))
            return path.StartsWith("/show/", StringComparison.OrdinalIgnoreCase);

        if (HostIs(host, "imagetwist.com") || HostIs(host, "imagehaha.com"))
            return !Regex.IsMatch(host, @"^img\d*\.") &&
                   !path.Contains("/i/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/th/", StringComparison.OrdinalIgnoreCase);

        if (HostIs(host, "imagebam.com"))
            return host is "imagebam.com" or "www.imagebam.com";

        if (HostIs(host, "turboimagehost.com"))
            return true;

        return false;
    }

    private static bool HostIs(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>Pull the largest sensible image URL from a known host's landing-page HTML.</summary>
    private static bool TryHostFromHtml(string link, string html, Uri baseUri, out string full)
    {
        full = string.Empty;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();

        // ---- ImageTwist / ImageHaha ----
        if (host.Contains("imagetwist") || host.Contains("imagehaha"))
        {
            // Common patterns: <img id="image" ...>, data-src on the main viewer, og:image.
            foreach (Match m in Regex.Matches(html,
                @"(?:id\s*=\s*[""']image[""'][^>]*src|src\s*=\s*[""']([^""']+)[""'][^>]*id\s*=\s*[""']image[""'])",
                RegexOptions.IgnoreCase))
            {
                var src = m.Groups[1].Success ? m.Groups[1].Value : Attr(m.Value, "src");
                if (AddIfImage(src, baseUri, ref full)) return true;
            }

            foreach (Match m in Regex.Matches(html,
                @"<img\b[^>]*(?:class\s*=\s*[""'][^""']*(?:pic|full|main|image)[^""']*[""'])[^>]*>",
                RegexOptions.IgnoreCase))
            {
                var src = Attr(m.Value, "data-src") ?? Attr(m.Value, "src");
                if (AddIfImage(src, baseUri, ref full)) return true;
            }

            // og:image is usually the full file on ImageTwist.
            foreach (Match m in MetaTag.Matches(html))
            {
                var key = (Attr(m.Value, "property") ?? Attr(m.Value, "name") ?? string.Empty).ToLowerInvariant();
                if (key is "og:image" or "og:image:url" or "og:image:secure_url")
                {
                    if (AddIfImage(Attr(m.Value, "content"), baseUri, ref full)) return true;
                }
            }

            // Direct file links that look like imgN.imagetwist.com/i/.../file.jpg
            foreach (Match m in Regex.Matches(html,
                @"https?://(?:img\d+\.)?(?:[\w.-]+\.)?image(?:twist|haha)\.com/[^""'\s>]+\.(?:jpe?g|png|webp|gif)",
                RegexOptions.IgnoreCase))
            {
                if (!m.Value.Contains("/th/", StringComparison.OrdinalIgnoreCase) &&
                    AddIfImage(m.Value, baseUri, ref full))
                    return true;
            }
        }

        // ---- ImageBam ----
        if (host.Contains("imagebam"))
        {
            foreach (Match m in Regex.Matches(html,
                @"<img\b[^>]*(?:class\s*=\s*[""'][^""']*main-image[^""']*[""']|id\s*=\s*[""'][^""']*image[^""']*[""'])[^>]*>",
                RegexOptions.IgnoreCase))
            {
                var src = Attr(m.Value, "src") ?? Attr(m.Value, "data-src");
                if (AddIfImage(src, baseUri, ref full)) return true;
            }
            foreach (Match m in MetaTag.Matches(html))
            {
                var key = (Attr(m.Value, "property") ?? Attr(m.Value, "name") ?? string.Empty).ToLowerInvariant();
                if (key is "og:image" or "og:image:url")
                {
                    if (AddIfImage(Attr(m.Value, "content"), baseUri, ref full)) return true;
                }
            }
        }

        // ---- Pixhost (pixhost.to / pixhost.org) ----
        // Landing: pixhost.to/show/NNNN/ID_name.jpg
        // Full:    imgN.pixhost.to/images/NNNN/ID_name.jpg  (id="image", class image-img)
        // og:image is the *thumbnail* — never use it as full size.
        if (host.Contains("pixhost"))
        {
            foreach (Match m in Regex.Matches(html,
                @"<img\b[^>]*(?:id\s*=\s*[""']image[""']|class\s*=\s*[""'][^""']*image-img[^""']*[""'])[^>]*>",
                RegexOptions.IgnoreCase))
            {
                var src = Attr(m.Value, "src") ?? Attr(m.Value, "data-src");
                if (src != null && src.Contains("/images/", StringComparison.OrdinalIgnoreCase) &&
                    AddIfImage(src, baseUri, ref full))
                    return true;
            }
            foreach (Match m in Regex.Matches(html,
                @"https?://(?:img\d+\.)?pixhost\.(?:to|org)/images/[^""'\s>]+\.(?:jpe?g|png|webp|gif)",
                RegexOptions.IgnoreCase))
            {
                if (AddIfImage(m.Value, baseUri, ref full)) return true;
            }
        }

        // ---- TurboImageHost ----
        if (host.Contains("turboimagehost"))
        {
            foreach (Match m in Regex.Matches(html,
                @"<img\b[^>]*(?:id\s*=\s*[""']imageid[""']|class\s*=\s*[""'][^""']*(?:uimage|main-image|full)[^""']*[""'])[^>]*>",
                RegexOptions.IgnoreCase))
            {
                var src = Attr(m.Value, "src") ?? Attr(m.Value, "data-src");
                if (AddIfImage(src, baseUri, ref full)) return true;
            }
            foreach (Match m in MetaTag.Matches(html))
            {
                var key = (Attr(m.Value, "property") ?? Attr(m.Value, "name") ?? string.Empty).ToLowerInvariant();
                if (key is "og:image" or "og:image:url")
                {
                    var content = Attr(m.Value, "content");
                    // Prefer non-thumb paths when possible
                    if (content != null && !content.Contains("/th/", StringComparison.OrdinalIgnoreCase) &&
                        AddIfImage(content, baseUri, ref full))
                        return true;
                }
            }
        }

        // ---- Generic: og:image that points at a real image file ----
        // Skip hosts where og:image is known to be a thumbnail only.
        if (host.Contains("pixhost"))
            return false;

        foreach (Match m in MetaTag.Matches(html))
        {
            var key = (Attr(m.Value, "property") ?? Attr(m.Value, "name") ?? string.Empty).ToLowerInvariant();
            if (key is "og:image" or "og:image:url" or "og:image:secure_url" or "twitter:image" or "twitter:image:src")
            {
                var content = Attr(m.Value, "content");
                if (content != null && IsImageUrl(content) && AddIfImage(content, baseUri, ref full))
                    return true;
            }
        }

        return false;
    }

    private static bool AddIfImage(string? url, Uri baseUri, ref string full)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(baseUri, url.Trim(), out var abs) || !IsWeb(abs.AbsoluteUri)) return false;
        var u = abs.AbsoluteUri;
        // Prefer URLs that look like actual image files; skip known thumbnail paths.
        if (u.Contains("/thumbs/", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("/th/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsImageUrl(u) && !u.Contains("/i/", StringComparison.OrdinalIgnoreCase) &&
            !u.Contains("/images/", StringComparison.OrdinalIgnoreCase))
            return false;
        full = u;
        return true;
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
            var src = Attr(tag, "data-full") ?? Attr(tag, "data-original") ?? Attr(tag, "data-original-src") ??
                      Attr(tag, "data-large") ?? Attr(tag, "data-src") ?? Attr(tag, "data-lazy-src") ??
                      Attr(tag, "src") ?? FirstSrcsetUrl(Attr(tag, "data-srcset") ?? Attr(tag, "srcset"));
            _ = int.TryParse(Attr(tag, "width"), out var w);
            _ = int.TryParse(Attr(tag, "height"), out var h);
            var hint = ((Attr(tag, "id") ?? "") + " " + (Attr(tag, "class") ?? "")).ToLowerInvariant();
            var named = hint.Contains("full") || hint.Contains("main") || hint.Contains("photo") || hint.Contains("picture") || hint.Contains("image") || hint.Contains("pic");
            return (src, area: w * h, named);
        })
        .Where(t => t.src != null)
        .OrderByDescending(t => t.named).ThenByDescending(t => t.area);
        foreach (var t in tags) Add(t.src);

        return found;
    }

    private static string? FirstSrcsetUrl(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset)) return null;
        return srcset.Split(',')
            .Select(part => part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .LastOrDefault(url => !string.IsNullOrWhiteSpace(url));
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
