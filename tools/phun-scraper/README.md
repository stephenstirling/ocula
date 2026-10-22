# phun-scraper (Docker, runs on the Synology NAS)

Python forum image scraper. Not part of the Ocula app or its Store build.

- Put these four files in one NAS folder and create a Container Manager project from `docker-compose.yml`.
- In `docker-compose.yml`, change the left side of the volume to the NAS path of the folder Windows sees as `Z:\Babes`.
- Output: `<downloads>/<Celebrity Name>/[Pregnant|Bikini|Swimwear]/`, EXIF/IPTC keywords, and
  `ocula_library.db` (read by `Phun/LibraryIndex.cs`).
- Live status: `scraper_status.json` and `http://<nas>:8765/status`; `POST /scan-now` (optional `STATUS_TOKEN`).
  Read by `Phun/ScraperMonitor.cs` and shown on `Phun/ScraperPage.xaml`.
- `python phun_scraper.py --reindex` backfills the index from images already on disk.
