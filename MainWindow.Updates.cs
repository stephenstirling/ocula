using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ocula.Services;

namespace Ocula;

/// <summary>Update checks, the "update available" bar, and "What's new" after an update.</summary>
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _updateTimer;
    private UpdateStatus? _update;

    private void InitialiseUpdates()
    {
        _updateTimer = DispatcherQueue.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromSeconds(15); // after start-up has settled
        _updateTimer.IsRepeating = false;
        _updateTimer.Tick += async (_, _) =>
        {
            await ShowWhatsNewIfUpdatedAsync();
            if (AppSettings.CheckForUpdates && UpdateService.IsConfigured &&
                DateTimeOffset.UtcNow - AppSettings.LastUpdateCheck > TimeSpan.FromHours(20))
            {
                ShowUpdate(await UpdateService.CheckAsync(WindowHandle));
            }
        };
        _updateTimer.Start();
    }

    private IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>Shows the update bar when there's something to install (also used by Settings).</summary>
    public void ShowUpdate(UpdateStatus status)
    {
        _update = status;
        if (!status.Available) return;
        UpdateBar.Title = status.Release != null ? $"Ocula {status.Release.Version} is available" : "An update is available";
        UpdateBar.Message = UpdateService.Channel switch
        {
            UpdateChannel.Store => "It installs from the Microsoft Store in a moment.",
            UpdateChannel.AppInstaller => "Ocula restarts to install it; you'll be back where you were in a few seconds.",
            _ => "Download it from GitHub."
        };
        UpdateNowButton.Content = UpdateService.Channel switch
        {
            UpdateChannel.AppInstaller => "Restart and update",
            UpdateChannel.Store => "Update",
            _ => "Open download page"
        };
        UpdateBar.IsOpen = true;
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync();

    public async Task InstallUpdateAsync()
    {
        UpdateNowButton.IsEnabled = false;
        var problem = await UpdateService.InstallAsync(WindowHandle, _update?.Release);
        UpdateNowButton.IsEnabled = true;
        if (problem != null) ShowError("Update not installed", problem);
        else UpdateBar.IsOpen = false;
    }

    private async void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        var release = _update?.Release ?? await UpdateService.GetLatestReleaseAsync();
        if (release != null) await ShowReleaseNotesAsync(release, $"What's new in Ocula {release.Version}");
    }

    /// <summary>The first time a new version runs, show its release notes.</summary>
    private async Task ShowWhatsNewIfUpdatedAsync()
    {
        var current = UpdateService.CurrentVersion.ToString();
        var previous = AppSettings.LastRunVersion;
        AppSettings.LastRunVersion = current;
        if (previous == null || previous == current || !UpdateService.IsConfigured) return; // first run, or nothing new

        var release = await UpdateService.GetReleaseAsync(UpdateService.CurrentVersion);
        if (release != null && release.Notes.Length > 0)
            await ShowReleaseNotesAsync(release, $"Ocula has been updated to {release.Version}");
    }

    public async Task ShowReleaseNotesAsync(ReleaseInfo release, string title)
    {
        var notes = new ScrollViewer
        {
            MaxHeight = 420,
            Content = new TextBlock { Text = release.Notes.Length > 0 ? release.Notes : "No notes for this release.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Width = 460 }
        };
        var dialog = Dialog(title, notes, "View on GitHub");
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && release.Url.Length > 0)
            await Windows.System.Launcher.LaunchUriAsync(new Uri(release.Url));
    }
}
