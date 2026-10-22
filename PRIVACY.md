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
- **Text in screenshots.** A screenshot saved by Capta carries, inside the PNG, the app and window
  title it was taken from and the text Capta found in it (read on your PC). Ocula copies that into its
  library on your PC so you can find a screenshot by what it says. It is stored only there, is never
  sent anywhere, and is only ever shown and searched as plain text. Turn it off, and remove what Ocula
  saved, in **Settings > Privacy > Search the text in screenshots**. Ocula never changes the PNG files.
- **Share people with Porta (optional, off by default).** If you turn this on in **Settings > Privacy > Share people
  with Porta**, Ocula saves a file on your PC, `people-index.json`, so Porta (the Start menu replacement) can find your photos of
  the people you have named in People. It holds, for each person you have named: the name you typed, other names Wikidata gave for
  them (only if you looked them up), a short description such as "American actress and singer" (only if you looked them up; any
  description with a birth year or other personal detail is left out), how many photos they are in, and the location of a small
  picture of their face. Those pictures (about 96 pixels, cut from your photos) are saved next to the file, in a folder called
  `people-thumbs`. People you have not named, and groups you have hidden, are not included. Nothing else from a person's card is
  shared: no birth date, birthplace, height, spouse, children, languages or occupations. The files are saved in
  `%LOCALAPPDATA%\Stirling\Ocula`, a normal folder in your user profile that other programs running as you can read; they are not
  encrypted. Nothing is sent over the internet. Turning the switch off, turning People off, or choosing "Delete all face data" deletes
  the file and the pictures. Ocula never writes them while the switch is off.
- **Settings** (theme, folders, export presets) are stored on your PC.

Ocula has no accounts, no advertising, and no analytics or tracking.

## What Ocula sends over the internet

**Camera photos (optional).** To show a picture of the camera that took a photo, Ocula sends
the camera's model name (for example "Canon EOS R5") to Wikidata and downloads a matching
image from Wikimedia Commons. Nothing else is sent: no photos, file names, locations or other
metadata. Downloaded camera images are cached on your PC. These services are run by the
Wikimedia Foundation under its own privacy policy: https://foundation.wikimedia.org/wiki/Policy:Privacy_policy

**Person details (optional).** In People, when you press "Look up on Wikidata" for a group you have named, Ocula sends
that name to Wikidata, then asks Wikidata for the facts about the person you pick (birth date and place, death date, place and cause,
occupation, spouse, number of children, height, languages). If the card is then missing something, Ocula asks DBpedia
(https://dbpedia.org) or MusicBrainz (https://musicbrainz.org) to fill it, using only an address Wikidata gave for that person (their English
Wikipedia page title, or their MusicBrainz id), never the name you typed. Those sites see your IP address and that address. DBpedia's answers are
credited on the card (its data is CC BY-SA). Nothing is sent until you press the button, and no photos, faces or
file names are ever sent. The facts are kept on your PC only and you can remove each card. Wikidata is run by the
Wikimedia Foundation under its own privacy policy (linked above). Turn it off in **Settings > Privacy > Look up people
online**.

You can turn camera photos off in **Settings > Privacy > Show camera photos**. With this, person lookups, the map and
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
Questions: https://github.com/stephenstirling/ocula/issues
