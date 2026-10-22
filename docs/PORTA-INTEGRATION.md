# Porta ↔ Ocula integration (to build later, after Ocula compiles)

Ocula is the provider. Porta calls it over a local Windows named pipe via the shared library
`stirling-shared` (StirlingPipeServer, IntegrationSettings, StirlingFolders, StirlingLinks).
**Get the `stirling-shared` source first and match its exact API.** Do not guess signatures.

Calls (contract copied from the Porta design chat):
- `photos.search` (integration id `porta.photo-search`): `PhotoSearchParams(Query, Kind="auto", Limit=10, ThumbSize=160)`
  -> `PhotoSearchResult(Total, Items[], Explanation, OpenAllLink)`;
  `PhotoHit(Path, ThumbPath, Badge, Reason, TakenAt, Score, Snippet)`.
  Kinds: auto, scene, text, person, tag, camera, rating, date.
  Auto order: named person -> "n stars" rating -> camera make/model -> date words (combinable)
  -> screenshot text -> plain-words scene search (CLIP) -> tags can join any.
  Keyword kinds answer within 1.5 s; scene may take up to 4 s cold.
  Thumbnails go to the shared Thumbs folder, named by hash of path + modified time, reused.
  Never return photos from folders the user excluded. Honour the on/off switch.
  Support `ocula:background` (start without a window, quit after 10 idle minutes).
- `photos.pinnables` (`porta.pin-people-albums`): named people and albums, 256x256 square covers,
  plus a "Pin to Start" right-click item that opens `porta:pin?link=...&name=...&cover=...`.
- People file (built, branch `porta-people-export`; alternative to calling Ocula for names): with Settings > Privacy > "Share people with Porta"
  on, Ocula writes `%LocalAppData%\Stirling\Ocula\people-index.json` (version 1: id, name, aliases, description?, photoCount, thumbnail?) and 96 px
  face PNGs at `%LocalAppData%\Stirling\Ocula\people-thumbs\<id>.png` (`thumbnail` is the absolute path). Open a person with
  `ocula:person?id=<url-encoded id>`: the People window opens on them and their photos show in the grid; a running Ocula is brought forward, not
  copied. Details, risks (the package excludes that folder from MSIX file redirection) and what is untested are in BUILDING.md
  ("Share people with Porta"). It never carries card details (height, spouse, children, dates, ...).
- Screenshot text search: built in Ocula (FTS5 over the `Capta` PNG chunk's app, window title and
  `ocrText`; see BUILDING.md). Capta writes one `Capta` chunk, not `Stirling.*` chunks.
