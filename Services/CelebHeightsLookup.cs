using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>One match on CelebHeights (name index or profile URL).</summary>
public sealed record CelebHeightsCandidate(string Name, string ProfileUrl, string? HeightPreview);

/// <summary>
/// Height from a CelebHeights profile page. The site publishes estimated barefoot heights;
/// values are not official measurements. Only the name is sent; nothing about photos.
/// </summary>
public sealed class CelebHeightsHeight
{
    public string Name { get; init; } = string.Empty;
    public string ProfileUrl { get; init; } = string.Empty;
    /// <summary>Height in centimetres as listed on the profile (e.g. 170.8).</summary>
    public double HeightCm { get; init; }
    /// <summary>Raw display text from the page, e.g. "5ft 7 ¼ (170.8 cm)".</summary>
    public string? DisplayText { get; init; }
    public DateTimeOffset RetrievedAt { get; init; }
}

/// <summary>
/// Looks up celebrity heights on celebheights.com. Search uses the site's first-name index
/// pages (/n/{firstname}.html); the profile page supplies the precise centimetre value.
/// Run only when the user asks (same privacy model as <see cref="PersonLookup"/>).
/// </summary>
public sealed class CelebHeightsLookup
{
    private const string Root = "https://www.celebheights.com";

    // Profile: /s/Katy-Perry-2673.html  or  /s/-Rihanna-2307.html
    private static readonly Regex ProfileHref = new(
        @"href\s*=\s*[""'](?<path>/s/[^""']+\.html)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Primary height on profile: <h3 class="nameH3">5ft 7 ¼ (170.8 cm)</h3>
    private static readonly Regex HeightHeading = new(
        @"<(?:h3)[^>]*class\s*=\s*[""'][^""']*nameH3[^""']*[""'][^>]*>\s*(?<text>[^<]+?)\s*</h3>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fallback: first "N.N cm" near "Height" in the top of the page.
    private static readonly Regex HeightNearLabel = new(
        @"Height[^<]{0,120}?(\d+(?:\.\d+)?)\s*cm",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CmInText = new(
        @"(\d+(?:\.\d+)?)\s*cm",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;

    public CelebHeightsLookup(HttpClient http) => _http = http;

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ocula/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        return client;
    }

    /// <summary>
    /// Finds CelebHeights profiles whose name matches <paramref name="name"/>.
    /// Uses /n/{firstname}.html; many people share a first name so the caller may need to pick.
    /// </summary>
    public async Task<List<CelebHeightsCandidate>> SearchAsync(string name, CancellationToken cancel = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return new List<CelebHeightsCandidate>();

        var parts = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new List<CelebHeightsCandidate>();

        // Index is by first given name only: /n/katy.html
        var first = SanitizeIndexToken(parts[0]);
        if (first.Length == 0) return new List<CelebHeightsCandidate>();

        string html;
        try
        {
            html = await _http.GetStringAsync($"{Root}/n/{first}.html", cancel);
        }
        catch (HttpRequestException)
        {
            return new List<CelebHeightsCandidate>();
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            // An HTTP timeout is a lookup miss; caller cancellation still propagates.
            return new List<CelebHeightsCandidate>();
        }

        var found = new Dictionary<string, CelebHeightsCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ProfileHref.Matches(html))
        {
            var path = m.Groups["path"].Value;
            // Skip pagination and non-person pages
            if (path.Contains("-2.html", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("latest", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("top50", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("compare", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = Root + path;
            if (found.ContainsKey(url)) continue;

            // Name is often encoded in the path: /s/Katy-Perry-2673.html
            var file = path.Split('/').LastOrDefault() ?? "";
            var stem = Regex.Replace(file, @"\.html$", "", RegexOptions.IgnoreCase);
            // Strip leading dash and trailing -digits id
            stem = Regex.Replace(stem, @"^-+", "");
            stem = Regex.Replace(stem, @"-\d+$", "");
            var pathName = stem.Replace('-', ' ').Trim();
            if (pathName.Length == 0) continue;

            // Prefer entries whose path name resembles the query
            if (!NameLooksLike(pathName, name)) continue;

            // Optional height preview from surrounding text (index often rounds cm)
            string? preview = null;
            var idx = m.Index;
            var window = html.Substring(idx, Math.Min(200, html.Length - idx));
            var cm = CmInText.Match(window);
            if (cm.Success) preview = cm.Value;

            found[url] = new CelebHeightsCandidate(pathName, url, preview);
        }

        // Exact (or near-exact) name first, then the rest
        return found.Values
            .OrderByDescending(c => NameScore(c.Name, name))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads the listed height from a CelebHeights profile page.
    /// For Katy Perry this is the h3 "5ft 7 ¼ (170.8 cm)" → 170.8.
    /// </summary>
    public async Task<CelebHeightsHeight?> GetHeightAsync(string profileUrl, CancellationToken cancel = default)
    {
        if (!Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("celebheights", StringComparison.OrdinalIgnoreCase))
            return null;

        string html;
        try
        {
            html = await _http.GetStringAsync(uri, cancel);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            // An HTTP timeout is a lookup miss; caller cancellation still propagates.
            return null;
        }

        string? display = null;
        double? cm = null;

        var h3 = HeightHeading.Match(html);
        if (h3.Success)
        {
            display = System.Net.WebUtility.HtmlDecode(h3.Groups["text"].Value).Trim();
            cm = ParseCm(display);
        }

        if (cm == null)
        {
            var near = HeightNearLabel.Match(html);
            if (near.Success &&
                double.TryParse(near.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
                v is >= 50 and <= 260)
            {
                cm = v;
                display ??= near.Value.Trim();
            }
        }

        if (cm is not double height) return null;

        // Name from <h1>How tall is X</h1> or path
        var name = "Unknown";
        var h1 = Regex.Match(html, @"<h1[^>]*>\s*How tall is\s+([^<]+?)\s*</h1>", RegexOptions.IgnoreCase);
        if (h1.Success)
            name = System.Net.WebUtility.HtmlDecode(h1.Groups[1].Value).Trim();
        else
        {
            var file = uri.AbsolutePath.Split('/').LastOrDefault() ?? "";
            var stem = Regex.Replace(file, @"\.html$", "", RegexOptions.IgnoreCase);
            stem = Regex.Replace(stem, @"^-+", "");
            stem = Regex.Replace(stem, @"-\d+$", "");
            if (stem.Length > 0) name = stem.Replace('-', ' ');
        }

        return new CelebHeightsHeight
        {
            Name = name,
            ProfileUrl = uri.AbsoluteUri,
            HeightCm = Math.Round(height, 1),
            DisplayText = display,
            RetrievedAt = DateTimeOffset.Now
        };
    }

    /// <summary>
    /// Search + open the best match. If several people share the name, returns null height and the candidate list
    /// so the UI can ask the user to pick (same pattern as Wikidata).
    /// </summary>
    public async Task<(CelebHeightsHeight? Height, List<CelebHeightsCandidate> Candidates)> LookupAsync(
        string name, CancellationToken cancel = default)
    {
        var candidates = await SearchAsync(name, cancel);
        if (candidates.Count == 0) return (null, candidates);

        // Single clear winner or exact name match → fetch height without a picker.
        var best = candidates[0];
        var exact = candidates.Where(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            best = exact[0];
        else if (candidates.Count > 1 && NameScore(best.Name, name) < 90)
            return (null, candidates); // ambiguous

        var height = await GetHeightAsync(best.ProfileUrl, cancel);
        return (height, candidates);
    }

    /// <summary>
    /// Applies CelebHeights height onto an existing person card. Prefers CelebHeights for height
    /// when present (that is the site's specialty); leaves all other fields untouched.
    /// </summary>
    public static void ApplyHeight(PersonFacts facts, CelebHeightsHeight height)
    {
        facts.HeightCm = height.HeightCm;
        facts.Sources["height"] = "CelebHeights";
        // Optional: keep profile URL for the credit link
        facts.Sources["heightUrl"] = height.ProfileUrl;
    }

    // ---------- helpers ----------

    private static string SanitizeIndexToken(string token)
    {
        // Index pages use lowercase ASCII letters only (O'Brian → obrian path uses OBrian in profile, but index is first name)
        var cleaned = Regex.Replace(token.ToLowerInvariant(), @"[^a-z]", "");
        return cleaned;
    }

    private static bool NameLooksLike(string candidate, string query)
    {
        var c = Normalize(candidate);
        var q = Normalize(query);
        if (c.Length == 0 || q.Length == 0) return false;
        if (c == q) return true;
        // All query tokens appear in candidate (order preserved roughly)
        var qParts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cParts = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (qParts.Length == 0) return false;
        // Require first name match and at least half the tokens
        if (cParts.Length == 0 || cParts[0] != qParts[0]) return false;
        var hits = qParts.Count(p => cParts.Contains(p));
        return hits >= Math.Max(1, (qParts.Length + 1) / 2);
    }

    private static int NameScore(string candidate, string query)
    {
        var c = Normalize(candidate);
        var q = Normalize(query);
        if (c == q) return 100;
        if (c.StartsWith(q) || q.StartsWith(c)) return 80;
        var qParts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cParts = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = qParts.Count(p => cParts.Contains(p));
        return hits * 100 / Math.Max(qParts.Length, 1);
    }

    private static string Normalize(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9\s]", " ").Replace("  ", " ").Trim();

    private static double? ParseCm(string text)
    {
        var m = CmInText.Match(text);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cm))
            return null;
        if (cm is < 50 or > 260) return null;
        return cm;
    }
}
