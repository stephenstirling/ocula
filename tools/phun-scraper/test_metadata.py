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
WANTED = {"IPTC_EXTS", "metadata_tags_for", "_as_list", "embed_metadata", "_write_and_read_back", "exiftool_detail"}


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

    heal_on_minor = False

    def set_tags(self, files, tags, params=None):
        if FakeExifTool.fail_on_write:
            raise RuntimeError("exiftool failed")
        if FakeExifTool.heal_on_minor and "-m" in (params or []):
            FakeExifTool.drop = ()          # ignoring minor errors was enough
        FakeExifTool.written.append((files[0], dict(tags), list(params or [])))

    executed = []
    heal_on_execute = False

    def execute(self, *params):
        FakeExifTool.executed.append(params)
        if FakeExifTool.heal_on_execute:
            FakeExifTool.drop = ()          # the broken XMP block is gone, so the next write takes
        return ""

    def get_tags(self, files, tags):
        _, written, _ = FakeExifTool.written[-1]
        subject = [k for k in written.get("XMP:Subject", []) if k not in FakeExifTool.drop]
        return [{"SourceFile": files[0], "XMP:Subject": subject}]


class MetadataTests(unittest.TestCase):
    def setUp(self):
        FakeExifTool.written, FakeExifTool.drop, FakeExifTool.fail_on_write = [], (), False
        FakeExifTool.executed, FakeExifTool.heal_on_execute, FakeExifTool.heal_on_minor = [], False, False
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

    def test_a_write_that_does_not_take_is_retried_once_without_the_files_own_xmp(self):
        FakeExifTool.drop = ("Elsa Hosk",)               # the first write silently doesn't take
        FakeExifTool.heal_on_execute = True              # removing the XMP block lets the second one through
        scope, status = load(FakeExifTool)
        self.assertTrue(scope["embed_metadata"]("x.jpg", ["Elsa Hosk"]))
        self.assertEqual(len(FakeExifTool.executed), 1)
        self.assertIn("-XMP:all=", FakeExifTool.executed[0])
        self.assertEqual(len(FakeExifTool.written), 3)   # written normally, again ignoring minor errors, and once more after the block was removed
        self.assertEqual(status.bumps, [])               # and it is not counted as a failure

    def test_ignoring_minor_errors_is_tried_before_anything_is_removed(self):
        FakeExifTool.drop = ("Elsa Hosk",)
        FakeExifTool.heal_on_minor = True                # the -m retry is enough
        scope, status = load(FakeExifTool)
        self.assertTrue(scope["embed_metadata"]("x.jpg", ["Elsa Hosk"]))
        self.assertEqual(FakeExifTool.executed, [])      # the file's own XMP block was left alone
        self.assertEqual(len(FakeExifTool.written), 2)
        self.assertIn("-m", FakeExifTool.written[1][2])

    def test_an_exiftool_error_on_the_first_write_still_gets_the_later_attempts(self):
        calls = {"n": 0}

        class FailsOnce(FakeExifTool):
            def set_tags(self, files, tags, params=None):
                calls["n"] += 1
                if calls["n"] == 1:
                    raise RuntimeError("execute returned a non-zero exit status: 1")
                return super().set_tags(files, tags, params)
        scope, status = load(FailsOnce)
        self.assertTrue(scope["embed_metadata"]("x.jpg", ["Kelly Brook"]))
        self.assertEqual(status.bumps, [])

    def test_the_log_carries_exiftools_own_message(self):
        class Refuses(FakeExifTool):
            def set_tags(self, files, tags, params=None):
                error = RuntimeError("execute returned a non-zero exit status: 1")
                error.stderr = "Error: Bad format (-2) for XMP block in x.jpg"
                raise error
        scope, status = load(Refuses)
        self.assertFalse(scope["embed_metadata"]("x.jpg", ["Kelly Brook"]))
        self.assertEqual(status.bumps, ["metadata_failed"])
        self.assertIn("Bad format (-2) for XMP block", status.events[-1][1])

    def test_a_file_whose_xmp_is_fine_is_never_stripped(self):
        scope, status = load(FakeExifTool)
        self.assertTrue(scope["embed_metadata"]("x.jpg", ["Kelly Brook"]))
        self.assertEqual(FakeExifTool.executed, [])
        self.assertEqual(len(FakeExifTool.written), 1)

    def test_if_the_retry_does_not_help_it_is_reported_as_a_failure(self):
        FakeExifTool.drop = ("Elsa Hosk",)
        scope, status = load(FakeExifTool)
        self.assertFalse(scope["embed_metadata"]("x.jpg", ["Elsa Hosk"]))
        self.assertEqual(len(FakeExifTool.executed), 1)
        self.assertEqual(status.bumps, ["metadata_failed"])

    def test_a_single_value_comes_back_as_a_string_not_a_list(self):
        class Single(FakeExifTool):
            def get_tags(self, files, tags):
                return [{"XMP:Subject": "Celeb Name"}]
        scope, status = load(Single)
        self.assertTrue(scope["embed_metadata"]("x.jpg", ["Celeb Name"]))


if __name__ == "__main__":
    unittest.main()
