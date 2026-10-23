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
import sqlite3
import tempfile
import threading
import unittest
from contextlib import closing

SOURCE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "phun_scraper.py")
FUNCS = {"sanitize_filename", "rel_library_path", "name_from_title", "plan_name_fixes", "fix_celebrity_names"}
SCHEMA = """CREATE TABLE images (id INTEGER PRIMARY KEY AUTOINCREMENT, rel_path TEXT NOT NULL UNIQUE, celebrity TEXT, category TEXT,
 tags TEXT, thread_title TEXT);
CREATE TABLE image_tags (image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE, tag TEXT NOT NULL, PRIMARY KEY (image_id, tag));"""


def load(root, tagged):
    with open(SOURCE, encoding="utf-8") as f:
        tree = ast.parse(f.read())
    keep = [n for n in tree.body if (isinstance(n, ast.FunctionDef) and n.name in FUNCS)
            or (isinstance(n, ast.Assign) and getattr(n.targets[0], "id", None) == "NAME_PARTICLES")]
    db = os.path.join(root, "ocula_library.db")

    def connect():
        conn = sqlite3.connect(db)
        conn.execute("PRAGMA foreign_keys = ON")
        return conn

    def embed(path, tags):
        tagged.append((path, list(tags)))
        return True

    scope = {"os": os, "re": re, "json": json, "hashlib": hashlib, "logging": logging, "closing": closing,
             "DOWNLOAD_DIR": root, "db_lock": threading.Lock(), "_library_connect": connect, "embed_metadata": embed}
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


if __name__ == "__main__":
    unittest.main()
