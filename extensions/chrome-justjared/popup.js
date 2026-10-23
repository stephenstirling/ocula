// Popup: asks the page's content script what it found, lets you edit the
// tags and the save folder, then hands the article to Ocula with an
// ocula:webdownload link. Ocula does the preview and the download.
const DEFAULTS = { root: "Z:\\Babes", defaultTags: [] };
const MAX_TAGS = 20;
const MAX_STRIP = 8;

const $ = (id) => document.getElementById(id);
const show = (id) => {
  for (const s of ["idle", "loading", "failed", "ready"]) $(s).hidden = s !== id;
};

let state = { url: "", root: DEFAULTS.root, chips: [], folderTouched: false };

const isArticle = (u) => {
  try {
    const x = new URL(u);
    return /(^|\.)justjared\.com$/.test(x.hostname) && /^\/\d{4}\/\d{2}\/\d{2}\//.test(x.pathname);
  } catch { return false; }
};

// Windows folder names can't hold these characters.
const safeFolderName = (name) => name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, "").trim();
const joinPath = (root, name) => root.replace(/[\\/]+$/, "") + "\\" + name;
const isFullPath = (p) => /^[A-Za-z]:[\\/]/.test(p) || /^\\\\[^\\]+\\[^\\]+/.test(p);

function firstPerson() {
  const c = state.chips.find((x) => x.person);
  return c ? c.text : "";
}

function suggestedFolder() {
  const name = safeFolderName(firstPerson());
  return name ? joinPath(state.root, name) : "";
}

function renderChips() {
  const box = $("chips");
  box.textContent = "";
  state.chips.forEach((c, i) => {
    const el = document.createElement("span");
    el.className = "chip" + (c.person ? " person" : "");
    el.append(document.createTextNode(c.text));
    const x = document.createElement("button");
    x.type = "button";
    x.textContent = "\u00d7";
    x.setAttribute("aria-label", "Remove " + c.text);
    x.onclick = () => {
      state.chips.splice(i, 1);
      if (!state.folderTouched) $("folder").value = suggestedFolder();
      renderChips();
      renderHint();
    };
    el.append(x);
    box.append(el);
  });
}

function renderHint() {
  $("folderHint").textContent = $("folder").value.trim()
    ? "Ocula uses this folder if it is inside one of your library folders; otherwise it asks."
    : "No name found. Ocula will use its own default folder.";
}

function addTag(raw) {
  const err = $("tagErr");
  err.textContent = "";
  // ";" separates tags in the link, so it can't be part of one.
  const text = raw.replace(/[;\u0000-\u001f]/g, " ").replace(/\s+/g, " ").trim();
  if (!text) { err.textContent = "Enter a tag first."; return false; }
  if (state.chips.some((c) => c.text.toLowerCase() === text.toLowerCase())) {
    err.textContent = "That tag is already there.";
    return false;
  }
  if (state.chips.length >= MAX_TAGS) { err.textContent = "That's the most tags allowed (" + MAX_TAGS + ")."; return false; }
  state.chips.push({ text, person: false });
  renderChips();
  return true;
}

function buildLink() {
  const enc = encodeURIComponent;
  const folder = $("folder").value.trim();
  let link = "ocula:webdownload?url=" + enc(state.url);
  if (folder) link += "&folder=" + enc(folder);
  if (state.chips.length) link += "&tags=" + enc(state.chips.map((c) => c.text).join(";"));
  return link;
}

function validate() {
  const folder = $("folder").value.trim();
  const err = $("folderErr");
  err.textContent = "";
  if (folder && !isFullPath(folder)) {
    err.textContent = "Enter a full path, like Z:\\Babes\\Name, or clear the field.";
    return false;
  }
  return true;
}

async function init() {
  const settings = await chrome.storage.local.get(DEFAULTS);
  state.root = settings.root || DEFAULTS.root;

  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab || !tab.url || !isArticle(tab.url)) { show("idle"); return; }

  let result;
  try {
    result = await chrome.tabs.sendMessage(tab.id, { type: "scan" });
  } catch {
    $("failedText").textContent = "Reload the page, then click the extension again.";
    show("failed");
    return;
  }
  if (!result || !result.ok) {
    $("failedText").textContent = "The page couldn't be read. Reload it and try again.";
    show("failed");
    return;
  }

  state.url = result.url;
  state.chips = [
    ...result.names.map((text) => ({ text, person: true })),
    ...(settings.defaultTags || [])
      .filter((t) => !result.names.some((n) => n.toLowerCase() === t.toLowerCase()))
      .map((text) => ({ text, person: false })),
  ];

  const n = result.images.length;
  $("found").textContent = n === 1 ? "1 photo found" : n + " photos found";
  $("articleTitle").textContent = result.title;

  const strip = $("strip");
  result.images.slice(0, MAX_STRIP).forEach((u) => {
    const img = document.createElement("img");
    img.loading = "lazy";
    img.alt = "";
    img.src = u;
    strip.append(img);
  });

  $("folder").value = suggestedFolder();
  $("folder").addEventListener("input", () => { state.folderTouched = true; renderHint(); });
  renderChips();
  renderHint();
  show("ready");
}

$("tagInput").addEventListener("keydown", (e) => {
  if (e.key !== "Enter" && e.key !== ",") return;
  e.preventDefault();
  if (addTag($("tagInput").value)) $("tagInput").value = "";
});
$("tagInput").addEventListener("input", () => { $("tagErr").textContent = ""; });

$("open").onclick = () => {
  // A tag typed but not yet confirmed still counts.
  const pending = $("tagInput").value.trim();
  if (pending && addTag(pending)) $("tagInput").value = "";
  if (!validate()) return;
  window.location.href = buildLink();
};

$("copy").onclick = async () => {
  if (!validate()) return;
  await navigator.clipboard.writeText(buildLink());
  $("copy").textContent = "Copied";
  setTimeout(() => { $("copy").textContent = "Copy link"; }, 1500);
};

init();
