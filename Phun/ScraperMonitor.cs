// Ocula side of the scraper monitor.  No extra NuGet packages needed (System.Text.Json + HttpClient).
//
// Two ways to see the container:
//   1. HTTP   http://<nas-ip>:8765/status   (live; also lets Ocula press "Scan now")
//   2. File   <downloads>\scraper_status.json (works even if the port isn't reachable)
// The monitor tries HTTP first and falls back to the file. If the data is older than 90 s,
// the container is treated as offline.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ocula.Library;

public sealed class ScraperEvent
{
    [JsonPropertyName("time")]    public string Time { get; set; } = "";
    [JsonPropertyName("level")]   public string Level { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class ScraperStatus
{
    [JsonPropertyName("state")]              public string State { get; set; } = "unknown";
    [JsonPropertyName("updated_at")]         public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("started_at")]         public DateTime? StartedAt { get; set; }
    [JsonPropertyName("scan_number")]        public int ScanNumber { get; set; }
    [JsonPropertyName("scan_started_at")]    public DateTime? ScanStartedAt { get; set; }
    [JsonPropertyName("last_scan_finished")] public DateTime? LastScanFinished { get; set; }
    [JsonPropertyName("next_scan_at")]       public DateTime? NextScanAt { get; set; }
    [JsonPropertyName("threads_total")]      public int ThreadsTotal { get; set; }
    [JsonPropertyName("threads_done")]       public int ThreadsDone { get; set; }
    [JsonPropertyName("thread_title")]       public string? ThreadTitle { get; set; }
    [JsonPropertyName("thread_links_total")] public int ThreadLinksTotal { get; set; }
    [JsonPropertyName("thread_links_done")]  public int ThreadLinksDone { get; set; }
    [JsonPropertyName("images_saved")]       public int ImagesSaved { get; set; }
    [JsonPropertyName("upgrades")]           public int Upgrades { get; set; }
    [JsonPropertyName("duplicates_skipped")] public int DuplicatesSkipped { get; set; }
    [JsonPropertyName("failed_downloads")]   public int FailedDownloads { get; set; }
    [JsonPropertyName("threads_skipped")]    public int ThreadsSkipped { get; set; }
    [JsonPropertyName("last_error")]         public string? LastError { get; set; }
    [JsonPropertyName("events")]             public List<ScraperEvent> Events { get; set; } = new();

    // Derived, for binding
    [JsonIgnore] public bool Offline { get; set; }
    [JsonIgnore] public double ThreadProgress => ThreadsTotal == 0 ? 0 : 100.0 * ThreadsDone / ThreadsTotal;
    [JsonIgnore] public double LinkProgress   => ThreadLinksTotal == 0 ? 0 : 100.0 * ThreadLinksDone / ThreadLinksTotal;
    [JsonIgnore] public string Summary =>
        Offline ? "Scraper offline"
        : State == "scanning" ? $"Scanning: thread {ThreadsDone + 1} of {ThreadsTotal}"
        : State == "sleeping" ? (NextScanAt is { } n ? $"Idle — next scan {n.ToLocalTime():t}" : "Idle")
        : State;
}

public sealed class ScraperMonitor : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly string? _baseUrl;       // e.g. "http://192.168.86.62:8765"
    private readonly string _statusFile;     // e.g. @"Z:\...\scraper_status.json"
    private readonly string? _token;
    private CancellationTokenSource? _cts;

    /// Raised on the polling thread; marshal to the UI thread (DispatcherQueue) before touching controls.
    public event Action<ScraperStatus>? StatusChanged;

    public ScraperMonitor(string downloadsFolder, string? baseUrl = null, string? token = null)
    {
        _statusFile = Path.Combine(downloadsFolder, "scraper_status.json");
        _baseUrl = baseUrl?.TrimEnd('/');
        _token = token;
    }

    public void Start(TimeSpan? interval = null)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var every = interval ?? TimeSpan.FromSeconds(2);
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(every);
            do { StatusChanged?.Invoke(await ReadOnceAsync(_cts.Token)); }
            while (await timer.WaitForNextTickAsync(_cts.Token));
        }, _cts.Token);
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    public async Task<ScraperStatus> ReadOnceAsync(CancellationToken ct = default)
    {
        string? json = null;
        if (_baseUrl is not null)
        {
            try { json = await _http.GetStringAsync($"{_baseUrl}/status", ct); } catch { /* fall back to file */ }
        }
        if (json is null)
        {
            try
            {
                using var fs = new FileStream(_statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                json = await sr.ReadToEndAsync(ct);
            }
            catch { /* missing or mid-write */ }
        }

        var status = json is null ? null : JsonSerializer.Deserialize<ScraperStatus>(json, Json);
        status ??= new ScraperStatus();
        status.Offline = json is null || DateTime.UtcNow - status.UpdatedAt.ToUniversalTime() > StaleAfter;
        return status;
    }

    /// Wakes the container early instead of waiting for the 4-hour timer. Needs the HTTP address.
    public async Task<bool> ScanNowAsync()
    {
        if (_baseUrl is null) return false;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/scan-now");
        if (!string.IsNullOrEmpty(_token)) req.Headers.Add("X-Token", _token);
        try { return (await _http.SendAsync(req)).IsSuccessStatusCode; } catch { return false; }
    }

    public void Dispose() { Stop(); _http.Dispose(); }
}
