// Runs on justjared.com pages. Answers the popup's "scan" message with the
// article's title, the people it names, and its photo addresses.
// Nothing leaves the page: no network calls except same-site page fetches
// (to read a thumbnail's linked page), and nothing is stored.
(() => {
  const MEDIA_HOST = "jj-justjared-media"; // Just Jared's S3 bucket for article photos
  const SKIP = ["avatar", "gravatar", "logo", "icon", "sprite", "banner",
                "adserver", "doubleclick", "pixel", "emoji", "placeholder"];
  const IMG_RE = /\.(jpe?g|png|webp)(\?|$)/i;
  const MAX_PAGE_LINKS = 40;

  const clean = (t) => (t || "").replace(/\s+/g, " ").trim();
  const uniq = (list) => [...new Set(list)];

  // The site styles tags in capitals; read the raw text and tidy it.
  function tidy(text) {
    const t = clean(text);
    return t === t.toUpperCase()
      ? t.toLowerCase().replace(/\b[a-z]/g, (c) => c.toUpperCase())
      : t;
  }

  function looksLikeName(text) {
    const words = text.split(" ");
    return words.length >= 2 && words.length <= 4 &&
      words.every((w) => /^[A-Z][A-Za-z.'\-]+$/.test(w));
  }

  function getTitle() {
    const h1 = document.querySelector("h1");
    return clean((h1 && h1.textContent) || document.title.split("|")[0]);
  }

  // Names named in the headline win. A name that is only mentioned further
  // down (a quoted tweet, say) is used only when the headline has none, and a
  // category tag such as "Pregnant Celebrities" never appears in the text, so
  // it is dropped.
  // The page's structured data: the article's categories/tags and its canonical address.
  function getLd() {
    for (const el of document.querySelectorAll("script[type='application/ld+json']")) {
      try {
        const data = JSON.parse(el.textContent);
        const graph = Array.isArray(data) ? data : data["@graph"] || [data];
        const art = graph.find((x) => x && x["@type"] === "Article");
        if (art) return art;
      } catch { /* not JSON we can read */ }
    }
    return null;
  }

  // Gallery pages (/photo-gallery/ID/slug/) show one photo of the article; the article itself is named in the page data.
  function articleUrl() {
    const art = getLd();
    const main = art && art.mainEntityOfPage && art.mainEntityOfPage["@id"];
    try {
      const u = new URL(main, location.href);
      if (u.hostname.endsWith("justjared.com") && /^\/\d{4}\/\d{2}\/\d{2}\//.test(u.pathname)) return u.origin + u.pathname;
    } catch { /* fall through */ }
    return location.origin + location.pathname;
  }

  function getNames(title) {
    const prose = (title + " " + [...document.querySelectorAll("p")]
      .map((p) => p.textContent).join(" ")).toLowerCase();
    const inTitle = [];
    const inBody = [];
    const art = getLd();
    const sections = art && Array.isArray(art.articleSection) ? art.articleSection : [];
    sections.forEach((text) => {
      const t = tidy(String(text));
      if (looksLikeName(t) && title.toLowerCase().includes(t.toLowerCase())) inTitle.push(t);
    });
    document.querySelectorAll("a[rel~='tag'], .entry-content a, article a").forEach((a) => {
      const text = tidy(a.textContent);
      if (!looksLikeName(text)) return;
      const low = text.toLowerCase();
      if (title.toLowerCase().includes(low)) inTitle.push(text);
      else if (prose.includes(low)) inBody.push(text);
    });
    return uniq(inTitle.length ? inTitle : inBody);
  }

  const isSiteLink = (href) => {
    try { return new URL(href, location.href).hostname.endsWith("justjared.com"); }
    catch { return false; }
  };

  // Strip WordPress "-300x200" thumbnail suffixes to get the original file.
  const fullSize = (u) => u.replace(/-\d+x\d+(?=\.\w+(\?|$))/, "");

  function bestSrc(img) {
    let best = img.currentSrc || img.src || img.dataset.src || img.dataset.lazySrc || "";
    if (img.srcset) {
      let top = 0;
      img.srcset.split(",").forEach((part) => {
        const [u, w] = part.trim().split(/\s+/);
        const n = parseInt(w, 10) || 0;
        if (u && n >= top) { top = n; best = u; }
      });
    }
    return best;
  }

  // A thumbnail that links to another page instead of the image file: read
  // that page's declared main image (og:image). Same site only.
  async function resolvePage(link) {
    try {
      const res = await fetch(link, { credentials: "same-origin" });
      if (!res.ok) return null;
      const doc = new DOMParser().parseFromString(await res.text(), "text/html");
      const og = doc.querySelector("meta[property='og:image']");
      return og && og.content ? og.content : null;
    } catch {
      return null;
    }
  }

  async function getImages() {
    const gallery = /^\/photo-gallery\//.test(location.pathname);
    const root = (!gallery && document.querySelector("article, .entry-content, .post-content, #content")) || document.body;
    const found = [];
    const pages = [];

    root.querySelectorAll("a[href]").forEach((a) => {
      if (IMG_RE.test(a.href)) found.push(a.href);
      else if (a.querySelector("img") && isSiteLink(a.href) && !a.querySelector("img[src*='/thumbs/']")) pages.push(a.href);
    });
    // The filmstrip on a gallery page: the original sits one folder up from its "/thumbs/" copy.
    root.querySelectorAll("img").forEach((img) => {
      const src = bestSrc(img);
      if (src.includes(MEDIA_HOST) && src.includes("/thumbs/")) found.push(src.replace("/thumbs/", "/"));
    });
    root.querySelectorAll("img").forEach((img) => {
      const src = bestSrc(img);
      const a = img.closest("a[href]");
      const handled = a && (IMG_RE.test(a.href) || isSiteLink(a.href));
      if (src && !handled && !src.includes("/thumbs/")) found.push(src);
    });

    for (const link of uniq(pages).slice(0, MAX_PAGE_LINKS)) {
      const og = await resolvePage(link);
      if (og) found.push(og);
    }

    // The same photo can come from two S3 host names (s3.amazonaws.com and s3.us-east-1...), so compare by path.
    const seen = new Set();
    return found
      .filter((u) => !u.startsWith("data:") && !SKIP.some((w) => u.toLowerCase().includes(w)))
      .filter((u) => u.includes(MEDIA_HOST))
      .map(fullSize)
      .filter((u) => {
        let key = u;
        try { key = new URL(u).pathname; } catch { /* keep the whole address */ }
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
      });
  }

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!message || message.type !== "scan") return false;
    (async () => {
      const title = getTitle();
      sendResponse({ ok: true, url: articleUrl(), title, names: getNames(title), images: await getImages() });
    })().catch((e) => sendResponse({ ok: false, error: String(e) }));
    return true; // the response is sent asynchronously
  });
})();
