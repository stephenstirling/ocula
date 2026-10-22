# Wiring the Phun tab into MainWindow (not done yet)

Add to the NavigationView menu items:

```xml
<NavigationViewItem Content="Phun" Tag="scraper">
    <NavigationViewItem.Icon><FontIcon Glyph="&#xE896;"/></NavigationViewItem.Icon>
</NavigationViewItem>
```

and in the ItemInvoked handler: `case "scraper": ContentFrame.Navigate(typeof(Ocula.Views.ScraperPage)); break;`

`ScraperPage.xaml.cs` has the downloads folder, scraper URL and token as constants at the top; move them to Settings.
These files were written without being compiled against this project.
