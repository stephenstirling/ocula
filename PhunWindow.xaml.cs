using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace Ocula;

/// <summary>Hosts the Phun scraper page (Phun/ScraperPage). The page polls the scraper only while this window is open.</summary>
public sealed partial class PhunWindow : Window
{
    public PhunWindow(MainWindow main)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 860));
        Root.RequestedTheme = main.CurrentTheme;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;

        ContentFrame.Navigate(typeof(Ocula.Views.ScraperPage));
        Closed += (_, _) => (ContentFrame.Content as Ocula.Views.ScraperPage)?.Stop();   // closing the window doesn't navigate away, so stop the polling here
    }
}
