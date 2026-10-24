"""Tests for the one-time name clean-up (plan_name_fixes, fix_celebrity_names).

phun_scraper.py can't be imported here, so the functions are pulled out with ast, as in test_parsing.py. The clean-up runs
against a real SQLite file and real files in a temporary folder; ExifTool is replaced by a recorder.

    python -m unittest tools/phun-scraper/test_namefix.py
"""
import ast
import hashlib
import json
import logging
import os
import re
import shutil
import sqlite3
import tempfile
import threading
import unicodedata
import unittest
from datetime import datetime, timezone
from contextlib import closing

SOURCE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "phun_scraper.py")
FUNCS = {"sanitize_filename", "rel_library_path", "name_from_title", "people_from_title", "_second_person", "plan_name_fixes",
         "fix_celebrity_names", "plan_second_people", "fix_second_people", "canonical_person", "most_used_spellings",
         "load_canonical_names", "failed_tag_paths", "retag_logged_failures", "name_key", "spelling_rank", "parse_aliases",
         "remove_empty_folders", "plan_folder_renames", "fix_folder_names"}
SCHEMA = """CREATE TABLE images (id INTEGER PRIMARY KEY AUTOINCREMENT, rel_path TEXT NOT NULL UNIQUE, celebrity TEXT, category TEXT,
 tags TEXT, source_url TEXT, thread_title TEXT, thread_url TEXT, post_date TEXT, phash TEXT, upgraded INTEGER NOT NULL DEFAULT 0,
 downloaded_at TEXT);
CREATE TABLE image_tags (image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE, tag TEXT NOT NULL, PRIMARY KEY (image_id, tag));"""


def load(root, tagged):
    with open(SOURCE, encoding="utf-8") as f:
        tree = ast.parse(f.read())
    keep = [n for n in tree.body if (isinstance(n, ast.FunctionDef) and n.name in FUNCS)
            or (isinstance(n, ast.Assign) and getattr(n.targets[0], "id", None) in ("NAME_PARTICLES", "NAME_VERBS", "NAME_RELATIONS", "_canon", "_aliases"))]
    db = os.path.join(root, "ocula_library.db")

    def connect():
        conn = sqlite3.connect(db)
        conn.execute("PRAGMA foreign_keys = ON")
        return conn

    def embed(path, tags):
        tagged.append((path, list(tags)))
        return True

    def record(path, celebrity, tags, category=None, source_url=None, thread_title=None, thread_url=None, post_date=None, phash=None,
               upgraded=False):
        with closing(connect()) as c, c:
            cur = c.execute("INSERT INTO images (rel_path, celebrity, category, tags, thread_title) VALUES (?,?,?,?,?)",
                            (scope["rel_library_path"](path), celebrity, category, json.dumps(tags), thread_title))
            c.executemany("INSERT INTO image_tags VALUES (?,?)", [(cur.lastrowid, t) for t in tags])

    scope = {"os": os, "re": re, "json": json, "hashlib": hashlib, "logging": logging, "closing": closing, "shutil": shutil,
             "unicodedata": unicodedata, "datetime": datetime, "timezone": timezone, "ALIASES_FILE": os.path.join(root, "name_aliases.txt"),
             "DOWNLOAD_DIR": root, "db_lock": threading.Lock(), "_library_connect": connect, "embed_metadata": embed,
             "record_image": record}
    exec(compile(ast.Module(body=keep, type_ignores=[]), SOURCE, "exec"), scope)
    return scope, db


class NameFixTests(unittest.TestCase):
    def setUp(self):
        logging.disable(logging.CRITICAL)
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.tagged = []
        self.s, self.db = load(self.root, self.tagged)
        with closing(sqlite3.connect(self.db)) as c, c:
            c.executescript(SCHEMA)

    def tearDown(self):
        self.tmp.cleanup()

    def add(self, rel, celebrity, tags, title, create=True):
        if create:
            path = os.path.join(self.root, *rel.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f:
                f.write(b"x" + rel.encode())
        with closing(sqlite3.connect(self.db)) as c, c:
            cur = c.execute("INSERT INTO images (rel_path, celebrity, tags, thread_title) VALUES (?,?,?,?)",
                            (rel, celebrity, json.dumps(tags), title))
            c.executemany("INSERT INTO image_tags VALUES (?,?)", [(cur.lastrowid, t) for t in tags])

    def rows(self):
        with closing(sqlite3.connect(self.db)) as c:
            return c.execute("SELECT rel_path, celebrity, tags FROM images ORDER BY id").fetchall()

    def exists(self, rel):
        return os.path.exists(os.path.join(self.root, *rel.split("/")))

    def test_pictures_move_to_the_right_folder_and_get_the_right_tags(self):
        title = "Kelly Brook for NEXT UK - Swimwear Collection 2026"
        self.add("Kelly Brook for NEXT UK/a.jpg", "Kelly Brook for NEXT UK", ["Kelly Brook for NEXT UK", "Bikini"], title)
        self.add("Kelly Brook for NEXT UK/Bikini/b.jpg", "Kelly Brook for NEXT UK", ["Kelly Brook for NEXT UK", "Bikini"], title)
        self.assertEqual(self.s["fix_celebrity_names"](), 2)

        self.assertTrue(self.exists("Kelly Brook/a.jpg"))
        self.assertTrue(self.exists("Kelly Brook/Bikini/b.jpg"))
        self.assertFalse(os.path.exists(os.path.join(self.root, "Kelly Brook for NEXT UK")))   # empty old folder removed
        self.assertEqual([r[:2] for r in self.rows()], [("Kelly Brook/a.jpg", "Kelly Brook"), ("Kelly Brook/Bikini/b.jpg", "Kelly Brook")])
        self.assertEqual(json.loads(self.rows()[0][2]), ["Kelly Brook", "Bikini"])
        self.assertEqual([t for _, t in self.tagged], [["Kelly Brook", "Bikini"]] * 2)        # written into the files
        with closing(sqlite3.connect(self.db)) as c:
            self.assertEqual(sorted(r[0] for r in c.execute("SELECT tag FROM image_tags WHERE image_id=1")), ["Bikini", "Kelly Brook"])

    def test_running_it_again_does_nothing(self):
        self.add("Famke Janssen out/a.jpg", "Famke Janssen out", ["Famke Janssen out"], "Famke Janssen out in New York 10/20/2026 x1")
        self.assertEqual(self.s["fix_celebrity_names"](), 1)
        n = len(self.tagged)
        self.assertEqual(self.s["fix_celebrity_names"](), 0)
        self.assertEqual(len(self.tagged), n)

    def test_correct_names_and_pictures_without_a_title_are_left_alone(self):
        self.add("Miranda Kerr/a.jpg", "Miranda Kerr", ["Miranda Kerr"], "Miranda Kerr at the 2026 Awards")
        self.add("Margot Robbie/b.jpg", "Margot Robbie", [], None)           # added by --reindex: no thread title
        self.assertEqual(self.s["fix_celebrity_names"](), 0)
        self.assertTrue(self.exists("Miranda Kerr/a.jpg") and self.exists("Margot Robbie/b.jpg"))

    def test_joining_an_existing_folder_never_overwrites_a_file(self):
        self.add("Kristen Bell/a.jpg", "Kristen Bell", ["Kristen Bell"], "Kristen Bell at a party")
        self.add("Kristen Bell for Bustle/a.jpg", "Kristen Bell for Bustle", ["Kristen Bell for Bustle"], "Kristen Bell for Bustle - October 2026")
        self.assertEqual(self.s["fix_celebrity_names"](), 1)
        with open(os.path.join(self.root, "Kristen Bell", "a.jpg"), "rb") as f:
            self.assertEqual(f.read(), b"xKristen Bell/a.jpg")                  # the original is untouched
        moved = [r[0] for r in self.rows() if r[0] != "Kristen Bell/a.jpg"][0]
        self.assertRegex(moved, r"^Kristen Bell/a_[0-9a-f]{6}\.jpg$")
        self.assertTrue(self.exists(moved))

    def test_a_folder_that_still_has_other_files_is_not_removed(self):
        self.add("Zara Larsson for The Times/a.jpg", "Zara Larsson for The Times", ["Zara Larsson for The Times"], "Zara Larsson for The Times - Oct 2026")
        with open(os.path.join(self.root, "Zara Larsson for The Times", "keep.txt"), "w") as f:
            f.write("mine")
        self.s["fix_celebrity_names"]()
        self.assertTrue(self.exists("Zara Larsson for The Times/keep.txt"))

    def test_an_interrupted_move_is_finished_next_time(self):
        # the file was already moved, but the index still has the old path
        self.add("Kylie Jenner arriving/a.jpg", "Kylie Jenner arriving", ["Kylie Jenner arriving"], "Kylie Jenner arriving at Baldi", create=False)
        os.makedirs(os.path.join(self.root, "Kylie Jenner"))
        with open(os.path.join(self.root, "Kylie Jenner", "a.jpg"), "wb") as f:
            f.write(b"x")
        self.assertEqual(self.s["fix_celebrity_names"](), 1)
        self.assertEqual(self.rows()[0][:2], ("Kylie Jenner/a.jpg", "Kylie Jenner"))

    def test_a_missing_file_is_skipped_not_invented(self):
        self.add("Kristen Bell for Bustle/a.jpg", "Kristen Bell for Bustle", ["Kristen Bell for Bustle"], "Kristen Bell for Bustle", create=False)
        self.assertEqual(self.s["fix_celebrity_names"](), 0)
        self.assertEqual(self.rows()[0][0], "Kristen Bell for Bustle/a.jpg")

    def test_dry_run_changes_nothing(self):
        self.add("Kelly Brook for NEXT UK/a.jpg", "Kelly Brook for NEXT UK", ["Kelly Brook for NEXT UK"], "Kelly Brook for NEXT UK - x")
        self.s["fix_celebrity_names"](dry_run=True)
        self.assertTrue(self.exists("Kelly Brook for NEXT UK/a.jpg"))
        self.assertEqual(self.rows()[0][1], "Kelly Brook for NEXT UK")
        self.assertEqual(self.tagged, [])

    def test_a_row_not_filed_under_its_own_name_is_left_alone(self):
        self.add("Somewhere Else/a.jpg", "Kelly Brook for NEXT UK", [], "Kelly Brook for NEXT UK - x")
        self.assertEqual(self.s["fix_celebrity_names"](), 0)
        self.assertTrue(self.exists("Somewhere Else/a.jpg"))


class RetagTests(unittest.TestCase):
    def setUp(self):
        logging.disable(logging.CRITICAL)
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.tagged = []
        self.s, self.db = load(self.root, self.tagged)
        with closing(sqlite3.connect(self.db)) as c, c:
            c.executescript(SCHEMA)
        self.s["LOG_FILE"] = os.path.join(self.root, "scraper.log")
        self.s["RETAG_FILE"] = os.path.join(self.root, "retag_list.txt")

    def tearDown(self):
        self.tmp.cleanup()

    def test_the_failed_files_are_read_from_the_log_once_each(self):
        text = ("2026-10-23 00:46:30 [ERROR] Metadata error tagging /downloads/Elsa Hosk/a.jpg: tags not found after writing: elsa hosk\n"
                "2026-10-23 00:47:00 [INFO] Saved to Root: b.jpg\n"
                "2026-10-23 06:47:59 [ERROR] Metadata error tagging /downloads/Callina Liang/c.jpg: tags not found after writing: callina liang\n"
                "2026-10-23 07:00:00 [ERROR] Metadata error tagging /downloads/Elsa Hosk/a.jpg: tags not found after writing: elsa hosk\n")
        self.assertEqual(self.s["failed_tag_paths"](text), ["/downloads/Elsa Hosk/a.jpg", "/downloads/Callina Liang/c.jpg"])
        self.assertEqual(self.s["failed_tag_paths"]("nothing to see"), [])

    def test_files_the_log_names_are_tagged_again_with_their_index_tags_and_gone_files_are_skipped(self):
        folder = os.path.join(self.root, "Elsa Hosk")
        os.makedirs(folder)
        kept, gone = os.path.join(folder, "a.jpg"), os.path.join(folder, "gone.jpg")
        open(kept, "wb").write(b"x")
        with closing(sqlite3.connect(self.db)) as c, c:
            c.execute("INSERT INTO images (rel_path, celebrity, tags) VALUES (?,?,?)", ("Elsa Hosk/a.jpg", "Elsa Hosk", json.dumps(["Elsa Hosk", "Dress"])))
        open(self.s["LOG_FILE"], "w", encoding="utf-8").write(
            f"x [ERROR] Metadata error tagging {kept}: tags not found after writing: elsa hosk\n"
            f"x [ERROR] Metadata error tagging {gone}: tags not found after writing: elsa hosk\n")
        self.assertEqual(self.s["retag_logged_failures"](), 1)
        self.assertEqual(self.tagged, [(kept, ["Elsa Hosk", "Dress"])])

    def test_a_file_that_is_not_in_the_index_gets_the_name_of_its_folder(self):
        folder = os.path.join(self.root, "Callina Liang")
        os.makedirs(folder)
        path = os.path.join(folder, "c.jpg")
        open(path, "wb").write(b"x")
        open(self.s["LOG_FILE"], "w", encoding="utf-8").write(f"x [ERROR] Metadata error tagging {path}: tags not found after writing: callina liang\n")
        self.assertEqual(self.s["retag_logged_failures"](), 1)
        self.assertEqual(self.tagged, [(path, ["Callina Liang"])])

    def test_no_log_means_nothing_to_do(self):
        self.s["RETAG_FILE"] = os.path.join(self.root, "retag_list.txt")
        self.assertEqual(self.s["retag_logged_failures"](), 0)

    def test_files_named_in_the_retag_list_are_tagged_again_even_when_the_log_has_forgotten_them(self):
        folder = os.path.join(self.root, "Elsa Hosk")
        os.makedirs(folder)
        path = os.path.join(folder, "a.jpg")
        open(path, "wb").write(b"x")
        self.s["RETAG_FILE"] = os.path.join(self.root, "retag_list.txt")
        open(self.s["RETAG_FILE"], "w", encoding="utf-8").write("# the Getty one\nElsa Hosk/a.jpg\nElsa Hosk/not-there.jpg\n\n")
        self.assertEqual(self.s["retag_logged_failures"](), 1)
        self.assertEqual(self.tagged, [(path, ["Elsa Hosk"])])


class EmptyFolderTests(unittest.TestCase):
    def setUp(self):
        logging.disable(logging.CRITICAL)
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.s, self.db = load(self.root, [])
        with closing(sqlite3.connect(self.db)) as c, c:
            c.executescript(SCHEMA)

    def tearDown(self):
        self.tmp.cleanup()

    def first_picture_saved(self, when="2026-10-22T20:00:00Z"):
        with closing(sqlite3.connect(self.db)) as c, c:
            c.execute("INSERT INTO images (rel_path, celebrity, downloaded_at) VALUES ('Kelly Brook/a.jpg', 'Kelly Brook', ?)", (when,))

    def make(self, rel, files=()):
        path = os.path.join(self.root, *rel.split("/"))
        os.makedirs(path, exist_ok=True)
        for name in files:
            open(os.path.join(path, name), "wb").write(b"x")
        return path

    def test_an_empty_folder_made_since_the_first_picture_is_removed_and_one_with_a_file_is_kept(self):
        self.first_picture_saved("2000-01-01T00:00:00Z")           # long ago, so everything here is newer
        self.make("Christina Aguilera Celebration")
        self.make("Florence Pugh/Bikini")                          # nested and empty
        self.make("Kelly Brook", files=["a.jpg"])
        self.make("Alana Haim/Swimwear", files=["b.jpg"])
        self.assertEqual(self.s["remove_empty_folders"](), 2)
        self.assertFalse(os.path.exists(os.path.join(self.root, "Christina Aguilera Celebration")))
        self.assertFalse(os.path.exists(os.path.join(self.root, "Florence Pugh")))
        self.assertTrue(os.path.exists(os.path.join(self.root, "Kelly Brook", "a.jpg")))
        self.assertTrue(os.path.exists(os.path.join(self.root, "Alana Haim", "Swimwear", "b.jpg")))

    def test_a_folder_that_has_any_file_in_it_at_any_depth_is_kept(self):
        self.first_picture_saved("2000-01-01T00:00:00Z")
        self.make("Someone/notes", files=["keep.txt"])
        self.assertEqual(self.s["remove_empty_folders"](), 0)
        self.assertTrue(os.path.exists(os.path.join(self.root, "Someone", "notes", "keep.txt")))

    def test_an_empty_folder_older_than_the_scraper_is_left_alone(self):
        self.first_picture_saved("2999-01-01T00:00:00Z")           # the first picture is "later" than this folder
        path = self.make("Margot Robbie")
        self.assertEqual(self.s["remove_empty_folders"](), 0)
        self.assertTrue(os.path.isdir(path))

    def test_nothing_is_removed_when_the_library_is_empty(self):
        self.make("Anyone")
        self.assertEqual(self.s["remove_empty_folders"](), 0)
        self.assertTrue(os.path.isdir(os.path.join(self.root, "Anyone")))


class TrailingDotFolderTests(unittest.TestCase):
    def setUp(self):
        logging.disable(logging.CRITICAL)
        self.tmp = tempfile.TemporaryDirectory()
        # Windows drops a trailing dot from a folder name unless the path starts with \?\ (the NAS, which is Linux, keeps it)
        self.root = ("\\\\?\\" + self.tmp.name) if os.name == "nt" else self.tmp.name
        self.s, self.db = load(self.tmp.name, [])
        self.s["DOWNLOAD_DIR"] = self.root
        with closing(sqlite3.connect(self.db)) as c, c:
            c.executescript(SCHEMA)

    def tearDown(self):
        self.tmp.cleanup()

    def add(self, rel, create=True):
        if create:
            path = os.path.join(self.root, *rel.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f:
                f.write(b"x")
        with closing(sqlite3.connect(self.db)) as c, c:
            c.execute("INSERT INTO images (rel_path, celebrity, tags) VALUES (?,?,?)", (rel, rel.split("/")[0], "[]"))

    def rels(self):
        with closing(sqlite3.connect(self.db)) as c:
            return sorted(r[0] for r in c.execute("SELECT rel_path FROM images"))

    def test_a_folder_name_never_ends_in_a_dot_or_a_space(self):
        self.assertEqual(self.s["sanitize_filename"]("T.A.T.U."), "T.A.T.U")
        self.assertEqual(self.s["sanitize_filename"]("Jr. "), "Jr")
        self.assertEqual(self.s["sanitize_filename"]("St. John Smith"), "St. John Smith")
        self.assertEqual(self.s["sanitize_filename"]("a:b.jpg"), "ab.jpg")

    def test_pictures_move_out_of_a_folder_windows_cannot_open(self):
        # the real case: T.A.T.U. Performs Live was shortened to T.A.T.U. and the folder kept the dot
        self.add("T.A.T.U./a.jpg")
        self.add("T.A.T.U./b.jpg")
        self.add("Kelly Brook/c.jpg")
        self.assertEqual(self.s["fix_folder_names"](), 2)
        self.assertEqual(self.rels(), ["Kelly Brook/c.jpg", "T.A.T.U/a.jpg", "T.A.T.U/b.jpg"])
        self.assertTrue(os.path.exists(os.path.join(self.root, "T.A.T.U", "a.jpg")))
        self.assertFalse(os.path.exists(os.path.join(self.root, "T.A.T.U.")) and os.listdir(os.path.join(self.root, "T.A.T.U.")))

    def test_a_picture_with_the_same_file_name_in_the_target_is_kept_too(self):
        self.add("T.A.T.U./a.jpg")
        self.add("T.A.T.U/a.jpg")
        self.assertEqual(self.s["fix_folder_names"](), 1)
        rels = self.rels()
        self.assertEqual(len(rels), 2)
        self.assertEqual(len(set(rels)), 2)
        self.assertTrue(all(r.startswith("T.A.T.U/") for r in rels))
        self.assertTrue(all(os.path.exists(os.path.join(self.root, *r.split("/"))) for r in rels))

    def test_nothing_happens_when_every_folder_is_fine(self):
        self.add("Kelly Brook/c.jpg")
        self.assertEqual(self.s["fix_folder_names"](), 0)
        self.assertEqual(self.rels(), ["Kelly Brook/c.jpg"])


class CaseVariantTests(unittest.TestCase):
    """The NAS is case sensitive, so "McKenna Grace" and "Mckenna Grace" are two folders there; a Windows temp folder can't hold both, so these
    test the plan (which pictures move where) rather than the move."""

    def plan(self, rows, spellings=None):
        logging.disable(logging.CRITICAL)
        scope = {"re": re, "json": json}
        with open(SOURCE, encoding="utf-8") as f:
            tree = ast.parse(f.read())
        keep = [n for n in tree.body if (isinstance(n, ast.FunctionDef) and n.name in {"sanitize_filename", "name_from_title", "people_from_title",
                                                                                        "_second_person", "plan_name_fixes"})
                or (isinstance(n, ast.Assign) and getattr(n.targets[0], "id", None) in ("NAME_PARTICLES", "NAME_VERBS", "NAME_RELATIONS"))]
        exec(compile(ast.Module(body=keep, type_ignores=[]), SOURCE, "exec"), scope)
        canon = (lambda n: spellings.get(n.lower(), n)) if spellings else None
        return scope["plan_name_fixes"](rows, scope["people_from_title"], canon)

    def test_the_minority_spelling_moves_to_the_majority_one_and_its_tags_follow(self):
        rows = [(1, "McKenna Grace/a.jpg", "McKenna Grace", json.dumps(["McKenna Grace", "Dress"]), "McKenna Grace - Newport Beach Film Festival"),
                (2, "Mckenna Grace/b.jpg", "Mckenna Grace", json.dumps(["Mckenna Grace"]), "Mckenna Grace - Academy Museum Gala")]
        plan = self.plan(rows, {"mckenna grace": "Mckenna Grace"})
        self.assertEqual(len(plan), 1)
        self.assertEqual((plan[0]["old_rel"], plan[0]["new_rel"], plan[0]["new_name"]), ("McKenna Grace/a.jpg", "Mckenna Grace/a.jpg", "Mckenna Grace"))
        self.assertEqual(plan[0]["tags"], ["Mckenna Grace", "Dress"])

    def test_a_second_persons_picture_goes_to_the_second_person_not_the_first(self):
        rows = [(1, "Vittoria Cerettifor Vogue UK/a.jpg", "Vittoria Cerettifor Vogue UK", json.dumps(["Vittoria Cerettifor Vogue UK"]),
                 "Gigi Hadid & Vittoria Cerettifor Vogue UK - September 2026"),
                (2, "AJ Michalka Exiting/b.jpg", "AJ Michalka Exiting", json.dumps(["AJ Michalka Exiting"]),
                 "Alyson Michalka and AJ Michalka Exiting their hotel in Paris 08/02/2026"),
                (3, "Gigi Hadid/c.jpg", "Gigi Hadid", json.dumps(["Gigi Hadid", "Vittoria Ceretti"]), "Gigi Hadid & Vittoria Cerettifor Vogue UK - September 2026")]
        plan = self.plan(rows, {"vittoria cerettifor vogue uk": "Vittoria Ceretti"})
        self.assertEqual([(p["old_name"], p["new_name"]) for p in plan],
                         [("Vittoria Cerettifor Vogue UK", "Vittoria Ceretti"), ("AJ Michalka Exiting", "AJ Michalka")])

    def test_without_a_spelling_table_nothing_changes(self):
        rows = [(1, "McKenna Grace/a.jpg", "McKenna Grace", json.dumps(["McKenna Grace"]), "McKenna Grace - Newport Beach Film Festival")]
        self.assertEqual(self.plan(rows), [])

    def test_a_two_person_copy_under_a_differently_capitalised_name_is_not_moved(self):
        rows = [(1, "Ana De Armas/a.jpg", "Ana De Armas", json.dumps(["Ana De Armas"]), "Ana De Armas and Tom Hardy at a premiere")]
        plan = self.plan(rows, {"ana de armas": "Ana de Armas", "tom hardy": "Tom Hardy"})
        self.assertEqual([(p["old_name"], p["new_name"]) for p in plan], [("Ana De Armas", "Ana de Armas")])


class PairTests(unittest.TestCase):
    TITLE = "Carrie-Anne Moss and Clark Backo for Entertainment Weekly photoshoot - October 2026, ADDS"

    def setUp(self):
        logging.disable(logging.CRITICAL)
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.tagged = []
        self.s, self.db = load(self.root, self.tagged)
        with closing(sqlite3.connect(self.db)) as c, c:
            c.executescript(SCHEMA)

    def tearDown(self):
        self.tmp.cleanup()

    def add(self, rel, celebrity, tags, title, create=True):
        if create:
            path = os.path.join(self.root, *rel.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f:
                f.write(b"x" + rel.encode())
        with closing(sqlite3.connect(self.db)) as c, c:
            cur = c.execute("INSERT INTO images (rel_path, celebrity, category, tags, thread_title) VALUES (?,?,?,?,?)",
                            (rel, celebrity, "Bikini" if "/Bikini/" in rel else None, json.dumps(tags), title))
            c.executemany("INSERT INTO image_tags VALUES (?,?)", [(cur.lastrowid, t) for t in tags])

    def rows(self):
        with closing(sqlite3.connect(self.db)) as c:
            return c.execute("SELECT rel_path, celebrity, tags FROM images ORDER BY id").fetchall()

    def exists(self, rel):
        return os.path.exists(os.path.join(self.root, *rel.split("/")))

    def test_the_second_person_gets_a_copy_and_both_names_are_tagged(self):
        self.add("Carrie-Anne Moss/a.jpg", "Carrie-Anne Moss", ["Carrie-Anne Moss", "Magazine"], self.TITLE)
        self.add("Carrie-Anne Moss/Bikini/b.jpg", "Carrie-Anne Moss", ["Carrie-Anne Moss"], self.TITLE)
        self.assertEqual(self.s["fix_second_people"](), 2)

        self.assertTrue(self.exists("Clark Backo/a.jpg"))
        self.assertTrue(self.exists("Clark Backo/Bikini/b.jpg"))                       # same category subfolder
        self.assertTrue(self.exists("Carrie-Anne Moss/a.jpg"))                          # the original stays
        rows = self.rows()
        self.assertEqual([r[:2] for r in rows if r[0].startswith("Clark")],
                         [("Clark Backo/a.jpg", "Clark Backo"), ("Clark Backo/Bikini/b.jpg", "Clark Backo")])
        self.assertEqual(json.loads(rows[0][2]), ["Carrie-Anne Moss", "Clark Backo", "Magazine"])   # the original now has both names
        self.assertEqual(json.loads(rows[2][2]), ["Carrie-Anne Moss", "Clark Backo", "Magazine"])   # and so does the copy
        self.assertIn((os.path.join(self.root, "Carrie-Anne Moss", "a.jpg"), ["Carrie-Anne Moss", "Clark Backo", "Magazine"]), self.tagged)

    def test_running_it_again_does_nothing(self):
        self.add("Carrie-Anne Moss/a.jpg", "Carrie-Anne Moss", ["Carrie-Anne Moss"], self.TITLE)
        self.assertEqual(self.s["fix_second_people"](), 1)
        self.assertEqual(self.s["fix_second_people"](), 0)
        self.assertEqual(len(self.rows()), 2)

    def test_the_copy_is_not_treated_as_filed_under_the_wrong_name(self):
        self.add("Carrie-Anne Moss/a.jpg", "Carrie-Anne Moss", ["Carrie-Anne Moss"], self.TITLE)
        self.s["fix_second_people"]()
        self.assertEqual(self.s["fix_celebrity_names"](), 0)                             # the name clean-up leaves the copy where it is
        self.assertTrue(self.exists("Clark Backo/a.jpg"))

    def test_one_person_threads_and_pictures_filed_under_the_second_person_are_left_alone(self):
        self.add("Miranda Kerr/a.jpg", "Miranda Kerr", ["Miranda Kerr"], "Miranda Kerr at the 2026 Awards")
        self.add("Clark Backo/c.jpg", "Clark Backo", ["Carrie-Anne Moss", "Clark Backo"], self.TITLE)
        self.assertEqual(self.s["fix_second_people"](), 0)
        self.assertEqual(len(self.rows()), 2)

    def test_a_missing_file_is_skipped_and_a_dry_run_changes_nothing(self):
        self.add("Carrie-Anne Moss/gone.jpg", "Carrie-Anne Moss", ["Carrie-Anne Moss"], self.TITLE, create=False)
        self.assertEqual(self.s["fix_second_people"](), 0)
        self.add("Carrie-Anne Moss/a.jpg", "Carrie-Anne Moss", ["Carrie-Anne Moss"], self.TITLE)
        self.assertEqual(self.s["fix_second_people"](dry_run=True), 0)
        self.assertFalse(self.exists("Clark Backo/a.jpg"))
        self.assertEqual(self.tagged, [])


if __name__ == "__main__":
    unittest.main()
