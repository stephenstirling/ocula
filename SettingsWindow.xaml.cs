using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;

namespace Ocula;

public sealed partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly ObservableCollection<string> _folders = new();
    private bool _loading = true;

    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(860, 820));
        ApplyTheme(AppSettings.Theme);

        // Appearance
        ThemeBox.SelectedIndex = AppSettings.Theme switch { "Light" => 0, "Dark" => 1, _ => 2 };

        // Folders
        foreach (var folder in main.RootFolders) _folders.Add(folder);
        FoldersExpander.ItemsSource = _folders;

        // Privacy
        CameraToggle.IsOn = AppSettings.OnlineCameraLookup;
        ColourToggle.IsOn = AppSettings.ColourManaged;
        MapToggle.IsOn = AppSettings.OnlineMaps;

        // Photo windows
        PhotoWindowsToggle.IsOn = AppSettings.PhotoWindowsOnDoubleClick;
        SpotsBox.SelectedIndex = AppSettings.PhotoWindowSpots - PhotoWindowLayout.MinSpots;
        FillLayoutBox(AppSettings.PhotoWindowSpots, AppSettings.PhotoWindowLayoutKind);
        WhenFullBox.SelectedIndex = (int)AppSettings.PhotoWindowsWhenFull;
        ReuseSpotToggle.IsOn = AppSettings.PhotoWindowsReuseLastSpot;
        LinkViewToggle.IsOn = AppSettings.PhotoWindowsLinkView;

        // Smart recognition
        SmartToggle.IsOn = AppSettings.SmartEnabled;
        SmartTierBox.SelectedIndex = AppSettings.SmartTier == SmartTier.Accurate ? 1 : 0;
        RefreshSmartStatus();

        // People
        PeopleToggle.IsOn = AppSettings.PeopleEnabled;
        RefreshPeopleStatus();

        // Updates
        UpdatesExpander.Description = $"Version {UpdateService.CurrentVersion} · {UpdateService.ChannelDescription}";
        AutoUpdateToggle.IsOn = AppSettings.CheckForUpdates;
        if (!UpdateService.IsConfigured)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatusCard.Header = "Updates aren't set up for this build.";
        }
        else if (AppSettings.LastUpdateCheck > DateTimeOffset.MinValue)
        {
            UpdateStatusCard.Header = $"Last checked {AppSettings.LastUpdateCheck.ToLocalTime():d MMM, HH:mm}";
        }

        // About
        AboutExpander.Description = $"Version {AppInfo.Version} · {AppInfo.Copyright}. All rights reserved.";
        SupportButton.Visibility = AppInfo.ShowSupport ? Visibility.Visible : Visibility.Collapsed;
        SupportCard.Visibility = SupportButton.Visibility;
        SourceCard.Visibility = AppInfo.SourceUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SourceCard.Description = AppInfo.SourceUrl;
        FeedbackCard.Visibility = AppInfo.FeedbackUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RateCard.Visibility = AppInfo.StoreProductId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivacyCard.Visibility = AppInfo.PrivacyUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticesList.ItemsSource = AppInfo.Notices;

        UpdateCacheSize();
        _loading = false;
    }

    // ---------- Appearance ----------

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || (ThemeBox.SelectedItem as ComboBoxItem)?.Tag is not string theme) return;
        _main.SetTheme(theme); // updates the main window, its pill and the saved setting
        ApplyTheme(theme);
    }

    public void ApplyTheme(string theme)
    {
        var wasLoading = _loading;
        _loading = true; // keep the combo box in step without re-triggering its handler
        if (ThemeBox != null) ThemeBox.SelectedIndex = theme switch { "Light" => 0, "Dark" => 1, _ => 2 };
        _loading = wasLoading;

        Root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        var dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    // ---------- Folders ----------

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _main.AddFolder(folder.Path);
        if (!_folders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)) _folders.Add(folder.Path);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        _main.RemoveFolder(path);
        _folders.Remove(path);
    }

    // ---------- Library cache ----------

    private void UpdateCacheSize()
    {
        var bytes = _main.Index.SizeOnDisk;
        CacheCard.Description =
            $"{DuplicateCandidate.FormatSize(bytes)} of thumbnails, metadata and duplicate fingerprints. " +
            "Clearing it is safe for tags and ratings, which live in your files; it's rebuilt as you browse.";
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "Clear the library cache?",
            Content = "Thumbnails and duplicate fingerprints will be rebuilt as you browse. " +
                      "Colour labels on JPEG, TIFF and HEIC files are only stored in the library, so those labels will be lost.",
            PrimaryButtonText = "Clear cache",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await _main.Index.ClearAsync();
        await _main.RefreshAsync();
        UpdateCacheSize();
    }

    // ---------- Photo windows ----------

    private void PhotoWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var spots = PhotoWindowLayout.MinSpots + Math.Max(0, SpotsBox.SelectedIndex);
        var layout = (LayoutBox.SelectedItem as ComboBoxItem)?.Tag is SnapLayout chosen ? chosen : AppSettings.PhotoWindowLayoutKind;

        AppSettings.PhotoWindowsOnDoubleClick = PhotoWindowsToggle.IsOn;
        AppSettings.PhotoWindowSpots = spots;
        AppSettings.PhotoWindowLayoutKind = layout;
        AppSettings.PhotoWindowsWhenFull = (WhenSpotsFull)Math.Max(0, WhenFullBox.SelectedIndex);
        AppSettings.PhotoWindowsReuseLastSpot = ReuseSpotToggle.IsOn;
        AppSettings.PhotoWindowsLinkView = LinkViewToggle.IsOn;

        // Layout names depend on the number of spots ("1 large + 4").
        if (ReferenceEquals(sender, SpotsBox))
        {
            _loading = true;
            FillLayoutBox(spots, layout);
            _loading = false;
        }
        _main.PhotoWindowSettingsChanged();
    }

    private void FillLayoutBox(int spots, SnapLayout selected)
    {
        LayoutBox.Items.Clear();
        foreach (var layout in PhotoWindowLayout.Layouts)
            LayoutBox.Items.Add(new ComboBoxItem { Content = PhotoWindowLayout.Name(layout, spots), Tag = layout });
        LayoutBox.SelectedIndex = PhotoWindowLayout.Layouts.ToList().IndexOf(selected);
    }

    // ---------- Privacy ----------

    private void CameraToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.OnlineCameraLookup = CameraToggle.IsOn;
    }

    private void ColourToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.ColourManaged = ColourToggle.IsOn;
    }

    private void MapToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.OnlineMaps = MapToggle.IsOn;
    }

    // ---------- Smart recognition ----------

    private CancellationTokenSource? _smartDownload;

    private SmartTier SelectedTier => SmartTierBox.SelectedIndex == 1 ? SmartTier.Accurate : SmartTier.Compact;

    private void RefreshSmartStatus()
    {
        var tier = SelectedTier;
        var installed = SmartModels.IsInstalled(tier);
        var downloading = _smartDownload != null;
        SmartStatusCard.Description = downloading ? "Downloading…"
            : installed ? "Ready. Photos are analysed in the background while you're not using the computer."
            : $"Not downloaded yet ({SmartModels.SizeText(tier)}).";
        SmartDownloadButton.Visibility = !installed && !downloading ? Visibility.Visible : Visibility.Collapsed;
        SmartCancelButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        SmartProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SmartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.SmartEnabled = SmartToggle.IsOn;

        // Turning it on for the first time offers the download straight away.
        if (SmartToggle.IsOn && !SmartModels.IsInstalled(SelectedTier) && _smartDownload == null)
            await DownloadSmartModelAsync();

        RefreshSmartStatus();
        _main.SmartSettingsChanged();
    }

    private void SmartTierBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.SmartTier = SelectedTier;
        RefreshSmartStatus();
        _main.SmartSettingsChanged(); // photos are analysed again with the other model, in the background
    }

    private async void SmartDownload_Click(object sender, RoutedEventArgs e) => await DownloadSmartModelAsync();

    private void SmartCancel_Click(object sender, RoutedEventArgs e) => _smartDownload?.Cancel();

    private async Task DownloadSmartModelAsync()
    {
        _smartDownload = new CancellationTokenSource();
        SmartProgress.Value = 0;
        RefreshSmartStatus();
        try
        {
            var progress = new Progress<double>(p => SmartProgress.Value = p);
            await SmartModels.DownloadAsync(SelectedTier, progress, _smartDownload.Token);
            SmartStatusCard.Description = "Downloaded.";
        }
        catch (OperationCanceledException)
        {
            SmartStatusCard.Description = "Download cancelled.";
        }
        catch (Exception ex)
        {
            SmartStatusCard.Description = "Couldn't download the model: " + ex.Message;
        }
        finally
        {
            _smartDownload = null;
            RefreshSmartStatus();
            _main.SmartSettingsChanged();
        }
    }

    private async void SmartDelete_Click(object sender, RoutedEventArgs e)
    {
        _smartDownload?.Cancel();
        AppSettings.SmartEnabled = false;
        _loading = true;
        SmartToggle.IsOn = false;
        _loading = false;

        foreach (var tier in Enum.GetValues<SmartTier>()) SmartModels.Delete(tier);
        await _main.DeleteSmartDataAsync();
        RefreshSmartStatus();
        SmartStatusCard.Description = "Recognition data deleted.";
        _main.SmartSettingsChanged();
    }

    // ---------- People ----------

    private CancellationTokenSource? _peopleDownload;

    private void RefreshPeopleStatus()
    {
        var installed = FaceModels.IsInstalled;
        var downloading = _peopleDownload != null;
        PeopleStatusCard.Description = downloading ? "Downloading…"
            : installed ? "Ready. Faces are found in the background while you're not using the computer."
            : $"Not downloaded yet ({FaceModels.SizeText}).";
        PeopleDownloadButton.Visibility = !installed && !downloading ? Visibility.Visible : Visibility.Collapsed;
        PeopleCancelButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        PeopleProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PeopleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.PeopleEnabled = PeopleToggle.IsOn;
        if (PeopleToggle.IsOn && !FaceModels.IsInstalled && _peopleDownload == null)
            await DownloadPeopleModelsAsync();
        RefreshPeopleStatus();
        _main.PeopleSettingsChanged();
    }

    private async void PeopleDownload_Click(object sender, RoutedEventArgs e) => await DownloadPeopleModelsAsync();

    private void PeopleCancel_Click(object sender, RoutedEventArgs e) => _peopleDownload?.Cancel();

    private async Task DownloadPeopleModelsAsync()
    {
        _peopleDownload = new CancellationTokenSource();
        PeopleProgress.Value = 0;
        RefreshPeopleStatus();
        try
        {
            var progress = new Progress<double>(p => PeopleProgress.Value = p);
            await FaceModels.DownloadAsync(progress, _peopleDownload.Token);
            PeopleStatusCard.Description = "Downloaded.";
        }
        catch (OperationCanceledException)
        {
            PeopleStatusCard.Description = "Download cancelled.";
        }
        catch (Exception ex)
        {
            PeopleStatusCard.Description = "Couldn't download the models: " + ex.Message;
        }
        finally
        {
            _peopleDownload = null;
            RefreshPeopleStatus();
            _main.PeopleSettingsChanged();
        }
    }

    private async void PeopleDelete_Click(object sender, RoutedEventArgs e)
    {
        _peopleDownload?.Cancel();
        AppSettings.PeopleEnabled = false;
        _loading = true;
        PeopleToggle.IsOn = false;
        _loading = false;

        await _main.DeleteFaceDataAsync();
        FaceModels.Delete();
        RefreshPeopleStatus();
        PeopleStatusCard.Description = "Face data deleted.";
        _main.PeopleSettingsChanged();
    }

    // ---------- Updates ----------

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusCard.Header = "Checking…";
        var status = await UpdateService.CheckAsync(WinRT.Interop.WindowNative.GetWindowHandle(this));
        UpdateStatusCard.Header = status.Message;
        InstallUpdateButton.Visibility = status.Available ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Content = UpdateService.Channel switch
        {
            UpdateChannel.AppInstaller => "Restart and update",
            UpdateChannel.Store => "Update",
            _ => "Download"
        };
        CheckUpdatesButton.IsEnabled = true;
        _main.ShowUpdate(status);
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await _main.InstallUpdateAsync();

    private void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.CheckForUpdates = AutoUpdateToggle.IsOn;
    }

    private async void WhatsNewSettings_Click(object sender, RoutedEventArgs e)
    {
        var release = await UpdateService.GetReleaseAsync(UpdateService.CurrentVersion) ?? await UpdateService.GetLatestReleaseAsync();
        if (release == null)
        {
            UpdateStatusCard.Header = "Release notes aren't available right now.";
            return;
        }
        _main.Activate();
        await _main.ShowReleaseNotesAsync(release, $"What's new in Ocula {release.Version}");
    }

    // ---------- About ----------

    private async void Support_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(AppInfo.SupportUrl));
    private async void Source_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(AppInfo.SourceUrl));
    // Opens the Store app straight on Ocula's review page.
    private async void Rate_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri($"ms-windows-store://review/?ProductId={AppInfo.StoreProductId}"));
    private async void Privacy_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(AppInfo.PrivacyUrl));
    private async void Feedback_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(AppInfo.FeedbackUrl));
}
