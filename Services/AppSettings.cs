using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;

namespace Ocula.Services;

/// <summary>Remembers the theme and the folders you've added between sessions.</summary>
public static class AppSettings
{
    private static ApplicationDataContainer Store => ApplicationData.Current.LocalSettings;

    /// <summary>"Light", "Dark" or "System".</summary>
    public static string Theme
    {
        get => Store.Values["Theme"] as string ?? "System";
        set => Store.Values["Theme"] = value;
    }

    /// <summary>Whether to look up camera photos on Wikidata / Wikimedia Commons.</summary>
    public static bool OnlineCameraLookup
    {
        get => Store.Values["OnlineCameraLookup"] as bool? ?? true;
        set => Store.Values["OnlineCameraLookup"] = value;
    }

    /// <summary>Whether the map may download map tiles from OpenFreeMap.</summary>
    public static bool OnlineMaps
    {
        get => Store.Values["OnlineMaps"] as bool? ?? true;
        set => Store.Values["OnlineMaps"] = value;
    }

    /// <summary>Whether to convert wide-gamut photos (Display P3, Adobe RGB…) for the screen.</summary>
    public static bool ColourManaged
    {
        get => Store.Values["ColourManaged"] as bool? ?? true;
        set => Store.Values["ColourManaged"] = value;
    }

    /// <summary>Whether to show HDR photos in HDR when the display supports it.</summary>
    public static bool HdrPreview
    {
        get => Store.Values["HdrPreview"] as bool? ?? true;
        set => Store.Values["HdrPreview"] = value;
    }

    // ---------- Smart recognition ----------

    /// <summary>Whether Ocula analyses photos on this PC for tag suggestions and "describe it" search.</summary>
    public static bool SmartEnabled
    {
        get => Store.Values["SmartEnabled"] as bool? ?? false;
        set => Store.Values["SmartEnabled"] = value;
    }

    public static SmartTier SmartTier
    {
        get => Enum.TryParse<SmartTier>(Store.Values["SmartTier"] as string, out var tier) ? tier : SmartTier.Compact;
        set => Store.Values["SmartTier"] = value.ToString();
    }

    // ---------- People ----------

    /// <summary>Whether Ocula finds and groups faces on this PC. Off by default.</summary>
    public static bool PeopleEnabled
    {
        get => Store.Values["PeopleEnabled"] as bool? ?? false;
        set => Store.Values["PeopleEnabled"] = value;
    }

    // ---------- Updates ----------

    /// <summary>Whether Ocula checks for updates by itself (once a day).</summary>
    public static bool CheckForUpdates
    {
        get => Store.Values["CheckForUpdates"] as bool? ?? true;
        set => Store.Values["CheckForUpdates"] = value;
    }

    public static DateTimeOffset LastUpdateCheck
    {
        get => Store.Values["LastUpdateCheck"] is long ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : DateTimeOffset.MinValue;
        set => Store.Values["LastUpdateCheck"] = value.UtcTicks;
    }

    /// <summary>The version that ran last time, for showing "What's new" after an update.</summary>
    public static string? LastRunVersion
    {
        get => Store.Values["LastRunVersion"] as string;
        set => Store.Values["LastRunVersion"] = value;
    }

    public static int VideoVolume
    {
        get => Store.Values["VideoVolume"] as int? ?? 80;
        set => Store.Values["VideoVolume"] = value;
    }

    public static bool SidebarOpen
    {
        get => Store.Values["SidebarOpen"] as bool? ?? true;
        set => Store.Values["SidebarOpen"] = value;
    }

    public static bool DetailsOpen
    {
        get => Store.Values["DetailsOpen"] as bool? ?? true;
        set => Store.Values["DetailsOpen"] = value;
    }

    // ---------- Import ----------

    public static string ImportDestination
    {
        get => Store.Values["ImportDestination"] as string
               ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        set => Store.Values["ImportDestination"] = value;
    }

    // ---------- Photo windows (Settings > Photo windows) ----------

    /// <summary>Double-clicking a thumbnail opens the photo in its own window (otherwise: full screen).</summary>
    public static bool PhotoWindowsOnDoubleClick
    {
        get => Store.Values["PhotoWindowsOnDoubleClick"] as bool? ?? true;
        set => Store.Values["PhotoWindowsOnDoubleClick"] = value;
    }

    /// <summary>How many spots photo windows share a monitor with (2 to 6).</summary>
    public static int PhotoWindowSpots
    {
        get => PhotoWindowLayout.ClampSpots(Store.Values["PhotoWindowSpots"] as int? ?? PhotoWindowLayout.DefaultSpots);
        set => Store.Values["PhotoWindowSpots"] = PhotoWindowLayout.ClampSpots(value);
    }

    public static SnapLayout PhotoWindowLayoutKind
    {
        get => Enum.TryParse<SnapLayout>(Store.Values["PhotoWindowLayout"] as string, out var layout) ? layout : SnapLayout.Columns;
        set => Store.Values["PhotoWindowLayout"] = value.ToString();
    }

    public static WhenSpotsFull PhotoWindowsWhenFull
    {
        get => Enum.TryParse<WhenSpotsFull>(Store.Values["PhotoWindowsWhenFull"] as string, out var full) ? full : WhenSpotsFull.ReplaceOldest;
        set => Store.Values["PhotoWindowsWhenFull"] = value.ToString();
    }

    /// <summary>New photo windows open in the spot used last, replacing the photo there.</summary>
    public static bool PhotoWindowsReuseLastSpot
    {
        get => Store.Values["PhotoWindowsReuseLastSpot"] as bool? ?? false;
        set => Store.Values["PhotoWindowsReuseLastSpot"] = value;
    }

    /// <summary>The spot (0-based) last chosen with a spot button or Alt+number.</summary>
    public static int PhotoWindowsLastSpot
    {
        get => Store.Values["PhotoWindowsLastSpot"] as int? ?? 0;
        set => Store.Values["PhotoWindowsLastSpot"] = value;
    }

    /// <summary>Zooming or panning one photo window does the same in the others.</summary>
    public static bool PhotoWindowsLinkView
    {
        get => Store.Values["PhotoWindowsLinkView"] as bool? ?? false;
        set => Store.Values["PhotoWindowsLinkView"] = value;
    }

    // ---------- Capta ----------

    /// <summary>Capta's capture folder, as its last ocula: link said (Capta's default until then).</summary>
    public static string CapturesFolder
    {
        get => Store.Values["CapturesFolder"] as string
               ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Captures");
        set => Store.Values["CapturesFolder"] = value;
    }

    // ---------- Download from web ----------

    public static string WebDestination
    {
        get => Store.Values["WebDestination"] as string
               ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Web downloads");
        set => Store.Values["WebDestination"] = value;
    }

    public static bool WebSaveSource
    {
        get => Store.Values["WebSaveSource"] as bool? ?? true;
        set => Store.Values["WebSaveSource"] = value;
    }

    public static string WebLastUrl
    {
        get => Store.Values["WebLastUrl"] as string ?? string.Empty;
        set => Store.Values["WebLastUrl"] = value;
    }

    public static string ImportFolderPattern
    {
        get => Store.Values["ImportFolderPattern"] as string ?? RenamePattern.DefaultFolder;
        set => Store.Values["ImportFolderPattern"] = value;
    }

    public static bool ImportRename
    {
        get => Store.Values["ImportRename"] as bool? ?? true;
        set => Store.Values["ImportRename"] = value;
    }

    public static string ImportNamePattern
    {
        get => Store.Values["ImportNamePattern"] as string ?? RenamePattern.DefaultFileName;
        set => Store.Values["ImportNamePattern"] = value;
    }

    public static string ImportBackupFolder
    {
        get => Store.Values["ImportBackupFolder"] as string ?? string.Empty;
        set => Store.Values["ImportBackupFolder"] = value;
    }

    public static bool ImportBackupOn
    {
        get => Store.Values["ImportBackupOn"] as bool? ?? false;
        set => Store.Values["ImportBackupOn"] = value;
    }

    public static bool ImportEject
    {
        get => Store.Values["ImportEject"] as bool? ?? true;
        set => Store.Values["ImportEject"] = value;
    }

    // ---------- Rename, editors, proofing ----------

    public static string RenamePatternSetting
    {
        get => Store.Values["RenamePattern"] as string ?? "{date}_{seq}";
        set => Store.Values["RenamePattern"] = value;
    }

    public static string? LastEditor
    {
        get => Store.Values["LastEditor"] as string;
        set => Store.Values["LastEditor"] = value;
    }

    /// <summary>Editors added by hand, stored as "name|path|raw" lines.</summary>
    public static List<Editor> CustomEditors
    {
        get => (Store.Values["CustomEditors"] as string ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|'))
            .Where(p => p.Length == 3)
            .Select(p => new Editor(p[0], p[1], p[2] == "1"))
            .ToList();
        set => Store.Values["CustomEditors"] = string.Join('\n', value.Select(e => $"{e.Name}|{e.ExePath}|{(e.PrefersRaw ? 1 : 0)}"));
    }

    public static string ProofingEmail
    {
        get => Store.Values["ProofingEmail"] as string ?? string.Empty;
        set => Store.Values["ProofingEmail"] = value;
    }

    public static string ProofingDestination
    {
        get => Store.Values["ProofingDestination"] as string ?? string.Empty;
        set => Store.Values["ProofingDestination"] = value;
    }

    public static string? LastExportPreset
    {
        get => Store.Values["LastExportPreset"] as string;
        set => Store.Values["LastExportPreset"] = value;
    }

    public static List<string> Folders
    {
        get => (Store.Values["Folders"] as string)?
                   .Split('|', StringSplitOptions.RemoveEmptyEntries).ToList()
               ?? new List<string>();
        set => Store.Values["Folders"] = string.Join('|', value);
    }
}
