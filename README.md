# VibeCast

![Platform: Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

A portable, single-folder podcast client for Windows — for RSS and YouTube, with a
built-in player and offline downloads. No install, no account, no cloud.

Double-click `VibeCast.exe` and it opens in your default browser. The `.exe` is a
self-contained backend (Blazor Server on Kestrel, bound to loopback only); the browser
is just the renderer. Nothing is written outside the app folder, and it only ever
talks to the feeds you subscribe to — no telemetry, and it stays off the LAN.

## Features

- **Subscribe to anything** — RSS/Atom podcast feeds *and* YouTube. Paste a channel
  URL, `@handle`, or a `playlist?list=…` link; VibeCast resolves it for you, no API key
  needed. Optionally exclude YouTube Shorts. Import/export your list as OPML.
- **Built-in player** — audio and video play right in the browser; no external app
  required. Resumes each episode where you left off, with variable speed (RSS 1.0–3.0×,
  YouTube 0.25–2.0×), keyboard skip, and YouTube captions. A now-playing bar persists as
  you navigate. An "open in external app" fallback covers codecs the browser can't play.
- **Offline downloads** — new RSS episodes auto-download for offline listening (a
  per-feed toggle), with concurrency and an age cutoff you control. Downloads are
  resumable, so a restart picks up where it left off. YouTube always streams and is
  never downloaded.
- **Stays tidy on its own** — unplayed / played / archived states, with the archive kept
  forever but hidden by default. Marking an RSS episode played reclaims its disk space; a
  per-feed *keep-last-N* cap keeps downloads from piling up. The sidebar shows unplayed
  counts per feed, and show notes are sanitized but keep their links.
- **Portable & private** — a single self-contained `.exe`: no installer, no .NET runtime
  to install, no admin rights, no registry. The whole app is one folder you can copy,
  back up, or move. Light / dark / system themes, a system-tray control, and
  refresh-on-open.

## Requirements

- **Windows 10 or 11, x64.** That's it — the `.exe` is self-contained, so there's no
  separate .NET runtime to install and nothing to run as administrator.

## Getting started

1. **Launch.** Double-click `VibeCast.exe`. It binds to `http://127.0.0.1:5123` (or the
   next free port if that one's taken), opens a browser tab against it, and drops a tray
   icon (running indicator, **Reopen UI**, **Quit**). On first launch it offers to create
   a desktop shortcut, since a portable app gets no Start-menu entry.
2. **Add a feed.** Go to the **Feeds** page and paste a podcast RSS URL or a YouTube
   channel/playlist URL, then **Add**. New subscriptions refresh immediately; existing
   ones refresh on launch and on demand.
3. **Listen.** Pick an episode from the library. RSS episodes download first, then play
   locally; YouTube streams. Close the tab when you're done.

Closing the last tab shuts the backend down on its own after a short grace window (it
won't exit mid-download), so there's usually nothing to stop by hand — but you can always
quit immediately from the tray.

## Settings

Everything is in-app at **`/settings`**:

- **Startup & window** — listen port, refresh-on-open, system tray on/off, shutdown grace
  window.
- **Appearance** — theme (light / dark / system).
- **Playback** — default speed, skip-seconds, closed-captions default, and
  auto-mark-as-played on completion.
- **Downloads & retention** — concurrent downloads, default auto-download age cutoff,
  default keep-last-N, and how many episodes stay active on a brand-new RSS subscription.
- **YouTube** — exclude Shorts by default.
- **Subscriptions** — OPML import / export (the subscription list only — not a full
  backup; see below).

## The folder is the entire app

```
VibeCast/
├─ VibeCast.exe            # backend + launcher
├─ wwwroot/                # static web assets (present in self-contained builds)
├─ config.json             # settings, including the sticky port preference
├─ run.lock                # live port for the current run (present only while running)
├─ podcasts.db             # SQLite: feeds, episodes, state, archive
├─ backups/                # once-per-day pre-migration backups, last 10 of each kept
│  ├─ podcasts-yyyyMMdd.db.bak
│  └─ config-yyyyMMdd.json.bak
├─ downloads/<feed-slug>/  # downloaded RSS enclosures + per-feed cover art
└─ logs/vibecast-YYYYMMDD.log
```

Nothing is written outside this folder — no registry, no `%AppData%`. **Back up or move
the app by copying the whole folder** (or just `podcasts.db` + `config.json` for state
only). There's no separate backup format.

## Updating

There's no installer or auto-updater — VibeCast ships as a folder you replace by hand:

1. **Quit VibeCast first** (tray icon → **Quit**, or close every tab and let it shut
   itself down). Windows locks a running `.exe`; you can't overwrite it while it's open.
2. Drop the new `VibeCast.exe` (and `wwwroot/`, if the build is self-contained) into the
   folder, overwriting the old one.
3. Leave `config.json`, `podcasts.db`, `downloads/`, and `logs/` in place — they're your
   data, untouched by the swap. The app auto-migrates the database on next launch, backing
   up `podcasts.db` and `config.json` to `backups/` first (once per calendar day, keeping
   the last 10 of each).
4. Relaunch.

## How it works

The browser is the renderer; the `.exe` is the app.

- One process hosts both a **Blazor Server** site on **Kestrel** (UI state and logic live
  server-side) and a Win32 **system-tray** icon. Kestrel binds `127.0.0.1` only, so it
  never touches the LAN and skips the firewall prompt.
- Feeds and episode state persist in **SQLite** (via EF Core, in WAL mode); the schema
  auto-migrates on startup after a same-day backup.
- Feeds are treated as hostile input: show-note HTML is sanitized before rendering,
  downloads take their file extension from the enclosure's media type (never a
  feed-supplied name), fetches are size-capped, and XML parsing forbids DTDs (no XXE).
- Published as a **single self-contained single-file** `.exe` with a sibling `wwwroot/`.

See [`CLAUDE.md`](CLAUDE.md) for the full architecture and maintenance invariants.

## Building from source

Requires the **.NET 10 SDK** on Windows (the target framework is `net10.0-windows`).

```
# Publish a shippable, self-contained build
dotnet publish src/VibeCast/VibeCast.csproj -c Release -p:PublishProfile=win-x64-singlefile

# Run the test suite
dotnet test
```

The publish output lands in `src/VibeCast/bin/Release/net10.0-windows/win-x64/publish/`:
a single self-contained `VibeCast.exe` plus a sibling `wwwroot/` folder (static web assets
aren't embedded into the single file). Copy that whole output folder to ship — it's the
"folder is the entire app" unit described above, minus `config.json` / `podcasts.db` /
etc., which are created on first run.

Windows x64 only — no arm64 build.

## License

[MIT](LICENSE)
