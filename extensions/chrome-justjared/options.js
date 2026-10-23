const DEFAULTS = { root: "Z:\\Babes", defaultTags: [] };
const $ = (id) => document.getElementById(id);
const isFullPath = (p) => /^[A-Za-z]:[\\/]/.test(p) || /^\\\\[^\\]+\\[^\\]+/.test(p);

function say(text, kind) {
  const m = $("msg");
  m.textContent = text;
  m.className = kind || "";
}

async function load() {
  const s = await chrome.storage.local.get(DEFAULTS);
  $("root").value = s.root;
  $("tags").value = (s.defaultTags || []).join(", ");
}

$("save").onclick = async () => {
  const root = $("root").value.trim();
  if (!isFullPath(root)) {
    say("Enter a full path, like Z:\\Babes.", "err");
    return;
  }
  const defaultTags = $("tags").value
    .split(",")
    .map((t) => t.replace(/[;\u0000-\u001f]/g, " ").replace(/\s+/g, " ").trim())
    .filter(Boolean)
    .slice(0, 20);
  await chrome.storage.local.set({ root, defaultTags });
  say("Saved.", "ok");
};

load();
