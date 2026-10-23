# Chrome extension hand-off (`ocula:webdownload`)

The owner's personal Chrome extension (`extensions/chrome-justjared/`) reads a Just Jared article
in the browser and launches Ocula with a link. Ocula's existing web downloader does the rest. This
file is the contract for the Ocula side. **Not built yet** (see the prompt at the end).

## The link

```
ocula:webdownload?url=<article address>&folder=<save folder>&tags=<tag;tag;...>
```

Values are percent-encoded with `encodeURIComponent` (so a space is `%20`, which `Uri.UnescapeDataString`
reads correctly; a `+` is not a space). `folder` and `tags` are optional.

| Key | Meaning | Rules on arrival |
| --- | --- | --- |
| `url` | The article to open | Required. `http` or `https` only; anything else drops the link. |
| `folder` | Suggested "Save to" folder | Used only if fully qualified **and** inside a library folder (`OculaLink.IsInside`). Otherwise ignored and Ocula's own default applies. |
| `tags` | Tags to tick, separated by `;` | Split on `;`, trim, drop empties and control characters, at most 60 characters each and 20 tags. |

Any web page can launch an `ocula:` link, so every value is untrusted. That is why the folder is
restricted to the library and why **nothing is ever downloaded without the user pressing the
existing Download button**.

## What Ocula does with it

1. `OculaLink.Parse` gains the verb `webdownload` with `Url` and `Tags` fields (the record
   currently has `Verb, File, Folder, Id`). The verb is not a background verb: it takes the focus.
2. `HandleLinkAsync` opens `WebDownloadWindow` (reusing an open one), navigates its WebView2 to
   `url`, and runs the normal "Grab images on this page" step once the page has loaded. All the
   existing rules stay: forum gating, size minimums, thumbnail-to-full-size resolution, one link
   deep, same-site cookies only, no ad-screen, form, paywall or login circumvention. If the site
   shows a challenge page, the user deals with it in the pane.
3. The resolved images start **ticked**. Failed resolutions show the existing "Couldn't find the
   full-size version" state.
4. The tag chips from `tags` start ticked. They are the user's own words: add them as chips as given
   (case kept). `TagSuggester` still proposes its own chips as today, unticked.
5. "Save to" is filled from `folder` when allowed. Under the field show "Existing folder" or "Will
   be created".
6. A small strip at the top reads "Opened from the browser extension" so the origin is clear.
7. Downloading, tagging (`MainWindow.TagDownloadsAsync`) and opening the folder in the library are
   the existing code paths, unchanged.

## Not to do

- No body-focused tags, suggestions or defaults (CLAUDE.md rule). The extension suggests only names
  taken from the article's tag links and whatever the user types.
- No telemetry and no new network use: the page load and the downloads are what the user started.
- No registration changes: `Package.appxmanifest` already registers the `ocula:` scheme.

## Tests to add

- `OculaLink.Parse("ocula:webdownload?url=https%3A%2F%2Fwww.justjared.com%2F2013%2F06%2F07%2Fx%2F&folder=Z%3A%5CBabes%5CBusy%20Philipps&tags=Busy%20Philipps%3Bcandid")`
  gives `Url`, the folder with a real space, and two tags.
- A `url` that is `file:`, `javascript:` or `ocula:` is rejected.
- Tags: empty items dropped, an item over 60 characters trimmed or dropped, more than 20 cut to 20.
- A `folder` outside every library folder is ignored; one inside is kept.
- Existing `captures`, `import`, `edit` and `person` links still parse the same.

## Prompt for Claude Code (also Prompt 9 in CLAUDE-CODE-PROMPTS.md)

```
Read CLAUDE.md, docs/CHROME-EXTENSION.md, Services/OculaLink.cs, App.xaml.cs, MainWindow.Web.cs,
WebDownloadWindow.xaml(.cs), Services/PageScanner.cs and BUILDING.md (Phase 6). Implement the
ocula:webdownload link exactly as docs/CHROME-EXTENSION.md describes: parse it in OculaLink
(Url, Tags), handle it in HandleLinkAsync by opening WebDownloadWindow on the address, grabbing the
page's images, ticking them, pre-ticking the user's tags, and filling "Save to" only when the folder
is inside a library folder. Never start a download without the Download button. Keep every rule in
CLAUDE.md (no body-focused labels, no circumvention, same-site cookies only). Add the unit tests
listed in the doc, run `dotnet build -p:Platform=x64` and the test project, and commit on a branch.
Report anything that behaves differently from the doc.
```
