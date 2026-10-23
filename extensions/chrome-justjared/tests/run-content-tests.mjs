// Runs content.js against hand-written Just Jared-style pages in jsdom.
// The pages are written from the structure we expect, NOT saved from the real site,
// so a pass means the logic works on that structure, not that the live site matches it.
//   cd extensions/chrome-justjared/tests && npm install && npm test
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const require = createRequire(import.meta.url);
const { JSDOM } = require("jsdom");
const here = dirname(fileURLToPath(import.meta.url));
const contentJs = readFileSync(join(here, "..", "content.js"), "utf8");

const B = "https://jj-justjared-media.s3.us-east-1.amazonaws.com/wp-content/uploads/2013/06/philipps-ultrasound/";
const pageUrl = "https://www.justjared.com/2013/06/07/pregnant-busy-philipps-shares-babys-sonogram-picture/";

async function scan(html, pages = {}) {
  const dom = new JSDOM(html, { url: pageUrl, runScripts: "outside-only" });
  const w = dom.window;
  let listener;
  w.chrome = { runtime: { onMessage: { addListener: (fn) => { listener = fn; } } } };
  w.fetch = async (u) => {
    const body = pages[u];
    return { ok: body !== undefined, text: async () => body ?? "" };
  };
  w.eval(contentJs);
  // Arrays built inside jsdom have another realm's prototype; copy them so deepEqual compares values.
  return new Promise((resolve) => listener({ type: "scan" }, {}, (r) => resolve(JSON.parse(JSON.stringify(r)))));
}

const shell = (h1, tags, body, gallery) => `<!doctype html><html><head><title>${h1} | Just Jared</title></head><body>
  <header><img src="https://www.justjared.com/assets/logo.png"></header>
  <article><h1>${h1}</h1>${body}${gallery}
    <p class="tags">Posted to: ${tags}</p></article>
  <aside><img src="${B}ad-banner-300x250.jpg"><img src="https://ads.example.com/x.jpg"></aside>
</body></html>`;

const direct = [1, 2, 3, 4, 5].map((n) =>
  `<a href="${B}busy-philipps-shares-babys-ultrasound-pic-0${n}.jpg"><img src="${B}busy-philipps-shares-babys-ultrasound-pic-0${n}-150x150.jpg"></a>`).join("");

const tests = [];
const test = (name, fn) => tests.push([name, fn]);

test("direct image links: five photos, one name, category tag and body mention dropped", async () => {
  const html = shell("Pregnant Busy Philipps Shares Baby's Sonogram Picture!",
    `<a rel="tag" href="/tag/busy-philipps/">Busy Philipps</a> <a rel="tag" href="/tag/pregnant-celebrities/">Pregnant Celebrities</a>`,
    `<p>Busy Philipps brings her baby bump out. "I get why <a href="/tag/tom-cruise/">Tom Cruise</a> bought one."</p>`,
    `<div>${direct}</div>`);
  const r = await scan(html);
  assert.equal(r.ok, true);
  assert.deepEqual(r.names, ["Busy Philipps"]);
  assert.equal(r.images.length, 5);
  assert.equal(r.images[4], B + "busy-philipps-shares-babys-ultrasound-pic-05.jpg");
  assert.ok(r.images.every((u) => u.includes("jj-justjared-media") && !u.includes("ad-banner")));
});

test("tag text stored in capitals is tidied", async () => {
  const html = shell("Busy Philipps Out And About", `<a rel="tag" href="/t/">BUSY PHILIPPS</a>`,
    `<p>Busy Philipps is out.</p>`, `<div>${direct}</div>`);
  assert.deepEqual((await scan(html)).names, ["Busy Philipps"]);
});

test("thumbnails that link to gallery pages are resolved through og:image", async () => {
  const links = [1, 2, 3].map((n) => `<a href="https://www.justjared.com/2013/06/07/x/pic-${n}/"><img src="${B}t-${n}-150x150.jpg"></a>`).join("");
  const pages = Object.fromEntries([1, 2, 3].map((n) => [
    `https://www.justjared.com/2013/06/07/x/pic-${n}/`,
    `<html><head><meta property="og:image" content="${B}full-${n}.jpg"></head></html>`]));
  const r = await scan(shell("Busy Philipps Out", `<a rel="tag" href="/t/">Busy Philipps</a>`, `<p>Busy Philipps.</p>`, `<div>${links}</div>`), pages);
  assert.deepEqual(r.images, [1, 2, 3].map((n) => B + `full-${n}.jpg`));
});

test("an unlinked thumbnail has its size suffix stripped", async () => {
  const r = await scan(shell("Busy Philipps Out", `<a rel="tag" href="/t/">Busy Philipps</a>`, `<p>Busy Philipps.</p>`,
    `<img src="${B}solo-300x200.jpg">`));
  assert.deepEqual(r.images, [B + "solo.jpg"]);
});

test("two names in the headline are both found", async () => {
  const r = await scan(shell("Busy Philipps & Anna Chlumsky: Pregnant Pals for Variety!",
    `<a rel="tag" href="/t/1/">Busy Philipps</a> <a rel="tag" href="/t/2/">Anna Chlumsky</a>`,
    `<p>Busy Philipps and Anna Chlumsky pose.</p>`, `<div>${direct}</div>`));
  assert.deepEqual(r.names, ["Busy Philipps", "Anna Chlumsky"]);
});

test("a name only in the body is the fallback when the headline has none", async () => {
  const r = await scan(shell("Red Carpet Roundup", `<a rel="tag" href="/t/">Busy Philipps</a>`,
    `<p>Busy Philipps arrived early.</p>`, `<div>${direct}</div>`));
  assert.deepEqual(r.names, ["Busy Philipps"]);
});

async function popupFor(scanResult, settings = {}) {
  const html = readFileSync(join(here, "..", "popup.html"), "utf8").replace(/<script[^>]*><\/script>/, "");
  const dom = new JSDOM(html, { url: "chrome-extension://abc/popup.html", runScripts: "outside-only" });
  const w = dom.window;
  const copied = [];
  w.chrome = {
    storage: { local: { get: async (d) => ({ ...d, ...settings }) } },
    tabs: {
      query: async () => [{ id: 1, url: pageUrl }],
      sendMessage: async () => scanResult,
    },
  };
  Object.defineProperty(w.navigator, "clipboard", { value: { writeText: async (t) => { copied.push(t); } } });
  w.eval(readFileSync(join(here, "..", "popup.js"), "utf8"));
  await new Promise((r) => setTimeout(r, 20));
  return { w, copied, doc: w.document };
}

const scanned = { ok: true, url: pageUrl, title: "Pregnant Busy Philipps Shares Baby's Sonogram Picture!",
  names: ["Busy Philipps"], images: [B + "a.jpg", B + "b.jpg"] };

test("popup: name chip, suggested folder, user tag and the link", async () => {
  const { w, copied, doc } = await popupFor(scanned);
  assert.equal(doc.getElementById("ready").hidden, false);
  assert.equal(doc.getElementById("found").textContent, "2 photos found");
  assert.equal(doc.getElementById("folder").value, "Z:\\Babes\\Busy Philipps");
  const input = doc.getElementById("tagInput");
  input.value = "candid";
  input.dispatchEvent(new w.KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
  doc.getElementById("copy").click();
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(copied[0],
    "ocula:webdownload?url=" + encodeURIComponent(pageUrl) +
    "&folder=Z%3A%5CBabes%5CBusy%20Philipps&tags=Busy%20Philipps%3Bcandid");
});

test("popup: empty tag, duplicate tag and a relative folder each show an error", async () => {
  const { w, copied, doc } = await popupFor(scanned);
  const input = doc.getElementById("tagInput");
  const press = () => input.dispatchEvent(new w.KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
  input.value = ""; press();
  assert.match(doc.getElementById("tagErr").textContent, /Enter a tag/);
  input.value = "busy philipps"; press();
  assert.match(doc.getElementById("tagErr").textContent, /already there/);
  doc.getElementById("folder").value = "Babes\\x";
  doc.getElementById("copy").click();
  await new Promise((r) => setTimeout(r, 20));
  assert.match(doc.getElementById("folderErr").textContent, /full path/);
  assert.equal(copied.length, 0);
});

test("popup: removing the name chip clears the untouched folder", async () => {
  const { doc } = await popupFor(scanned);
  doc.querySelector(".chip button").click();
  assert.equal(doc.getElementById("folder").value, "");
});

let failed = 0;
for (const [name, fn] of tests) {
  try { await fn(); console.log("ok   ", name); }
  catch (e) { failed++; console.log("FAIL ", name, "\n     ", e.message); }
}
console.log(`\n${tests.length - failed}/${tests.length} passed`);
process.exit(failed ? 1 : 0);
