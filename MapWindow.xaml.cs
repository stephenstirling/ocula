using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ocula.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Ocula;

/// <summary>
/// Shows every photo with a GPS location on a map. The map itself is MapLibre GL JS
/// (bundled in Assets\Map) drawing free OpenFreeMap tiles; photos are sent to it from the
/// library and grouped into numbered clusters until you zoom in.
/// </summary>
public sealed partial class MapWindow : Window
{
    private readonly MainWindow _main;

    public MapWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 820));
        Root.RequestedTheme = _main.CurrentTheme;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;

        InitialiseAsync();
    }

    private async void InitialiseAsync()
    {
        if (!AppSettings.OnlineMaps)
        {
            CountText.Text = "The map is turned off in Settings > Privacy.";
            return;
        }
        try
        {
            await Map.EnsureCoreWebView2Async();
            var web = Map.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsStatusBarEnabled = false;

            // Serve the bundled map page from the app folder under a private address.
            web.SetVirtualHostNameToFolderMapping("ocula.map",
                Path.Combine(AppContext.BaseDirectory, "Assets", "Map"),
                CoreWebView2HostResourceAccessKind.Allow);
            web.WebMessageReceived += OnMessage;
            Map.Source = new Uri("https://ocula.map/map.html");
        }
        catch (Exception ex)
        {
            CountText.Text = "The map couldn't start: " + ex.Message;
        }
    }

    private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var type = doc.RootElement.GetProperty("type").GetString();
            var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;

            switch (type)
            {
                case "ready":
                    await SendPhotosAsync();
                    break;

                case "thumb" when path != null:
                    var bytes = await _main.Index.GetThumbAsync(path);
                    if (bytes != null)
                    {
                        sender.PostWebMessageAsJson(JsonSerializer.Serialize(new
                        {
                            type = "thumb",
                            path,
                            data = "data:image/jpeg;base64," + Convert.ToBase64String(bytes)
                        }));
                    }
                    break;

                case "open" when path != null:
                    _main.Activate();
                    await _main.OpenPathAsync(path);
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages.
        }
    }

    private async System.Threading.Tasks.Task SendPhotosAsync()
    {
        var photos = await _main.Index.GetGeotaggedAsync();
        CountText.Text = photos.Count == 1 ? "1 photo with a location" : $"{photos.Count:N0} photos with a location";
        var items = photos.Select(p => new
        {
            path = p.Path,
            name = p.Name,
            lat = p.Latitude,
            lon = p.Longitude,
            date = p.Taken?.ToString("d MMMM yyyy")
        });
        Map.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "photos", items }));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Map.CoreWebView2 != null) await SendPhotosAsync();
    }
}
