# Chrome extension hand-off (`ocula:webdownload`)

The owner's personal Chrome extension (`extensions/chrome-justjared/`) reads a Just Jared article
in the browser and launches Ocula with a link. Ocula's web downloader does the rest.

**Status (October 2026):** the code is added (`OculaLink`, `MainWindow.OpenWebDownloadFromLink`,
`WebDownloadWindow.OpenFromLink`). `OculaLink` was compiled and its parsing checked on Linux with a
scratch project; the WinUI parts (the window, the XAML note) were written without a Windows machine and
have **not been compiled or run**. `tests/Ocula.Tests/WebDownloadLinkTests.cs` has not been run with
xunit; its Windows-path cases need the PC. Never tried on a real Just Jared page.

## The link

```
ocula:webdownload?url=<article address>&media=<text>&folder=<save folder>&tags=<tag;tag;...>
```

Values are percent-encoded with `encodeURIComponent` (so a space is `%20`, which `Uri.UnescapeDataString`
reads correctly; a `+` is not a space). Everything except `url` is optional.

| Key | Meaning | Rules on arrival (`OculaLink`) |
| --- | --- | --- |
| `url` | The article to open | Required. `http` or `https`, with a host and no embedded sign-in. Anything else (`file:`, `javascript:`, `ocula:`, nothing) drops the whole link. |
| `media` | Only pictures whose address contains this text start ticked | At most 80 characters, no white space or control characters, otherwise ignored. The extension sends `jj-justjared-media`. |
| `folder` | Suggested "Save to" folder, the **final** folder | Used only if fully qualified, free of characters Windows rejects in a name, **and** inside a library folder (`DownloadFolderOrNull`, which uses `IsInside` on the normalised path, so `..` can't climb out). Otherwise ignored, a note says so, and the window keeps its own default. |
| `tags` | Tags to tick, separated by `;` | Control characters become spaces, white space collapses, empty and duplicate (any case) items are dropped, an item over 60 characters is dropped (not cut), at most 20 are kept. |

Any web page can launch an `ocula:` link, so every value is untrusted. That is why the folder is
restricted to the library and why **nothing is ever downloaded without the user pressing the
existing Download button**.

## What Ocula does with it

1. `OculaLink.Parse` reads the verb `webdownload` into `Url`, `Tags` and `Media` (the record's other
   fields are unchanged). It is not a background verb: it takes the focus.
2. `MainWindow.HandleLinkAsync` hands it to `OpenWebDownloadFromLink`, which reuses an open Web
   download window or opens one, and calls `WebDownloadWindow.OpenFromLink`.
3. The window navigates its WebView2 to `url` (waiting for the pane to start if it hasn't), and when
   the page has loaded waits 1.5 seconds and runs the normal "Grab images on this page" step. All the
   existing rules stay: forum gating, size minimums, thumbnail-to-full-size resolution, one link deep,
   same-site cookies only, no ad-screen, form, paywall or login circumvention. If the site shows a
   challenge page, the user deals with it in the pane; a failed load shows a message and grabs nothing.
4. The usual ticking applies, then `media` unticks every picture whose thumbnail, full-size or link
   address doesn't contain it (they stay listed). On a Just Jared page this leaves out sidebar and ad
   pictures, which a non-forum page would otherwise list and tick. "Select all" still ticks everything.
5. The link's tags are added as ticked chips, case kept ("From the browser extension" on hover). A
   suggested chip with the same label is just ticked. `TagSuggester` still proposes its own chips as today.
6. If `folder` was accepted, "Save to" holds it and the window is in **exact folder** mode: photos go
   straight into it, with no page-title folder added, and the note under the field says whether it
   already exists or will be created. That folder is not saved as the remembered "Save to" root. Pressing
   Browse, or typing a new address in the address bar, leaves exact mode.
7. A line in the picker reads "Opened from the browser extension", plus a sentence when the suggested
   folder was outside the library and ignored.
8. Downloading, tagging (`MainWindow.TagDownloadsAsync`) and opening the folder in the library are the
   existing code paths, unchanged.

## Not to do

- No body-focused tags, suggestions or defaults (CLAUDE.md rule). The extension suggests only names
  taken from the article's tag links and whatever the user types.
- No telemetry and no new network use: the page load and the downloads are what the user started.
- No registration changes: `Package.appxmanifest` already registers the `ocula:` scheme.

## Known gap

`TagSuggester` (existing code, not changed here) pre-ticks a chip when the word is in the page title or
on most pictures. On a page whose headline says "Pregnant …" it can offer, and pre-tick, "Pregnant".
That sits badly with the CLAUDE.md rule about body-focused labels, which so far covers only the
smart-recognition labels. Decide whether `TagSuggester` should drop such words.

## To check on the PC

1. `dotnet build -p:Platform=x64`, then the test project (`WebDownloadLinkTests`, including the Windows-path cases).
2. Load the extension (`extensions/chrome-justjared/README.md`), open a Just Jared article, press
   "Preview in Ocula", and confirm: the window opens on the article, the pictures are found and the
   right ones ticked, the tags are ticked, the folder note is right, and nothing downloads until Download.
3. Try a folder outside the library, a closed and an open Web window, a second link while a page is
   already grabbed, and an article whose pictures link to other pages.
