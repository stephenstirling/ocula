using System.Globalization;
using Ocula.Library;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// The Phun Forum Scraper tab's reader: it turns the scraper's scraper_status.json (written by tools/phun-scraper/phun_scraper.py) into what the page shows.
/// The JSON below has the keys and the UTC "...Z" timestamps the Python status class writes.
/// </summary>
public class ScraperMonitorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ocula-phun-test-" + Guid.NewGuid().ToString("N"));

    public ScraperMonitorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private void WriteStatus(string state = "scanning", DateTime? updated = null, string extra = "")
    {
        var json = $$"""
            {
              "schema_version": 1, "state": "{{state}}", "updated_at": "{{Iso(updated ?? DateTime.UtcNow)}}",
              "started_at": "{{Iso(DateTime.UtcNow.AddHours(-3))}}", "scan_number": 4,
              "last_scan_finished": "{{Iso(DateTime.UtcNow.AddHours(-1))}}", "next_scan_at": "{{Iso(DateTime.UtcNow.AddHours(3))}}",
              "threads_total": 40, "threads_done": 12, "thread_title": "A thread title", "thread_url": "https://forum.example.test/t/1",
              "thread_links_total": 23, "thread_links_done": 9,
              "images_saved": 128, "upgrades": 9, "duplicates_skipped": 41, "failed_downloads": 2, "threads_skipped": 5, "metadata_failed": 3,
              "last_error": null{{extra}},
              "events": [ {"time": "{{Iso(DateTime.UtcNow)}}", "level": "INFO", "message": "Saved to Root: a.jpg"} ]
            }
            """;
        File.WriteAllText(Path.Combine(_folder, "scraper_status.json"), json);
    }

    [Fact]
    public async Task A_fresh_status_file_is_read_with_every_counter()
    {
        WriteStatus();
        using var monitor = new ScraperMonitor(_folder);

        var s = await monitor.ReadOnceAsync();

        Assert.False(s.Offline);
        Assert.Equal((128, 9, 41, 2, 5), (s.ImagesSaved, s.Upgrades, s.DuplicatesSkipped, s.FailedDownloads, s.ThreadsSkipped));
        Assert.Equal((40, 12, 23, 9), (s.ThreadsTotal, s.ThreadsDone, s.ThreadLinksTotal, s.ThreadLinksDone));
        Assert.Equal(3, s.MetadataFailed);
        Assert.Equal("A thread title", s.ThreadTitle);
        Assert.Equal("Saved to Root: a.jpg", Assert.Single(s.Events).Message);
        Assert.Equal(DateTimeKind.Utc, s.UpdatedAt.Kind);
    }

    [Fact]
    public async Task A_status_file_not_updated_for_ninety_seconds_means_the_scraper_is_offline()
    {
        WriteStatus(updated: DateTime.UtcNow.AddSeconds(-120));
        using var monitor = new ScraperMonitor(_folder);

        Assert.True((await monitor.ReadOnceAsync()).Offline);

        WriteStatus(updated: DateTime.UtcNow.AddSeconds(-60));
        Assert.False((await monitor.ReadOnceAsync()).Offline);
    }

    [Fact]
    public async Task A_missing_unreadable_or_odd_status_file_means_offline_and_never_throws()
    {
        using var monitor = new ScraperMonitor(_folder);
        Assert.True((await monitor.ReadOnceAsync()).Offline);                      // no file

        File.WriteAllText(Path.Combine(_folder, "scraper_status.json"), "{ not json");
        var s = await Record.ExceptionAsync(async () => await monitor.ReadOnceAsync());
        Assert.NotNull(s);                                                         // a half-written file is a JSON error the poller must survive
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(s);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_means_offline()
    {
        using var monitor = new ScraperMonitor(Path.Combine(_folder, "nope"));

        Assert.True((await monitor.ReadOnceAsync()).Offline);
    }

    [Fact]
    public async Task Fields_the_scraper_adds_later_are_ignored()
    {
        WriteStatus(extra: ", \"something_new\": {\"a\": 1}");
        using var monitor = new ScraperMonitor(_folder);

        Assert.Equal(128, (await monitor.ReadOnceAsync()).ImagesSaved);
    }

    [Fact]
    public void The_summary_and_progress_follow_the_state()
    {
        var scanning = new ScraperStatus { State = "scanning", ThreadsTotal = 40, ThreadsDone = 12, ThreadLinksTotal = 20, ThreadLinksDone = 5 };
        Assert.Equal("Scanning: thread 13 of 40", scanning.Summary);
        Assert.Equal(30.0, scanning.ThreadProgress);
        Assert.Equal(25.0, scanning.LinkProgress);

        Assert.Equal("Idle", new ScraperStatus { State = "sleeping" }.Summary);
        Assert.StartsWith("Idle — next scan", new ScraperStatus { State = "sleeping", NextScanAt = DateTime.UtcNow.AddHours(2) }.Summary);
        Assert.Equal("Scraper offline", new ScraperStatus { State = "scanning", Offline = true }.Summary);
        Assert.Equal(0, new ScraperStatus().ThreadProgress);   // nothing to divide by
        Assert.Equal(0, new ScraperStatus().LinkProgress);
    }

    [Fact]
    public async Task Scan_now_needs_an_address()
    {
        using var monitor = new ScraperMonitor(_folder);

        Assert.False(await monitor.ScanNowAsync());
    }

    [Fact]
    public async Task Polling_reports_status_and_stops_when_stopped()
    {
        WriteStatus();
        using var monitor = new ScraperMonitor(_folder);
        var reports = 0;
        monitor.StatusChanged += _ => Interlocked.Increment(ref reports);

        monitor.Start(TimeSpan.FromMilliseconds(50));
        await Task.Delay(500);
        monitor.Stop();
        await Task.Delay(150);                                // a read that was already under way finishes without reporting
        var atStop = Volatile.Read(ref reports);
        await Task.Delay(300);

        Assert.True(atStop >= 2, $"only {atStop} reports");
        Assert.Equal(atStop, Volatile.Read(ref reports));   // none after Stop
    }
}
