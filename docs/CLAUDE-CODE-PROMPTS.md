# Prompts to paste into Claude Code, in order

Open a terminal in `Z:\Data\sites\ocula`, run `claude`, and paste one prompt at a time.
Finish and commit each before the next.

## Prompt 1 — create the project and get it compiling
Read CLAUDE.md, BUILDING.md and Ocula.csproj-additions.xml. This folder holds source files but no
project. Create a WinUI 3 (Windows App SDK, .NET 8, x64) project named Ocula here, keeping the
existing files in place (do not move them into a subfolder). Merge Ocula.csproj-additions.xml and
Package.appxmanifest-additions.xml. Run `dotnet build -p:Platform=x64` and fix every error. The
code has never been compiled, so expect many. Fix root causes, don't stub features out. Commit when it
builds. Report anything you had to change in behaviour.

## Prompt 2 — git and GitHub
Initialise git here if needed (`main` branch, author Stephen Stirling
<8118368+stephenstirling@users.noreply.github.com>), add a .gitignore for bin/obj/.vs and
Ocula-Assets.zip, commit. Do not push until I create the private repo stephenstirling/ocula and tell
you. Then add the remote, push, and set AppInfo.GitHubRepository = "stephenstirling/ocula".

## Prompt 3 — first run
Run the app. Check and fix: the library opens and indexes a test folder; thumbnails show; viewer,
slideshow, compare and edit open; settings save. Write findings to docs/FIRST-RUN.md.

## Prompt 4 — Smart recognition checks
Verify the CLIP tokenizer on "a photo of a cat", the ONNX input/output names for the image and
text models, and that a query like "? sunset over water" ranks sensibly on a real folder.

## Prompt 5 — People checks
Verify the YuNet decoding and SFace embeddings, and tune the grouping threshold (currently cosine
0.45) on real photos. Check the SFace licence and training-data provenance and record it in
THIRD-PARTY-NOTICES.md.

## Prompt 6 — Duplicate finder tuning
Run the duplicate finder on real folders at Exact, Very similar and Similar. Check false matches
against the limits in Services/DuplicateFinder.cs and adjust them if needed.

## Prompt 7 — Web download (Phase 6) on real pages
Test WebDownloadWindow with real gallery pages, including thumbnail-to-full-image resolution. Fix
PageScanner/WebDownloader issues. Keep the no-circumvention rules in CLAUDE.md.

## Prompt 8 — Release blockers
Paste the real licence texts (PolyForm Strict and third-party), fill the privacy policy date and
contact, re-answer the IARC questionnaire in store/listing.md (web access: Yes), and follow
RELEASING.md for a first MSIX build.

## Prompt 9 — Chrome extension hand-off (`ocula:webdownload`)
Read CLAUDE.md, docs/CHROME-EXTENSION.md, Services/OculaLink.cs, App.xaml.cs, MainWindow.Web.cs,
WebDownloadWindow.xaml(.cs), Services/PageScanner.cs and BUILDING.md (Phase 6). Implement the
ocula:webdownload link exactly as docs/CHROME-EXTENSION.md describes: parse it in OculaLink (Url,
Tags), handle it in HandleLinkAsync by opening WebDownloadWindow on the address, grabbing the page's
images, ticking them, pre-ticking the user's tags, and filling "Save to" only when the folder is
inside a library folder. Never start a download without the Download button. Keep every rule in
CLAUDE.md. Add the unit tests listed in the doc, build, run the tests and commit on a branch.

## Later
Person-card lookup (Wikidata -> DBpedia -> MusicBrainz) and search by person details; Porta
integration (docs/PORTA-INTEGRATION.md, needs stirling-shared); backup to Google Photos/iCloud.
