# Ocula: Just Jared photos (Chrome extension)

For the owner's own use. It is not published to the Chrome Web Store and isn't meant to be.

On a Just Jared article, the extension reads the page you already have open, shows the photos it
found, the people the headline names, and a save folder, lets you add your own tags, and then hands
the article to Ocula's web downloader with an `ocula:webdownload` link. **Ocula** shows the preview
and does the download. The extension never downloads anything itself.

## Install (developer mode)

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Choose **Load unpacked** and pick this folder (`extensions/chrome-justjared`).
3. Open a Just Jared article, click the extension, and use **Settings** to set your library folder
   (default `Z:\Babes`) and any tags you want on every article.
4. The first time you press **Preview in Ocula**, Chrome asks whether to open Ocula. Tick
   "Always allow".

There are no icons yet, so Chrome shows a letter tile. Add `icons/` and an `icons` entry in
`manifest.json` if you want one.

## What it does

- Recognises Just Jared article addresses (`/YYYY/MM/DD/slug/`). Anywhere else the popup says so.
- **Names** come from the article's own tag links, using the same rules as `justjared_scraper.py`:
  names in the headline win, a name only mentioned in the body is a fallback, and category tags
  such as "Pregnant Celebrities" are dropped because they never appear in the text. No face
  recognition.
- **Photos** are the article's images from Just Jared's media bucket. A thumbnail that links to
  another Just Jared page is resolved through that page's `og:image`. Logos, avatars, ads and
  other hosts are skipped.
- **Tags**: the detected names start as chips (outlined). Remove any, and add your own free-text
  tags. Nothing is suggested for you.
- **Save to**: your library folder plus the first detected name. Edit it freely. Clear it to let
  Ocula use its own default.

## The link

```
ocula:webdownload?url=<article address>&media=jj-justjared-media&folder=<save folder>&tags=<tag;tag;...>
```

Values are percent-encoded. `folder` and `tags` are optional. `media` tells Ocula to tick only
pictures from Just Jared's photo bucket and leave anything else on the page unticked. The Ocula side
is built (`MainWindow.OpenWebDownloadFromLink`, `WebDownloadWindow.OpenFromLink`) and specified in
`docs/CHROME-EXTENSION.md`. It has not been compiled or run on Windows yet.

## Privacy

No telemetry, no accounts, no analytics. The content script runs only on justjared.com and makes
no requests apart from reading a thumbnail's linked page on the same site. Settings are kept in
`chrome.storage.local`. The popup shows the photos by loading them from Just Jared in your own
browser.

## Limits

- An article that names two people uses the first for the folder. Copying into both folders is
  not built.
- Written without access to the live site and tested only against hand-written pages
  (`tests/run-content-tests.mjs`). The selectors are the first thing to check if a real page
  shows no photos or no names.
