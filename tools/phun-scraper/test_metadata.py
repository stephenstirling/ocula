"""Tests for the scraper's tag writing (metadata_tags_for, embed_metadata).

phun_scraper.py can't be imported here (it needs ExifTool, Pillow, BeautifulSoup and a download folder at import time),
so the functions under test are pulled out of the file with ast and run against a fake ExifTool.

    python -m unittest tools/phun-scraper/test_metadata.py
"""
import ast
import logging
import os
import threading
import unittest

SOURCE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "phun_scraper.py")
WANTED = {"IPTC_EXTS", "metadata_tags_for", "_as_list", "embed_metadata"}


class FakeStatus:
    def __init__(self):
        self.bumps, self.events = [], []

    def bump(self, key, n=1):
        self.bumps.append(key)

    def add_event(self, level, message):
        self.events.append((level, message))


def load(fake_et_factory):
    tree = ast.parse(open(SOURCE, encoding="utf-8").read())
    keep = []
    for node in tree.body:
        if isinstance(node, ast.FunctionDef) and node.name in WANTED:
            keep.append(node)
        elif isinstance(node, ast.Assign) and any(getattr(t, "id", None) in WANTED for t in node.targets):
            keep.append(node)
    module = ast.Module(body=keep, type_ignores=[])
    status = FakeStatus()
    scope = {"os": os, "logging": logging, "exif_lock": threading.Lock(), "status": status, "ExifToolHelper": fake_et_factory}
    exec(compile(module, SOURCE, "exec"), scope)
    return scope, status


class FakeExifTool:
    """Records what was written; read-back returns what was written unless told otherwise."""
    written = []
    drop = ()
    fail_on_write = False

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False

    def set_tags(self, files, tags, params=None):
        if FakeExifTool.fail_on_write:
            raise RuntimeError("exiftool failed")
        FakeExifTool.written.append((files[0], dict(tags), list(params or [])))

    def get_tags(self, files, tags):
        _, written, _ = FakeExifTool.written[-1]
        subject = [k for k in written.get("XMP:Subject", []) if k not in FakeExifTool.drop]
        return [{"SourceFile": files[0], "XMP:Subject": subject}]


class MetadataTests(unittest.TestCase):
    def setUp(self):
        FakeExifTool.written, FakeExifTool.drop, FakeExifTool.fail_on_write = [], (), False
        logging.disable(logging.CRITICAL)
        self.scope, self.status = load(FakeExifTool)

    def tearDown(self):
        logging.disable(logging.NOTSET)

    def test_a_jpeg_gets_xmp_iptc_and_exif_tags(self):
        tags = self.scope["metadata_tags_for"]("a/b.JPG", ["Celeb Name", "Bikini"])
        self.assertEqual(set(tags), {"XMP:Subject", "XMP:Description", "IPTC:Keywords", "EXIF:ImageDescription"})
        self.assertEqual(tags["IPTC:Keywords"], ["Celeb Name", "Bikini"])

    def test_png_and_webp_get_xmp_only_because_they_cannot_hold_iptc(self):
        for name in ("a.png", "a.webp", "a.PNG"):
            tags = self.scope["metadata_tags_for"](name, ["Celeb Name"])
            self.assertEqual(set(tags), {"XMP:Subject", "XMP:Description"}, name)

    def test_success_is_true_and_counts_nothing(self):
        ok = self.scope["embed_metadata"]("x.jpg", ["Celeb Name", "Beach"])
        self.assertTrue(ok)
        self.assertEqual(self.status.bumps, [])
        self.assertIn("-overwrite_original", FakeExifTool.written[0][2])

    def test_a_tag_that_is_not_there_after_writing_is_a_failure_that_is_counted_and_shown(self):
        FakeExifTool.drop = ("Beach",)
        ok = self.scope["embed_metadata"]("x.jpg", ["Celeb Name", "Beach"])
        self.assertFalse(ok)
        self.assertEqual(self.status.bumps, ["metadata_failed"])
        self.assertEqual(self.status.events[0][0], "ERROR")
        self.assertIn("x.jpg", self.status.events[0][1])
        self.assertIn("beach", self.status.events[0][1])

    def test_an_exiftool_error_is_a_failure_not_a_crash(self):
        FakeExifTool.fail_on_write = True
        self.assertFalse(self.scope["embed_metadata"]("x.png", ["Celeb Name"]))
        self.assertEqual(self.status.bumps, ["metadata_failed"])

    def test_the_comparison_ignores_case(self):
        class Lower(FakeExifTool):
            def get_tags(self, files, tags):
                return [{"XMP:Subject": [k.lower() for k in FakeExifTool.written[-1][1]["XMP:Subject"]]}]
        scope, status = load(Lower)
        self.assertTrue(scope["embed_metadata"]("x.jpg", ["Celeb Name"]))
        self.assertEqual(status.bumps, [])

    def test_a_single_value_comes_back_as_a_string_not_a_list(self):
        class Single(FakeExifTool):
            def get_tags(self, files, tags):
                return [{"XMP:Subject": "Celeb Name"}]
        scope, status = load(Single)
        self.assertTrue(scope["embed_metadata"]("x.jpg", ["Celeb Name"]))


if __name__ == "__main__":
    unittest.main()
