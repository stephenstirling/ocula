using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

/// <summary>
/// Bra size from public specialty pages. Prefer CelebrityBraSizes (evidence tiers),
/// then Boobpedia, then CelebrityInside. Only the name is sent; run when the user asks.
/// </summary>
public sealed class BraSizeResult
{
    public string Name { get; init; } = string.Empty;
    public string ProfileUrl { get; init; } = string.Empty;
    /// <summary>US-style label when present, e.g. "32D" or "34E".</summary>
    public string Size { get; init; } = string.Empty;
    /// <summary>Canonical normalized display size, e.g. 32D or D.</summary>
    public string SizeKey { get; init; } = string.Empty;
    /// <summary>Band size in inches when the source provides one.</summary>
    public int? BandInches { get; init; }
    /// <summary>Comparable cup rank, or -1 when the size cannot be normalized.</summary>
    public int CupRank { get; init; } = -1;
    /// <summary>Approximate volume index used for sorting, when available.</summary>
    public int? VolumeIndex { get; init; }
    /// <summary>Confirmed, Reported, Estimated, Conflicting, or empty.</summary>
    public string Evidence { get; init; } = string.Empty;
    /// <summary>CelebrityBraSizes, Boobpedia, or CelebrityInside.</summary>
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset RetrievedAt { get; init; }
}

public sealed class BraSizeLookup
{
    private const string CbsRoot = "https://celebritybrasizes.com";
    private const string BoobpediaRoot = "https://www.boobpedia.com";
    private const string CelebrityInsideRoot = "https://celebrityinside.com";

    private static readonly Regex FullSize = new(
        @"\b(\d{2})\s*(DDD|DD|AA|[A-K])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EvidencePattern = new(
        @"\b(Confirmed|Reported|Estimated|Conflicting|Historical statement|Third-Party Estimate|Not Publicly Confirmed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;

    public BraSizeLookup(HttpClient http) => _http = http;

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ocula/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        return client;
    }

    /// <summary>
    /// Tries sources in order until one returns a full band+cup size.
    /// </summary>
    public async Task<BraSizeResult?> LookupAsync(string name, CancellationToken cancel = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return null;

        var found = await TryCelebrityBraSizesAsync(name, cancel)
            ?? await TryBoobpediaAsync(name, cancel)
            ?? await TryCelebrityInsideAsync(name, cancel)
            ?? await TryBabepediaAsync(name, cancel);
        return found is null ? null : Finalize(found);
    }

    public static void Apply(PersonFacts facts, BraSizeResult result)
    {
        facts.BraSize = result.Size;
        facts.BraSizeEvidence = result.Evidence;
        facts.Sources["braSize"] = result.Source;
        facts.Sources["braSizeUrl"] = result.ProfileUrl;
    }

    // ---------- 1. CelebrityBraSizes ----------

    private async Task<BraSizeResult?> TryCelebrityBraSizesAsync(string name, CancellationToken cancel)
    {
        var slug = ToSlug(name, dash: true);
        if (slug.Length == 0) return null;

        var url = $"{CbsRoot}/{slug}-bra-size/";
        if (await GetHtmlAsync(url, cancel) is not { } html) return null;

        string? size = null;
        var table = Regex.Match(html,
            @"Reported\s+bra\s+size\s*[|\u2014\-:]+\s*([0-9]{2}\s*(?:DDD|DD|AA|[A-K]))",
            RegexOptions.IgnoreCase);
        if (table.Success) size = NormalizeSize(table.Groups[1].Value);

        if (size == null)
        {
            var headline = Regex.Match(html,
                @"(?:Reported|Confirmed|Estimated)\s+(\d{2}\s*(?:DDD|DD|AA|[A-K]))",
                RegexOptions.IgnoreCase);
            if (headline.Success) size = NormalizeSize(headline.Groups[1].Value);
        }

        if (size == null)
        {
            var title = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
            if (title.Success)
            {
                var m = FullSize.Match(System.Net.WebUtility.HtmlDecode(title.Groups[1].Value));
                if (m.Success) size = NormalizeSize(m.Groups[1].Value + m.Groups[2].Value);
            }
        }

        if (size == null) return null;

        var evidence = "Reported";
        var ev = EvidencePattern.Match(html);
        if (ev.Success)
        {
            var raw = ev.Groups[1].Value;
            if (raw.Contains("Confirmed", StringComparison.OrdinalIgnoreCase) &&
                !raw.Contains("Not", StringComparison.OrdinalIgnoreCase))
                evidence = "Confirmed";
            else if (raw.Contains("Estimated", StringComparison.OrdinalIgnoreCase))
                evidence = "Estimated";
            else if (raw.Contains("Conflicting", StringComparison.OrdinalIgnoreCase))
                evidence = "Conflicting";
        }

        return new BraSizeResult
        {
            Name = name,
            ProfileUrl = url,
            Size = size,
            Evidence = evidence,
            Source = "CelebrityBraSizes",
            RetrievedAt = DateTimeOffset.Now
        };
    }

    // ---------- 2. Boobpedia ----------

    private async Task<BraSizeResult?> TryBoobpediaAsync(string name, CancellationToken cancel)
    {
        // https://www.boobpedia.com/boobs/Jessica_Simpson
        var page = name.Replace(' ', '_');
        page = Regex.Replace(page, @"[^\w\-']", "");
        if (page.Length == 0) return null;

        var url = $"{BoobpediaRoot}/boobs/{Uri.EscapeDataString(page).Replace("%20", "_")}";
        // EscapeDataString encodes underscore wrongly for path; build manually
        url = $"{BoobpediaRoot}/boobs/{page}";

        if (await GetHtmlAsync(url, cancel) is not { } html) return null;

        // "Bra/cup size  34E (75E)" in infobox
        var m = Regex.Match(html,
            @"Bra\s*/?\s*cup\s*size[^<]{0,80}?(\d{2})\s*(DDD|DD|E{1,3}|F{1,3}|G{1,3}|AA|[A-K])",
            RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            m = Regex.Match(html,
                @"bra[/\s-]*cup[/\s-]*size[^0-9]{0,40}(\d{2})\s*(DDD|DD|E{1,3}|F{1,3}|G{1,3}|AA|[A-K])",
                RegexOptions.IgnoreCase);
        }
        if (!m.Success) return null;

        var size = NormalizeSize(m.Groups[1].Value + m.Groups[2].Value);
        return new BraSizeResult
        {
            Name = name,
            ProfileUrl = url,
            Size = size,
            Evidence = "Reported",
            Source = "Boobpedia",
            RetrievedAt = DateTimeOffset.Now
        };
    }

    // ---------- 3. CelebrityInside ----------

    private async Task<BraSizeResult?> TryCelebrityInsideAsync(string name, CancellationToken cancel)
    {
        // Common pattern: .../jessica-simpson-body-measurements-height-weight-bra-size-stats-bio/
        var slug = ToSlug(name, dash: true);
        if (slug.Length == 0) return null;

        var candidates = new[]
        {
            $"{CelebrityInsideRoot}/body-measurements/musicians/{slug}-body-measurements-height-weight-bra-size-stats-bio/",
            $"{CelebrityInsideRoot}/body-measurements/actress/{slug}-height-weight-bra-size-stats-facts/",
            $"{CelebrityInsideRoot}/body-measurements/actress/{slug}-body-measurements-height-weight-bra-size-stats-bio/",
            $"{CelebrityInsideRoot}/body-measurements/musicians/{slug}-bra-size-weight-height-shoe-vital-statistics/",
        };

        foreach (var url in candidates)
        {
            if (await GetHtmlAsync(url, cancel) is not { } html) continue;

            // Prefer full "Bra size: 34DD" over cup-only
            var full = Regex.Match(html,
                @"Bra\s*size\s*[:\-]?\s*(\d{2}\s*(?:DDD|DD|AA|[A-K]))",
                RegexOptions.IgnoreCase);
            if (full.Success)
            {
                return new BraSizeResult
                {
                    Name = name,
                    ProfileUrl = url,
                    Size = NormalizeSize(full.Groups[1].Value),
                    Evidence = "Reported",
                    Source = "CelebrityInside",
                    RetrievedAt = DateTimeOffset.Now
                };
            }

            // Cup only is weaker; still usable if nothing else
            var cup = Regex.Match(html,
                @"Cup\s*size\s*[:\-]?\s*(DDD|DD|AA|[A-K])\b",
                RegexOptions.IgnoreCase);
            if (cup.Success)
            {
                return new BraSizeResult
                {
                    Name = name,
                    ProfileUrl = url,
                    Size = cup.Groups[1].Value.ToUpperInvariant(),
                    Evidence = "Reported",
                    Source = "CelebrityInside",
                    RetrievedAt = DateTimeOffset.Now
                };
            }
        }

        return null;
    }


    // ---------- 4. Babepedia ----------

    private async Task<BraSizeResult?> TryBabepediaAsync(string name, CancellationToken cancel)
    {
        // https://www.babepedia.com/babe/Haylie_Duff
        var page = name.Trim().Replace(' ', '_');
        page = System.Text.RegularExpressions.Regex.Replace(page, @"[^\w\-']", "");
        if (page.Length == 0) return null;

        var url = $"https://www.babepedia.com/babe/{page}";
        if (await GetHtmlAsync(url, cancel) is not { } html) return null;

        // Cloudflare challenge page → treat as miss
        if (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase))
            return null;

        // Prefer full band+cup
        var full = System.Text.RegularExpressions.Regex.Match(html,
            @"Bra\s*/?\s*cup\s*size\s*[:\-]?\s*(\d{2})\s*(DDD|DD|AA|[A-K])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (full.Success)
        {
            return new BraSizeResult
            {
                Name = name,
                ProfileUrl = url,
                Size = NormalizeSize(full.Groups[1].Value + full.Groups[2].Value),
                Evidence = "Reported",
                Source = "Babepedia",
                RetrievedAt = DateTimeOffset.Now
            };
        }

        // Cup only as last resort
        var cup = System.Text.RegularExpressions.Regex.Match(html,
            @"Bra\s*/?\s*cup\s*size\s*[:\-]?\s*(DDD|DD|AA|[A-K])\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!cup.Success) return null;

        return new BraSizeResult
        {
            Name = name,
            ProfileUrl = url,
            Size = cup.Groups[1].Value.ToUpperInvariant(),
            Evidence = "Reported",
            Source = "Babepedia",
            RetrievedAt = DateTimeOffset.Now
        };
    }


    private static BraSizeResult Finalize(BraSizeResult r)
    {
        var n = BraSizeNormalizer.Parse(r.Size);
        return new BraSizeResult
        {
            Name = r.Name,
            ProfileUrl = r.ProfileUrl,
            Size = n.Ok ? n.SizeKey : r.Size,
            SizeKey = n.SizeKey,
            BandInches = n.BandInches,
            CupRank = n.CupRank,
            VolumeIndex = n.VolumeIndex,
            Evidence = r.Evidence,
            Source = r.Source,
            RetrievedAt = r.RetrievedAt
        };
    }

    // ---------- helpers ----------

    private async Task<string?> GetHtmlAsync(string url, CancellationToken cancel)
    {
        try
        {
            using var resp = await _http.GetAsync(url, cancel);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(cancel);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            // Per-request timeout is a lookup miss; caller cancellation must propagate.
            return null;
        }
    }

    private static string ToSlug(string name, bool dash)
    {
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"\s+", dash ? "-" : "_").Trim('-', '_');
        return s;
    }

    private static string NormalizeSize(string raw)
    {
        var s = Regex.Replace(raw.ToUpperInvariant(), @"\s+", "");
        var m = Regex.Match(s, @"^(\d{2})(DDD|DD|E{1,3}|F{1,3}|G{1,3}|AA|[A-K])$");
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : s;
    }
}
