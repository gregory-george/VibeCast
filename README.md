# VibeCast

A portable, single-folder podcast client for Windows. Double-click `VibeCast.exe` and
it opens in your default browser — the `.exe` is a self-contained backend (Blazor
Server on Kestrel, loopback-only); the browser is just the renderer. Subscribes to
RSS/Atom podcast feeds and YouTube channels.

## Running it

Double-click `VibeCast.exe`. It binds to `http://127.0.0.1:5123` (or the next free
port if that one's taken), opens a browser tab against it, and drops a tray icon
(running indicator, **Reopen UI**, **Quit**). Closing the last tab eventually shuts
the backend down on its own (a short grace window, and it won't exit mid-download) —
there's nothing to manually stop unless you want to via tray **Quit**.

First launch offers to drop a desktop shortcut, since a portable app gets no Start
menu entry.

## The folder is the entire app

```
VibeCast/
├─ VibeCast.exe          # backend + launcher
├─ wwwroot/               # static web assets (present in self-contained builds)
├─ config.json            # settings, including the sticky port preference
├─ run.lock                # live port for the current run (present only while running)
├─ podcasts.db             # SQLite: feeds, episodes, state, archive
├─ podcasts.db.bak         # pre-migration backup, refreshed automatically
├─ downloads/<feed-slug>/  # downloaded RSS enclosures
└─ logs/vibecast-YYYYMMDD.log
```

Nothing is written outside this folder — no registry, no `%AppData%`. **Backup or
move the app by copying the whole folder** (or just `podcasts.db` + `config.json` for
state only). There's no separate backup format.

## Settings

In-app at `/settings`: port, tray on/off, shutdown grace window, refresh-on-open,
concurrent downloads, default auto-download cutoff, default Shorts exclusion,
default keep-last-N retention, default playback speed, auto-mark-on-completion, and
OPML import/export (subscription list only — not a full backup, see above).

## Updating

There's no installer or auto-updater — VibeCast ships as a folder you replace by
hand:

1. **Quit VibeCast first** (tray icon → Quit, or close every tab and let it shut
   itself down). Windows locks a running `.exe`; you can't overwrite it while it's
   open.
2. Drop the new `VibeCast.exe` (and `wwwroot/`, if the build is self-contained) into
   the folder, overwriting the old one.
3. Leave `config.json`, `podcasts.db`, `downloads/`, and `logs/` in place — they're
   your data, untouched by the swap. The app auto-migrates the database on next
   launch (backing it up to `podcasts.db.bak` first).
4. Relaunch.

## Building a release

```
dotnet publish src/VibeCast/VibeCast.csproj -c Release -p:PublishProfile=win-x64-singlefile
```

Output lands in `src/VibeCast/bin/Release/net10.0-windows/win-x64/publish/`: a single
self-contained `VibeCast.exe` plus a sibling `wwwroot/` folder (static web assets
aren't embedded into the single file). Copy that whole output folder to ship — that's
the "folder is the entire app" unit described above, just without `config.json`/
`podcasts.db`/etc. yet, since those are created on first run.

Windows x64 only; no arm64 build.
