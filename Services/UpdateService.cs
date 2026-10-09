using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel;
using Windows.Services.Store;
using Windows.System;
using AppInstance = Microsoft.Windows.AppLifecycle.AppInstance;

namespace Ocula.Services;

/// <summary>How this copy of Ocula was installed, which decides how it updates.</summary>
public enum UpdateChannel
{
    Store,        // Microsoft Store: updates come from the Store
    AppInstaller, // installed from GitHub with Ocula.appinstaller: Windows updates it
    Sideloaded,   // installed from a package file by hand: download the new release
    Development   // running from Visual Studio
}

public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string Url, DateTimeOffset Published);

public sealed record UpdateStatus(bool Available, ReleaseInfo? Release, string Message);

/// <summary>
/// Checks for and installs updates, using GitHub releases for version information and
/// release notes. The update itself always comes through the channel Ocula was installed
/// from, which is what Windows (and Microsoft Store policy) expect:
/// - Store installs: StoreContext downloads and installs the update.
/// - App Installer installs: Windows applies the update when Ocula restarts.
/// - Other installs: the GitHub release page opens for a manual download.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = CreateClient();

    public static UpdateChannel Channel { get; } = DetectChannel();

    public static Version CurrentVersion
    {
        get
        {
            try
            {
                var v = Package.Current.Id.Version;
                return new Version(v.Major, v.Minor, v.Build);
            }
            catch
            {
                return new Version(0, 0, 0);
            }
        }
    }

    public static string ChannelDescription => Channel switch
    {
        UpdateChannel.Store => "installed from the Microsoft Store",
        UpdateChannel.AppInstaller => "installed from GitHub, updates automatically",
        UpdateChannel.Sideloaded => "installed from a package file",
        _ => "development build"
    };

    public static bool IsConfigured => AppInfo.GitHubRepository.Length > 0;

    private static UpdateChannel DetectChannel()
    {
        try
        {
            var package = Package.Current;
            if (package.SignatureKind == PackageSignatureKind.Store) return UpdateChannel.Store;
            if (package.GetAppInstallerInfo()?.Uri != null) return UpdateChannel.AppInstaller;
            return package.SignatureKind == PackageSignatureKind.Developer ? UpdateChannel.Development : UpdateChannel.Sideloaded;
        }
        catch
        {
            return UpdateChannel.Development; // no package identity
        }
    }

    // ================= Checking =================

    public static async Task<UpdateStatus> CheckAsync(IntPtr window)
    {
        try
        {
            switch (Channel)
            {
                case UpdateChannel.Store:
                {
                    var store = StoreContext.GetDefault();
                    WinRT.Interop.InitializeWithWindow.Initialize(store, window);
                    var updates = await store.GetAppAndOptionalStorePackageUpdatesAsync();
                    var latest = await GetLatestReleaseAsync();
                    return updates.Count > 0
                        ? new UpdateStatus(true, latest, latest != null ? $"Ocula {latest.Version} is ready to install." : "An update is ready to install.")
                        : new UpdateStatus(false, latest, "Ocula is up to date.");
                }

                case UpdateChannel.AppInstaller:
                {
                    var result = await Package.Current.CheckUpdateAvailabilityAsync();
                    var latest = await GetLatestReleaseAsync();
                    var available = result.Availability is PackageUpdateAvailability.Available or PackageUpdateAvailability.Required;
                    return available
                        ? new UpdateStatus(true, latest, $"Ocula {latest?.Version.ToString() ?? "update"} is ready. Restart Ocula to install it.")
                        : new UpdateStatus(false, latest, "Ocula is up to date.");
                }

                default:
                {
                    var latest = await GetLatestReleaseAsync();
                    if (latest == null) return new UpdateStatus(false, null, "Couldn't reach GitHub to check for updates.");
                    return latest.Version > CurrentVersion
                        ? new UpdateStatus(true, latest, $"Ocula {latest.Version} is available on GitHub.")
                        : new UpdateStatus(false, latest, "Ocula is up to date.");
                }
            }
        }
        catch (Exception ex)
        {
            return new UpdateStatus(false, null, "Couldn't check for updates: " + ex.Message);
        }
        finally
        {
            AppSettings.LastUpdateCheck = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Installs the update the way this copy was installed. Returns a message if it couldn't.</summary>
    public static async Task<string?> InstallAsync(IntPtr window, ReleaseInfo? release)
    {
        switch (Channel)
        {
            case UpdateChannel.Store:
            {
                var store = StoreContext.GetDefault();
                WinRT.Interop.InitializeWithWindow.Initialize(store, window);
                var updates = await store.GetAppAndOptionalStorePackageUpdatesAsync();
                if (updates.Count == 0) return "There's no update to install.";
                var result = await store.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
                return result.OverallState == StorePackageUpdateState.Completed ? null : "The update didn't finish. Try again from the Microsoft Store.";
            }

            case UpdateChannel.AppInstaller:
                // Windows checks Ocula.appinstaller every time Ocula starts and applies the update then.
                var failure = AppInstance.Restart("--after-update");
                return $"Ocula couldn't restart ({failure}). Close and reopen it to finish updating.";

            default:
                var url = release?.Url ?? $"https://github.com/{AppInfo.GitHubRepository}/releases/latest";
                await Launcher.LaunchUriAsync(new Uri(url));
                return null;
        }
    }

    // ================= GitHub releases =================

    public static Task<ReleaseInfo?> GetLatestReleaseAsync() =>
        FetchReleaseAsync($"https://api.github.com/repos/{AppInfo.GitHubRepository}/releases/latest");

    public static Task<ReleaseInfo?> GetReleaseAsync(Version version) =>
        FetchReleaseAsync($"https://api.github.com/repos/{AppInfo.GitHubRepository}/releases/tags/v{version}");

    private static async Task<ReleaseInfo?> FetchReleaseAsync(string url)
    {
        if (!IsConfigured) return null;
        try
        {
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            return new ReleaseInfo(
                Version: new Version(version.Major, version.Minor, Math.Max(0, version.Build)),
                Tag: tag,
                Name: root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name ? name : $"Ocula {tag}",
                Notes: PlainText(root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty),
                Url: root.GetProperty("html_url").GetString() ?? string.Empty,
                Published: root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var when) ? when : DateTimeOffset.UtcNow);
        }
        catch
        {
            return null; // offline, rate-limited, or the repository isn't public yet
        }
    }

    /// <summary>Release notes are Markdown; show them as tidy plain text.</summary>
    private static string PlainText(string markdown)
    {
        var lines = markdown.Replace("\r", string.Empty).Split('\n').Select(line =>
        {
            line = Regex.Replace(line, @"^#{1,6}\s*", string.Empty);       // headings
            line = Regex.Replace(line, @"^\s*[-*+]\s+", "• ");             // bullets
            line = Regex.Replace(line, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2"); // bold
            line = Regex.Replace(line, @"\[(.+?)\]\((.+?)\)", "$1");        // links
            return line.Replace("`", string.Empty);
        });
        return Regex.Replace(string.Join('\n', lines).Trim(), @"\n{3,}", "\n\n");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Ocula/{CurrentVersion}");   // required by GitHub's API
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
