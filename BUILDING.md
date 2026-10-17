# Building Ocula

A WinUI 3 (Windows App SDK) app in C# / .NET 8, built as a native **x64-only** app for Windows 10 (version 1809 and later) and Windows 11.

## Setup

The project already exists: `Ocula.csproj` (the app, packaged as MSIX), `Ocula.slnx` and
`OculaShell\OculaShell.vcxproj` (the C++ folder right-click menu) live at the repo root, next to
the source. `Ocula.csproj-additions.xml` and `Package.appxmanifest-additions.xml` are already
merged into `Ocula.csproj` and `Package.appxmanifest` and are kept only as notes.

### What to install

1. **Visual Studio 2026** (Community is fine) with:
   - **WinUI application development** (the Windows App SDK C# tools), and
   - **Desktop development with C++** (`Microsoft.VisualStudio.Workload.NativeDesktop`, with
     its recommended components: the MSVC v145 build tools and a Windows 11 SDK). OculaShell
     can't build without it. To add it from a terminal:
     ```
     "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vs_installer.exe" modify --installPath "C:\Program Files\Microsoft Visual Studio\18\Community" --add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive --norestart
     ```
     Close the Visual Studio Installer window first; otherwise the command exits at once
     without installing anything.
2. The **.NET 10 SDK** (it builds the .NET 8 app) and the **.NET 8 Desktop Runtime**.
3. **Developer Mode** (Windows Settings > System > For developers), to deploy the unsigned
   debug package.

### Build output goes to local disk

`Directory.Build.props` sends every project's `bin` and `obj` to
`%USERPROFILE%\.stirling-build\ocula\<project>\`, because the repo lives on a network drive
(`Z:`) and Windows won't register an MSIX package from a network share. The same scheme is used
by Capta. To put the output somewhere else, pass `-p:StirlingLocalBuildRoot=<folder>\`.

### Map library (one-time download)

MapLibre GL JS is bundled with the app rather than loaded from the internet, so the map page
works the same for everyone and only the map style and tiles are fetched online. The files are
committed in `Assets\Map`; to download them again, or to update MapLibre, get both files for the
**same 4.x version** (currently 4.7.1, recorded in `Assets\Map\README.md`):

- https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.js
- https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.css

Then update the version in `Assets\Map\README.md`, `THIRD-PARTY-NOTICES.md` and `AppInfo.Notices`.

### Building and running

- **Visual Studio:** open `Ocula.slnx`, choose **Debug | x64** and press **F5**. `Ocula.csproj`
  builds OculaShell first (it isn't listed in the solution, so that `dotnet build` can use the
  same file), packages the DLL and deploys the app. Deploying registers "Open with", the `ocula`
  command and the folder menu; if "Open in Ocula" doesn't appear straight away, restart File
  Explorer (Task Manager > Windows Explorer > Restart).
- **Command line, everything:** Visual Studio's MSBuild builds OculaShell too:
  ```
  "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Ocula.slnx -restore -p:Configuration=Debug -p:Platform=x64
  ```
- **Command line, quick check:** `dotnet build -p:Platform=x64` builds the app but not
  OculaShell (the .NET CLI can't build C++). It packages an OculaShell.dll that was already built,
  and otherwise warns that the folder menu is missing.
- **Store package:** add `-p:Configuration=Release -p:GenerateAppxPackageOnBuild=true` to the
  MSBuild command (or Project > Publish > Create App Packages). Packages go to `AppPackages\`.

NuGet versions are pinned in `Directory.Packages.props`. Packages and what they are for:

| Package | Used for |
|---|---|
| `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` | WinUI 3, app lifecycle, HDR display information, MSIX packaging |
| `Magick.NET-Q8-AnyCPU` | Decoding RAW and other files Windows can't, thumbnails, export |
| `MetadataExtractor` | Camera, lens and exposure details (including CR2) |
| `Microsoft.Data.Sqlite` | The local library database |
| `CommunityToolkit.WinUI.Controls.Segmented`, `.SettingsControls` | The Light/Dark/System pill and settings cards |
| `Microsoft.Graphics.Win2D` | HDR preview and colour management |
| `LibVLCSharp.WinUI`, `VideoLAN.LibVLC.Windows` | Playing MKV, WebM, AVI and other videos Windows' player can't (x64 only) |
| `Microsoft.ML.OnnxRuntime` | Smart recognition and People models (downloaded when turned on) |
| `CoenM.ImageSharp.ImageHash`, `SixLabors.ImageSharp` 2.1.13 | Duplicate detection; see `docs/KNOWN-ISSUES.md` |

### Before publishing

In `Services/CameraImageService.cs`, put your own contact in the User-Agent, and in
`Services/AppInfo.cs` set the donation page and (optionally) the feedback link.

## App icon

"Lens and light": a lens seen head-on with a warm light at its centre. `AppIcon.svg` is the
master; sizes 32 px and below use the simplified `AppIcon-small.svg` so they stay crisp.
`AppIcon.ico` holds every size from 16 to 256 px and is used for the window, the .exe and
the folder right-click menu. The PNGs are the package logos (Start menu, taskbar, splash).

## What's in this build (phase 1)

- **Light / Dark / System pill** in the title bar, remembered between sessions.
- **Remembered folders**: folders you add stay in the tree next time.
- **Local library** (`%LOCALAPPDATA%\Ocula\library.db`): thumbnails and metadata are stored
  after the first visit, so folders reopen instantly. Changed files are re-read automatically.
- **RAW+JPEG pairing**: IMG_0001.CR2 + IMG_0001.JPG show as one tile with a "RAW+JPG" badge;
  tags and ratings are written to both.
- **Ratings and colour labels**, with Lightroom's shortcuts (0–5 stars; 6 red, 7 yellow,
  8 green, 9 blue) and filters for both.
- **Multi-select editing**: ratings, labels and tags apply to every selected image.
- **Search** across file names, tags and camera models.
- **Folder watching**: new, deleted or renamed images appear without a refresh.
- **HDR detection**: an HDR card in the details panel and an "HDR" badge on tiles. Detects
  gain-map photos (Ultra HDR, iPhone, ISO 21496-1), PQ/HLG-encoded AVIF/HEIC/PNG, 32-bit HDR
  merges (DNG/TIFF), and photos taken in a phone's HDR mode. Existing libraries are re-checked
  automatically after updating.
- **OneDrive online-only files** are detected. Ocula uses OneDrive's cloud thumbnails and
  doesn't read their contents, so browsing a cloud folder won't download it.

## Phase 2 (this build)

- **Batch export** (Ctrl+E) with presets: Web, Social, Full-size JPEG and Archive TIFF
  (from RAW), plus your own. Each preset sets size, format (JPEG, PNG, WebP, AVIF, TIFF),
  quality, location/metadata removal, watermark, file-name suffix and destination folder.
  Each preset remembers its folder, so "Web" can point at the website folder on the NAS.
  Exports run in the background with progress and Cancel.
- **Share** selected photos through the Windows share sheet.
- **Open from File Explorer**: Ocula appears under "Open with" for image files and can be
  the default app. Opening a file shows its folder with that file selected.
- **Command line**: `ocula "D:\Photos"` opens a folder.
- **"Open in Ocula" on folders** in File Explorer's right-click menu (the main Windows 11
  menu, not just "Show more options").
- **Single window**: opening a photo or folder while Ocula is running brings the existing
  window forward and shows it there, instead of starting a second copy.

Export notes: output is standard range (HDR gain maps aren't kept); RAW exports use a
neutral conversion and don't include edits made in other apps; removing location also
removes the XMP block, which is where some apps keep a second copy of the GPS position.

## Publishing to the Microsoft Store

1. Create a free **individual** developer account in Partner Center
   (https://storedeveloper.microsoft.com): a Microsoft account and a quick ID check, with no
   fee or credit card. Use the name Stephen Stirling.
2. **Reserve the name "Ocula"**. If it's taken, the Store will say so straight away.
3. In Visual Studio: **Project > Publish > Associate App with the Store** and pick the reserved
   name. This writes the correct Identity and publisher values into `Package.appxmanifest`.
4. **Project > Publish > Create App Packages > Microsoft Store**, for **x64 only** (untick
   ARM64; ARM PCs can still run it through Windows' x64 emulation). Run the Windows App Certification Kit when offered.
5. The listing text (description, features, search terms, system requirements, screenshot
   plan, age-rating answers and certification notes) is ready to paste from `store/listing.md`.
   In Partner Center, create a submission: upload the `.msixupload`, set pricing to **Free**,
   add screenshots and a description, complete the age rating questionnaire, and give the
   **privacy policy** URL (the published `PRIVACY.md`).
6. Ocula declares the `runFullTrust` capability (all WinUI 3 desktop apps do) and a File
   Explorer menu extension; Partner Center may ask you to explain why. "Desktop photo browser
   that integrates with File Explorer" is the honest answer.
7. After certification (usually a few days), copy the Store ID into `AppInfo.StoreProductId`
   and the GitHub README links.

Never commit `.pfx` signing files to GitHub; `.gitignore` already excludes them.

## Publishing and the "Unknown publisher" warning

Windows takes the publisher name from the package's digital signature, not from the app, so
the name only shows (and install warnings go away) once the package is signed with a
certificate Windows trusts:

- **Microsoft Store** (recommended): free for individual developers. Microsoft signs the
  package and the Store lists "Stephen Stirling" as the publisher. Use Project > Publish >
  Associate App with the Store in Visual Studio.
- **Your own download** isn't planned: it would need a paid certificate (see `RELEASING.md`
  for the options if that ever changes). GitHub carries the source code and release notes.

The copyright line (© 2026 Stephen Stirling) appears on the About page and in the .exe's
file details.

## Settings and About

The gear button in the title bar opens Settings:
- **Appearance**: theme (also available from the pill in the title bar).
- **Library**: add or remove folders from the tree, and see or clear the library cache.
- **Privacy**: turn online camera photos (Wikidata / Wikimedia Commons) on or off.
- **About**: version, "Support Ocula" donation button, source and feedback links (hidden
  until set in `AppInfo.cs`), and third-party notices.

Before publishing, finish `THIRD-PARTY-NOTICES.md` by pasting in each licence text, and ship
it with the app (and in the repository if you open-source it).

## Phase 3: library search, background indexing and map

**Search your whole library.** Typing in the search box still filters the open folder as you
type; press **Enter** to search every indexed photo instead. Plain words match file names,
tags and camera models, and filters can be combined:

| Filter | Example |
|---|---|
| Camera | `camera:R5` |
| Tag | `tag:wedding`, `tag:"smith wedding"` |
| Rating | `rating>=4`, `rating:5` |
| Colour label | `label:red` |
| Date | `year:2025`, `date:2026-09`, `date:2026-09-26` |
| Folder | `folder:Lisbon` |
| Type | `hdr`, `raw`, `gps` (has a location) |

The box suggests filters, camera models and tags as you type. Clear it to go back to the folder.

**Background indexing.** A few seconds after launch, Ocula works through every folder in the
tree, reading metadata and building thumbnails, so search is complete and folders open
instantly, which makes the biggest difference for NAS folders. It's gentle: a few photos at a
time on local drives and one at a time on network drives. The status and a pause button sit
under the folder tree. Unchanged photos are skipped quickly, so later runs finish fast.

**Map.** The map button (next to Find duplicates) shows every photo with a GPS location,
grouped into numbered clusters until you zoom in. Click a photo for a preview and "Show in
Ocula". It uses MapLibre (bundled) and free OpenFreeMap tiles, and can be turned off in
Settings > Privacy. Existing libraries are re-read once to pick up locations.

## HDR preview

On a display with HDR turned on (Windows Settings > System > Display > Use HDR), HDR photos
are shown in true HDR in the preview panel, marked with an "HDR" badge. A "Show in HDR"
switch in the HDR card turns it off, and the card explains why when HDR preview isn't possible.

- **Gain-map JPEGs** (Android Ultra HDR, Pixel, Samsung, Lightroom and Adobe exports): the
  photo and its gain map are combined using the Adobe/Ultra HDR gain map formula, scaled to
  the screen's actual headroom, so a 400-nit laptop and a 1,000-nit monitor both look right.
- **PQ and HLG images** (AVIF, HEIC, PNG): converted to real brightness, with highlights
  beyond the screen's peak rolled off smoothly instead of clipped. AVIF and HEIC need
  Microsoft's AV1 Video Extension and HEIF Image Extensions, as for any Windows app.
- **Not yet supported:** iPhone gain maps (Apple's own format, in HEIC and some JPEGs) and
  32-bit HDR merges. These show the standard version, with "Open in Photos" for HDR.

It follows the monitor: switching HDR on or off, or moving the window to another screen,
updates the preview straight away. Untested combinations of monitors and photo sources may
need tuning; the preview is computed at screen size, so it's quick even for large photos.

## Culling: sharpness, stacks, compare and flags

**Sharpness.** Every photo gets a sharpness score from 0 to 100 as it's indexed. Photos under
35 get an amber **SOFT** badge, and the details panel shows "Sharp · 82" or "Soft · 21". The
score is based on the sharpest parts of the photo, so a portrait with a blurred background
still counts as sharp. The eye button in the toolbar hides soft photos; search with `soft` or
`sharp` across the library. The threshold is a starting point and may need tuning on your photos.

**Stacks.** Photos taken within 1.5 seconds of each other on the same camera collapse into
one tile with a "×6" badge, showing the picked, best-rated or sharpest shot. "Show all" in the
details panel expands a stack; the stack button in the toolbar turns stacking off.

**Compare** (button in the toolbar, or press **C**). Select 2 or more photos, or one stack, to
see them side by side, 2 or 4 at a time. Zooming or panning one photo moves them all to the
same spot; **100%** shows real pixels for checking focus. Each photo shows its sharpness, and
the sharpest is tagged. Click a photo in the strip to put it in the highlighted pane.

**Delete** (the Delete key in the grid, or right-click / More > Move to Recycle Bin) sends the selected photos
to the Recycle Bin, with their RAW partner and .xmp sidecar, so they can be restored from there. Ten or more
photos ask first; fewer don't, like File Explorer. The next photo is selected afterwards, and the library
forgets the photos' rows, Smart and face data and collection entries. Not yet in the full-screen viewer or
photo windows.

**Pick and reject** (P, X, U to clear; also in compare). Rejected photos are dimmed. Search
with `picked` or `rejected`, and in compare, "Move rejects to Recycle Bin" clears them out.
Flags are kept in Ocula's library: there's no standard place for them in JPEG files.

## Getting photos in and out

**Memory card import.** When a camera card (anything with a DCIM folder) is inserted, a bar
offers to import it; the **Import** button in the title bar does the same at any time. Photos
are grouped by day, already-imported ones are hidden, and RAW+JPEG pairs stay together. Choose:
- an event name, where to save, and a folder pattern (default `{year}\{date} {event}`);
- renaming, with a pattern such as `{date}_{event}_{seq}` and a live example;
- tags to add, a backup copy to a second folder (e.g. `\\NAS\photo\Backups`), and ejecting
  the card when done.
Every copy is size-checked and keeps its original dates. Ocula remembers what it has imported
(by name, size and capture time), so the same photo isn't imported twice even after the card
is reused. Videos are copied too; playing them in Ocula comes in the next batch. Cameras that
connect over USB without showing up as a drive (MTP) aren't supported; use a card reader.

**Batch rename** (More > Rename…, or **F2**). Same pattern tokens as import, with a live preview
and checks for clashes. RAW+JPEG pairs and `.xmp` sidecars are renamed together, library data
(flags, sharpness, thumbnails) moves with them, and **Undo** reverses the last rename.

**Edit in…** opens the selected photos in Lightroom Classic, Photoshop, Capture One, Affinity
Photo, darktable, RawTherapee or GIMP, whichever are installed, and remembers your choice.
RAW-capable editors get the RAW file of a pair. "Add an editor…" adds any other program.
How each editor treats the files (open, or import into its catalogue) is up to that editor.

**Contact sheets** (More > Contact sheet). A PDF of the selection, or the whole folder, with
3 to 6 photos per row, file names, star ratings, a title, page numbers and your copyright line.

**Client proofing** (More > Client proofing gallery). Creates a folder with web-sized,
watermarked photos (location removed) and an `index.html`. Copy it to any web space, such as
the NAS's Web Station folder, and send the client the link. They tap a heart on favourites
(saved in their browser) and press **Send my favourites**, which emails you the file names.
Then More > **Import client favourites**, paste the list, and those photos are marked as
picked and tagged "client pick". There's no server or login involved, so anyone with the link
can view; use the web server's password protection for private shoots.

## Viewing and organising

**Video.** Videos appear alongside photos with their length on the tile, and play in the
preview panel and the full-screen viewer. Two playback engines work together:
- **Windows' own player** handles MP4, MOV, M4V and WMV (hardware-accelerated, nothing extra).
- **VLC's engine** handles MKV, WebM, AVI, FLV, 3GP, MPEG, VOB and anything else, including
  clips Windows can't open. It adds a control bar with seek, volume, **audio-track** and
  **subtitle** menus and playback speed. Subtitle files with the same name as the video
  (`movie.srt` next to `movie.mkv`) load automatically.
If Windows' player fails on a file, Ocula retries with VLC by itself. Thumbnails for formats
Windows can't preview are captured with VLC too, and tags and ratings go in an `.xmp` sidecar,
as for RAW files. Search with `video`. Export, compare and proofing galleries skip videos.
`.ts` files aren't listed, because they're far more often TypeScript than video.

**Licence note for VLC.** LibVLC is licensed under the LGPL 2.1. Ocula uses it as separate DLLs
(not compiled in), and the licence and credit are in the third-party notices. The LGPL expects
people to be able to swap in their own build of the library; a signed MSIX package makes that
awkward, and Ocula's own source licence doesn't allow modified builds. That's a recognised
grey area for LGPL libraries in packaged apps, so read the LGPL (or take advice) before
publishing. Playing only MP4, MOV and WMV with Windows' own player avoids the question: remove
the three LibVLC packages and `MediaPlayerView` falls back to it automatically.

**Slideshow** (More > Slideshow, or **F5**). Full screen on any monitor, with cross-fades, 3 to
15 seconds per photo, shuffle, and file names on request (I). Space pauses, arrows step, Esc
closes. With several photos selected, only those play.

**Second screen** (More > Show on second screen). A TV, projector or second monitor shows
whatever you select in Ocula, full screen, while you keep browsing on your own screen. Handy
for going through photos with a client.

**Colour management.** Photos with a wide-gamut colour profile (Display P3 from phones, Adobe
RGB and ProPhoto from cameras and editors) are converted properly instead of looking dull or
oversaturated; the preview shows a "P3" or "ADOBE RGB" badge. For the best results on a
wide-gamut monitor, also turn on **Automatic colour management** (Windows Settings > System >
Display). It can be turned off in Settings > Appearance.

**Timeline.** Every indexed photo, newest first, grouped by month (top of the sidebar).

**Collections.** Group photos without moving or copying them (More > Add to collection, or
"New collection…" in the sidebar). A photo can be in any number of collections, and they
survive renames. Right-click a collection to rename or delete it.

**Smart albums.** Saved searches that keep themselves up to date: search the library, then
"Save as smart album" under the title. For example `rating>=4 year:2026` or `tag:wedding picked`.

## Editing, full-screen viewing and layout (phase 5)

**Edit** (E, the pencil on the preview, or Edit in the full-screen viewer):
- **Crop**: drag the corners, edges or the whole box, with Free, Original, 1:1, 4:5, 3:2,
  16:9 and 9:16 shapes and rule-of-thirds guides.
- **Straighten** (±45°, zooming in just enough to hide the corners), **rotate** ([ and ]) and
  **flip**.
- **Adjust**: exposure, contrast, highlights, shadows, warmth and saturation, previewed live.
  Double-click a slider to reset it. **Before / after** (B) splits the photo down the middle.
- **Save as copy** (default) writes `name_edited.jpg` next to the original. **Save over
  original** (JPEG, PNG, TIFF) sends the original to the Recycle Bin first, so it can be
  restored. Saved photos keep their camera details, tags, rating and colour profile.
- RAW files are edited from Windows' RAW conversion (or the camera preview) and saved as a JPEG
  copy; for full RAW development, use Edit in… with Lightroom, darktable or similar.

**Full-screen viewer** (Enter, double-click, or the expand button on the preview). Zoom with
the mouse wheel, pinch, + and -, double-click, Fit (0) and 100% (1); arrows move between photos;
videos play. The top bar, arrows and filmstrip fade out after a moment and come back when the
mouse moves. Esc returns to the library on the same photo.

**Filmstrip** under the preview for quick stepping between photos.

**Panels**: the two buttons left of Import (or Ctrl+[ and Ctrl+]) hide the folders and details
panels for a bigger grid; Ocula remembers the layout.

**Helpful empty states**: a welcome screen, "Opening folder…", an empty folder, nothing found,
and "no photos match these filters" each explain what's happening, with a button for the
obvious next step.

## Duplicate and similar photos

The button next to the thumbnail-size slider opens **Find duplicates** for the current folder
(optionally including subfolders). Choose how close a match must be:

- **Exact copies**: the same photo, even if renamed, re-saved or converted.
- **Very similar** (default): also resized or re-compressed versions and light edits.
- **Similar**: also lightly cropped copies (up to roughly 8% trimmed), colour edits and near-identical
  burst shots. Heavier crops are not reliably found.

A "Rotated or mirrored" switch (on by default) also matches copies that were turned or flipped.

How matching works: each photo gets two kinds of hash (pHash and dHash, from CoenM.ImageHash) in all 8
orientations (4 rotations, with and without a mirror), plus a 16x16 grayscale picture, stored as 384 bytes
in the library (`images.fp`). A pair of photos matches only if (1) both hashes agree within the limits for
the chosen level and (2) the 16x16 pictures correlate closely enough (a brightness-independent pixel check,
tried in all 8 orientations when "Rotated or mirrored" is on). The limits are in `DuplicateFinder.Limits`
(differing bits of 64 for pHash / dHash, then minimum correlation): Exact 2 / 4 / 0.97, Very similar
6 / 12 / 0.93, Similar 20 / 30 / 0.75. The pixel check is what lets "Similar" use loose hash limits and
still find lightly cropped copies without lumping together different photos.
Groups are built around the best copy: every other photo must match that copy, so a chain of look-alikes
(A like B, B like C) no longer merges unrelated photos. The old single-hash column (`phash`) is unused;
the first scan after updating recomputes fingerprints.

Checked so far: a Python re-creation (the `imagehash` library plus a 16x16 correlation) on 60 synthetic
images (smooth random scenes, not real photos). Resized, re-compressed, brightened, rotated and mirrored
copies were found every time; at the "Similar" settings, copies cropped by 5% and 8% per side were found
every time and 15% crops never; and there were no false matches among 1,770 different pairs (the highest
correlation between different synthetic photos was 0.45). Real photos with similar layouts, such as two
sunsets or two shots of the same room, will correlate much higher than random scenes, so expect more
look-alikes at "Similar" and tune the limits on a real library. The C# is not compiled or run.

Each group shows every match with its size, folder, rating and RAW+JPEG badge. Ocula ticks
"Keep" on the best copy (highest rating, then RAW+JPEG pairs, then largest file); change any
tick before pressing **Move to Recycle Bin**. Pairs and their `.xmp` sidecars move together,
and every group must keep at least one photo. Everything can be restored from the Recycle Bin.

Fingerprints come from the cached thumbnails and are saved in the library, so RAW files are
checked quickly and repeat scans are nearly instant. Online-only OneDrive files are skipped.

### Where tags, ratings and labels are saved

| File type | Tags | Rating | Colour label |
|---|---|---|---|
| JPEG, TIFF, HEIC | inside the file | inside the file | Ocula library only* |
| RAW (CR2 etc.), PNG, WebP… | `.xmp` sidecar | `.xmp` sidecar | `.xmp` sidecar |
| RAW+JPEG pair | both | both | `.xmp` sidecar |

*Windows has no metadata field for colour labels in JPEGs. Writing raw XMP into JPEGs with
ExifTool would fix this and is a possible later addition.

Test on copies of your photos first.

## Still to build

**Roadmap at a glance.** Nothing here is marked done unless it has been tested on a PC.

| Item | Status |
|---|---|
| Photo windows with snap spots | Tested on one 3440x1440 monitor; second monitor, mixed scaling and other taskbar positions untested. Default is 6 spots (decided) |
| Person card and lookup | Not built |
| Search by person details | Not built |
| Screenshot text search | Built and tested: FTS5 search, Ocula's text switch, "Remove saved text" button, untrusted-text handling, and Capta's own switch (on its own branch, `claude/capta-text-switch`, not yet merged). Checked with real Capta PNGs for Window captures only |
| Camera firmware (detect only) | Research tool written (`tools/CameraProbe`), not yet run on a real camera; no UI. See `docs/CAMERA-FIRMWARE.md` |
| Porta integration | Not built; blocked until the `stirling-shared` source is available |
| Backup to Google Photos and iCloud (Phase 7) | Not built (planned) |
| Duplicate finder extras | Not built |
| Web download: forum threads (Forum Scraper) | Code added (f311a33); run only on hand-written XenForo, phpBB and Discourse pages in headless Edge, and on fake image-host responses. No real forum or image-host page tried. Four known problems are listed under Phase 6. Reading a thread's other pages (`PageUrls`) is designed, not built (`docs/WEB-THREAD-PAGES.md`) |

**Phase 4 – culling, viewing, organising and sharing**

_Culling_ (done: compare view, blur detection, burst stacks, pick/reject flags; see above)
- (done) Memory card import, batch rename, "Edit in…", contact sheets, client proofing; see above.

_Viewing and organising_ (done: video, slideshow and second screen, colour management,
timeline, collections and smart albums; see above)

_Still to come_
- **People (face grouping, you name the faces)**: on-device and opt-in.
  - Faces are found with YuNet and matched with SFace (both from OpenCV's model collection),
    run through ONNX Runtime (MIT). Check the models' licences and training-data provenance
    before release.
  - Similar faces are grouped; you name a group once and Ocula offers that name as a tag on
    every matching photo, including in the web downloader's "Suggested tags".
  - **Person card and lookup (not built).** Optional "Look up on Wikidata" when you name someone: Ocula lists matches with a short
    description (many people share a name) and you pick one. The person card (full name including
    middle names, from Wikidata's "birth name" when it has one; date and place of
    birth, occupation, current spouse, number of children, height and languages spoken when Wikidata has them) comes from Wikidata (CC0), is kept in the local library only, and only the
    name is sent. Only the name is written into photos, as a tag. Never automatic, and not
    offered for people you haven't chosen to look up.
    Spouse is the one with no end date on Wikidata, shown with the retrieval date and an "can be
    out of date" note.
    Former spouses are the P26 entries that have an end date, and "former partners" are Wikidata's
    unmarried-partner entries (P451, which is not gender specific, so the label is "partners"). Both
    are only what Wikidata lists, with years when it has them, and are often incomplete.
  - Lookup order: Wikidata first. Any field it leaves empty (height and former partners most
    often) is filled from DBpedia's public endpoint (CC BY-SA, no key). For musicians,
    MusicBrainz (CC0, needs a User-Agent, about one request a second) can add more. Anything still
    empty stays blank. Never guessed.
  - The card doesn't label each field with its source, to keep it uncluttered. One line under the
    card credits "Wikidata and DBpedia (CC BY-SA)" with the retrieval date (the credit DBpedia
    requires), and Ocula stores the source per field internally. DBpedia is itself derived from
    Wikipedia, so it fills gaps and is not an independent second opinion. When this is built it
    needs two new entries in both `THIRD-PARTY-NOTICES.md` and `AppInfo.Notices`: DBpedia
    (CC BY-SA) and MusicBrainz (CC0). Wikidata is already listed there.
  - Skipped on purpose: TMDB (needs an API key that can't be hidden in a distributed app, and
    non-commercial terms), IMDb datasets (non-commercial, little data), Google Knowledge Graph
    (terms don't allow storing the results).
  - **Search by person details (not built).** Once a card is saved, the normal search box understands things
    like `people taller than 5'3"`, `born before 1990`, `over 40`, `married`, `has children`,
    `actress`, `born in Texas` or `speaks Spanish`, and they can be combined. Ocula shows how it read the query
    ("Understood as" chips you can remove) and returns photos of the matching people. It only
    searches saved cards, runs on this PC, and says what it left out ("1 person has no height on
    record, 4 named groups haven't been looked up"). It never guesses for people with no card.
    The cards would be stored as fields (height in cm, birth date, place, occupation, spouse
    status, child count), not just text, so these are plain database filters rather than AI. Children appear as a count only, never by name.
  - The Wikidata picker lists people only (items that are instances of "human"); given names,
    disambiguation pages and other non-person entries are filtered out. The name tags it offers
    are chips you choose from, for example "Shakira" (ticked) and her full name (not ticked),
    and only the ticked ones are written into the photos' tags.
  - No database of named people is shipped, so Ocula only knows names you give it. It does not
    identify celebrities or strangers.
  - Off by default, nothing leaves the PC, a one-click "Delete all face data" button, and face
    data stays in the local library (never written into photo files). Names are written as
    tags only when you choose.
- **Smart recognition (what's in the photo)**: on-device and opt-in.
  - Uses OpenAI's CLIP (MIT licence) via ONNX Runtime to suggest scene and object tags such as
    beach, sunset, dog, car, snow, and to power natural-language search ("sunset over water").
  - Embeddings are stored in the local library, computed in the background after indexing.
  - Suggested tags are shown for approval, never added automatically.
  - Model download of roughly 100-350 MB, fetched on first use, with a size-versus-accuracy
    choice in Settings.
  - Shares the ONNX Runtime setup with People, so build them together.

**Phase 6 – download from the web** (code added; never run on a real forum or image-host page. The grab script, the resolver and
the tag suggester have been run only on hand-written pages and fake responses, see "Checked so far")
- Toolbar > Web opens `WebDownloadWindow`: an address bar and a WebView2 browser pane (so sign-ins and
  script-built pages work). "Grab images on this page" runs `PageScanner.GrabScript` in the page and
  lists the images (img, srcset, lazy-load attributes, plain links to image files). Small pictures are
  skipped: under 60 px inside a forum post, under 120 px elsewhere. Anything inside a header, nav, aside or
  footer, and avatars, signatures, logos and smilies, is skipped too.
- Forum pages (Forum Scraper commit, f311a33): the script knows the post containers of XenForo, vBulletin, phpBB and Discourse
  (`.message-body`, `.bbWrapper`, `.postbody`, `.cooked` and so on). When a page has at least two pictures in posts, only those are
  listed (`WebImage.InPost`); with one or none, the page's other pictures are listed too. It also lists the other pages of the thread
  that the page's own page-number links point to (`PageSnapshot.PageUrls`, at most 40), but **nothing uses them**: see
  `docs/WEB-THREAD-PAGES.md` for a design, and for the wording the "one link" rule would need.
- Thumbnail > full size: an image wrapped in a link to an image file uses that file; a link to another
  page is opened once (`PageScanner.ResolveAsync`) and the full picture is chosen from the declared
  main image (`og:image`), then large `<img>` tags, by file size (under about 30 KB is treated as an icon
  or ad). Rules for four named image hosts (ImageTwist/ImageHaha, ImageBam, Pixhost, TurboImageHost) read their
  landing pages first. Nothing deeper than one link, and no clicking through ad screens, countdowns, forms,
  paywalls or logins. Cookies from the page the user is signed in to are only sent back to that same site.
  Images where no larger version is found show "Couldn't find the full-size version" and aren't selectable.
- Suggested tags: `TagSuggester` reads the title, headings, breadcrumbs, keywords, alt text, captions and
  file names, and proposes repeated names and topics as chips. Hover shows why. Text only.
- Download: `WebDownloader` fetches three at a time into `<Save to>\<page title>`, keeps the real file name,
  skips pictures already in the folder (same bytes), and reports failures. Then ticked tags (and, if on,
  `source: site` and `page: title`) are written into the files with `MainWindow.TagDownloadsAsync`,
  and the folder is opened in the library.
- Files: `WebImage.cs`, `PageScanner.cs`, `TagSuggester.cs`, `WebDownloader.cs`, `WebDownloadWindow.xaml(.cs)`,
  `MainWindow.Web.cs`.
- Checked so far (October 2026): the real grab script was run in headless Edge on **hand-written** XenForo, phpBB and Discourse pages
  (not saved from real sites) and on two plain pages (`tests/Ocula.Tests/ForumGrabTests.cs`, pages in `TestData/forums`). On those it kept
  the post pictures, dropped avatars, signature banners, logos, smilies and quoted posts, kept each thumbnail's link, and found the other
  pages of a XenForo and a phpBB thread. The resolver was run against fake responses written to look like each host's landing page
  (`PageScannerTests.cs`); `WebImage` and `TagSuggester` have unit tests. **Never tried: a real forum, a real image host (ImageTwist,
  ImageBam, Pixhost, TurboImageHost), the Web window itself, the downloader, or tags landing in files.** The host rules rest on what the
  commit's author remembered of those sites' pages, not on pages seen here.
- Known problems found in review of f311a33 (not fixed; two have skipped tests that say what should happen):
  - **Pixhost and ImageTwist landing addresses that end in `.jpg` are never opened.** `ResolveOneAsync` takes any address ending in an
    image extension as the picture itself, before the host rules, and Pixhost (`pixhost.to/show/NNNN/ID_name.jpg`) and ImageTwist
    (`imagetwist.com/CODE/name.jpg`) landing addresses look like that. The "full-size" address is then the web page. (`TryHostDirect` can't
    help: it only acts on addresses the first rule already took.)
  - **An `og:image` that is an image file is accepted without the 30 KB size check**, on any site, so a logo or small preview can be taken
    as the full-size picture.
  - **Pictures inside `<header>`, `<nav>`, `<aside>` or `<footer>` are dropped on every page, not just forums**: a news or blog article's
    lead picture inside its `<header>` is no longer offered (the previous script offered it).
  - **A thumbnail that failed to load and has alt text is dropped as an icon** (Chrome reports the size of its alt text). phpBB's
    pictures have `alt="Image"`, so a thread whose thumbnails can't load (a host that blocks hotlinking) gives no pictures. Older than the
    commit, but it bites forum pages.
  - Smaller: the first ImageTwist pattern's `id` before `src` branch captures nothing (the later patterns still find the picture);
    host rules match on a substring of the host name; `WebImage.FileName` keeps `%20` and similar, so tags from file names with encoded spaces
    don't match; the forum word list in `TagSuggester` removes city and month words everywhere ("June Carter Cash" becomes "Carter Cash").
  - `grok/forum-web-download/` (untracked, byte-identical copies of the three committed files plus an `APPLY.md`) is **not excluded from
    `Ocula.csproj`**, so with it in the repository folder the app fails to build (duplicate types). Move it out or delete it.
- First things to check on a PC: a plain gallery page; a page whose thumbnails link to full-size images;
  a real forum thread on each of XenForo, phpBB and Discourse (post pictures only, names from the title, host landing pages
  resolve to the full picture and not to a web page); a page that needs sign-in (sign in in the pane, then grab); that tags land
  in the files; and what `TagSuggester` proposes on a page with a name in the title.
- Named image-host rules now exist (ImageTwist/ImageHaha, ImageBam, Pixhost, TurboImageHost). This replaces the earlier line "Not planned:
  site-specific scrapers or rules for named image hosts"; the owner should confirm that is wanted. Still not planned: DRM, paywall, ad-screen
  or login circumvention. Later: reading a thread's other pages (designed, `docs/WEB-THREAD-PAGES.md`), resume, scheduling.
- (Original plan, kept for reference)
- "Download from web…" next to Import from card. Browse to a page in a built-in WebView2 pane
  (so logins and JavaScript-heavy pages work) and choose "Grab images on this page".
- Finds `<img>`, `srcset`, `og:image` and links to larger images; size filter, select all,
  thumbnail grid to pick from.
- Parallel downloads with progress, rename pattern and destination folder as in card import;
  duplicates skipped using the existing duplicate finder.
- Writes the source URL and page title into each file's metadata (tags / XMP) so downloads stay
  searchable.
- Suggested tags before downloading: Ocula reads the page's own text (title, headings, captions,
  image alt text and file names, breadcrumbs, `og:` / schema.org data, forum thread titles) and
  proposes repeated names and topics as tag chips, for example a person's name that appears in
  the thread title and most captions. Nothing is added until you tick it; ticked tags are written
  to every downloaded photo. Text only: no face recognition, nothing leaves the PC.
- Single page first. Following "next page" links, resume and scheduling come later.
- Not planned: site-specific scrapers, DRM, paywall or login circumvention.
- Before release: update the Store listing's IARC answers (web access), the privacy policy
  (Ocula would contact sites the user chooses) and add a short "only download what you have the
  right to use" note in the dialog.

**People** (checked on a PC in October 2026; see docs/PEOPLE-EVALUATION.md. The Wikidata person card is not built)
- Settings > People: switch on and Ocula downloads YuNet and SFace (about 39 MB) from Hugging Face,
  pinned to one revision each and rejected unless their size and SHA-256 match (`FaceModels.cs`).
- Faces are found in the background (idle only), grouped when similarity is above 0.45, and
  stored in the library (`people`, `faces`, `face_scans` tables). Face data is never written to photos.
- Sidebar > People…: name a group (Enter or Save name), optionally add the name as a tag to all
  its photos, show the photos, hide the group, "Not this person" on a face (right-click), or
  delete all face data. Naming a group with an existing name merges the two.
- Files: `FaceModels.cs`, `FaceService.cs` (runs the models), `YuNetDecoder.cs`, `FaceAlignment.cs` and
  `FaceGrouping.cs` (the arithmetic, unit-tested), `FaceIndexer.cs`, `MainWindow.People.cs`,
  `PeopleWindow.xaml(.cs)`. `tools/FaceEval` measures detection and grouping on real photos.
- Checked: the downloads and hashes; the models' real tensor names and shapes; YuNet's decoding against
  OpenCV's source; detection on 20 photos (22 of 30 visible faces kept, no wrong boxes); grouping thresholds
  0.35 to 0.55 (0.45 stays: best precision for the same F1); naming, reopening, hiding and deleting in the
  People window. Fixed on the way: SFace was fed BGR instead of RGB; Delete all face data left People on; a name
  being typed was wiped by the 3-second refresh while scanning; the People and Smart start timers could be
  collected so nothing started after a restart.
- Known limits: groups follow pose and sunglasses as much as identity, so each person ends up in several groups
  and crowd photos make mixed ones. Hidden groups can't be shown again from the window.
- Still unchecked: what "Add the name as a tag" writes into files, and
  SFace's licence and training data (unclear; see docs/KNOWN-ISSUES.md). Resolve the last before release.

**Smart recognition** (code added; measured with SmartEval, not yet checked in the app)
- Settings > Smart recognition: switch on, choose Compact (about 155 MB) or Accurate (about
  610 MB), and Ocula downloads OpenAI's CLIP (ONNX export by Xenova, MIT) from Hugging Face.
  The files are pinned to one revision and rejected unless their size and SHA-256 match
  (`SmartModels.cs`).
- Analysis runs in the background, one photo at a time, only while you're not using the
  computer, and results are stored in the library (`embeddings` table). Each photo is analysed
  once per model and encoding version (`SmartModels.EncodingVersion`; bump it when a change
  alters the vectors, and every photo is analysed again). OneDrive online-only files and videos
  are skipped. It runs on the CPU; GPU acceleration isn't built (see "Not yet").
- Search: type `? sunset over water` in the search box. Ocula asks CLIP about "a photo of
  {query}" and shows only photos clearly closer to that than to neutral prompts such as "a photo"
  (`SmartScoring.SearchMargin`, 0.025), best first. A query that matches nothing shows nothing.
- Suggested tags: select a photo and tick the chips under Tags, or "Add all". Nothing is added
  until you click. The best label is offered from 5% confidence and further ones from 10%;
  near-duplicates in `SmartLabels.Groups` (portrait / posed portrait and so on) share their
  confidence and show once. The labels are in `Services/SmartLabels.cs`.
- Files: `ClipTokenizer.cs`, `ClipModel.cs` (models, preprocessing), `SmartScoring.cs` (search
  and tag rules), `SmartModels.cs` (download), `SmartService.cs`, `SmartIndexer.cs`
  (background), `SmartLabels.cs`, `MainWindow.Smart.cs`, plus the `embeddings` table in
  `IndexService.cs`.
- Checked so far: the tokenizer and image preprocessing match the reference implementation
  (`tests/Ocula.Tests`), and both tiers' input and output names are confirmed. Search and tag
  quality are measured on 178 labelled photos with `tools/SmartEval`: search F1 went from 0.60
  to 0.94; details in `docs/SMART-EVALUATION.md`. Rerun SmartEval after changing a threshold or
  a label.
- Still to check on a PC: after updating, Ocula analyses the library again once; then confirm in
  the app that `? dog` and `? sunset over water` and the tag chips match what SmartEval reports.
- Not yet: GPU acceleration (Windows ML, which picks a GPU or NPU; DirectML is no longer
  developed), "find similar photos" from a selected photo, and Smart search as its own sidebar
  view (it is a search-box prefix for now).

**Photo windows with snap spots** (tested on one 3440x1440 monitor, October 2026; see the last bullet for what isn't)
- A single click shows the photo in the main window, as before. A double-click opens it in its
  own photo window (Settings > Photo windows turns this off; double-click is then full screen).
  "Open in separate windows" (right-click on the thumbnails, or More) opens the selected photos.
- Each window takes the next free spot on its monitor, left to right. With the default 6 columns
  each window is a sixth of the monitor's width (573 px on a 3440 px ultrawide); spot 1 is the far
  left, 6 the far right. Its title bar has a layout chip (6 columns, 1 large + 5, 3 + 3, Cascade
  and so on) and numbered spot buttons; Alt+1 to Alt+6
  do the same, Alt+0 centres the window, and the display button sends it to the next monitor.
  Narrow windows show one "Spot 3 of 6" menu instead. Choosing a taken spot swaps the two windows.
- Spots are fixed: a window keeps its spot's size however many are open, and closing one leaves its
  spot free for the next photo. Moving or resizing a window by hand takes it out of its spot.
- Settings > Photo windows: number of spots (2 to 6, default 6), layout, what happens when every
  spot is taken (replace the oldest, ask, or open on top), open in the spot used last, and link
  zoom and pan across windows.
- Files: `Services/PhotoWindowLayout.cs` (spot rectangles, next free spot; unit-tested
  in `tests/Ocula.Tests/PhotoWindowLayoutTests.cs`), `Services/WindowFrame.cs` (hides the
  invisible resize borders), `MainWindow.PhotoWindows.cs`, and the photo-window mode of
  `ViewerWindow`. Design: `docs/design-canvas/Windows1.dc.html` to `Windows4.dc.html` and
  `docs/mockups/Ocula-windows-*.png`.
- Spots, design versus build: the design boards and mock-ups show **5** spots, with the layouts
  "5 columns", "1 large + 4", "3 + 2", "2 + 3" and "Cascade". The built and agreed default is **6 fixed
  spots** (the owner confirmed 6; the Windows mock-ups are superseded on that point). The layouts are the
  ones the code has, named from the spot count: with 6, "6 columns", "1 large + 5", "3 + 3" and "Cascade".
- A mouse click waits 200 ms before the side panel shows its photo, so a double-click doesn't
  flash it first (keyboard and Ctrl/Shift selections don't wait; Ctrl or Shift + double-click opens nothing).
  The spot rectangles are unit-tested for 2 to 6 spots on 1920x1080 and 3440x1440 monitors with the taskbar at
  the bottom or on the left.
- **Tested on the owner's PC** (one 3440x1440 monitor at 100%, taskbar at the bottom, real mouse and keyboard):
  - Six windows land in spots 1 to 6 at x = 0, 574, 1148, 1721, 2294, 2867, each 573 or 574 wide and the full
    1392 px work-area height, with no gaps or overlaps. With 4 spots each is 860 wide.
  - Double-click opens one; "Open in separate windows" opens a Shift-selected range, left to right in order;
    five photos with four spots open four and say so; with every spot taken, "Replace the oldest" closes the
    oldest and puts the new photo in its spot; closing a window frees its spot for the next photo.
  - The spot buttons and Alt+1 to Alt+6 swap two windows; Alt+0 centres a window (two thirds size);
    dragging a window by its title bar takes it out of its spot, and only that does.
  - Layouts: 6 columns, 1 large + 5 (left half, then two columns, last row spanning), 3 + 3 (1147 x 696) and
    Cascade (2064 x 835, stepped 58 px) all fit inside the work area. Switching layout re-arranges every window.
  - Settings > Photo windows: spots 2 to 6, layout names follow the spot count, link zoom and pan (zooming one
    window zoomed the others to the same view).
  - Fixed while testing: a double-click on a thumbnail that wasn't selected yet opened the window behind the main
    window (the main window took focus back about 300 ms later); the photo sat at the top of tall windows
    instead of the middle; with 2, 4 or 6 spots the layout list had "3 + 3" twice.
  - **Not tested:** a second monitor (the display button, "next display", moving between monitors), monitors
    with different scaling, the taskbar anywhere but the bottom (unit tests only), "Ask" and "Open on top" for a
    full set of spots, "Open in the spot used last", and panning (only zoom was linked and checked).

**Phase 7 – back up to Google Photos and iCloud Photos** (planned, not built)
- **Google Photos (upload only):** sign in once, choose folders or albums, and Ocula uploads new
  photos and videos, skipping ones already sent. Uses the Google Photos Library API's
  `appendonly` scope. Since 31 March 2025 Google no longer lets apps read a user's whole library,
  so Ocula can upload and make albums but cannot browse, list or download what is already in
  Google Photos, and cannot manage sharing.
  - Needs a free Google Cloud project and OAuth client for Ocula, plus Google's app review for
    the Photos scope. Check the current quota and review requirements before committing.
- **iCloud Photos:** Apple has no public API, so Ocula hands photos to the iCloud for Windows
  app by copying them into its "Uploads" folder, which iCloud then uploads. Needs iCloud for
  Windows installed and signed in; Ocula cannot see upload progress or what is in iCloud.
- **Also:** OneDrive and NAS folders are plain destinations, and the import screen already
  backs up to a NAS while importing.
- Settings: choose what to back up (folders, ratings or labels, RAW or JPEG only), a log of what
  was sent, and a clear "disconnect and forget my sign-in" button.
- Not planned: syncing edits back from Google or Apple, or reading either library.

**Photoshop files (.psd, .psb)** (code added, untested)
- Listed with thumbnails and previews. Ocula reads the flattened composite that Photoshop saves
  inside the file ("Maximize Compatibility"), so large layered files open quickly. Files saved
  without it may preview slightly differently (blend modes and adjustment layers are approximated).
- CMYK files are converted to sRGB for display; transparent areas show on white in thumbnails.
- Read-only: tags, ratings and labels go in an `.xmp` sidecar, so the PSD is never modified.
  Export and Edit in… work from the composite.
- Not included: browsing layers, and other layered formats (GIMP .xcf, Krita .kra, Affinity).

**Screenshot text search** (built and tested on a PC with generated Capta-format PNGs; see "Not tested")
- **Source:** Ocula reads the `Capta` tEXt chunk Capta writes into saved PNGs (one ASCII JSON object; Capta's
  `docs/ocula-integration.md` lists `mode`, `sourceApp`, `windowTitle`, `capturedAt` and `ocrText`). Capta's
  format was checked again on 16 October 2026: still that one chunk, no `Stirling.*` chunks, so only `Capta` is read.
  Capta OCRs the image *after* it has covered personal info, so redacted text is not in `ocrText` (read from
  Capta's `AutoRedact.cs` and `CaptureMetadata.cs`; not run).
- **Search:** SQLite FTS5 (`capture_fts`, columns `details` = mode, app and window title, and `body` = the text),
  kept in step with the library by triggers in `Services/CaptureTextIndex.cs`. Plain words match whole words or
  the start of a word, accents ignored ("reservation" finds "réservation", "netw" finds "Network"), alongside the
  old substring match on file name, tags and camera. This replaces the old substring match on the text, so a
  word's *middle* ("work" in "network") no longer finds a screenshot. `text:word` looks only in the image text;
  `app:`, `capture` and `hastext` still work. Each typed word is wrapped in quotes and its quotes doubled, so no
  FTS5 operator (AND, OR, NOT, NEAR, `*`, `:`, `^`, brackets) can be typed; LIKE wildcards typed by the user match
  themselves.
- **Untrusted text:** Ocula never uses it as markup, a file name, a path or a command. When read, each field is
  cleaned and capped once (`CaptureTextIndex.CleanLine/CleanText`): control characters and right-to-left overrides
  removed, one line for app and title (260 characters), the text 50,000 characters, and a non-Capta `tEXt` chunk is
  skipped without being read (a Capta chunk over 1 MB is ignored). It is shown only in `TextBlock`s as plain text.
  `tests/Ocula.Tests/CaptureTextIndexTests.cs` and `CaptaIntegrationTests.cs` cover hostile files and queries.
- **The switch (decided):** Ocula doesn't read Capta's settings (they sit in another app's package). Capta's own
  switch, **Save text found in captures** (Capta `Settings.SaveTextInCaptures`, on by default, branch
  `claude/capta-text-switch`, built October 2026), stops Capta running OCR for saved files: a capture saved with it
  off simply has no `ocrText` and Ocula has nothing to index; PNGs already saved keep theirs. Ocula's own switch,
  **Settings > Privacy > Search the text in screenshots** (default on), covers what Ocula holds: off = the text isn't
  read, stored or searched, and the stored copy is removed; on again = the PNGs are read again in the background.
  The app and window title are not part of the switch.
- **"Remove saved text from my captures" (decided):** clears only Ocula's library copy and turns the switch off so it
  isn't read back at the next scan. The PNG files are never changed, and the text Capta put in them stays there.
  (Rewriting PNGs was left out on purpose: it would change files the user didn't ask Ocula to touch.)
- **Details panel:** a "Capta screenshot" card with how it was taken and a "Text in this screenshot" box.
- **Tested on this PC** (October 2026, real Ocula, real mouse, generated PNGs in Capta's format because the
  earlier real captures were no longer on disk): search by text, by window title, by source app, by prefix, with
  accents and with punctuation ("Wi-Fi"); a hostile PNG (markup and paths in the text, FTS5 operators, a
  right-to-left override and a line break in the title, a 5,000-character app name, 200,000 characters of text)
  was cleaned, capped and searchable, and displayed as literal text; 12 hostile search strings ran as plain
  searches; the switch off and the Remove button cleared the text (app and title still found), a re-read with the
  switch off did not bring it back, and switching on read it back in. Library migration on the real 30,000-photo
  library added the FTS5 table without trouble (backup: `ocula-library-backup-20261016`, made before it).
- **Checked with a real Capta** (dev build with the switch, Window capture of a Notepad window, sent to Ocula by
  Capta's own "Send to Ocula"): with Capta's switch on, the PNG's `Capta` chunk had `ocrText` and Ocula held 169
  characters of it; with it off, the chunk had no `ocrText` (223 bytes against 447) and Ocula held none, but still
  found the capture by app and window title.
- **Not tested:** other Capta modes (Region, Scrolling, Freeform) and HDR-sourced captures beyond that one, text in
  non-Latin scripts (FTS5's `unicode61` tokenizer treats Chinese or Japanese runs as one word, so short searches
  won't match inside them), and performance with tens of thousands of screenshots (delete and rename scan the FTS
  table by path).
- **Found while testing:** `RefreshAsync` crashed Ocula in one state (`docs/KNOWN-ISSUES.md`); Ocula now logs
  unhandled exceptions to `ocula.log`.

**Camera firmware (detect only)** (planned; research first, nothing built)
- Scope: when a camera is connected over USB and shows up as a portable device, Ocula shows its **make,
  model and current firmware version**, and a button that **opens the maker's official firmware page for that
  model in the browser**.
- What it will not do: download, install or flash firmware; contact a maker's site by itself (the browser
  opens only when the user presses the button); say whether a newer version exists.
- **Opt-in** (off until the user turns it on in Settings), and **no data leaves the PC**: reading the
  camera is local, and the only network use is the user's own click on the button.
- Ocula can't read MTP/PTP cameras today (card import needs a card reader), so this is new device access.
  Feasibility on this PC, how each camera mode behaves, and the maker firmware pages are in
  `docs/CAMERA-FIRMWARE.md`, from `tools/CameraProbe`. Don't build any UI until that research says it is
  worth building.

**Porta integration** (not built; blocked)
- See `docs/PORTA-INTEGRATION.md`. Don't build it until the `stirling-shared` source is
  available in this repository.

**Duplicate finder extras** (not built)
- A "Thorough" matching level beyond Similar; what it checks is still to be decided.
- A "looks alike" mode that compares CLIP embeddings (the ones Smart recognition already stores
  per photo) instead of pixel hashes, so the same scene shot differently can match. Needs Smart
  recognition turned on.
- "Find similar photos" from a selected photo (also listed under Smart recognition, "Not yet").

**Later**
- **Own duplicate hashing, then ImageSharp 4.x.** Replace `CoenM.ImageSharp.ImageHash` with
  Ocula's own pHash and dHash (`DuplicateFinder`), keeping the stored fingerprint format or
  recomputing it once, then move ImageSharp from 2.1.13 to 4.1.2 or later to clear the five
  advisories in `docs/KNOWN-ISSUES.md`. Check the Six Labors Split License (3.0 and later) first.
- Phase 6 extras: reading the other pages of a forum thread (designed, `docs/WEB-THREAD-PAGES.md`), resumable downloads.
- iPhone HDR gain maps and 32-bit HDR merges in the HDR preview.