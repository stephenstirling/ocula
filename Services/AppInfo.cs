using System.Collections.Generic;
using Windows.ApplicationModel;

namespace Ocula.Services;

public sealed record ThirdPartyNotice(string Name, string License, string Url, string Purpose)
{
    public System.Uri Link => new(Url);
}

/// <summary>Links and credits shown on the About page.</summary>
public static class AppInfo
{
    public const string Publisher = "Stephen Stirling";
    public const string Copyright = "© 2026 Stephen Stirling";

    // TODO: replace with your real pages before release. Empty strings hide the buttons.
    public const string SupportUrl = "https://buymeacoffee.com/stephenstirling";
    /// <summary>"owner/repository" on GitHub, used for updates and release notes. Empty turns both off.</summary>
    public const string GitHubRepository = ""; // e.g. "stephenstirling/ocula"

    public const string SourceUrl = "";        // e.g. https://github.com/you/ocula
    /// <summary>
    /// Microsoft Store policy 10.8.2 sets rules for receiving donations in an app, so the Store
    /// build leaves the "Support Ocula" button out. Keep donations on the GitHub page, or set this
    /// to true after checking the current policy. Other installs always show the button.
    /// </summary>
    public const bool DonationsInStoreBuild = false;

    public static bool ShowSupport =>
        SupportUrl.Length > 0 && (DonationsInStoreBuild || UpdateService.Channel != UpdateChannel.Store);

    public const string FeedbackUrl = "";      // e.g. https://github.com/you/ocula/issues
    public const string PrivacyUrl = "";       // e.g. https://github.com/you/ocula/blob/main/PRIVACY.md
    public const string StoreProductId = "";   // from Partner Center, e.g. 9NXXXXXXXXXX

    public static string Version
    {
        get
        {
            try
            {
                var v = Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                return "development build"; // running without package identity
            }
        }
    }

    /// <summary>
    /// Every component shipped with Ocula and its licence. Keep THIRD-PARTY-NOTICES.md
    /// (with the full licence texts) in step with this list.
    /// </summary>
    public static readonly IReadOnlyList<ThirdPartyNotice> Notices = new[]
    {
        new ThirdPartyNotice("Windows App SDK / WinUI 3", "MIT", "https://github.com/microsoft/WindowsAppSDK", "App framework"),
        new ThirdPartyNotice("Windows Community Toolkit", "MIT", "https://github.com/CommunityToolkit/Windows", "Theme pill, settings cards"),
        new ThirdPartyNotice("LibVLC and LibVLCSharp", "LGPL-2.1", "https://www.videolan.org/vlc/libvlc.html", "Playing MKV, WebM, AVI and other videos"),
        new ThirdPartyNotice("Win2D", "MIT", "https://github.com/microsoft/Win2D", "HDR preview"),
        new ThirdPartyNotice("Magick.NET", "Apache-2.0", "https://github.com/dlemstra/Magick.NET", "Decoding, RAW support and export"),
        new ThirdPartyNotice("ImageMagick, LibRaw and other bundled libraries", "Various (see Magick.NET notices)", "https://github.com/dlemstra/Magick.NET", "Included with Magick.NET"),
        new ThirdPartyNotice("ONNX Runtime", "MIT", "https://github.com/microsoft/onnxruntime", "Running the recognition model on this PC"),
        new ThirdPartyNotice("YuNet face detector (OpenCV Model Zoo)", "MIT", "https://huggingface.co/opencv/face_detection_yunet", "People: finds faces, downloaded on request"),
        new ThirdPartyNotice("SFace face recogniser (OpenCV Model Zoo)", "Apache-2.0", "https://huggingface.co/opencv/face_recognition_sface", "People: groups faces that look alike, downloaded on request"),
        new ThirdPartyNotice("CLIP (OpenAI), ONNX export by Xenova", "MIT", "https://huggingface.co/Xenova/clip-vit-base-patch32", "Smart recognition model, downloaded on request"),
        new ThirdPartyNotice("MetadataExtractor", "Apache-2.0", "https://github.com/drewnoakes/metadata-extractor-dotnet", "Camera and EXIF details"),
        new ThirdPartyNotice("Microsoft.Data.Sqlite / SQLite", "MIT / public domain", "https://github.com/dotnet/efcore", "Local library"),
        new ThirdPartyNotice("CoenM.ImageHash", "MIT", "https://github.com/coenm/ImageHash", "Duplicate detection"),
        new ThirdPartyNotice("SixLabors.ImageSharp", "Six Labors Split License", "https://github.com/SixLabors/ImageSharp", "Used by ImageHash"),
        new ThirdPartyNotice("MapLibre GL JS", "BSD-3-Clause", "https://github.com/maplibre/maplibre-gl-js", "Map view"),
        new ThirdPartyNotice("OpenFreeMap, OpenMapTiles and OpenStreetMap", "Map data © OpenStreetMap contributors (ODbL)", "https://openfreemap.org", "Map tiles (credited on the map)"),
        new ThirdPartyNotice("Wikidata and Wikimedia Commons", "CC0 data; photos under their own free licences", "https://commons.wikimedia.org", "Camera photos (credited per photo)"),
    };
}
