# phun-scraper (Docker, runs on the Synology NAS)

Python forum image scraper. Not part of the Ocula app or its Store build.

- Put these four files in one NAS folder and create a Container Manager project from `docker-compose.yml`.
- In `docker-compose.yml`, change the left side of the volume to the NAS path of the folder Windows sees as `Z:\Babes`.
- Output: `<downloads>/<Celebrity Name>/[Pregnant|Bikini|Swimwear]/`, EXIF/IPTC keywords, and
  `ocula_library.db` (read by `Phun/LibraryIndex.cs`).
- Live status: `scraper_status.json` and `http://<nas>:8765/status`; `POST /scan-now` (optional `STATUS_TOKEN`).
  Read by `Phun/ScraperMonitor.cs` and shown on `Phun/ScraperPage.xaml`.
- `python phun_scraper.py --reindex` backfills the index from images already on disk.
- **How far it reads** (set in `docker-compose.yml`): `FORUM_PAGES` pages of the forum's thread list each scan (about 60 threads a page; the list has thousands of pages),
  `MAX_THREAD_PAGES` pages of replies in each thread. A thread with pictures that failed is tried up to 3 times. `MIN_FREE_GB`: when the downloads volume has less free
  space than this the scan stops (the status shows why) and tries again at the next scan, so the scraper can't fill the NAS. The NAS has to keep up: roughly 90 MB per thread.
- **Two people in a title** ("X and Y ...", "X & Y ..."): the picture is saved in both people's folders (the same category subfolder) and tagged with both names.
  Every person named has to pass the exclusion and age checks, not only the first. Pictures saved before this get their copy at start-up (`--fix-pairs [--dry-run]`).
- At every start the scraper also moves pictures filed under a wrong name to the right folder and fixes their tags (`--fix-names [--dry-run]`).
- **One folder per person, whatever the capitalisation** ("Mckenna Grace" and "McKenna Grace" are two folders on the NAS but one to Windows, which then can only
  reach one of them): the spelling most pictures use wins, and pictures filed under another spelling are moved at start-up. Pictures that were saved but never
  indexed (the scraper was stopped mid-save) are indexed at start-up too, and tagged with the person's name if they have no tags.
- **Hosts that block scripts.** turboimagehost.com is behind a Cloudflare challenge ("Just a moment..."), which a script can't pass, and the scraper doesn't try to get
  around it. Links on it are written once each to `blocked_links.txt` in the downloads folder (date, who, thread, link; tab separated) so they can be opened and saved by
  hand in a browser. Any other host that answers with such a challenge is reported in `failed_downloads.log` and not retried. About 3% of threads use turboimagehost.
