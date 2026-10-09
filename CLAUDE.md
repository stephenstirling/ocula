# Ocula — instructions for Claude Code

Project root (on the owner's PC): `Z:\Data\sites\ocula`. Treat this folder as the repo root and
reference it for everything. Owner: Stephen Stirling (USA, github.com/stephenstirling).

## What this is
Ocula is a free Windows 10 (1809+) / 11, **x64-only** photo browser. WinUI 3, C#, .NET 8,
Windows App SDK, SQLite library, WebView2, ImageSharp, CoenM.ImageHash, ONNX Runtime.
Distributed through the Microsoft Store (free signing) and GitHub. No paid certificates, no Azure.
Donations (Buy Me a Coffee) are hidden in Store builds (`AppInfo.DonationsInStoreBuild = false`).

## Honest status
**Nothing here has ever been compiled or run.** All code was written without a Windows machine.
Expect compile errors. There is no `.csproj` yet: `Ocula.csproj-additions.xml` and
`Package.appxmanifest-additions.xml` describe what to merge into a fresh WinUI 3 project.

## Read first
1. `BUILDING.md` — architecture, phases, roadmap, first-run checks.
2. `README.md`, `RELEASING.md`, `PRIVACY.md`, `THIRD-PARTY-NOTICES.md`.
3. `docs/mockups/*.png` and `docs/design-canvas/*.dc.html` — the agreed UI designs (design only).

## Rules
- x64 only. Min Windows 10.0.17763.0.
- Privacy: everything local. No telemetry. Network only for what the user starts
  (updates, web download pane, optional person lookup, model download).
- Do not add body-focused smart labels (bikini, lingerie, pregnant, etc.). Not wanted.
- Web download: no ad-screen, paywall, form or login circumvention. Same-site cookies only.
- Keep the git author as `Stephen Stirling <8118368+stephenstirling@users.noreply.github.com>`.
- Do not invent facts in lookup mock data (Shakira/Hilary examples are placeholders).

## Sibling apps (separate repos, same owner)
Porta (Start menu replacement), Capta (screenshot tool). Shared code lives in `stirling-shared`.
Ocula must later serve `photos.search` and `photos.pinnables` over a local named pipe for Porta
(see `docs/PORTA-INTEGRATION.md`). Do not build that until `stirling-shared` is available here.
