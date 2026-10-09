# Ocula privacy policy

_Last updated: [DATE]_

Ocula is a photo browser that runs on your PC. This policy explains what it does with your
information. In short: your photos and personal data stay on your computer.

## What stays on your PC

- **Your photos** are only read from and written to the folders you choose. Ocula never uploads them.
- **Tags, ratings and colour labels** are saved in your photo files or in `.xmp` files next to them.
- **Ocula's library** (thumbnails, metadata including photo locations, and duplicate-detection
  fingerprints) is stored in
  `%LOCALAPPDATA%\Ocula` on your PC. You can clear it in Settings > Library.
- **Settings** (theme, folders, export presets) are stored on your PC.

Ocula has no accounts, no advertising, and no analytics or tracking.

## What Ocula sends over the internet

**Camera photos (optional).** To show a picture of the camera that took a photo, Ocula sends
the camera's model name (for example "Canon EOS R5") to Wikidata and downloads a matching
image from Wikimedia Commons. Nothing else is sent: no photos, file names, locations or other
metadata. Downloaded camera images are cached on your PC. These services are run by the
Wikimedia Foundation under its own privacy policy: https://foundation.wikimedia.org/wiki/Policy:Privacy_policy

You can turn this off in **Settings > Privacy > Show camera photos**. With this, the map and
automatic update checks turned off, Ocula makes no internet requests of its own.

**Map (optional).** When you open the map, Ocula downloads map images from OpenFreeMap
(https://openfreemap.org). Like any website, the map provider sees your IP address and which
areas of the map are loaded. Because the map opens on the places your photos were taken, this
reveals those general areas. Your photos, file names and exact photo locations are not sent.
You can turn the map off in **Settings > Privacy > Map**.

**Smart recognition (optional, off by default).** If you turn it on, Ocula downloads a
recognition model once from Hugging Face (https://huggingface.co); that site sees your IP
address and which model files you fetch. After that, photos are analysed entirely on your PC.
No photo, thumbnail, tag or search is sent anywhere. The results are stored in Ocula's library
on your PC and can be removed with **Settings > Smart recognition > Delete recognition data**.

**Download from the web (only when you use it).** The Web window loads the pages you type in,
like any browser, and then fetches the images you choose to download, and the pages those
images link to when it looks for a larger version. Those sites see your IP address and what you
request, and the page you were on as the referrer. Ocula sends nothing else, and nothing about
your library. Suggested tags are worked out on your PC from the page's text. The source site and
page title are only written into your photos if you leave "Save the source in each photo" on.

**People (optional, off by default).** If you turn it on, Ocula downloads two small face
models once from Hugging Face; that site sees your IP address and which files you fetch. After
that, faces are found and grouped entirely on your PC. Face crops and numbers describing them
are stored in Ocula's library on your PC only, and are never written into your photos or sent
anywhere. Ocula does not identify anyone: a group only gets a name when you type one. Names you
add as tags are written into those photos. Remove everything with **Settings > People > Delete
face data**.

**Update checks (optional).** Once a day, Ocula asks GitHub's public API which version is
the newest, and downloads that version's release notes. The request includes Ocula's version
number; GitHub sees your IP address, as with any website. Turn it off in **Settings > Updates >
Check for updates automatically**. Store installs are updated by the Microsoft Store.

**Links you choose to open**, such as "Support Ocula", open in your web browser, where that
website's own privacy policy applies.

## Client proofing galleries

A proofing gallery is a folder of resized photos (with location data removed) and a web page,
saved where you choose. Ocula doesn't upload it anywhere; you decide where to publish it. The
page has no tracking: a client's favourites are stored only in their own browser until they
choose to email them to you.

## Sharing and exporting

When you use Share or Export, Ocula only passes files to the app or folder you pick. Export can
remove location data from the copies it creates; your original files are unchanged.

## Changes and contact

If this policy changes, the new version will be published here with a new date.
Questions: [CONTACT EMAIL OR GITHUB ISSUES LINK]
