import io
import os
import sys
import sqlite3
from contextlib import closing
import json
import re
import time
import hashlib
import logging
import threading
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from logging.handlers import RotatingFileHandler
from datetime import datetime, timedelta
from urllib.parse import urljoin, quote_plus
from concurrent.futures import ThreadPoolExecutor
from bs4 import BeautifulSoup
import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
from PIL import Image
import imagehash
from exiftool import ExifToolHelper

# --- Configuration ---
FORUM_URL = "https://forum.phun.org/forums/celebrity-photos.15/"
DOWNLOAD_DIR = os.environ.get("DOWNLOAD_DIR", "/downloads")  # Docker path; mount the NAS "Babes" folder here
HISTORY_FILE = os.path.join(DOWNLOAD_DIR, "download_history.json")
CELEB_CACHE_FILE = os.path.join(DOWNLOAD_DIR, "celeb_data_cache.json")
LOG_FILE = os.path.join(DOWNLOAD_DIR, "scraper.log")
LIBRARY_DB = os.path.join(DOWNLOAD_DIR, "ocula_library.db")  # sidecar index read by Ocula
IMAGE_EXTS = (".jpg", ".jpeg", ".png", ".webp")
SUBFOLDER_CATEGORIES = ("Pregnant", "Bikini", "Swimwear")
STATUS_FILE = os.path.join(DOWNLOAD_DIR, "scraper_status.json")  # live status for Ocula
STATUS_PORT = int(os.environ.get("STATUS_PORT", "8765"))
STATUS_TOKEN = os.environ.get("STATUS_TOKEN", "")  # if set, required (X-Token header) for POST /scan-now
CHECK_INTERVAL_HOURS = 4
MIN_FILE_SIZE_BYTES = 50 * 1024
UPGRADE_FILE_SIZE_THRESHOLD = 400 * 1024
PHASH_SIMILARITY_THRESHOLD = 5
MAX_DOWNLOAD_WORKERS = 5
MAX_UPGRADE_CANDIDATES = 8
MAX_AGE_AT_POST = 57
MIN_AGE_AT_POST = 18  # safeguard: never keep photos of anyone under 18 at post date

os.makedirs(DOWNLOAD_DIR, exist_ok=True)

# --- Threading Locks ---
history_lock = threading.Lock()
exif_lock = threading.Lock()
cache_lock = threading.Lock()

# --- Logging Setup ---
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        RotatingFileHandler(LOG_FILE, maxBytes=5 * 1024 * 1024, backupCount=3),
        logging.StreamHandler(),
    ],
)

# --- Exclusion Lists ---
EXCLUDED_CELEBRITIES = [
    "kim petras",
    "kelly osbourne",
    "sharon osbourne",
    "madonna",
    "lindsay lohan",
]
EXCLUDED_TITLE_KEYWORDS = ["trans", "transgender"]

HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/124.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
}

session = requests.Session()
retry_strategy = Retry(total=3, backoff_factor=2, status_forcelist=[429, 500, 502, 503, 504])
adapter = HTTPAdapter(max_retries=retry_strategy)
session.mount("https://", adapter)
session.mount("http://", adapter)
session.headers.update(HEADERS)


# --- Image helpers ---
def image_ok(data):
    """True if bytes are a decodable image of at least 300x300."""
    try:
        with Image.open(io.BytesIO(data)) as img:
            img.verify()
        with Image.open(io.BytesIO(data)) as img:
            w, h = img.size
            return w >= 300 and h >= 300
    except Exception:
        return False


def phash_of(data):
    with Image.open(io.BytesIO(data)) as img:
        return imagehash.phash(img)


# --- Multi-Engine Reverse Image Search Module ---
class MultiEngineReverseSearch:
    def __init__(self, session):
        self.session = session

    def search_yandex(self, image_url):
        candidates = []
        url = f"https://yandex.com/images/search?rpt=imageview&url={quote_plus(image_url)}"
        try:
            resp = self.session.get(url, timeout=10)
            if resp.status_code == 200:
                for m in re.findall(r'"original":\s*\{\s*"url":\s*"([^"]+)"', resp.text):
                    candidates.append(m.replace("\\/", "/"))
        except Exception as e:
            logging.warning("Yandex reverse search error: %s", e)
        return candidates

    def search_google_lens(self, image_url):
        candidates = []
        url = f"https://lens.google.com/uploadbyurl?url={quote_plus(image_url)}"
        try:
            resp = self.session.get(url, timeout=10)
            if resp.status_code == 200:
                m = re.findall(r'https?://[^"]+\.(?:jpg|jpeg|png|webp)', resp.text, re.IGNORECASE)
                candidates.extend(m[:10])
        except Exception as e:
            logging.warning("Google Lens reverse search error: %s", e)
        return candidates

    def search_bing(self, image_url):
        candidates = []
        url = f"https://www.bing.com/images/searchbyimage?cbir=sbi&imgurl={quote_plus(image_url)}"
        try:
            resp = self.session.get(url, timeout=10)
            if resp.status_code == 200:
                m = re.findall(r'murl(?:&quot;|")\s*:\s*(?:&quot;|")(https?://[^"&]+)', resp.text)
                candidates.extend(m[:10])
        except Exception as e:
            logging.warning("Bing Visual Search error: %s", e)
        return candidates

    def find_better_version(self, original_url, current_size, current_phash):
        """Return bytes of a larger copy of the SAME photo, or None."""
        logging.info("Reverse search for higher-res copy (current %.1f KB)...", current_size / 1024)
        candidates = []
        for fn in (self.search_yandex, self.search_google_lens, self.search_bing):
            candidates.extend(fn(original_url))
        candidates = [c for c in dict.fromkeys(candidates) if c != original_url]

        best, best_size, tried = None, current_size, 0
        for url in candidates:
            if tried >= MAX_UPGRADE_CANDIDATES:
                break
            try:
                head = self.session.head(url, timeout=5, allow_redirects=True)
                if head.status_code != 200 or "image" not in head.headers.get("Content-Type", ""):
                    continue
                if int(head.headers.get("Content-Length", 0)) <= best_size:
                    continue
                tried += 1
                r = self.session.get(url, timeout=20)
                data = r.content
                if r.status_code != 200 or len(data) <= best_size or not image_ok(data):
                    continue
                # Must be the same picture, not just a "visually similar" result
                if phash_of(data) - current_phash > PHASH_SIMILARITY_THRESHOLD:
                    continue
                best, best_size = data, len(data)
                logging.info("Found superior candidate (%.1f KB): %s", best_size / 1024, url)
            except Exception:
                continue
        return best


# --- Cache & History Management ---
def load_json_file(filepath, default_value):
    if os.path.exists(filepath):
        try:
            with open(filepath, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return default_value


def save_json_file(filepath, data):
    """Atomic write so a crash can't corrupt the file."""
    tmp = filepath + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        os.replace(tmp, filepath)
    except Exception as e:
        logging.error("Error saving %s: %s", filepath, e)


CELEBRITY_DATA_CACHE = load_json_file(CELEB_CACHE_FILE, {})


def load_history():
    data = load_json_file(HISTORY_FILE, {})
    data.setdefault("processed_threads", [])
    data.setdefault("downloaded_urls", [])
    data.setdefault("downloaded_hashes", [])
    return data


def save_history(history):
    with history_lock:
        save_json_file(HISTORY_FILE, history)


# --- Ocula sidecar index (SQLite) ---
# Paths are stored RELATIVE to the downloads folder with forward slashes, so the same
# database works whether Ocula sees the share as a mapped drive, a UNC path, or Docker sees /downloads.
LIBRARY_SCHEMA_VERSION = 1
db_lock = threading.Lock()

LIBRARY_SCHEMA = """
CREATE TABLE IF NOT EXISTS images (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    rel_path      TEXT NOT NULL UNIQUE,
    celebrity     TEXT,
    category      TEXT,            -- Pregnant / Bikini / Swimwear or NULL
    tags          TEXT,            -- JSON array
    source_url    TEXT,            -- original link from the forum post
    thread_title  TEXT,
    thread_url    TEXT,
    post_date     TEXT,            -- ISO date parsed from the thread title, if any
    width         INTEGER,
    height        INTEGER,
    file_size     INTEGER,
    phash         TEXT,
    upgraded      INTEGER NOT NULL DEFAULT 0,  -- 1 = replaced by a higher-res reverse-search copy
    downloaded_at TEXT
);
CREATE TABLE IF NOT EXISTS image_tags (
    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
    tag      TEXT NOT NULL,
    PRIMARY KEY (image_id, tag)
);
CREATE INDEX IF NOT EXISTS idx_images_celebrity ON images(celebrity);
CREATE INDEX IF NOT EXISTS idx_images_category  ON images(category);
CREATE INDEX IF NOT EXISTS idx_images_date      ON images(downloaded_at);
CREATE INDEX IF NOT EXISTS idx_tags_tag         ON image_tags(tag);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
"""


def _library_connect():
    # Default rollback journal (not WAL): WAL does not work when the DB sits on a network share.
    conn = sqlite3.connect(LIBRARY_DB, timeout=30)
    conn.execute("PRAGMA foreign_keys = ON")
    return conn


def init_library():
    with db_lock, closing(_library_connect()) as conn, conn:
        conn.executescript(LIBRARY_SCHEMA)
        conn.execute(
            "INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', ?)",
            (str(LIBRARY_SCHEMA_VERSION),),
        )


def rel_library_path(file_path):
    return os.path.relpath(file_path, DOWNLOAD_DIR).replace(os.sep, "/")


def record_image(file_path, celebrity, tags, category=None, source_url=None, thread_title=None,
                 thread_url=None, post_date=None, phash=None, upgraded=False, size_px=None):
    """Insert/replace one row (and its tag rows) in the Ocula index. Never raises."""
    try:
        if size_px is None:
            with Image.open(file_path) as im:
                size_px = im.size
        width, height = size_px
        row = (
            rel_library_path(file_path), celebrity, category, json.dumps(tags, ensure_ascii=False),
            source_url, thread_title, thread_url, post_date, width, height,
            os.path.getsize(file_path), str(phash) if phash is not None else None,
            1 if upgraded else 0, datetime.utcnow().isoformat(timespec="seconds") + "Z",
        )
        with db_lock, closing(_library_connect()) as conn, conn:
            cur = conn.execute(
                """INSERT INTO images (rel_path, celebrity, category, tags, source_url, thread_title,
                       thread_url, post_date, width, height, file_size, phash, upgraded, downloaded_at)
                   VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                   ON CONFLICT(rel_path) DO UPDATE SET
                       celebrity=excluded.celebrity, category=excluded.category, tags=excluded.tags,
                       width=excluded.width, height=excluded.height, file_size=excluded.file_size,
                       phash=excluded.phash""",
                row,
            )
            image_id = conn.execute("SELECT id FROM images WHERE rel_path = ?", (row[0],)).fetchone()[0]
            conn.execute("DELETE FROM image_tags WHERE image_id = ?", (image_id,))
            conn.executemany(
                "INSERT OR IGNORE INTO image_tags(image_id, tag) VALUES (?, ?)",
                [(image_id, t) for t in tags],
            )
            conn.execute(
                "INSERT OR REPLACE INTO meta(key, value) VALUES ('last_updated', ?)", (row[-1],)
            )
    except Exception as e:
        logging.error("Library index error for %s: %s", file_path, e)


def reindex_existing():
    """One-off backfill: add images already on disk (from before the index existed).
    Run with:  python phun_scraper.py --reindex"""
    init_library()
    with closing(_library_connect()) as conn:
        known = {r[0] for r in conn.execute("SELECT rel_path FROM images")}

    todo = []
    for root, _dirs, files in os.walk(DOWNLOAD_DIR):
        for fn in files:
            if fn.lower().endswith(IMAGE_EXTS):
                full = os.path.join(root, fn)
                if rel_library_path(full) not in known:
                    todo.append(full)
    logging.info("Reindex: %d new images to add", len(todo))

    added = 0
    with ExifToolHelper() as et:
        for full in todo:
            try:
                parts = rel_library_path(full).split("/")
                celebrity = parts[0] if len(parts) > 1 else None
                category = parts[1] if len(parts) > 2 and parts[1] in SUBFOLDER_CATEGORIES else None
                tags = []
                try:
                    meta = et.get_tags([full], tags=["IPTC:Keywords"])
                    kw = meta[0].get("IPTC:Keywords", []) if meta else []
                    tags = [str(k) for k in (kw if isinstance(kw, list) else [kw])]
                except Exception:
                    pass
                with open(full, "rb") as f:
                    ph = phash_of(f.read())
                record_image(full, celebrity, tags, category=category, phash=ph)
                added += 1
            except Exception as e:
                logging.error("Reindex skipped %s: %s", full, e)
    logging.info("Reindex complete: %d added", added)


# --- Live status for Ocula ---
def _iso(ts=None):
    return datetime.utcfromtimestamp(ts if ts is not None else time.time()).isoformat(timespec="seconds") + "Z"


class ScraperStatus:
    """Thread-safe progress tracker. Mirrored to scraper_status.json (atomic, throttled to ~1/s)
    and served over HTTP, so Ocula can watch the container from the shared folder or the network."""

    def __init__(self):
        self._lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._last_flush = 0.0
        self._events = deque(maxlen=100)
        self.data = {
            "schema_version": 1,
            "state": "starting",          # starting | scanning | sleeping
            "started_at": _iso(),
            "scan_number": 0,
            "scan_started_at": None,
            "last_scan_finished": None,
            "next_scan_at": None,
            "threads_total": 0,           # new threads found this scan
            "threads_done": 0,
            "thread_title": None,         # thread being processed right now
            "thread_url": None,
            "thread_links_total": 0,
            "thread_links_done": 0,
            "images_saved": 0,            # counters below are since container start
            "upgrades": 0,
            "duplicates_skipped": 0,
            "failed_downloads": 0,
            "threads_skipped": 0,
            "last_error": None,
        }

    def update(self, force=False, **fields):
        with self._lock:
            self.data.update(fields)
        self.flush(force)

    def bump(self, key, n=1):
        with self._lock:
            self.data[key] = self.data.get(key, 0) + n
        self.flush()

    def add_event(self, level, message):
        with self._lock:
            self._events.append({"time": _iso(), "level": level, "message": message})
        self.flush()

    def snapshot(self):
        with self._lock:
            snap = dict(self.data)
            snap["events"] = list(self._events)
        snap["updated_at"] = _iso()
        return snap

    def flush(self, force=False):
        now = time.time()
        if not force and now - self._last_flush < 1.0:
            return
        self._last_flush = now  # set first so a logging error inside the write can't recurse
        with self._write_lock:
            save_json_file(STATUS_FILE, self.snapshot())


status = ScraperStatus()
scan_now_event = threading.Event()


class StatusLogHandler(logging.Handler):
    def emit(self, record):
        try:
            msg = record.getMessage()
            if msg.strip("= "):  # skip the ==== banner lines
                status.add_event(record.levelname, msg)
        except Exception:
            pass


_h = StatusLogHandler(level=logging.INFO)
logging.getLogger().addHandler(_h)


class StatusHandler(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.rstrip("/") in ("", "/status"):
            self._send(200, status.snapshot())
        elif self.path.rstrip("/") == "/health":
            self._send(200, {"ok": True})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path.rstrip("/") != "/scan-now":
            return self._send(404, {"error": "not found"})
        if STATUS_TOKEN and self.headers.get("X-Token") != STATUS_TOKEN:
            return self._send(403, {"error": "bad token"})
        scan_now_event.set()
        logging.info("Manual scan requested from Ocula")
        self._send(200, {"ok": True})

    def log_message(self, *args):
        pass


def start_status_server():
    try:
        server = ThreadingHTTPServer(("0.0.0.0", STATUS_PORT), StatusHandler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        logging.info("Status server listening on port %d", STATUS_PORT)
    except Exception as e:
        logging.error("Status server could not start (%s); status.json still works", e)


def sanitize_filename(name):
    return re.sub(r'[\\/*?:"<>|]', "", name).strip()


# --- Date helper ---
DATE_RE = re.compile(r"(\d{2}[-/]\d{2}[-/]\d{4})|(\d{4}[-/]\d{2}[-/]\d{2})")


def parse_title_date(title_text):
    m = DATE_RE.search(title_text)
    if not m:
        return None
    s = m.group(0).replace("/", "-")
    try:
        if len(s.split("-")[0]) == 4:
            return datetime.strptime(s, "%Y-%m-%d")
        return datetime.strptime(s, "%m-%d-%Y")
    except ValueError:
        return None


# --- Wikidata Integration ---
def sparql_escape(s):
    return s.replace("\\", "\\\\").replace('"', '\\"')


def query_wikidata_sparql(query):
    """Returns list of bindings, or None if the request failed."""
    url = "https://query.wikidata.org/sparql"
    headers = {
        "User-Agent": "PhunScraperBot/1.0 (contact@example.com)",
        "Accept": "application/sparql-results+json",
    }
    try:
        res = requests.get(url, params={"query": query, "format": "json"}, headers=headers, timeout=10)
        if res.status_code == 200:
            return res.json().get("results", {}).get("bindings", [])
        logging.warning("Wikidata HTTP %s", res.status_code)
    except Exception as e:
        logging.warning("Wikidata query failed: %s", e)
    return None


def fetch_celeb_data_from_wikidata(celeb_name):
    """Returns (birthdate, timelines, ok). ok=False means a lookup failed and must not be cached."""
    name = sparql_escape(celeb_name)
    birthdate, timelines = None, []

    dob_bindings = query_wikidata_sparql(f"""
    SELECT ?birthdate WHERE {{
      ?person rdfs:label "{name}"@en ; wdt:P31 wdt:Q5 ; wdt:P569 ?birthdate .
    }} LIMIT 1
    """)
    if dob_bindings is None:
        return None, [], False
    if dob_bindings:
        m = re.search(r"\d{4}-\d{2}-\d{2}", dob_bindings[0]["birthdate"]["value"])
        if m:
            birthdate = m.group(0)

    child_bindings = query_wikidata_sparql(f"""
    SELECT ?childDob WHERE {{
      ?person rdfs:label "{name}"@en ; wdt:P31 wdt:Q5 ; wdt:P40 ?child .
      ?child wdt:P569 ?childDob .
    }}
    """)
    if child_bindings is None:
        return birthdate, [], False
    for b in child_bindings:
        m = re.search(r"\d{4}-\d{2}-\d{2}", b["childDob"]["value"])
        if m:
            try:
                child_dob = datetime.strptime(m.group(0), "%Y-%m-%d")
            except ValueError:
                continue
            timelines.append([
                (child_dob - timedelta(days=270)).strftime("%Y-%m-%d"),
                (child_dob + timedelta(days=30)).strftime("%Y-%m-%d"),
            ])
    return birthdate, timelines, True


def get_cached_celeb_data(celeb_name):
    key = celeb_name.lower().strip()
    with cache_lock:
        if key in CELEBRITY_DATA_CACHE:
            return CELEBRITY_DATA_CACHE[key]

    logging.info("Cache miss: querying Wikidata for '%s'...", celeb_name)
    birthdate, timelines, ok = fetch_celeb_data_from_wikidata(celeb_name)
    data = {"birthdate": birthdate, "pregnancy_timelines": timelines}
    if ok:  # only cache real answers, never transient failures
        with cache_lock:
            CELEBRITY_DATA_CACHE[key] = data
            save_json_file(CELEB_CACHE_FILE, CELEBRITY_DATA_CACHE)
    return data


def age_at_post(celeb_name, title_text):
    post_date = parse_title_date(title_text)
    if not post_date:
        return None
    dob_str = get_cached_celeb_data(celeb_name).get("birthdate")
    if not dob_str:
        return None
    try:
        dob = datetime.strptime(dob_str, "%Y-%m-%d")
    except ValueError:
        return None
    return post_date.year - dob.year - ((post_date.month, post_date.day) < (dob.month, dob.day))


def is_amanda_bynes_after_2012(celeb_name, title_text):
    if celeb_name.lower().strip() != "amanda bynes":
        return False
    post_date = parse_title_date(title_text)
    if post_date and post_date.year > 2012:
        logging.info("Skipped - Amanda Bynes year limit: post year %d is after 2012", post_date.year)
        return True
    return False


def is_within_pregnancy_timeline(celeb_name, title_text):
    timelines = get_cached_celeb_data(celeb_name).get("pregnancy_timelines", [])
    post_date = parse_title_date(title_text)
    if not timelines or not post_date:
        return False
    for start_str, end_str in timelines:
        try:
            if datetime.strptime(start_str, "%Y-%m-%d") <= post_date <= datetime.strptime(end_str, "%Y-%m-%d"):
                return True
        except ValueError:
            continue
    return False


def extract_metadata_tags(title_text, post_text=""):
    clean_title = re.sub(r"\[.*?\]|\(.*?\)", "", title_text).strip()
    match = re.search(r"^(.*?)\s+(?:at|in|@|on|-)\s+", clean_title, re.IGNORECASE)
    if match:
        celeb_name = match.group(1).strip()
    else:
        words = clean_title.split()
        celeb_name = " ".join(words[:2]) if len(words) >= 2 else clean_title

    common_keywords = [
        "bikini", "lingerie", "swimsuit", "red carpet", "dress", "gown",
        "photoshoot", "street style", "casual", "beach", "runway",
        "fashion week", "magazine", "pregnant", "maternity", "baby bump", "swimwear",
    ]
    found = [celeb_name]
    combined = (clean_title + " " + post_text).lower()

    for kw in common_keywords:
        if kw in combined:
            if kw in ("pregnant", "baby bump"):
                fkw = "Pregnant"
            elif kw in ("bikini", "swimsuit", "swimwear"):
                fkw = "Swimwear" if kw == "swimwear" else "Bikini"
            else:
                fkw = kw.title()
            if fkw not in found:
                found.append(fkw)

    if "Pregnant" not in found and is_within_pregnancy_timeline(celeb_name, title_text):
        found.append("Pregnant")
    return celeb_name, found


def should_skip_thread(thread_title):
    title_lower = thread_title.lower()

    for keyword in EXCLUDED_TITLE_KEYWORDS:
        if re.search(r"\b" + re.escape(keyword) + r"\b", title_lower):
            logging.info("Skipped filtered thread (keyword '%s'): %s", keyword, thread_title)
            return True

    celeb_name, _ = extract_metadata_tags(thread_title)
    if celeb_name.lower() in EXCLUDED_CELEBRITIES:
        logging.info("Skipped filtered thread (excluded celebrity '%s'): %s", celeb_name, thread_title)
        return True

    if is_amanda_bynes_after_2012(celeb_name, thread_title):
        return True

    age = age_at_post(celeb_name, thread_title)
    if age is not None and (age > MAX_AGE_AT_POST or age < MIN_AGE_AT_POST):
        logging.info("Skipped - age limit: %s was %d at post date: %s", celeb_name, age, thread_title)
        return True
    return False


def resolve_image_host_url(link_url):
    if re.search(r"\.(jpg|jpeg|png|gif|webp)$", link_url, re.IGNORECASE):
        if "imagetwist.com" in link_url:
            return re.sub(r"/th/", "/i/", link_url)
        return link_url

    if "pixhost.to" in link_url:
        m = re.search(r"pixhost\.to/show/(\d+)/(\d+_[^/]+)", link_url)
        if m:
            return f"https://img2.pixhost.to/images/{m.group(1)}/{m.group(2)}"

    try:
        resp = session.get(link_url, timeout=10, allow_redirects=True)
        if resp.status_code != 200:
            return None
        soup = BeautifulSoup(resp.text, "html.parser")
        og = soup.select_one("meta[property='og:image']")
        if og and og.get("content"):
            return og["content"]
    except Exception:
        pass
    return None


def embed_metadata(file_path, keywords_list):
    with exif_lock:
        try:
            with ExifToolHelper() as et:
                et.set_tags(
                    [file_path],
                    tags={
                        "IPTC:Keywords": keywords_list,
                        "XMP:Subject": keywords_list,
                        "EXIF:ImageDescription": ", ".join(keywords_list),
                    },
                    params=["-overwrite_original"],
                )
        except Exception as e:
            logging.error("Metadata error tagging %s: %s", file_path, e)


def download_file(url, celeb_root_folder, history, keywords_list, meta=None):
    ok = _download_file(url, celeb_root_folder, history, keywords_list, meta)
    if not ok:
        status.bump("failed_downloads")
    return ok


def _download_file(url, celeb_root_folder, history, keywords_list, meta=None):
    meta = meta or {}
    with history_lock:
        if url in history["downloaded_urls"]:
            return True

    direct_url = resolve_image_host_url(url)
    if not direct_url:
        return False

    try:
        res = session.get(direct_url, stream=True, timeout=15)
        if res.status_code != 200 or "text/html" in res.headers.get("Content-Type", ""):
            return False

        content = bytearray()
        for chunk in res.iter_content(chunk_size=8192):
            content.extend(chunk)
        content = bytes(content)

        if len(content) < MIN_FILE_SIZE_BYTES or not image_ok(content):
            return False

        current_phash = phash_of(content)

        upgraded = False
        # Upgrade to a higher-res copy of the same photo if this one is small
        if len(content) < UPGRADE_FILE_SIZE_THRESHOLD:
            better = MultiEngineReverseSearch(session).find_better_version(
                direct_url, len(content), current_phash
            )
            if better:
                logging.info("Upgraded %.1f KB -> %.1f KB", len(content) / 1024, len(better) / 1024)
                content = better
                current_phash = phash_of(content)
                upgraded = True

        subfolder_name = next((c for c in SUBFOLDER_CATEGORIES if c in keywords_list), None)
        target_folder = os.path.join(celeb_root_folder, subfolder_name) if subfolder_name else celeb_root_folder
        os.makedirs(target_folder, exist_ok=True)

        filename = direct_url.split("/")[-1].split("?")[0]
        if not filename or len(filename) < 3:
            filename = f"image_{int(time.time())}.jpg"
        filename = sanitize_filename(filename)

        # Dedupe + reserve filename + write, all atomically
        with history_lock:
            for existing in history["downloaded_hashes"]:
                if current_phash - imagehash.hex_to_hash(existing) <= PHASH_SIMILARITY_THRESHOLD:
                    logging.info("Skipped visual duplicate (pHash): %s", filename)
                    history["downloaded_urls"].append(url)
                    status.bump("duplicates_skipped")
                    return True

            file_path = os.path.join(target_folder, filename)
            if os.path.exists(file_path):
                name, ext = os.path.splitext(filename)
                suffix = hashlib.md5(direct_url.encode()).hexdigest()[:6]
                file_path = os.path.join(target_folder, f"{name}_{suffix}{ext}")
            with open(file_path, "wb") as f:
                f.write(content)
            history["downloaded_hashes"].append(str(current_phash))
            history["downloaded_urls"].append(url)

        status.bump("images_saved")
        if upgraded:
            status.bump("upgrades")
        logging.info("Saved to %s: %s", subfolder_name or "Root", os.path.basename(file_path))
        embed_metadata(file_path, keywords_list)
        record_image(
            file_path, meta.get("celebrity"), keywords_list, category=subfolder_name,
            source_url=url, thread_title=meta.get("thread_title"), thread_url=meta.get("thread_url"),
            post_date=meta.get("post_date"), phash=current_phash, upgraded=upgraded,
        )
        return True

    except Exception as e:
        logging.error("Download failed for %s: %s", url, e)
        return False


SUPPORTED_HOSTS = [
    "imagetwist.com", "pixhost.to", "pixhost.org", "imgbox.com",
    "imagevenue.com", "postimg.cc",
]


def process_thread(thread_title, thread_url, history):
    """Returns True when the thread is finished (don't retry), False to retry next cycle."""
    logging.info("Processing thread: %s", thread_title)

    status.update(thread_title=thread_title, thread_url=thread_url,
                  thread_links_total=0, thread_links_done=0)
    if should_skip_thread(thread_title):
        status.bump("threads_skipped")
        return True

    try:
        resp = session.get(thread_url, timeout=15)
    except Exception as e:
        logging.error("Thread fetch failed (%s): %s", thread_url, e)
        return False
    if resp.status_code != 200:
        logging.error("Thread fetch HTTP %d: %s", resp.status_code, thread_url)
        return False  # transient errors must not permanently mark the thread done

    soup = BeautifulSoup(resp.text, "html.parser")
    first_post = soup.select_one("article.message-body")
    post_text = first_post.get_text() if first_post else ""

    celeb_name, keywords_list = extract_metadata_tags(thread_title, post_text)
    celeb_folder = os.path.join(DOWNLOAD_DIR, sanitize_filename(celeb_name))
    os.makedirs(celeb_folder, exist_ok=True)

    links = []
    for post in soup.select("article.message-body"):
        for a in post.find_all("a", href=True):
            href = a["href"]
            if any(h in href.lower() for h in SUPPORTED_HOSTS) or re.search(
                r"\.(jpg|jpeg|png|webp)$", href, re.IGNORECASE
            ):
                links.append(href)
    links = list(dict.fromkeys(links))

    post_dt = parse_title_date(thread_title)
    meta = {
        "celebrity": celeb_name,
        "thread_title": thread_title,
        "thread_url": thread_url,
        "post_date": post_dt.date().isoformat() if post_dt else None,
    }

    status.update(thread_links_total=len(links))
    logging.info("Folder: '%s' | Tags: %s | Links found: %d", celeb_name, keywords_list, len(links))

    with ThreadPoolExecutor(max_workers=MAX_DOWNLOAD_WORKERS) as executor:
        futures = [
            executor.submit(download_file, link, celeb_folder, history, keywords_list, meta)
            for link in links
        ]
        for future in futures:
            try:
                future.result()
            except Exception as e:
                logging.error("Worker error: %s", e)
            status.bump("thread_links_done")
    return True


def run_cycle():
    logging.info("=" * 50)
    logging.info("SCAN START: %s", time.strftime("%Y-%m-%d %H:%M:%S"))
    logging.info("=" * 50)
    history = load_history()
    status.bump("scan_number")
    status.update(state="scanning", scan_started_at=_iso(), threads_total=0, threads_done=0,
                  next_scan_at=None, force=True)

    try:
        resp = session.get(FORUM_URL, timeout=15)
        if resp.status_code != 200:
            logging.error("Forum list failed: HTTP %d", resp.status_code)
            status.update(last_error=f"Forum list failed: HTTP {resp.status_code}")
            return

        soup = BeautifulSoup(resp.text, "html.parser")
        pending = []
        for a_tag in soup.select("div.structItem-title a[data-tp-primary='on']"):
            href = a_tag.get("href")
            if not href:
                continue
            url = urljoin(FORUM_URL, href)
            with history_lock:
                if url in history["processed_threads"]:
                    continue
            pending.append((a_tag.text.strip(), url))

        status.update(threads_total=len(pending), force=True)
        logging.info("%d new threads to process", len(pending))

        for title, url in pending:
            if process_thread(title, url, history):
                with history_lock:
                    history["processed_threads"].append(url)
            save_history(history)
            status.bump("threads_done")
            time.sleep(1)
        status.update(last_error=None)
    except Exception as e:
        logging.error("Cycle error: %s", e)
        status.update(last_error=str(e))
        save_history(history)
    finally:
        status.update(last_scan_finished=_iso(), thread_title=None, thread_url=None,
                      thread_links_total=0, thread_links_done=0, force=True)


def main():
    init_library()
    start_status_server()
    status.update(force=True)
    while True:
        scan_now_event.clear()
        run_cycle()
        next_at = time.time() + CHECK_INTERVAL_HOURS * 3600
        status.update(state="sleeping", next_scan_at=_iso(next_at), force=True)
        logging.info("Sleeping: waiting %d hours for next scan...", CHECK_INTERVAL_HOURS)
        while time.time() < next_at and not scan_now_event.is_set():
            scan_now_event.wait(30)       # wakes early on POST /scan-now
            status.flush(force=True)      # heartbeat so Ocula can tell the container is alive


if __name__ == "__main__":
    if "--reindex" in sys.argv:
        reindex_existing()
    else:
        main()
