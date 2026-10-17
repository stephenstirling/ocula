using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Ocula.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Ocula.Tests;

/// <summary>
/// Runs the real <see cref="PageScanner.GrabScript"/> on a saved page in headless Microsoft Edge, the same engine Ocula's
/// browser pane uses, and parses its answer with <see cref="PageScanner.Parse"/>. The script is JavaScript that needs a
/// real DOM, so this is the only honest way to test it.
///
/// Everything stays on this PC: Edge is told that every web address is this machine, and a small server here answers. The page
/// is served as http://forum.test/..., so its own address and its relative links behave as they would on a real site, and every
/// picture the page mentions is served as a real PNG of the size its width and height attributes give. That matters: a picture
/// that fails to load changes what the script reads (in Chrome a failed picture with alt text reports the size of its alt text,
/// not its attributes), so the pictures here always load, and the result doesn't depend on timing.
///
/// The fixtures use https:// addresses for other sites; the server speaks plain http, so they are rewritten to http:// for the
/// page and back again in the script's answer.
/// </summary>
internal static class BrowserGrab
{
    private static readonly string[] EdgePaths =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    };

    public static string? Edge => EdgePaths.FirstOrDefault(File.Exists);

    /// <summary>The address the page is served from, so tests can write the addresses they expect for its own links.</summary>
    public const string Origin = "http://forum.test";

    public sealed record Result(PageSnapshot Page, string Origin);

    /// <param name="fixture">A file in TestData/forums.</param>
    /// <param name="path">The address path (and query) the page is served at; it becomes the page's own URL.</param>
    /// <param name="picturesFail">Every picture answers 404 and the script runs once the page has finished loading, so every picture
    /// has already failed: what the script sees on a page whose pictures can't be loaded (for example a host that blocks hotlinking).</param>
    public static async Task<Result> RunAsync(string fixture, string path, bool picturesFail = false)
    {
        var edge = Edge ?? throw new InvalidOperationException("Microsoft Edge isn't installed.");
        var html = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "forums", fixture)))
            .Replace("https://", "http://");
        var pageUri = new Uri(Origin + path);
        var pictures = picturesFail ? new Dictionary<string, (int W, int H)>() : PictureSizes(html, pageUri);

        // Runs after the page has been parsed, so the whole page is there (or, when pictures fail, after it has loaded and they
        // have failed); the answer comes back as an attribute of <html>.
        var run = "document.documentElement.setAttribute('data-ocula-grab', " + PageScanner.GrabScript + ");";
        var served = html + "\n<script>" + (picturesFail ? "window.addEventListener('load', () => setTimeout(() => { " + run + " }, 0));" : run) + "</script>\n";
        var pageBytes = Encoding.UTF8.GetBytes(served);

        using var stop = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await server.AcceptTcpClientAsync(stop.Token); }
                catch { break; }
                _ = Task.Run(() => Answer(client, pageUri, pageBytes, pictures, stop.Token));
            }
        });

        var profile = Path.Combine(Path.GetTempPath(), "ocula-edge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new ProcessStartInfo(edge)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[]
            {
                "--headless", "--disable-gpu", "--no-first-run", "--disable-extensions", "--disable-sync",
                "--disable-background-networking", "--disable-component-update", "--disable-domain-reliability",
                $"--user-data-dir={profile}",
                // Only the sites the page mentions are this PC's server; any other address (Edge's own background requests) fails at once.
                "--host-resolver-rules=" + string.Join(", ", Hosts(html, pageUri).Select(h => $"MAP {h} 127.0.0.1:{port}").Append("MAP * ~NOTFOUND")),
                "--dump-dom", pageUri.AbsoluteUri,
            }) start.ArgumentList.Add(arg);

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                KillTree(process);
                throw new TimeoutException("Edge didn't finish dumping the page in 60 seconds.");
            }

            var dom = await output;
            var match = Regex.Match(dom, "data-ocula-grab=\"([^\"]*)\"");
            if (!match.Success)
                throw new InvalidOperationException("The grab script left no answer. Page started: " + dom[..Math.Min(300, dom.Length)]);

            // Back to the https:// addresses the fixture wrote, for every site but the page's own.
            var json = Regex.Replace(WebUtility.HtmlDecode(match.Groups[1].Value), "http://(?!forum\\.test)", "https://");
            return new Result(PageScanner.Parse(json), Origin);
        }
        finally
        {
            stop.Cancel();
            server.Stop();
            await serving;
            KillStragglers(profile);
            try { Directory.Delete(profile, recursive: true); } catch { /* Edge may still be letting go of it */ }
        }
    }

    // ---------- The little server ----------

    private static async Task Answer(TcpClient client, Uri page, byte[] pageBytes, Dictionary<string, (int W, int H)> pictures, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n") && head.Length < 16_384 && await stream.ReadAsync(buffer, stop) == 1)
                    head.Append((char)buffer[0]);

                var lines = head.ToString().Split("\r\n");
                var requestTarget = lines[0].Split(' ').ElementAtOrDefault(1) ?? "/";
                var host = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                                .Select(p => p[1].Trim().Split(':')[0]).FirstOrDefault() ?? page.Host;
                var url = new Uri($"http://{host}{requestTarget}");

                byte[] body; string type; var status = "200 OK";
                if (url.Host == page.Host && url.PathAndQuery == page.PathAndQuery)
                {
                    body = pageBytes; type = "text/html; charset=utf-8";
                }
                else if (pictures.TryGetValue(url.Host + url.PathAndQuery, out var size))
                {
                    body = Png(size.W, size.H); type = "image/png";
                }
                else
                {
                    body = Array.Empty<byte>(); type = "text/plain"; status = "404 Not Found";
                }

                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stop);
                await stream.WriteAsync(body, stop);
            }
            catch { /* the browser went away */ }
        }
    }

    /// <summary>The page's own host and every host its addresses mention.</summary>
    private static IEnumerable<string> Hosts(string html, Uri page) =>
        Regex.Matches(html, "http://([a-z0-9][a-z0-9.-]*)", RegexOptions.IgnoreCase)
             .Select(m => m.Groups[1].Value.ToLowerInvariant()).Append(page.Host).Distinct();

    /// <summary>The size of every picture the page mentions, from its width and height attributes, keyed by host and path.</summary>
    private static Dictionary<string, (int W, int H)> PictureSizes(string html, Uri page)
    {
        var sizes = new Dictionary<string, (int, int)>();
        foreach (Match tag in Regex.Matches(html, "<img\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            string? Attr(string name)
            {
                var m = Regex.Match(tag.Value, "\\b" + name + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value : null;
            }
            var src = Attr("src");
            if (src == null || !Uri.TryCreate(page, src, out var url)) continue;
            _ = int.TryParse(Attr("width"), out var w);
            _ = int.TryParse(Attr("height"), out var h);
            if (w > 0 && h > 0) sizes[url.Host + url.PathAndQuery] = (w, h);
        }
        return sizes;
    }

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(128, 128, 128, 255));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    // ---------- Tidying up ----------

    private static void KillTree(Process process)
    {
        try { process.Kill(true); } catch { /* already gone */ }
    }

    /// <summary>Edge starts helper processes that can outlive the one we started; stop the ones that use this run's profile folder.</summary>
    private static void KillStragglers(string profile)
    {
        foreach (var process in Process.GetProcessesByName("msedge"))
        {
            try
            {
                using (process)
                {
                    // The command line isn't available from Process, so match on the profile folder through the main module's session instead.
                    if (StartedWithProfile(process.Id, profile)) process.Kill(true);
                }
            }
            catch { /* not ours, or already gone */ }
        }
    }

    private static bool StartedWithProfile(int processId, string profile)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (System.Management.ManagementBaseObject o in searcher.Get())
                if (o["CommandLine"] is string line && line.Contains(profile, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* can't tell: leave it */ }
        return false;
    }
}
