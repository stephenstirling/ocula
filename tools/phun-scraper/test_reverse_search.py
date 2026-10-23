"""Regression tests for visual-upload reverse-search upgrades.

Run with:
    python -m unittest discover -s tools/phun-scraper -p "test_*.py"
"""
import ast
import io
import json
import logging
import os
import re
import unittest
from urllib.parse import quote_plus, urljoin

from PIL import Image

SOURCE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "phun_scraper.py")


class FakeResponse:
    def __init__(self, status_code=200, text="", content=b"", headers=None):
        self.status_code = status_code
        self.text = text
        self.content = content
        self.headers = headers or {}


def load_search_class():
    with open(SOURCE, encoding="utf-8") as stream:
        tree = ast.parse(stream.read())
    node = next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == "MultiEngineReverseSearch")
    module = ast.Module(body=[node], type_ignores=[])
    scope = {
        "io": io,
        "json": json,
        "logging": logging,
        "re": re,
        "html_lib": __import__("html"),
        "urljoin": urljoin,
        "quote_plus": quote_plus,
        "Image": Image,
        "MAX_UPGRADE_CANDIDATES": 8,
        "PHASH_SIMILARITY_THRESHOLD": 5,
        "image_ok": lambda data: True,
        "phash_of": lambda data: 0,
    }
    exec(compile(module, SOURCE, "exec"), scope)
    return scope["MultiEngineReverseSearch"]


class UploadSession:
    def __init__(self, post_response, result_response=None):
        self.post_response = post_response
        self.result_response = result_response
        self.post_call = None
        self.get_calls = []

    def post(self, url, **kwargs):
        self.post_call = (url, kwargs)
        return self.post_response

    def get(self, url, **kwargs):
        self.get_calls.append((url, kwargs))
        return self.result_response or FakeResponse(404)


class CandidateSession:
    def __init__(self, bodies):
        self.bodies = bodies
        self.head_calls = []
        self.get_calls = []

    def head(self, url, **kwargs):
        self.head_calls.append(url)
        body = self.bodies[url]
        return FakeResponse(headers={"Content-Type": "image/jpeg", "Content-Length": str(len(body))})

    def get(self, url, **kwargs):
        self.get_calls.append(url)
        return FakeResponse(content=self.bodies[url], headers={"Content-Type": "image/jpeg"})


class ReverseSearchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.Search = load_search_class()

    @staticmethod
    def sample_png(width=1600, height=900):
        output = io.BytesIO()
        Image.new("RGB", (width, height), (80, 120, 160)).save(output, format="PNG")
        return output.getvalue()

    def test_yandex_upload_uses_upfile_and_follows_result_url(self):
        html = r'<script>{"original":{"url":"https:\/\/cdn.example\/full.jpg"}}</script>'
        session = UploadSession(
            FakeResponse(text=json.dumps({
                "blocks": [{"block": "b-page_type_search-by-image__link",
                            "params": {"url": "?rpt=imageview&cbir_id=abc"}}]
            })),
            FakeResponse(text=html),
        )
        search = self.Search(session)

        results = search.search_yandex_upload(self.sample_png())

        self.assertEqual(results, ["https://cdn.example/full.jpg"])
        url, kwargs = session.post_call
        self.assertEqual(url, "https://yandex.com/images/search")
        self.assertIn("upfile", kwargs["files"])
        self.assertEqual(kwargs["params"]["rpt"], "imageview")
        uploaded = kwargs["files"]["upfile"][1]
        with Image.open(io.BytesIO(uploaded)) as query:
            self.assertLessEqual(max(query.size), 1024)
            self.assertEqual(query.format, "JPEG")
        self.assertEqual(session.get_calls[0][0],
                         "https://yandex.com/images/search?rpt=imageview&cbir_id=abc")

    def test_yandex_upload_returns_empty_for_captcha_page(self):
        session = UploadSession(FakeResponse(text="<html>Are you not a robot - SmartCaptcha</html>"))
        self.assertEqual(self.Search(session).search_yandex_upload(self.sample_png()), [])

    def test_visual_candidates_are_verified_before_url_fallback_candidates(self):
        visual = "https://cdn.example/visual.jpg"
        fallback = "https://cdn.example/fallback.jpg"
        original = "https://forum.example/image-page"
        bodies = {visual: b"v" * 40, fallback: b"f" * 60}
        session = CandidateSession(bodies)

        class StubSearch(self.Search):
            def search_yandex_upload(self, image_bytes):
                return [visual, original, visual]

            def search_yandex(self, image_url):
                return [fallback, visual]

            def search_google_lens(self, image_url):
                return []

            def search_bing(self, image_url):
                return []

        result = StubSearch(session).find_better_version(b"small-image", 10, 0, original_url=original)

        self.assertEqual(session.head_calls, [visual, fallback])
        self.assertEqual(result, bodies[fallback])

    def test_upload_failure_does_not_escape(self):
        class BrokenSession:
            def post(self, *args, **kwargs):
                raise RuntimeError("blocked")

        self.assertEqual(self.Search(BrokenSession()).search_yandex_upload(self.sample_png()), [])


if __name__ == "__main__":
    unittest.main()
