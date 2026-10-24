import io
import html as html_lib
import os
import shutil
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
from urllib.parse import urljoin, quote_plus, urlparse
from concurrent.futures import ThreadPoolExecutor
from bs4 import BeautifulSoup
import requests
import cloudscraper
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
from PIL import Image
import imagehash
from exiftool import ExifToolHelper

# --- Configuration ---
FORUM_URL = "https://forum.phun.org/forums/celebrity-photos.15/"
FORUM_BASE = "https://forum.phun.org/"  # thread links in the list are relative to this, not to FORUM_URL
DOWNLOAD_DIR = os.environ.get("DOWNLOAD_DIR", "/downloads")  # Docker path; mount the NAS "Babes" folder here
HISTORY_FILE = os.path.join(DOWNLOAD_DIR, "download_history.json")
CELEB_CACHE_FILE = os.path.join(DOWNLOAD_DIR, "celeb_data_cache.json")
FAILURES_FILE = os.path.join(DOWNLOAD_DIR, "failed_downloads.log")  # one line per picture that wasn't saved, and why
LOG_FILE = os.path.join(DOWNLOAD_DIR, "scraper.log")
LIBRARY_DB = os.path.join(DOWNLOAD_DIR, "ocula_library.db")  # sidecar index read by Ocula
IMAGE_EXTS = (".jpg", ".jpeg", ".png", ".webp")
SUBFOLDER_CATEGORIES = ("Pregnant", "Bikini", "Swimwear")
STATUS_FILE = os.path.join(DOWNLOAD_DIR, "scraper_status.json")  # live status for Ocula
STATUS_PORT = int(os.environ.get("STATUS_PORT", "8765"))
STATUS_TOKEN = os.environ.get("STATUS_TOKEN", "")  # if set, required (X-Token header) for POST /scan-now
CHECK_INTERVAL_HOURS = 4
FORUM_PAGES = int(os.environ.get("FORUM_PAGES", "1"))              # pages of the forum's thread list read per scan, newest first
MAX_THREAD_PAGES = int(os.environ.get("MAX_THREAD_PAGES", "10"))   # pages of replies read in each thread
MIN_FREE_GB = float(os.environ.get("MIN_FREE_GB", "100"))          # stop saving while the downloads volume has less free space than this
THREAD_ATTEMPTS = 3                                                # a thread with failed pictures is tried this many times in all
BLOCKED_HOSTS = ()  # Hosts that still return an unsolved bot challenge are logged as failed downloads.
BLOCKED_FILE = os.path.join(DOWNLOAD_DIR, "blocked_links.txt")  # Legacy file retained for any explicitly blocked hosts.
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

session = cloudscraper.create_scraper()
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


_hash_ints = []   # the saved pictures' perceptual hashes as integers, in the same order as history["downloaded_hashes"]


def hash_ints(history):
    """history["downloaded_hashes"] as integers (kept up to date as pictures are saved). Comparing integers is far faster than
    building a hash object for every saved picture, which matters once the library has hundreds of thousands of them."""
    hashes = history["downloaded_hashes"]
    if len(_hash_ints) > len(hashes):
        _hash_ints.clear()                   # the history was replaced by a shorter one: start again
    while len(_hash_ints) < len(hashes):
        _hash_ints.append(int(hashes[len(_hash_ints)], 16))
    return _hash_ints


def is_near_duplicate(value, known, threshold):
    """True when `value` is within `threshold` bits (Hamming distance) of any hash in `known`. Same test as imagehash's subtraction."""
    return any(bin(value ^ k).count("1") <= threshold for k in known)


def phash_of(data):
    with Image.open(io.BytesIO(data)) as img:
        return imagehash.phash(img)


# --- Multi-Engine Reverse Image Search Module ---
class MultiEngineReverseSearch:
    YANDEX_UPLOAD_URL = "https://yandex.com/images/search"
    YANDEX_UPLOAD_REQUEST = '{"blocks":[{"block":"b-page_type_search-by-image__link"}]}'

    def __init__(self, session):
        self.session = session

    @staticmethod
    def _prepare_query_image(image_bytes):
        """Return a JPEG query copy (max edge 1024 px); never alter the original bytes."""
        try:
            with Image.open(io.BytesIO(image_bytes)) as source:
                image = source.copy()
            image.thumbnail((1024, 1024), Image.Resampling.LANCZOS)
            if image.mode != "RGB":
                image = image.convert("RGB")
            output = io.BytesIO()
            image.save(output, format="JPEG", quality=85, optimize=True)
            return output.getvalue()
        except Exception as exc:
            # Keep the upgrade path non-fatal if a decoder cannot make a query copy.
            logging.warning("Could not prepare reverse-search upload image: %s", exc)
            return image_bytes

    @staticmethod
    def _is_block_page(text):
        lowered = (text or "").lower()
        return any(marker in lowered for marker in (
            "are you not a robot", "smartcaptcha", "captcha__image",
            "unusual traffic", "verify you are human",
        ))

    @staticmethod
    def _yandex_original_urls(text):
        """Best-effort extraction from Yandex's embedded result data."""
        text = html_lib.unescape(text or "")
        text = text.replace("\\/", "/").replace("\\u0026", "&").replace("\\u0026", "&")
        candidates = []
        # Keep the same original.url pattern used by the URL-search implementation.
        for match in re.findall(
            r'"original"\s*:\s*\{[^{}]{0,1000}?"url"\s*:\s*"([^"]+)"',
            text,
            re.IGNORECASE,
        ):
            url = match.replace("\\/", "/").replace("\\u0026", "&").replace("\\u0026", "&")
            url = url.replace("\\", "")
            if url.startswith(("http://", "https://")):
                candidates.append(url)
        return candidates

    def search_yandex_upload(self, image_bytes):
        """Upload downloaded image bytes to Yandex Images; return full-size original URLs.

        The public Yandex Images form has historically used multipart field upfile,
        with rpt=imageview, format=json, and a request block asking for the
        b-page_type_search-by-image__link URL. This is an unofficial web interface
        and can change or return CAPTCHA pages, so every failure degrades to [].
        """
        if not image_bytes:
            return []
        candidates = []
        try:
            query_bytes = self._prepare_query_image(image_bytes)
            # The public form sends dimensions and prg=1 alongside the uploaded file.
            # The POST often returns a small JSON redirect payload rather than results.
            with Image.open(io.BytesIO(query_bytes)) as image:
                width, height = image.size
            params = {
                "rpt": "imageview",
                "format": "json",
                "request": self.YANDEX_UPLOAD_REQUEST,
                "original_width": str(width),
                "original_height": str(height),
                "prg": "1",
            }
            response = self.session.post(
                self.YANDEX_UPLOAD_URL,
                params=params,
                files={"upfile": ("query.jpg", query_bytes, "image/jpeg")},
                headers={"Referer": "https://yandex.com/images/search?rpt=imageview"},
                timeout=10,
            )
            if response.status_code != 200 or not response.text:
                logging.warning("Yandex visual upload returned HTTP %s or an empty response",
                                response.status_code)
                return []
            if self._is_block_page(response.text):
                logging.warning("Yandex visual upload returned a CAPTCHA/block page")
                return []

            result_text = response.text
            # Some Yandex versions return JSON containing a result-page URL; others
            # may return result HTML directly.
            try:
                payload = json.loads(response.text)
            except (TypeError, ValueError):
                payload = None
            search_url = None
            if isinstance(payload, dict):
                for block in payload.get("blocks", []):
                    if not isinstance(block, dict):
                        continue
                    block_params = block.get("params")
                    if isinstance(block_params, dict) and block_params.get("url"):
                        search_url = block_params["url"]
                        break
                if search_url:
                    target = urljoin(self.YANDEX_UPLOAD_URL, search_url)
                    result_page = self.session.get(target, timeout=10)
                    if result_page.status_code == 200 and result_page.text:
                        if self._is_block_page(result_page.text):
                            logging.warning("Yandex visual upload follow-up returned a CAPTCHA/block page")
                            return []
                        result_text += "\n" + result_page.text
                    else:
                        logging.warning("Yandex visual upload follow-up returned HTTP %s",
                                        result_page.status_code)
            candidates.extend(self._yandex_original_urls(result_text))
        except Exception as exc:
            logging.warning("Yandex visual upload error: %s", exc)
        return list(dict.fromkeys(candidates))

    def search_yandex(self, image_url):
        candidates = []
        url = f"https://yandex.com/images/search?rpt=imageview&url={quote_plus(image_url)}"
        try:
            resp = self.session.get(url, timeout=10)
            if resp.status_code == 200 and resp.text:
                if self._is_block_page(resp.text):
                    logging.warning("Yandex URL reverse search returned a CAPTCHA/block page")
                    return []
                candidates.extend(self._yandex_original_urls(resp.text))
            else:
                logging.warning("Yandex URL reverse search returned HTTP %s or an empty response",
                                resp.status_code)
        except Exception as exc:
            logging.warning("Yandex reverse search error: %s", exc)
        return candidates

    def search_google_lens(self, image_url):
        candidates = []
        url = f"https://lens.google.com/uploadbyurl?url={quote_plus(image_url)}"
        try:
            resp = self.session.get(url, timeout=10)
            if resp.status_code == 200 and resp.text:
                if self._is_block_page(resp.text):
                    logging.warning("Google Lens URL search returned a CAPTCHA/block page")
                    return []
                matches = re.findall(r'https?://[^"\s<>]+?\.(?:jpg|jpeg|png|webp)(?:\?[^"\s<>]*)?',
                                     resp.text, re.IGNORECASE)
                candidates.extend(matches[:10])
            else:
                logging.warning("Google Lens URL search returned HTTP %s or an empty response",
                                resp.status_code)
        except Exception as exc:
            logging.warning("Google Lens reverse search error: %s", exc)
        return candidates

    def search_bing(self, image_url):
        candidates = []
        # Bing's documented multipart upload endpoint is the Visual Search API and
        # requires a subscription key. The consumer upload flow is JS/redirect driven,
        # so URL search remains the reliable unauthenticated fallback here.
        url = f"https://www.bing.com/images/searchbyimage?cbir=sbi&imgurl={quote_plus(image_url)}"
        try:
            resp = self.session.get(url, timeout=10)
            if resp.status_code == 200 and resp.text:
                if self._is_block_page(resp.text):
                    logging.warning("Bing URL reverse search returned a CAPTCHA/block page")
                    return []
                matches = re.findall(r'murl(?:&quot;|")\s*:\s*(?:&quot;|")(https?://[^"&]+)',
                                     resp.text)
                candidates.extend(html_lib.unescape(m).replace("\\/", "/") for m in matches[:10])
            else:
                logging.warning("Bing URL reverse search returned HTTP %s or an empty response",
                                resp.status_code)
        except Exception as exc:
            logging.warning("Bing Visual Search error: %s", exc)
        return candidates

    def find_better_version(self, image_bytes, current_size, current_phash, original_url=None):
        """Return bytes of a larger copy of the SAME photo, or None; never raise."""
        try:
            logging.info("Reverse search for higher-res copy (current %.1f KB)...", current_size / 1024)
            candidates = []

            # Primary strategy: upload actual downloaded bytes. URL-only search can fail
            # when the forum/hoster URL is a page, redirect, or protected thumbnail.
            try:
                candidates.extend(self.search_yandex_upload(image_bytes))
            except Exception as exc:
                logging.warning("Yandex visual upload failed non-fatally: %s", exc)

            # Fallback: preserve the existing three-engine URL searches.
            if original_url:
                for search in (self.search_yandex, self.search_google_lens, self.search_bing):
                    try:
                        candidates.extend(search(original_url))
                    except Exception as exc:
                        logging.warning("Reverse-search fallback failed non-fatally: %s", exc)

            unique_candidates = []
            seen = set()
            for candidate in candidates:
                if not isinstance(candidate, str):
                    continue
                candidate = html_lib.unescape(candidate.strip()).replace("\\/", "/")
                if (not candidate.startswith(("http://", "https://"))
                        or candidate == original_url or candidate in seen):
                    continue
                seen.add(candidate)
                unique_candidates.append(candidate)

            best, best_size, tried = None, current_size, 0
            for url in unique_candidates:
                if tried >= MAX_UPGRADE_CANDIDATES:
                    break
                tried += 1
                try:
                    head = self.session.head(url, timeout=5, allow_redirects=True)
                    if head.status_code != 200:
                        continue
                    head_type = head.headers.get("Content-Type", "").lower()
                    if head_type and "image" not in head_type:
                        continue
                    head_length = head.headers.get("Content-Length")
                    if head_length:
                        try:
                            if int(head_length) <= best_size:
                                continue
                        except (TypeError, ValueError):
                            pass

                    response = self.session.get(url, timeout=20)
                    if response.status_code != 200:
                        continue
                    data = response.content
                    response_type = response.headers.get("Content-Type", "").lower()
                    if response_type and "image" not in response_type:
                        continue
                    if len(data) <= best_size or not image_ok(data):
                        continue
                    # Compare and save the full candidate download, not the reduced query copy.
                    if phash_of(data) - current_phash > PHASH_SIMILARITY_THRESHOLD:
                        continue
                    best, best_size = data, len(data)
                    logging.info("Found superior candidate (%.1f KB): %s", best_size / 1024, url)
                except Exception as exc:
                    logging.debug("Skipping reverse-search candidate %s: %s", url, exc)
            return best
        except Exception as exc:
            logging.warning("Higher-resolution reverse search failed non-fatally: %s", exc)
            return None


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
    data.setdefault("thread_attempts", {})   # thread url -> times it was tried with failures
    data.setdefault("blocked_urls", [])      # links on BLOCKED_HOSTS that are already in blocked_links.txt
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
                    meta = et.get_tags([full], tags=["XMP:Subject", "IPTC:Keywords"])
                    row = meta[0] if meta else {}
                    for key in ("XMP:Subject", "IPTC:Keywords"):
                        value = row.get(key, [])
                        tags += [str(k) for k in (value if isinstance(value, list) else [value])]
                    tags = list(dict.fromkeys(tags))
                except Exception:
                    pass
                if not tags and celebrity:             # saved but never tagged (the scraper was stopped mid-save): tag it with the person
                    tags = [celebrity]
                    embed_metadata(full, tags)
                with open(full, "rb") as f:
                    ph = phash_of(f.read())
                record_image(full, celebrity, tags, category=category, phash=ph)
                added += 1
            except Exception as e:
                logging.error("Reindex skipped %s: %s", full, e)
    logging.info("Reindex complete: %d added", added)


_canon = {}   # lower-case name -> the one spelling used for that person


def canonical_person(name):
    """One spelling per person, whatever a title's capitalisation ("Mckenna Grace" and "McKenna Grace" are two folders on the NAS, which is
    case sensitive, but one folder to Windows, which isn't): the spelling seen first, or at start-up the one most pictures use."""
    return _canon.setdefault(name.lower(), name)


def most_used_spellings(counts):
    """`counts` are (name, number of pictures). Returns {lower-case name: the spelling most pictures use} (a tie goes to the later spelling
    alphabetically, so it is the same every time)."""
    best = {}
    for name, n in counts:
        key = name.lower()
        if key not in best or (n, name) > (best[key][1], best[key][0]):
            best[key] = (name, n)
    return {k: v[0] for k, v in best.items()}


def load_canonical_names():
    """Fills the spellings from the index, then from the folders that are not in it."""
    with closing(_library_connect()) as conn:
        counts = conn.execute("SELECT celebrity, COUNT(*) FROM images WHERE celebrity IS NOT NULL GROUP BY celebrity").fetchall()
    _canon.clear()
    _canon.update(most_used_spellings(counts))
    try:
        for entry in os.listdir(DOWNLOAD_DIR):
            if os.path.isdir(os.path.join(DOWNLOAD_DIR, entry)):
                _canon.setdefault(entry.lower(), entry)
    except OSError:
        pass


def plan_name_fixes(rows, people_for_title, canon=None):
    """Which indexed pictures sit under the wrong person's name. `rows` are (id, rel_path, celebrity, tags_json, thread_title);
    `people_for_title` turns a thread title into the people it names (the first is the main one). Returns one dict per picture to
    fix: where it is, where it should be, and its corrected tag list. A picture filed under any person the title names is right
    (a two-person post has a copy in each folder). Pictures with no thread title (added by --reindex) are never touched."""
    canon = canon or (lambda name: name)
    plan = []
    for image_id, rel_path, celebrity, tags_json, thread_title in rows:
        if not thread_title or not celebrity:
            continue
        people = [canon(p) for p in people_for_title(re.sub(r"\[.*?\]|\(.*?\)", "", thread_title).strip())]
        correct = people[0] if people else None
        if not correct or celebrity in people:
            continue
        parts = rel_path.split("/")
        if len(parts) < 2 or parts[0] != sanitize_filename(celebrity):
            continue                                   # not filed where the index says: leave it alone
        try:
            tags = json.loads(tags_json) if tags_json else []
        except ValueError:
            tags = []
        fixed = list(dict.fromkeys(correct if t == celebrity else t for t in tags))
        if correct not in fixed:
            fixed.insert(0, correct)
        plan.append({"id": image_id, "old_rel": rel_path, "new_rel": "/".join([sanitize_filename(correct)] + parts[1:]),
                     "old_name": celebrity, "new_name": correct, "tags": fixed})
    return plan


def fix_celebrity_names(dry_run=False):
    """Moves pictures filed under a wrong name (made before name_from_title) into the right folder, and corrects the name in
    their tags and in the index. Runs at every start and is a no-op once everything is right.
        python phun_scraper.py --fix-names --dry-run     (shows what it would do, changes nothing)"""
    load_canonical_names()
    with closing(_library_connect()) as conn:
        rows = conn.execute("SELECT id, rel_path, celebrity, tags, thread_title FROM images").fetchall()
    plan = plan_name_fixes(rows, people_from_title, canonical_person)
    if not plan:
        return 0
    logging.info("Name fix: %d pictures are filed under the wrong name", len(plan))
    moved, old_dirs = 0, set()
    for item in plan:
        old_path = os.path.join(DOWNLOAD_DIR, *item["old_rel"].split("/"))
        new_path = os.path.join(DOWNLOAD_DIR, *item["new_rel"].split("/"))
        if dry_run:
            logging.info("Would move %s -> %s (tags: %s)", item["old_rel"], item["new_rel"], ", ".join(item["tags"]))
            continue
        try:
            if os.path.exists(old_path):
                if os.path.exists(new_path):            # same file name already there: keep both
                    name, ext = os.path.splitext(new_path)
                    new_path = f"{name}_{hashlib.md5(item['old_rel'].encode()).hexdigest()[:6]}{ext}"
                os.makedirs(os.path.dirname(new_path), exist_ok=True)
                os.rename(old_path, new_path)
            elif not os.path.exists(new_path):
                logging.warning("Name fix: %s is missing, skipped", item["old_rel"])
                continue
            embed_metadata(new_path, item["tags"])      # failures are logged and counted there; the move still stands
            new_rel = rel_library_path(new_path)
            with db_lock, closing(_library_connect()) as conn, conn:
                conn.execute("UPDATE images SET rel_path=?, celebrity=?, tags=? WHERE id=?",
                             (new_rel, item["new_name"], json.dumps(item["tags"], ensure_ascii=False), item["id"]))
                conn.execute("DELETE FROM image_tags WHERE image_id=?", (item["id"],))
                conn.executemany("INSERT OR IGNORE INTO image_tags(image_id, tag) VALUES (?, ?)",
                                 [(item["id"], t) for t in item["tags"]])
            old_dirs.add(os.path.dirname(old_path))
            moved += 1
        except Exception as e:
            logging.error("Name fix failed for %s: %s", item["old_rel"], e)
    for folder in sorted(old_dirs, key=len, reverse=True):   # tidy: only folders that are now empty
        for d in (folder, os.path.dirname(folder)):
            try:
                if os.path.abspath(d) != os.path.abspath(DOWNLOAD_DIR):
                    os.rmdir(d)
            except OSError:
                pass
    logging.info("Name fix: %d of %d pictures moved and re-tagged", moved, len(plan))
    return moved


def plan_second_people(rows, people_for_title, existing_rel_paths, canon=None):
    """Pictures from a thread that names two people ("X and Y ...") that are filed under X only. `rows` are (id, rel_path, celebrity,
    category, tags_json, thread_title, source_url, thread_url, post_date, phash, upgraded). For each picture that has no copy under the
    other person yet it gives the copy to make and the tag list (both names first) that the picture and its copy should carry."""
    canon = canon or (lambda name: name)
    plan = []
    for (image_id, rel_path, celebrity, category, tags_json, thread_title, source_url, thread_url, post_date, phash, upgraded) in rows:
        if not thread_title or not celebrity:
            continue
        people = [canon(p) for p in people_for_title(re.sub(r"\[.*?\]|\(.*?\)", "", thread_title).strip())]
        if len(people) < 2 or celebrity != people[0]:
            continue
        parts = rel_path.split("/")
        if len(parts) < 2 or parts[0] != sanitize_filename(celebrity):
            continue
        try:
            tags = json.loads(tags_json) if tags_json else []
        except ValueError:
            tags = []
        tags = list(dict.fromkeys(people + [t for t in tags if t not in people]))
        for other in people[1:]:
            new_rel = "/".join([sanitize_filename(other)] + parts[1:])
            if new_rel in existing_rel_paths:
                continue
            plan.append({"id": image_id, "old_rel": rel_path, "new_rel": new_rel, "person": other, "category": category, "tags": tags,
                         "source_url": source_url, "thread_title": thread_title, "thread_url": thread_url, "post_date": post_date,
                         "phash": phash, "upgraded": bool(upgraded)})
    return plan


def fix_second_people(dry_run=False):
    """Gives the second person of a two-person thread a copy of each picture already saved under the first, and puts both names in the
    tags of the picture and its copy. Runs at every start and does nothing once every copy is there.
        python phun_scraper.py --fix-pairs [--dry-run]"""
    with closing(_library_connect()) as conn:
        rows = conn.execute("SELECT id, rel_path, celebrity, category, tags, thread_title, source_url, thread_url, post_date, phash, upgraded "
                            "FROM images").fetchall()
        existing = {r[1] for r in rows}
    plan = plan_second_people(rows, people_from_title, existing, canonical_person)
    if not plan:
        return 0
    logging.info("Pairs: %d copies to make for the second person of two-person threads", len(plan))
    made = 0
    for item in plan:
        old_path = os.path.join(DOWNLOAD_DIR, *item["old_rel"].split("/"))
        new_path = os.path.join(DOWNLOAD_DIR, *item["new_rel"].split("/"))
        if dry_run:
            logging.info("Would copy %s -> %s (tags: %s)", item["old_rel"], item["new_rel"], ", ".join(item["tags"]))
            continue
        try:
            if not os.path.exists(old_path):
                logging.warning("Pairs: %s is missing, skipped", item["old_rel"])
                continue
            embed_metadata(old_path, item["tags"])           # the original carries both names now
            with db_lock, closing(_library_connect()) as conn, conn:
                conn.execute("UPDATE images SET tags=? WHERE id=?", (json.dumps(item["tags"], ensure_ascii=False), item["id"]))
                conn.execute("DELETE FROM image_tags WHERE image_id=?", (item["id"],))
                conn.executemany("INSERT OR IGNORE INTO image_tags(image_id, tag) VALUES (?, ?)", [(item["id"], t) for t in item["tags"]])
            os.makedirs(os.path.dirname(new_path), exist_ok=True)
            shutil.copy2(old_path, new_path)
            record_image(new_path, item["person"], item["tags"], category=item["category"], source_url=item["source_url"],
                         thread_title=item["thread_title"], thread_url=item["thread_url"], post_date=item["post_date"],
                         phash=item["phash"], upgraded=item["upgraded"])
            made += 1
        except Exception as e:
            logging.error("Pairs failed for %s: %s", item["old_rel"], e)
    logging.info("Pairs: %d of %d copies made", made, len(plan))
    return made


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
            "metadata_failed": 0,         # saved, but the tags could not be written or read back
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


NAME_PARTICLES = {"de", "del", "della", "di", "da", "van", "von", "der", "den", "la", "le", "du", "bin", "al", "el", "ter", "ten"}

# Verbs that titles write with a capital ("Chloe Bailey Sings the national anthem"), so they look like part of the name.
# Only ever removed from the END of a name that has another word before it.
NAME_VERBS = {
    "sings", "performs", "attends", "arrives", "leaves", "walks", "steps", "stuns", "poses", "shows", "wears", "dines", "heads",
    "hits", "visits", "celebrates", "takes", "goes", "gets", "joins", "enjoys", "films", "promotes", "launches", "unveils",
    "appears", "looks", "wins", "hosts", "presents", "accepts", "greets", "departs", "spotted", "seen", "out", "pictured",
    "flaunts", "rocks", "slays", "wows", "sizzles", "models", "shops", "lunches", "exits", "shoots", "dazzles", "debuts",
    "receives", "reveals", "supports", "cheers", "struts", "stops", "catches", "grabs", "picks", "runs", "rides", "plays",
    "smiles", "waves", "arriving", "leaving", "walking", "heading", "attending", "performing",
    # the plain form, for a title like "The Pussycat Dolls Perform At The 3Arena" (only ones that are not also surnames)
    "perform", "attend", "arrive", "leave", "walk", "pose", "wear", "dine", "visit", "celebrate", "promote", "launch", "unveil",
    "appear", "greet", "depart", "flaunt", "debut", "receive", "reveal", "enjoy", "join", "cheer", "strut", "stun",
}


def name_from_title(clean_title):
    """The person's name at the start of a thread title. Names are capitalised words, and the title carries on in lower
    case ("Kelly Brook for NEXT UK", "Famke Janssen out in NYC", "Carrie-Anne Moss and Clark Backo ..."), so the name is
    the leading run of capitalised words when something lower case ends it. Otherwise: the text before " at / in / @ / on / - ",
    or the first two words. A name of more than four words is cut to two."""
    clean_title = clean_title.replace("‘", "'").replace("’", "'")   # one spelling of D'Amelio, whatever the forum typed
    words = clean_title.split()
    run = []
    stopped = False
    for i, word in enumerate(words):
        bare = word.lstrip("'\"")
        if bare[:1].isupper() or (i > 0 and word.lower() in NAME_PARTICLES):
            run.append(word)
            if word.endswith(",") and len(run) >= 2:      # "Daisy Edgar-Jones, Esme ...": the name ends at the comma
                run[-1] = word.rstrip(",")
                stopped = True
                break
        else:
            stopped = True
            break
    while run and run[-1].lower() in NAME_PARTICLES:
        run.pop()
    while len(run) > 1 and run[-1].lower() in NAME_VERBS:
        run.pop()
    if run:
        run[-1] = re.sub(r"['’]s$", "", run[-1])
    if stopped and 1 <= len(run) <= 4:
        return " ".join(run)

    match = re.search(r"^(.*?)\s+(?:at|in|@|on|-)\s+", clean_title, re.IGNORECASE)
    if match:
        name = match.group(1).strip()
    else:
        name = " ".join(words[:2]) if len(words) >= 2 else clean_title
    parts = name.split()
    while len(parts) > 1 and parts[-1].lower() in NAME_VERBS:
        parts.pop()
    return " ".join(parts[:2]) if len(parts) > 4 else " ".join(parts)


def _second_person(words):
    """The name that starts `words` (the text after "and" / "&"): two to four capitalised words that something lower case, a comma or
    the end of the title closes. None when it isn't a name."""
    run = []
    for i, word in enumerate(words):
        bare = word.rstrip(",").lstrip("'\"")
        if bare[:1].isupper() or (i > 0 and word.lower() in NAME_PARTICLES):
            run.append(word.rstrip(","))
            if word.endswith(","):
                break
        else:
            break
    while run and run[-1].lower() in NAME_PARTICLES:
        run.pop()
    while len(run) > 1 and run[-1].lower() in NAME_VERBS:
        run.pop()
    if run:
        run[-1] = re.sub(r"['\u2019]s$", "", run[-1])
    return " ".join(run) if 2 <= len(run) <= 4 else None


def people_from_title(clean_title):
    """The people a thread title names: [first] or [first, second] for "X and Y ..." / "X & Y ..." / "X - and Y ...". The first is
    always name_from_title; a second person only counts when the name comes straight after "and" or "&"."""
    clean_title = clean_title.replace("\u2018", "'").replace("\u2019", "'")
    first = name_from_title(clean_title)
    words = clean_title.split()
    n = len(first.split())
    if [w.rstrip(",") for w in words[:n]] != [w.rstrip(",") for w in first.split()]:
        return [first]                       # the name came from the older rules; don't guess at a second
    rest = words[n:]
    if rest and rest[0] == "-":
        rest = rest[1:]
    if rest and rest[0].lower() in ("and", "&"):
        second = _second_person(rest[1:])
        if second and second.lower() != first.lower():
            return [first, second]
    return [first]


def extract_metadata_tags(title_text, post_text=""):
    clean_title = re.sub(r"\[.*?\]|\(.*?\)", "", title_text).strip()
    people = [canonical_person(p) for p in people_from_title(clean_title)]
    celeb_name = people[0]

    common_keywords = [
        "bikini", "lingerie", "swimsuit", "red carpet", "dress", "gown",
        "photoshoot", "street style", "casual", "beach", "runway",
        "fashion week", "magazine", "pregnant", "maternity", "baby bump", "swimwear",
    ]
    found = list(people)                     # everyone the title names is a tag
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

    # Every person the title names has to pass, not just the first.
    for celeb_name in people_from_title(re.sub(r"\[.*?\]|\(.*?\)", "", thread_title).strip()):
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
    # pixhost /show/ links end in .jpg but are HTML pages. The picture's server is named on the page (img1, img2, ...,
    # so it can't be guessed), and og:image there is only a thumbnail.
    if re.search(r"pixho(?:st)?\.[a-z]+/show/", link_url, re.IGNORECASE):
        try:
            resp = session.get(link_url, timeout=10)
            if resp.status_code == 200:
                img = BeautifulSoup(resp.text, "html.parser").select_one("img#image")
                if img and img.get("src"):
                    return img["src"]
        except requests.RequestException:
            pass
        return None

    if "turboimagehost.com" in urlparse(link_url).netloc.lower():
        try:
            resp = session.get(link_url, timeout=15, allow_redirects=True)
            if resp.status_code == 200 and resp.headers.get("cf-mitigated", "").lower() != "challenge":
                soup = BeautifulSoup(resp.text, "html.parser")
                image = soup.select_one("img#imageid, img.uimg")
                if image:
                    candidate = image.get("src") or image.get("data-src") or image.get("data-original")
                    if candidate:
                        return urljoin(resp.url, candidate)
                og = soup.select_one("meta[property='og:image']")
                if og and og.get("content"):
                    return urljoin(resp.url, og["content"])
        except requests.RequestException as e:
            logging.warning("TurboImageHost page resolution failed (%s): %s", link_url, e)
        return None

    if re.search(r"\.(jpg|jpeg|png|gif|webp)$", link_url, re.IGNORECASE):
        if "imagetwist.com" in link_url:
            return re.sub(r"/th/", "/i/", link_url)
        return link_url

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


IPTC_EXTS = (".jpg", ".jpeg", ".tif", ".tiff")  # IPTC keywords can only be written into these; PNG and WebP carry XMP only


def metadata_tags_for(file_path, keywords_list):
    """The tags to write for a file. XMP:Subject is written for every format (it is what Windows reads as Keywords for PNG
    and WebP); the IPTC keywords are only written where the format has them."""
    tags = {
        "XMP:Subject": keywords_list,
        "XMP:Description": ", ".join(keywords_list),
    }
    if os.path.splitext(file_path)[1].lower() in IPTC_EXTS:
        tags["IPTC:Keywords"] = keywords_list
        tags["EXIF:ImageDescription"] = ", ".join(keywords_list)
    return tags


def _as_list(value):
    if value is None:
        return []
    return [str(v) for v in value] if isinstance(value, (list, tuple)) else [str(value)]


def _write_and_read_back(file_path, tags, replace_xmp=False):
    """Writes `tags` with ExifTool and returns the XMP:Subject keywords (lower case) found in the file afterwards."""
    with ExifToolHelper() as et:
        if replace_xmp:
            et.execute("-m", "-overwrite_original", "-XMP:all=", file_path)
        params = ["-overwrite_original", "-m"] if replace_xmp else ["-overwrite_original"]
        et.set_tags([file_path], tags=tags, params=params)
        back = et.get_tags([file_path], tags=["XMP:Subject"])
    return {k.lower() for k in _as_list(back[0].get("XMP:Subject") if back else None)}


def failed_tag_paths(log_text):
    """The files the log says couldn't be tagged (its "Metadata error tagging <path>: ..." lines), once each, in order."""
    return list(dict.fromkeys(re.findall(r"Metadata error tagging (.+?\.[A-Za-z0-9]{3,4}): ", log_text)))


def retag_logged_failures():
    """At start-up, tries again on the files the log says couldn't be tagged (now with the fallback in embed_metadata). Files that have
    moved or gone since are skipped; the tags are the index's for that file, or the person's name."""
    try:
        with open(LOG_FILE, encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        return 0
    fixed = 0
    for path in failed_tag_paths(text):
        if not os.path.exists(path):
            continue
        rel = rel_library_path(path)
        with closing(_library_connect()) as conn:
            row = conn.execute("SELECT tags FROM images WHERE rel_path = ?", (rel,)).fetchone()
        try:
            tags = json.loads(row[0]) if row and row[0] else []
        except ValueError:
            tags = []
        if embed_metadata(path, tags or [rel.split("/")[0]]):
            fixed += 1
    if fixed:
        logging.info("Retagged %d picture(s) that couldn't be tagged before", fixed)
    return fixed


def embed_metadata(file_path, keywords_list):
    """Writes the tags into the file, reads them back, and returns True only if they are there.
    A failure is logged, shown in the scraper's activity list and counted (metadata_failed), because a file
    without its tags can't be found by tag in Ocula."""
    wanted = {k.lower() for k in keywords_list}
    with exif_lock:
        try:
            tags = metadata_tags_for(file_path, keywords_list)
            found = _write_and_read_back(file_path, tags)
            if not wanted <= found:
                # A few pictures arrive with an XMP block ExifTool can't edit (agency files whose XMP is malformed), so the write doesn't
                # take. Only then: remove that block and write again. Pictures whose XMP is fine are never touched this way.
                logging.info("Retagging %s without its existing XMP block", os.path.basename(file_path))
                found = _write_and_read_back(file_path, tags, replace_xmp=True)
            if not wanted <= found:
                raise ValueError("tags not found after writing: %s" % ", ".join(sorted(wanted - found)))
            return True
        except Exception as e:
            logging.error("Metadata error tagging %s: %s", file_path, e)
            status.bump("metadata_failed")
            status.add_event("ERROR", "Couldn't tag %s: %s" % (os.path.basename(file_path), e))
            return False


failures_lock = threading.Lock()


def log_failure(url, reason, meta=None, direct_url=None):
    """Says why a picture wasn't saved: a WARNING in scraper.log, and a line in failed_downloads.log next to the pictures
    (time, thread, link, the address tried if different, reason; tab separated)."""
    thread = (meta or {}).get("thread_title") or ""
    logging.warning("Not saved: %s | %s | %s", reason, url, thread)
    line = "\t".join([_iso(), thread, url, direct_url if direct_url and direct_url != url else "", reason.replace("\n", " ")])
    try:
        with failures_lock, open(FAILURES_FILE, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError as e:
        logging.warning("Couldn't write %s: %s", FAILURES_FILE, e)


def is_blocked_host(url):
    """True for a link on a host in BLOCKED_HOSTS."""
    host = urlparse(url).netloc.lower()
    return any(host == h or host.endswith("." + h) for h in BLOCKED_HOSTS)


blocked_lock = threading.Lock()


def note_blocked_links(links, people, meta, history):
    """Links on hosts that block scripts can't be saved here, so each is written once to blocked_links.txt (date, who, thread, link;
    tab separated) to be fetched by hand in a browser. Returns how many were new."""
    with history_lock:
        known = history.setdefault("blocked_urls", [])
        new = [link for link in dict.fromkeys(links) if link not in known]
        known.extend(new)
    if not new:
        return 0
    try:
        with blocked_lock, open(BLOCKED_FILE, "a", encoding="utf-8") as f:
            for link in new:
                f.write("\t".join([_iso(), ", ".join(people), meta.get("thread_title") or "", meta.get("thread_url") or "", link]) + "\n")
    except OSError as e:
        logging.warning("Couldn't write %s: %s", BLOCKED_FILE, e)
    logging.warning("%d picture(s) are on a host that blocks scripts (%s); listed in blocked_links.txt: %s",
                    len(new), ", ".join(BLOCKED_HOSTS), meta.get("thread_title") or "")
    return len(new)


def download_file(url, celeb_root_folder, history, keywords_list, meta=None, also_for=None):
    ok = _download_file(url, celeb_root_folder, history, keywords_list, meta, also_for)
    if ok is False:                                  # None means "blocked by a bot check": reported, but not a failure to retry
        status.bump("failed_downloads")
    return ok


def copy_for_person(src, folder, person, category, tags, meta, phash, upgraded, source_url):
    """Puts a copy of a saved picture in another person's folder (same category subfolder) and indexes it under that person. The tags are
    inside the file already, so the copy carries them. Returns the new path."""
    dest_dir = os.path.join(folder, category) if category else folder
    os.makedirs(dest_dir, exist_ok=True)
    dest = os.path.join(dest_dir, os.path.basename(src))
    if os.path.exists(dest):
        name, ext = os.path.splitext(os.path.basename(src))
        dest = os.path.join(dest_dir, f"{name}_{hashlib.md5(src.encode()).hexdigest()[:6]}{ext}")
    shutil.copy2(src, dest)
    record_image(dest, person, tags, category=category, source_url=source_url, thread_title=meta.get("thread_title"),
                 thread_url=meta.get("thread_url"), post_date=meta.get("post_date"), phash=phash, upgraded=upgraded)
    return dest


def _download_file(url, celeb_root_folder, history, keywords_list, meta=None, also_for=None):
    meta = meta or {}
    with history_lock:
        if url in history["downloaded_urls"]:
            return True

    direct_url = resolve_image_host_url(url)
    if not direct_url:
        log_failure(url, "no picture found at this link (the host page had no og:image, or didn't load)", meta)
        return False

    try:
        res = session.get(direct_url, stream=True, timeout=15)
        if res.headers.get("cf-mitigated") == "challenge":
            log_failure(url, "blocked by the host's bot check (a Cloudflare challenge); fetch it by hand in a browser", meta, direct_url)
            return None
        if res.status_code != 200 or "text/html" in res.headers.get("Content-Type", ""):
            log_failure(url, f"HTTP {res.status_code}, {res.headers.get('Content-Type', 'no content type')}", meta, direct_url)
            return False

        content = bytearray()
        for chunk in res.iter_content(chunk_size=8192):
            content.extend(chunk)
        content = bytes(content)

        if len(content) < MIN_FILE_SIZE_BYTES:
            log_failure(url, f"too small ({len(content)} bytes, minimum {MIN_FILE_SIZE_BYTES})", meta, direct_url)
            return False
        if not image_ok(content):
            log_failure(url, f"not a readable image ({len(content)} bytes)", meta, direct_url)
            return False

        current_phash = phash_of(content)

        upgraded = False
        # Upgrade to a higher-res copy of the same photo if this one is small
        if len(content) < UPGRADE_FILE_SIZE_THRESHOLD:
            better = MultiEngineReverseSearch(session).find_better_version(
                content, len(content), current_phash, original_url=direct_url
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
            current_int = int(str(current_phash), 16)
            known = hash_ints(history)
            if is_near_duplicate(current_int, known, PHASH_SIMILARITY_THRESHOLD):
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
            known.append(current_int)
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
        for other_folder, other_name in also_for or []:
            try:
                copy_for_person(file_path, other_folder, other_name, subfolder_name, keywords_list, meta, current_phash, upgraded, url)
                logging.info("Also saved for %s: %s", other_name, os.path.basename(file_path))
            except Exception as e:
                logging.error("Couldn't save a copy for %s (%s): %s", other_name, os.path.basename(file_path), e)
        return True

    except Exception as e:
        log_failure(url, f"error: {type(e).__name__}: {e}", meta, direct_url)
        return False


SUPPORTED_HOSTS = [
    "imagetwist.com", "pixhost.to", "pixhost.cc", "pixho.st", "pixhost.org", "imgbox.com",
    "imagevenue.com", "postimg.cc", "turboimagehost.com",
]


def listing_threads(soup):
    """(title, href) for each normal thread in a forum listing, newest activity first. Handles XenForo 2 (structItem) and
    XenForo 1 (discussionListItem, which phun.org runs); pinned XenForo 1 threads (rules, requests) are skipped."""
    found = []
    for a_tag in soup.select("div.structItem-title a[data-tp-primary='on']"):
        if a_tag.get("href"):
            found.append((a_tag.text.strip(), a_tag["href"]))
    for li in soup.select("li.discussionListItem"):
        if "sticky" in (li.get("class") or []):
            continue
        for a_tag in li.select("h3.title a[href]"):
            if a_tag["href"].startswith("threads/"):
                found.append((a_tag.text.strip(), a_tag["href"]))
    return found


def post_bodies(soup):
    """The message bodies of a thread page (XenForo 2 `article.message-body`, XenForo 1 `blockquote.messageText`)."""
    return soup.select("article.message-body, blockquote.messageText")


def direct_from_thumbnail(href, thumb_src):
    """imagetwist links are HTML pages even when they end in .jpg; the picture itself is the thumbnail's address with
    /th/ changed to /i/. Returns None when this isn't an imagetwist link with a thumbnail."""
    if thumb_src and "imagetwist.com" in href.lower() and "/th/" in thumb_src:
        return thumb_src.replace("/th/", "/i/", 1)
    return None


def forum_page_url(page):
    """Page N of the forum's thread list (XenForo: .../celebrity-photos.15/page-2)."""
    return FORUM_URL if page <= 1 else f"{FORUM_URL}page-{page}"


def thread_page_url(thread_url, page):
    """Page N of a thread's replies."""
    return thread_url if page <= 1 else f"{thread_url.rstrip('/')}/page-{page}"


def last_page_number(soup):
    """How many pages a list or thread has (1 when there is no page navigation)."""
    nav = soup.select_one("div.PageNav")
    try:
        return max(1, int(nav.get("data-last"))) if nav is not None else 1
    except (TypeError, ValueError):
        return 1


def enough_free_space(path, min_gb):
    """False when the volume holding `path` has less than `min_gb` GB free, so the scraper stops before it fills the NAS."""
    return shutil.disk_usage(path).free >= min_gb * 1_000_000_000


def process_thread(thread_title, thread_url, history):
    """Returns True when the thread is finished (don't retry), False to retry next cycle (a failed fetch, or pictures that failed, up to
    THREAD_ATTEMPTS times in all)."""
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
    posts = post_bodies(soup)
    post_text = posts[0].get_text() if posts else ""

    # Every page of replies, up to MAX_THREAD_PAGES (the first page is only part of a long thread).
    incomplete = False
    total_pages = last_page_number(soup)
    for n in range(2, min(total_pages, MAX_THREAD_PAGES) + 1):
        try:
            more = session.get(thread_page_url(thread_url, n), timeout=15)
        except Exception as e:
            logging.warning("Thread page %d failed (%s): %s", n, thread_url, e)
            incomplete = True
            break
        if more.status_code != 200:
            logging.warning("Thread page %d: HTTP %d (%s)", n, more.status_code, thread_url)
            incomplete = True
            break
        posts = posts + post_bodies(BeautifulSoup(more.text, "html.parser"))
        time.sleep(0.5)
    if total_pages > MAX_THREAD_PAGES:
        logging.info("Read the first %d of %d pages of this thread", MAX_THREAD_PAGES, total_pages)

    celeb_name, keywords_list = extract_metadata_tags(thread_title, post_text)
    people = [canonical_person(p) for p in people_from_title(re.sub(r"\[.*?\]|\(.*?\)", "", thread_title).strip())]
    celeb_folder = os.path.join(DOWNLOAD_DIR, sanitize_filename(celeb_name))
    os.makedirs(celeb_folder, exist_ok=True)
    others = [(os.path.join(DOWNLOAD_DIR, sanitize_filename(p)), p) for p in people[1:]]   # a two-person thread is saved for both
    for folder, _ in others:
        os.makedirs(folder, exist_ok=True)

    links, blocked = [], []
    for post in posts:
        for a in post.find_all("a", href=True):
            href = a["href"]
            if is_blocked_host(href):
                if a.find("img"):                    # a picture link (its thumbnail is inside), not a link to the site
                    blocked.append(href)
                continue
            if any(h in href.lower() for h in SUPPORTED_HOSTS) or re.search(
                r"\.(jpg|jpeg|png|webp)$", href, re.IGNORECASE
            ):
                thumb = a.find("img")
                links.append(direct_from_thumbnail(href, thumb.get("src") if thumb else None) or href)
    links = list(dict.fromkeys(links))

    post_dt = parse_title_date(thread_title)
    meta = {
        "celebrity": celeb_name,
        "thread_title": thread_title,
        "thread_url": thread_url,
        "post_date": post_dt.date().isoformat() if post_dt else None,
    }

    note_blocked_links(blocked, people, meta, history)
    status.update(thread_links_total=len(links))
    logging.info("Folder: '%s'%s | Tags: %s | Links found: %d", celeb_name,
                 " (and " + ", ".join(p for _, p in others) + ")" if others else "", keywords_list, len(links))

    failures = 0
    with ThreadPoolExecutor(max_workers=MAX_DOWNLOAD_WORKERS) as executor:
        futures = [
            executor.submit(download_file, link, celeb_folder, history, keywords_list, meta, others)
            for link in links
        ]
        for future in futures:
            try:
                if future.result() is False:
                    failures += 1
            except Exception as e:
                logging.error("Worker error: %s", e)
                failures += 1
            status.bump("thread_links_done")

    if failures or incomplete:
        with history_lock:
            tries = history["thread_attempts"][thread_url] = history["thread_attempts"].get(thread_url, 0) + 1
        if tries < THREAD_ATTEMPTS:
            logging.info("%d picture(s) not saved%s: will try this thread again next scan (attempt %d of %d)",
                         failures, " and some pages not read" if incomplete else "", tries, THREAD_ATTEMPTS)
            return False
        logging.warning("Giving up on the %d picture(s) of this thread that can't be saved (tried %d times)", failures, tries)
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
        pending, seen = [], set()
        pages_read = 0
        paused = False
        for page in range(1, FORUM_PAGES + 1):
            resp = session.get(forum_page_url(page), timeout=15)
            if resp.status_code != 200:
                logging.error("Forum list page %d failed: HTTP %d", page, resp.status_code)
                if page == 1:
                    status.update(last_error=f"Forum list failed: HTTP {resp.status_code}")
                    return
                break                              # carry on with the pages that were read
            pages_read += 1
            soup = BeautifulSoup(resp.text, "html.parser")
            for title, href in listing_threads(soup):
                url = urljoin(FORUM_BASE, href)
                with history_lock:
                    if url in history["processed_threads"] or url in seen:
                        continue
                seen.add(url)
                pending.append((title, url))
            if page >= last_page_number(soup):
                break
            time.sleep(1)
        if not pending and not history["processed_threads"]:
            logging.warning("The forum list had no threads at all; its markup may have changed")

        status.update(threads_total=len(pending), force=True)
        logging.info("%d new threads to process (from %d page%s of the forum list)", len(pending), pages_read, "" if pages_read == 1 else "s")

        for title, url in pending:
            if not enough_free_space(DOWNLOAD_DIR, MIN_FREE_GB):
                free = shutil.disk_usage(DOWNLOAD_DIR).free / 1_000_000_000
                message = f"Paused: only {free:.0f} GB free on the downloads volume (the limit is {MIN_FREE_GB:.0f} GB). Free up space to carry on."
                logging.error(message)
                status.update(last_error=message)
                paused = True
                break
            if process_thread(title, url, history):
                with history_lock:
                    history["processed_threads"].append(url)
            save_history(history)
            status.bump("threads_done")
            time.sleep(1)
        if not paused:
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
    try:
        fix_celebrity_names()
        fix_second_people()
        reindex_existing()                  # pictures saved but never indexed (the scraper was stopped mid-save)
        retag_logged_failures()             # pictures the log says could not be tagged
    except Exception as e:                  # never stop the scraper over a tidy-up
        logging.error("Name fix failed: %s", e)
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
    elif "--fix-names" in sys.argv:
        init_library()
        fix_celebrity_names(dry_run="--dry-run" in sys.argv)
    elif "--fix-pairs" in sys.argv:
        init_library()
        load_canonical_names()
        fix_second_people(dry_run="--dry-run" in sys.argv)
    else:
        main()
