"""Tests for how the scraper reads the forum (listing_threads, post_bodies, direct_from_thumbnail, extract_metadata_tags).

phun_scraper.py can't be imported here, so the functions are pulled out with ast, as in test_metadata.py. The HTML
fixtures are cut down from the real phun.org pages (XenForo 1) and from XenForo 2 markup, which the scraper still accepts.

    python -m unittest tools/phun-scraper/test_parsing.py
"""
import ast
import logging
import os
import re
import shutil
import tempfile
import threading
import unittest
from urllib.parse import urljoin, urlparse

import requests
from bs4 import BeautifulSoup

SOURCE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "phun_scraper.py")
WANTED = {"listing_threads", "post_bodies", "direct_from_thumbnail", "extract_metadata_tags", "log_failure", "resolve_image_host_url", "name_from_title", "people_from_title", "_second_person",
          "forum_page_url", "thread_page_url", "last_page_number", "enough_free_space", "hash_ints", "is_near_duplicate", "canonical_person", "most_used_spellings",
          "is_blocked_host", "note_blocked_links"}


def load():
    tree = ast.parse(open(SOURCE, encoding="utf-8").read())
    keep = [n for n in tree.body if (isinstance(n, ast.FunctionDef) and n.name in WANTED)
            or (isinstance(n, ast.Assign) and getattr(n.targets[0], "id", None) in ("NAME_PARTICLES", "NAME_VERBS", "_canon"))]
    scope = {"re": re, "is_within_pregnancy_timeline": lambda *a: False, "logging": logging, "threading": threading,
             "urlparse": urlparse, "BLOCKED_HOSTS": ("turboimagehost.com",), "history_lock": threading.Lock(), "blocked_lock": threading.Lock(),
             "BLOCKED_FILE": None, "shutil": shutil, "FORUM_URL": "https://forum.phun.org/forums/celebrity-photos.15/", "failures_lock": threading.Lock(), "BeautifulSoup": BeautifulSoup, "requests": requests, "session": None, "_iso": lambda: "2026-10-22T21:00:00Z", "FAILURES_FILE": None}
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

    def test_a_capitalised_verb_at_the_end_is_not_part_of_the_name(self):
        for title, name in [
            ("Chloe Bailey Sings the national anthem before Lakers-Warriors NBA season opener", "Chloe Bailey"),
            ("Rihanna Performs at the Met Gala", "Rihanna"),
            ("Zoe de Vries Attends a premiere", "Zoe de Vries"),
            ("Katy Perry Out in New York", "Katy Perry"),
            # real names, including 3-word names, are left alone
            ("Millie Bobby Brown leaving the Corner Store after dinner", "Millie Bobby Brown"),
            ("Megan Thee Stallion walks the runway for the Victoria's Secret Fashion Show", "Megan Thee Stallion"),
            ("Olivia Taylor Dudley at Dead Meat & Hades Present Fresh Meat", "Olivia Taylor Dudley"),
            ("Peyton Elizabeth Lee at the U.S. Premiere", "Peyton Elizabeth Lee"),
            ("Brooke Shields at a charity dinner", "Brooke Shields"),
            ("Kate Hudson in Paris", "Kate Hudson"),
        ]:
            self.assertEqual(S["name_from_title"](title), name, title)

    def test_a_title_case_sentence_does_not_leave_a_verb_in_the_name(self):
        # the older rules take everything before " at ": the verb has to come off that name too
        self.assertEqual(S["name_from_title"]("The Pussycat Dolls Perform At The 3Arena Dublin in Dublin 10/05/2026"), "The Pussycat Dolls")
        self.assertEqual(S["name_from_title"]("Dua Lipa Attends At The Opera"), "Dua Lipa")

    def test_the_name_ends_at_a_comma_and_a_description_after_it_is_not_a_second_person(self):
        title = "Daisy Edgar-Jones, Esme Creed-Miles for Focus Features ''Sense and Sensibility'' promo October 2026"
        self.assertEqual(S["name_from_title"](title), "Daisy Edgar-Jones")
        self.assertEqual(S["people_from_title"](title), ["Daisy Edgar-Jones"])
        self.assertEqual(S["people_from_title"]("Kelly Brook, Swimwear Collection 2026"), ["Kelly Brook"])
        self.assertEqual(S["name_from_title"]("Kelly Brook, ADDS"), "Kelly Brook")
        # a one-word start before a comma is left alone
        self.assertEqual(S["name_from_title"]("Este, Danielle & Alana Haim at Louis Vuitton"), "Este, Danielle")
        # ... and "& Alana Haim" is still found as the second person, so her pictures stay where they are
        self.assertEqual(S["people_from_title"]("Este, Danielle & Alana Haim at Louis Vuitton"), ["Este, Danielle", "Alana Haim"])

    def test_a_verb_is_only_removed_when_a_name_is_left(self):
        self.assertEqual(S["name_from_title"]("Sings at the awards"), "Sings")   # nothing before it: keep what there is

    def test_curly_and_straight_apostrophes_give_the_same_name(self):
        straight = S["name_from_title"]("Charli D'Amelio at the Hollywood Reporter Creator A-List Party")
        curly = S["name_from_title"]("Charli D’Amelio at the Hollywood Reporter Creator A-List Party")
        self.assertEqual(straight, "Charli D'Amelio")
        self.assertEqual(curly, straight)

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
        S["session"] = FakeSession(FakeResponse(200, '<img id="image" src="https://img3.pixho.st/images/6606/b.jpg">'))
        self.assertEqual(S["resolve_image_host_url"]("https://pixho.st/show/6606/b.jpg"), "https://img3.pixho.st/images/6606/b.jpg")

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


class PeopleTests(unittest.TestCase):
    def test_a_thread_that_names_two_people_gives_both_in_order(self):
        for title, people in [
            ("Carrie-Anne Moss and Clark Backo for Entertainment Weekly photoshoot - October 2026, ADDS", ["Carrie-Anne Moss", "Clark Backo"]),
            ("Dua Lipa - and Callum Turner walk their dog, New York City - October 21, 2026", ["Dua Lipa", "Callum Turner"]),
            ("Taylor Swift & Travis Kelce out in New York", ["Taylor Swift", "Travis Kelce"]),
            ("Taylor Swift and Travis Kelce", ["Taylor Swift", "Travis Kelce"]),
            ("Zoe de Vries and Amy Smith, ADDS", ["Zoe de Vries", "Amy Smith"]),
        ]:
            self.assertEqual(S["people_from_title"](title), people, title)

    def test_one_person_titles_stay_one_person(self):
        for title in [
            "Miranda Kerr at the 2026 George H.W. Bush Points of Light Awards in Washington D.C. 10/13/2026",
            "Olivia Taylor Dudley at Dead Meat & Hades Present Fresh Meat in Los Angeles",   # the & is a company
            "Emmy Rossum at IndieWire x Hulu Furious Screening",
            "Sadie Sink - Stella McCartney",                                                  # a dash is not "and"
            "Kelly Brook for NEXT UK - Swimwear Collection 2026",
            "Xochitl Gomez at Dancing with the Stars 10/20/2026",
            "Selena Gomez And Hailey Bieber Attend The Met Gala",                             # a sentence, not two names
            "Rihanna",
        ]:
            self.assertEqual(len(S["people_from_title"](title)), 1, title)

    def test_every_person_named_is_a_tag(self):
        name, tags = S["extract_metadata_tags"]("Carrie-Anne Moss and Clark Backo for Entertainment Weekly photoshoot - October 2026, ADDS")
        self.assertEqual(name, "Carrie-Anne Moss")
        self.assertEqual(tags[:2], ["Carrie-Anne Moss", "Clark Backo"])


class SafetyFilterTests(unittest.TestCase):
    """The age and exclusion checks have to apply to every person a title names, not only the first."""

    def skip(self, title, ages):
        scope = dict(S)
        scope.update({"EXCLUDED_TITLE_KEYWORDS": ["trans"], "EXCLUDED_CELEBRITIES": ["lindsay lohan"], "MAX_AGE_AT_POST": 57,
                      "MIN_AGE_AT_POST": 18, "is_amanda_bynes_after_2012": lambda *a: False,
                      "age_at_post": lambda name, thread_title: ages.get(name)})
        with open(SOURCE, encoding="utf-8") as f:
            tree = ast.parse(f.read())
        fn = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "should_skip_thread"]
        exec(compile(ast.Module(body=fn, type_ignores=[]), SOURCE, "exec"), scope)
        logging.disable(logging.CRITICAL)
        return scope["should_skip_thread"](title)

    def test_a_second_person_under_the_minimum_age_skips_the_thread(self):
        self.assertTrue(self.skip("Adult Actor and Young Star at a premiere", {"Adult Actor": 30, "Young Star": 16}))

    def test_a_first_person_under_the_minimum_age_still_skips_the_thread(self):
        self.assertTrue(self.skip("Young Star and Adult Actor at a premiere", {"Adult Actor": 30, "Young Star": 16}))

    def test_a_second_person_who_is_excluded_skips_the_thread(self):
        self.assertTrue(self.skip("Adult Actor and Lindsay Lohan at a premiere", {"Adult Actor": 30}))

    def test_two_adults_are_kept(self):
        self.assertFalse(self.skip("Adult Actor and Other Person at a premiere", {"Adult Actor": 30, "Other Person": 41}))


class CrawlHelperTests(unittest.TestCase):
    def test_list_and_thread_page_urls(self):
        self.assertEqual(S["forum_page_url"](1), "https://forum.phun.org/forums/celebrity-photos.15/")
        self.assertEqual(S["forum_page_url"](3), "https://forum.phun.org/forums/celebrity-photos.15/page-3")
        self.assertEqual(S["thread_page_url"]("https://forum.phun.org/threads/a.1/", 1), "https://forum.phun.org/threads/a.1/")
        self.assertEqual(S["thread_page_url"]("https://forum.phun.org/threads/a.1/", 2), "https://forum.phun.org/threads/a.1/page-2")

    def test_last_page_number_reads_the_page_navigation(self):
        self.assertEqual(S["last_page_number"](BeautifulSoup('<div class="PageNav" data-last="6098"></div>', "html.parser")), 6098)
        self.assertEqual(S["last_page_number"](BeautifulSoup("<p>one page</p>", "html.parser")), 1)
        self.assertEqual(S["last_page_number"](BeautifulSoup('<div class="PageNav" data-last="x"></div>', "html.parser")), 1)

    def test_free_space_check(self):
        self.assertTrue(S["enough_free_space"](tempfile.gettempdir(), 0))
        self.assertFalse(S["enough_free_space"](tempfile.gettempdir(), 10_000_000))   # 10 million GB


class HashTests(unittest.TestCase):
    def setUp(self):
        S["_hash_ints"] = []
        S["_hash_ints"].clear()

    def test_hashes_become_integers_and_follow_the_history(self):
        history = {"downloaded_hashes": ["0000000000000000", "ffffffffffffffff"]}
        self.assertEqual(S["hash_ints"](history), [0, 2**64 - 1])
        history["downloaded_hashes"].append("0000000000000010")
        self.assertEqual(S["hash_ints"](history), [0, 2**64 - 1, 16])   # only the new one is converted

    def test_a_shorter_history_restarts_the_cache(self):
        S["hash_ints"]({"downloaded_hashes": ["0000000000000001", "0000000000000002"]})
        self.assertEqual(S["hash_ints"]({"downloaded_hashes": ["0000000000000005"]}), [5])

    def test_near_duplicates_are_found_by_bit_distance(self):
        known = [0b1010101010, 0xFFFF]
        near = S["is_near_duplicate"]
        self.assertTrue(near(0b1010101010, known, 5))            # identical
        self.assertTrue(near(0b1010101011, known, 5))            # 1 bit away
        self.assertTrue(near(0b1010111111, known, 5))            # 3 bits away
        self.assertFalse(near(0b0101010101, known, 5))           # every bit different
        self.assertFalse(near(123456789, [], 5))                 # nothing to match


class CanonicalNameTests(unittest.TestCase):
    def setUp(self):
        S["_canon"].clear()

    def test_the_first_spelling_seen_is_used_for_everyone_after_it(self):
        self.assertEqual(S["canonical_person"]("Mckenna Grace"), "Mckenna Grace")
        self.assertEqual(S["canonical_person"]("McKenna Grace"), "Mckenna Grace")
        self.assertEqual(S["canonical_person"]("MCKENNA GRACE"), "Mckenna Grace")
        self.assertEqual(S["canonical_person"]("Ana de Armas"), "Ana de Armas")
        self.assertEqual(S["canonical_person"]("Ana De Armas"), "Ana de Armas")

    def test_at_start_up_the_spelling_most_pictures_use_wins(self):
        spellings = S["most_used_spellings"]([("McKenna Grace", 107), ("Mckenna Grace", 398), ("Ana De Armas", 3), ("Ana de Armas", 40),
                                              ("Kelly Brook", 84)])
        self.assertEqual(spellings, {"mckenna grace": "Mckenna Grace", "ana de armas": "Ana de Armas", "kelly brook": "Kelly Brook"})

    def test_a_tie_is_settled_the_same_way_every_time(self):
        a = S["most_used_spellings"]([("McKenna Grace", 5), ("Mckenna Grace", 5)])
        b = S["most_used_spellings"]([("Mckenna Grace", 5), ("McKenna Grace", 5)])
        self.assertEqual(a, b)


class BlockedHostTests(unittest.TestCase):
    def test_only_links_on_a_blocked_host_are_recognised(self):
        self.assertTrue(S["is_blocked_host"]("https://www.turboimagehost.com/p/129936247/HH_15.jpg.html"))
        self.assertTrue(S["is_blocked_host"]("https://turboimagehost.com/p/1/a.jpg.html"))
        self.assertFalse(S["is_blocked_host"]("https://phun.imagetwist.com/abc/x.jpg"))
        self.assertFalse(S["is_blocked_host"]("https://notturboimagehost.com/p/1/a.jpg.html"))
        self.assertFalse(S["is_blocked_host"]("https://example.com/turboimagehost.com/a.jpg"))

    def test_each_blocked_link_is_listed_once(self):
        with tempfile.TemporaryDirectory() as d:
            S["BLOCKED_FILE"] = os.path.join(d, "blocked_links.txt")
            logging.disable(logging.CRITICAL)
            history = {"blocked_urls": []}
            meta = {"thread_title": "Hilary Duff soaking up the sun", "thread_url": "https://forum.example/t/1"}
            links = ["https://www.turboimagehost.com/p/1/a.jpg.html", "https://www.turboimagehost.com/p/2/b.jpg.html",
                     "https://www.turboimagehost.com/p/1/a.jpg.html"]
            self.assertEqual(S["note_blocked_links"](links, ["Hilary Duff"], meta, history), 2)
            self.assertEqual(S["note_blocked_links"](links, ["Hilary Duff"], meta, history), 0)     # a later scan adds nothing
            lines = open(S["BLOCKED_FILE"], encoding="utf-8").read().splitlines()
            self.assertEqual(len(lines), 2)
            fields = lines[0].split("\t")
            self.assertEqual(fields[1:], ["Hilary Duff", "Hilary Duff soaking up the sun", "https://forum.example/t/1",
                                          "https://www.turboimagehost.com/p/1/a.jpg.html"])
            self.assertEqual(len(history["blocked_urls"]), 2)

    def test_nothing_is_written_when_there_are_no_blocked_links(self):
        with tempfile.TemporaryDirectory() as d:
            S["BLOCKED_FILE"] = os.path.join(d, "blocked_links.txt")
            self.assertEqual(S["note_blocked_links"]([], ["X Y"], {}, {"blocked_urls": []}), 0)
            self.assertFalse(os.path.exists(S["BLOCKED_FILE"]))


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
