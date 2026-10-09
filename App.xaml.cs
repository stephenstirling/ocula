using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Ocula;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        var startup = AppInstance.GetCurrent().GetActivatedEventArgs();
        var path = GetPath(startup)
                   ?? FirstExistingPath(Environment.GetCommandLineArgs().Skip(1));
        if (path != null) await _window.OpenPathAsync(path);
    }

    /// <summary>
    /// Called (on a background thread) when another launch of Ocula was redirected here,
    /// e.g. "Open in Ocula" on a folder while Ocula is already open.
    /// </summary>
    public static void OnRedirectedActivation(AppActivationArguments args)
    {
        if (Current is not App { _window: { } window }) return;
        var path = GetPath(args);

        window.DispatcherQueue.TryEnqueue(async () =>
        {
            if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            window.Activate();
            if (path != null) await window.OpenPathAsync(path);
        });
    }

    /// <summary>The file or folder a launch refers to: "Open with" or command-line arguments.</summary>
    private static string? GetPath(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.File &&
            args.Data is IFileActivatedEventArgs file && file.Files.Count > 0)
        {
            return file.Files[0].Path;
        }

        if (args.Kind == ExtendedActivationKind.Launch &&
            args.Data is ILaunchActivatedEventArgs launch && !string.IsNullOrWhiteSpace(launch.Arguments))
        {
            return FirstExistingPath(SplitArguments(launch.Arguments));
        }

        return null;
    }

    /// <summary>
    /// The first argument that is an existing file or folder. A launch's arguments can include
    /// Ocula's own exe (or the "ocula" alias) as the first item; that's skipped, otherwise a plain
    /// launch would open Ocula's install folder.
    /// </summary>
    private static string? FirstExistingPath(IEnumerable<string> candidates) =>
        candidates.Select(a => a.Trim().Trim('"'))
                  .FirstOrDefault(a => a.Length > 0 &&
                                       !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                       (File.Exists(a) || Directory.Exists(a)));

    /// <summary>Splits a command line on spaces, keeping "quoted paths with spaces" together.</summary>
    private static IEnumerable<string> SplitArguments(string commandLine)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
