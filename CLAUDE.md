# Ocula — instructions for Claude Code

Project root (on the owner's PC): `Z:\Data\sites\ocula`. Treat this folder as the repo root and
reference it for everything. Owner: Stephen Stirling (USA, github.com/stephenstirling).

## What this is
Ocula is a free Windows 10 (1809+) / 11, **x64-only** photo browser. WinUI 3, C#, .NET 8,
Windows App SDK, SQLite library, WebView2, ImageSharp, CoenM.ImageHash, ONNX Runtime.
Distributed through the Microsoft Store (free signing) and GitHub. No paid certificates, no Azure.
Donations (Buy Me a Coffee) are hidden in Store builds (`AppInfo.DonationsInStoreBuild = false`).

## Status (October 2026)
The project builds and runs. `Ocula.csproj` (the app, single-project MSIX), `Ocula.slnx` and
`OculaShell\OculaShell.vcxproj` (C++ "Open in Ocula" folder menu) are at the repo root.
`Ocula.csproj-additions.xml` and `Package.appxmanifest-additions.xml` are already merged and are
kept only as notes. The code was first written without a Windows machine, so treat any feature
not listed as tested below as unverified.

**Build and run** (details in `BUILDING.md`):
- Visual Studio 2026 **Community** has the .NET/WinUI tools and the **Desktop development with
  C++** workload. Use it, not the separate "Visual Studio 18 Insiders" install (C++ only; leave
  it alone). Open `Ocula.slnx`, Debug | x64, F5. `Ocula.csproj` builds OculaShell itself, because
  OculaShell isn't in the solution (so `dotnet build` can use the same file).
- Everything from the command line: Community's
  `MSBuild.exe Ocula.slnx -restore -p:Configuration=Debug -p:Platform=x64`.
- Quick check: `dotnet build -p:Platform=x64` (builds the app but not the C++ DLL).
- `Directory.Build.props` sends all `bin`/`obj` output to `%USERPROFILE%\.stirling-build\ocula\`,
  because Windows can't register an MSIX package from the `Z:` network share.
- Command-line `devenv` builds log "NuGet package restore failed… could not be loaded" but
  succeed; Capta logs the same. It's harmless.
- Git refuses to run on the share ("dubious ownership") unless `safe.directory` allows it. Use
  `git -c safe.directory='%(prefix)///Plex/Plex/Data/sites/ocula' …` or ask the owner to add it
  globally; don't change global git config yourself.

**Repository:** `github.com/stephenstirling/ocula`, **private**, `origin`, branch `main`.
`AppInfo.GitHubRepository` points at it. While it's private, update checks fail (GitHub's public
API can't see it) and the in-app Source/Feedback/Privacy links stay hidden.

**Tested on the owner's PC:** launch from Start, the folder tree and background indexing
(including videos), the single-instance redirect, `ocula <folder>` from a terminal, screen-reader
names for tiles and sidebar entries, a Release MSIX build, and the Map page offline and online
(in headless Edge, not inside the app).

**Still untested:**
- F5 pressed in the Visual Studio IDE (only `devenv /Build` and `/Deploy` from the command line).
- "Open in Ocula" on folders in File Explorer, and "Open with" for image files.
- The Map window inside the app: points, popups, "Show in Ocula", and the offline message.
- Viewer, slideshow, second screen, compare, edit, export, import, rename, contact sheets,
  proofing galleries, collections, smart albums, and whether settings are saved.
- Video playback in either engine (VLC is only tested for length and thumbnails).
- HDR preview and colour management on real HDR and wide-gamut screens.
- Smart recognition inside the app (search and tag chips after the library is analysed again).
  The tokenizer, preprocessing, model names and downloads are checked against the reference, and
  search and tag quality are measured with tools/SmartEval (docs/SMART-EVALUATION.md).
- People: "Not this person" in the running app, and the "Add the name as a tag" option (it writes into
  files). Everything else was checked in October 2026 (docs/PEOPLE-EVALUATION.md). SFace's licence and
  training data are unresolved (docs/KNOWN-ISSUES.md): settle before release.
- Capta hand-off: ocula: links (import, edit, captures) and indexing Capta's PNG metadata.
- Photo windows (double-click a thumbnail): spot placement (6 fixed columns), swapping,
  Alt+number, next display, linked zoom, and mixed-DPI monitors. The layout maths is unit-tested.
- Duplicate finder limits on real photos.
- Web download on real pages.
- Delete / Move to Recycle Bin: the More-menu path was tested on copies (file in the Recycle Bin, grid,
  selection, library rows); the Delete key itself, the 10-photo confirmation and RAW pairs were not.
- Update checks and Store packaging with the Windows App Certification Kit.

## Read first
1. `BUILDING.md` — setup and build, architecture, phases, roadmap, first-run checks.
2. `docs/KNOWN-ISSUES.md` (ImageSharp 2.1.13 advisories), `README.md`, `RELEASING.md`,
   `PRIVACY.md`, `THIRD-PARTY-NOTICES.md`.
3. `docs/mockups/*.png` and `docs/design-canvas/*.dc.html` — the agreed UI designs (design only).

## Rules
- x64 only. Min Windows 10.0.17763.0.
- Privacy: everything local. No telemetry. Network only for what the user starts
  (updates, web download pane, optional person lookup, model download).
- Do not add body-focused smart labels (bikini, lingerie, pregnant, etc.). Not wanted.
- Web download: no ad-screen, paywall, form or login circumvention. Same-site cookies only.
- Keep the git author as `Stephen Stirling <8118368+stephenstirling@users.noreply.github.com>`.
- Do not invent facts in lookup mock data (Shakira/Hilary examples are placeholders).

## Sibling apps (separate repos, same owner)
Porta (Start menu replacement), Capta (screenshot tool). Shared code lives in `stirling-shared`.
Ocula must later serve `photos.search` and `photos.pinnables` over a local named pipe for Porta
(see `docs/PORTA-INTEGRATION.md`). Do not build that until `stirling-shared` is available here.
