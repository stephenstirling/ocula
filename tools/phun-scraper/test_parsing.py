"""Tests for how the scraper reads the forum (listing_threads, post_bodies, direct_from_thumbnail, extract_metadata_tags).

phun_scraper.py can't be imported here, so the functions are pulled out with ast, as in test_metadata.py. The HTML
fixtures are cut down from the real phun.org pages (XenForo 1) and from XenForo 2 markup, which the scraper still accepts.

    python -m unittest tools/phun-scraper/test_parsing.py
"""
import ast
import logging
import os
import re
import tempfile
import threading
import unittest
from urllib.parse import urljoin

import requests
from bs4 import BeautifulSoup

SOURCE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "phun_scraper.py")
WANTED = {"listing_threads", "post_bodies", "direct_from_thumbnail", "extract_metadata_tags", "log_failure", "resolve_image_host_url", "name_from_title"}


def load():
    tree = ast.parse(open(SOURCE, encoding="utf-8").read())
    keep = [n for n in tree.body if (isinstance(n, ast.FunctionDef) and n.name in WANTED)
            or (isinstance(n, ast.Assign) and getattr(n.targets[0], "id", None) == "NAME_PARTICLES")]
    scope = {"re": re, "is_within_pregnancy_timeline": lambda *a: False, "logging": logging, "threading": threading,
             "failures_lock": threading.Lock(), "BeautifulSoup": BeautifulSoup, "requests": requests, "session": None, "_iso": lambda: "2026-10-22T21:00:00Z", "FAILURES_FILE": None}
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

    def test_titles_that_carry_on_in_lower_case_give_just_the_name(self):
        # real thread titles from phun.org that the first run turned into folder names like "Kelly Brook for NEXT UK"
        for title, name in [
            ("Kelly Brook for NEXT UK - Swimwear Collection 2026", "Kelly Brook"),
            ("Zara Larsson for The New York Times Popcast - October 2026", "Zara Larsson"),
            ("Kristen Bell for Bustle", "Kristen Bell"),
            ("Famke Janssen out in NYC", "Famke Janssen"),
            ("Kylie Jenner arriving at Baldi at the Waldorf Astoria in Beverly Hills", "Kylie Jenner"),
            ("Carrie-Anne Moss and Clark Backo for Entertainment Weekly photoshoot in LA", "Carrie-Anne Moss"),
            ("Taylor Swift and Travis Kelce out in New York", "Taylor Swift"),
            ("Dua Lipa's new look in London", "Dua Lipa"),
            ("Zoe de Vries at a party", "Zoe de Vries"),
            ("Charli D'Amelio at The Hollywood Reporter Creator A-List Party", "Charli D'Amelio"),
            ("Joanna ''JoJo'' Levesque at an event", "Joanna ''JoJo'' Levesque"),
        ]:
            self.assertEqual(S["name_from_title"](title), name, title)

    def test_titles_with_nothing_lower_case_fall_back_to_the_old_rules(self):
        self.assertEqual(S["name_from_title"]("Rihanna"), "Rihanna")
        self.assertEqual(S["name_from_title"]("Taylor Swift"), "Taylor Swift")
        # a title-cased sentence must not become a four-word-plus "name"
        self.assertEqual(S["name_from_title"]("Selena Gomez Attends The Premiere Of Foo In LA"), "Selena Gomez")


class FakeResponse:
    def __init__(self, status, text=""):
        self.status_code, self.text = status, text


class FakeSession:
    def __init__(self, response):
        self.response, self.asked = response, []

    def get(self, url, **kw):
        self.asked.append(url)
        if isinstance(self.response, Exception):
            raise self.response
        return self.response


class ResolveTests(unittest.TestCase):
    PIXHOST = "https://pixhost.to/show/6626/789917917_1.jpg"

    def test_pixhost_show_page_gives_the_image_on_the_server_the_page_names(self):
        S["session"] = FakeSession(FakeResponse(200, '<img id="image" src="https://img3.pixhost.to/images/6626/789917917_1.jpg">'
                                                      '<meta property="og:image" content="https://t3.pixhost.to/thumbs/6626/789917917_1.jpg">'))
        self.assertEqual(S["resolve_image_host_url"](self.PIXHOST), "https://img3.pixhost.to/images/6626/789917917_1.jpg")

    def test_pixhost_mirrors_work_the_same_way(self):
        S["session"] = FakeSession(FakeResponse(200, '<img id="image" src="https://img3.pixhost.cc/images/6572/a.jpg">'))
        self.assertEqual(S["resolve_image_host_url"]("https://pixhost.cc/show/6572/a.jpg"), "https://img3.pixhost.cc/images/6572/a.jpg")

    def test_pixhost_page_that_fails_or_has_no_image_gives_nothing_rather_than_a_guess(self):
        S["session"] = FakeSession(FakeResponse(404))
        self.assertIsNone(S["resolve_image_host_url"](self.PIXHOST))
        S["session"] = FakeSession(FakeResponse(200, "<html>no picture</html>"))
        self.assertIsNone(S["resolve_image_host_url"](self.PIXHOST))
        S["session"] = FakeSession(requests.ConnectionError("down"))
        self.assertIsNone(S["resolve_image_host_url"](self.PIXHOST))

    def test_a_plain_image_link_is_used_as_it_is(self):
        S["session"] = FakeSession(FakeResponse(500))
        self.assertEqual(S["resolve_image_host_url"]("https://example.com/a.jpg"), "https://example.com/a.jpg")
        self.assertEqual(S["session"].asked, [])


class FailureLogTests(unittest.TestCase):
    def test_a_failure_is_written_with_thread_link_and_reason(self):
        logging.disable(logging.CRITICAL)
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "failed_downloads.log")
            S["FAILURES_FILE"] = path
            S["log_failure"]("https://host/a.jpg", "HTTP 404, text/html", {"thread_title": "Jane Doe at a premiere"}, "https://host/i/a.jpg")
            S["log_failure"]("https://host/b.jpg", "too small (10 bytes)", None)
            lines = open(path, encoding="utf-8").read().splitlines()
        self.assertEqual(lines[0].split("	"), ["2026-10-22T21:00:00Z", "Jane Doe at a premiere", "https://host/a.jpg", "https://host/i/a.jpg", "HTTP 404, text/html"])
        self.assertEqual(lines[1].split("	"), ["2026-10-22T21:00:00Z", "", "https://host/b.jpg", "", "too small (10 bytes)"])

    def test_an_unwritable_log_does_not_stop_the_scraper(self):
        logging.disable(logging.CRITICAL)
        S["FAILURES_FILE"] = os.path.join(tempfile.gettempdir(), "no-such-folder-xyz", "f.log")
        S["log_failure"]("https://host/a.jpg", "x")   # must not raise


if __name__ == "__main__":
    unittest.main()
