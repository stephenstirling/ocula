"""Tests for how the scraper reads the forum (listing_threads, post_bodies, direct_from_thumbnail, extract_metadata_tags).

phun_scraper.py can't be imported here, so the functions are pulled out with ast, as in test_metadata.py. The HTML
fixtures are cut down from the real phun.org pages (XenForo 1) and from XenForo 2 markup, which the scraper still accepts.

    python -m unittest tools/phun-scraper/test_parsing.py
"""
import ast
import os
import re
import unittest
from urllib.parse import urljoin

from bs4 import BeautifulSoup

SOURCE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "phun_scraper.py")
WANTED = {"listing_threads", "post_bodies", "direct_from_thumbnail", "extract_metadata_tags"}


def load():
    tree = ast.parse(open(SOURCE, encoding="utf-8").read())
    keep = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in WANTED]
    scope = {"re": re, "is_within_pregnancy_timeline": lambda *a: False}
    exec(compile(ast.Module(body=keep, type_ignores=[]), SOURCE, "exec"), scope)
    return scope


S = load()
BASE = "https://forum.phun.org/"

XF1_LIST = """
<ol class="discussionListItems">
 <li class="discussionListItem visible sticky prefix80"><h3 class="title">
   <a href="forums/celebrity-photos.15/?prefix_id=80" class="prefixLink"><span>RULES</span></a>
   <a href="threads/celebrity-photos-forum-rules.1123558/">Celebrity Photos Forum Rules</a></h3></li>
 <li class="discussionListItem visible prefix190"><h3 class="title">
   <a href="forums/celebrity-photos.15/?prefix_id=190" class="prefixLink"><span>APPEARANCE</span></a>
   <a href="threads/miranda-kerr-at-the-2026-awards.5941148/">Miranda Kerr at the 2026 George H.W. Bush Points of Light Awards in Washington D.C. 10/13/2026</a></h3></li>
</ol>"""

XF2_LIST = """
<div class="structItem-title"><a href="/threads/jane-doe-at-a-premiere.123/" data-tp-primary="on">Jane Doe at a premiere</a></div>"""


class ListingTests(unittest.TestCase):
    def test_xenforo1_list_skips_pinned_threads_and_the_prefix_link(self):
        found = S["listing_threads"](BeautifulSoup(XF1_LIST, "html.parser"))
        self.assertEqual(len(found), 1)
        self.assertTrue(found[0][0].startswith("Miranda Kerr at the 2026"))   # the title has no "APPEARANCE" prefix in it
        self.assertEqual(urljoin(BASE, found[0][1]), BASE + "threads/miranda-kerr-at-the-2026-awards.5941148/")

    def test_xenforo2_list_still_works(self):
        found = S["listing_threads"](BeautifulSoup(XF2_LIST, "html.parser"))
        self.assertEqual(found, [("Jane Doe at a premiere", "/threads/jane-doe-at-a-premiere.123/")])

    def test_an_unknown_layout_finds_nothing(self):
        self.assertEqual(S["listing_threads"](BeautifulSoup("<html><body><p>hi</p></body></html>", "html.parser")), [])


class PostTests(unittest.TestCase):
    def test_post_bodies_for_both_forum_versions(self):
        xf1 = BeautifulSoup('<li class="message"><blockquote class="messageText">a</blockquote></li>', "html.parser")
        xf2 = BeautifulSoup('<article class="message-body">b</article>', "html.parser")
        self.assertEqual([p.get_text() for p in S["post_bodies"](xf1)], ["a"])
        self.assertEqual([p.get_text() for p in S["post_bodies"](xf2)], ["b"])

    def test_imagetwist_page_link_becomes_the_full_picture_via_its_thumbnail(self):
        page = "https://phun.imagetwist.com/9lphrqtu9lo6/Miranda_Kerr_0001.jpg"
        thumb = "https://i6phun.imagetwist.com/th/00456/9lphrqtu9lo6.jpg"
        self.assertEqual(S["direct_from_thumbnail"](page, thumb), "https://i6phun.imagetwist.com/i/00456/9lphrqtu9lo6.jpg")

    def test_other_links_and_missing_thumbnails_are_left_alone(self):
        self.assertIsNone(S["direct_from_thumbnail"]("https://phun.imagetwist.com/x/y.jpg", None))
        self.assertIsNone(S["direct_from_thumbnail"]("https://example.com/a.jpg", "https://example.com/th/a.jpg"))


class NameTests(unittest.TestCase):
    def test_name_is_the_part_before_at(self):
        title = "Miranda Kerr at the 2026 George H.W. Bush Points of Light Awards in Washington D.C. 10/13/2026"
        self.assertEqual(S["extract_metadata_tags"](title), ("Miranda Kerr", ["Miranda Kerr"]))

    def test_dash_and_brackets(self):
        self.assertEqual(S["extract_metadata_tags"]("Sadie Sink - Stella McCartney (sep 2022)")[0], "Sadie Sink")


if __name__ == "__main__":
    unittest.main()
