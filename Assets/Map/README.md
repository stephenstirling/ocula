<!-- MapLibre GL JS 4.7.1 (npm maplibre-gl@4.7.1, BSD-3-Clause). Pinned: update both files together. -->
# Map assets

`map.html` is the Map window's page. Ocula's `MapWindow` serves this folder to WebView2 as
`https://ocula.map/`, so the page and MapLibre load from the app package, never from the
internet. Only the map style and tiles (OpenFreeMap) are fetched online.

| File | What it is |
|---|---|
| `map.html` | Ocula's map page: clusters, photo popups, messages to and from `MapWindow`. |
| `maplibre-gl.js`, `maplibre-gl.css` | MapLibre GL JS **4.7.1**, unmodified, from `https://unpkg.com/maplibre-gl@4.7.1/dist/`. |

To update MapLibre, download both files for the same version, check they match the npm
package, and change the version above and in `THIRD-PARTY-NOTICES.md` and `AppInfo.Notices`.
