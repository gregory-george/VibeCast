# CLAUDE.md — VibeCast

Portable, single-folder podcast client for Windows. A self-contained .NET `.exe` hosts a
Blazor Server site on Kestrel (loopback); the **browser is the renderer, the exe is the
app**. UI state and logic live server-side. Subscribes to RSS/Atom feeds and YouTube
channels and playlists.

VibeCast is **shipped** (v1.0.1, all features built). This file is the durable context
for *maintaining* it: the invariants that silently break the app if violated, and the
behavioral rules that aren't obvious from any single file. It is the source of truth over
older docs — `vibecast-requirements-and-build-plan.md` is the original design rationale,
kept for history, not an active spec.

---

## Stack (fixed)

- **.NET 10**, **Blazor Server**, **Kestrel**. Target framework **`net10.0-windows`**
  (the WinForms tray needs the Windows desktop TFM).
- **Hosting model:** one process hosts *both* the `IHost` (Kestrel + Blazor) **and** a
  Win32 tray icon. The **`IHost` runs on a background thread** (`AppHost/HostRunner`); the
  **WinForms message loop owns the main STA thread** (`Program.Main`, for the
  `NotifyIcon`). Shutdown is bridged both ways: the host's `WaitForShutdownAsync`
  completing (success *or* failure) posts `ExitThread()` back to the UI thread, and tray
  **Quit** calls `StopApplication()`.
- **EF Core** over **SQLite** (`Microsoft.EntityFrameworkCore.Sqlite`) in **WAL** mode. EF
  Core is committed, not optional — auto-migrate-on-startup depends on it. Access via the
  **pooled `IDbContextFactory<AppDbContext>`** (`AddPooledDbContextFactory`) only — **never
  a shared `DbContext`.**
- **`System.Text.Json`** for `config.json` (`AppHost/AppConfig`).
- **Windows x64 only.** Single self-contained single-file publish (with a sibling
  `wwwroot/`). No arm64.

## Build & run

- Source lives under `src/VibeCast/`. Release publish:
  `dotnet publish src/VibeCast/VibeCast.csproj -c Release -p:PublishProfile=win-x64-singlefile`
  → a single `VibeCast.exe` plus a sibling `wwwroot/` (static web assets are **not**
  embedded into the single file).
- Console window is suppressed at runtime via `FreeConsole()` P/Invoke, **not**
  `OutputType=WinExe` — setting `WinExe` breaks the SDK's static-web-asset / Razor JS
  pipeline (`blazor.web.js` 404s, no interactivity). Don't "fix" this back to `WinExe`.

## Hard rules — do not break these

These are the things that silently break the app if violated.

**Networking**
- Kestrel binds **`127.0.0.1` only — never `0.0.0.0`.** Keeps it off the LAN and skips the
  firewall prompt. (The browser is opened against `http://localhost:<port>` — `localhost`
  is the secure-context host.)
- `http://localhost` is a secure context, so **no HTTPS / no cert** (no HTTPS redirection
  or HSTS anywhere).
- Default port **`5123`**, **auto-fallback** to the next free port (up to 50 attempts) if
  taken.
- On successful bind, persist the live port **two ways**: write `run.lock` (the truly-live
  port for *this* run) and `config.json` (sticky preference for next run).
- The single-instance relaunch path reads the live port from **`run.lock`** — **never
  assume `5123`.** Treat `run.lock` as potentially **stale** (prior run crashed without
  removing it): validate the port is actually live (TCP connect) before using it; if not,
  fall through to a fresh launch.
- The single-instance `Mutex` is a **local, non-abandoned** mutex held for the full
  process lifetime.

**Portability**
- Resolve all paths relative to the exe via **`AppContext.BaseDirectory`** (see
  `AppHost/AppPaths`) **/ `Environment.ProcessPath`.**
- **Never use `Assembly.Location`** — it's empty under single-file publish.
- **Never write to `%AppData%`.** The folder is the entire app.

**Database**
- WAL mode; **WAL checkpoint on shutdown** (`PRAGMA wal_checkpoint(TRUNCATE)`).
- **Never share a `DbContext`** (not thread-safe). Use the pooled
  `IDbContextFactory<AppDbContext>` (fresh context per unit of work). The download worker
  and UI circuits write from different threads in the same process.
- The connection string sets **`Default Timeout=30`** so a transient `SQLITE_BUSY` waits
  rather than throwing immediately. WAL = many-readers / one-writer, not free concurrent
  writes.
- **All timestamps are `DateTime` in UTC** — the `Utc` suffix on each timestamp property
  is load-bearing, not decorative. A value converter re-stamps `Kind=Utc` on read because
  SQLite drops `Kind` on round-trip. **Don't switch to `DateTimeOffset`:** the SQLite
  provider can't translate `ORDER BY` on it.
- EF migrations **auto-apply on startup**. Immediately before, a **once-per-calendar-day**
  backup copies `podcasts.db → backups/podcasts-yyyyMMdd.db.bak` **and** `config.json →
  backups/config-yyyyMMdd.json.bak` (skipped if today's already exists), keeping only the
  **last 10 of each** (oldest pruned automatically).

**Untrusted input (feeds are hostile)**
- **Always sanitize show-note HTML before rendering** (`Episodes/ShowNotesSanitizer`, built
  on `HtmlSanitizer`): strip scripts and unsafe markup, keep links clickable. Store the
  **raw** HTML; sanitize at render time, never store sanitized.
- **Derive each download's filename and extension from the enclosure's media type — never
  from the URL or any feed-supplied name** (`Downloads/DownloadFileNaming`). Prevents a
  feed landing a `.exe`/`.bat` in `downloads/` that later gets `ShellExecute`d. The media
  endpoint serves files with the stored `EnclosureMediaType`.

## Data model invariants

- **Additive feed model.** Refresh **adds** new items and **never removes** ones that aged
  out of the feed window. The **DB is the source of truth**; the feed is discovery only.
  (Deleting a feed is the one deliberate, user-confirmed exception — it wipes the rows and
  the `downloads/<slug>` folder.)
- **Composite de-dup key**, computed once at ingest (`Feeds/DedupKeyComputer`) and stored
  in a dedicated **`dedup_key`** column, enforced by a unique index scoped `(feed_id,
  dedup_key)`. Keys are source-prefixed for debuggability:
  - RSS: `guid:<guid>` → `url:<enclosure URL, query+fragment stripped>` → `hash:<sha256(title|pubdate)>`.
  - YouTube: `yt:<watch?v= video ID>`.
- **Persisted episode flags:** `IsPlayed`, `IsArchived`, `IsDownloaded`. `played` and
  `archived` are tracked as **distinct flags** even though they currently move together.
  There is **no stored `new` flag** — "new" is the derived default (none of the above).
- **Resume position:** `PlaybackPositionSeconds` per episode so the in-app player resumes
  where the user left off. Saved periodically during playback and on pause/stop. Becomes
  irrelevant once the item is played (RSS file is deleted at that point).

## Behavioral invariants

- **Initial subscribe (RSS):** only the newest `InitialActiveEpisodeCount` (default **15**)
  episodes stay active; older back-catalog items are **pre-archived** (`played + archived`)
  at ingest so auto-download-all doesn't flood disk on a deep feed on day one. YouTube is
  **not** pre-archived (its feed already returns only ~15).
- **Mark-as-played:** for RSS, **delete the downloaded file immediately** *and* move the
  record to **Archive**. If the file is **locked** (still open in the player), the flags
  still move now and the deletion is **deferred** — the retention sweep retries it on the
  next refresh/shutdown. For YouTube it's a read-flag + archive only (nothing on disk).
  Retention/deletion does **not** apply to YouTube.
- **Archive:** kept in the DB forever, hidden from the active/unplayed list. Show/hide
  toggle, **default off**, applies to RSS and YouTube.
- **Unarchive** sets `played = false`. For RSS it **re-downloads the enclosure only if the
  file is actually gone** (avoids a redundant re-fetch of one still on disk); surface a
  clear error if the URL is dead — no half-played ghost record. YouTube is a view-only move.
- **keep-last-N per feed** backstop (`DefaultKeepLastCount` default **100**, per-feed
  `KeepLastCount` override; `0` disables). Caps **downloaded files on disk per feed**, not
  DB rows — DB records are kept forever (additive model). RSS only. Policies **stack**.
  Cleanup runs on refresh (per feed) and on shutdown (all feeds).
- **Downloads:** auto-download all new by default (per-feed `AutoDownloadEnabled`
  override). **RSS enclosures only** — YouTube always streams and is **never** downloaded.
- **90-day auto-download cutoff** (`DefaultAutoDownloadMaxAgeDays` default **90**, per-feed
  `AutoDownloadMaxAgeDays`; `null` = no limit). Evaluated at **ingest/refresh** against
  pubdate (unknown date → treat as **today**). An item that ages past the cutoff while
  undownloaded simply never auto-downloads — it is **not** retroactively purged (that's
  keep-last-N's job).
- **Range-resume:** the downloader always re-requests the **original enclosure URL** (never
  a cached redirect target), so a resume naturally **re-resolves through redirector chains**
  (podtrac/op3/chartable sign short-lived URLs). If a resume request returns `200` instead
  of `206`, **restart from zero**.
- **In-app player is the default.** RSS audio/video play in an HTML5 `<audio>`/`<video>`
  element fed by the loopback media endpoint `/media/episodes/{id}`, which serves the local
  file with ASP.NET Core `enableRangeProcessing` (seek/scrub). YouTube plays via the
  **YouTube IFrame Player API** (embed). A persistent "now playing" component survives
  navigation between feeds/episodes.
- **RSS plays a local file — download-first.** Hitting play on an undownloaded RSS episode
  **queues the download and plays the local file when ready** — no direct-from-URL
  streaming. The file must exist on disk to play; this is why mark-as-played deletion is
  tied to playback **completion**, not open.
- **Playback speed:** RSS exposes a full **1.0–3.0 in 0.1 steps** control (HTML5
  `playbackRate` with `preservesPitch = true`). YouTube is capped to its **native
  0.25–2.0 / 0.25 menu** — surface YouTube's stepped control honestly; never fake 0.1
  granularity or 3x it can't honor. (Keyboard skip is `SkipSeconds`, default 10; YouTube
  captions default on, RSS has none.)
- **External handoff is the fallback, not the default.** An "Open in external app" action
  uses `Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })`: RSS hands
  the **local file** to the default app; YouTube opens the **plain
  `https://www.youtube.com/watch?v=VIDEO_ID`** URL in the default browser — no params, no
  embed. Escape hatch for codecs the browser's HTML5 engine can't play.
- **Tray on by default:** running indicator + **Quit** (clean `StopApplication()`; confirm
  if a download is mid-flight, then cancel it — not a force-kill; a ~10 s watchdog hard-exits
  only if graceful shutdown hangs) + **Reopen UI** (opens the live port).
- **Shutdown is "finish, then exit":** call `IHostApplicationLifetime.StopApplication()`
  only when **zero active circuits AND zero active downloads** hold past a grace window
  (`GraceWindowSeconds`, **default 10**, configurable). In-flight downloads finish first.
  Never evaluates before the first circuit has ever connected (so a slow first browser
  launch can't race the app into shutting down before anyone opened it).

## YouTube specifics

- Resolve `@handle`/`/c/`/`/user/` URLs to the `UC…` channel ID by scraping the channel
  page (`<meta itemprop="channelId">` or canonical `/channel/<id>`). No API key. A raw
  `channel_id` or feed-URL paste is always accepted as a fallback if scraping breaks.
- **Channel feed (default):** `…/feeds/videos.xml?channel_id=UC…` (includes Shorts +
  long-form).
- **"Exclude Shorts"** swaps to `…/feeds/videos.xml?playlist_id=UULF…` (`UC` → `UULF`).
- **User playlists:** a `youtube.com/playlist?list=PL…` URL (or a raw `playlist_id=PL…`
  feed URL) subscribes to that playlist's `videos.xml`. A distinct kind from channel feeds:
  **Exclude-Shorts does not apply** (`PL…` ≠ `UULF…`), and artwork comes from the playlist
  page's `og:image` rather than a channel avatar.
- YouTube `videos.xml` carries no duration; it's scraped separately
  (`Feeds/YouTubeDurationService`). It also carries no channel-level artwork — the cover is
  scraped from the channel/playlist page (`Feeds/YouTubeChannelResolver`,
  `Feeds/FeedArtworkService`).
- Feeds return only ~15 most recent items (incl. `UULF`); the additive model keeps seen
  items but **cannot** recover a pre-subscription back catalog.

## Parsing

- Parse with **`XDocument`** (LINQ-to-XML) for control over enclosures and the `itunes:` /
  `media:` namespaces (`Feeds/FeedDocumentParser`).
- Extract per episode: GUID/id, title, publish date, enclosure URL + media type, duration,
  description (HTML), artwork, and (YouTube) the `watch?v=` video ID.

## Code style (established conventions)

- Nullable reference types **on**; implicit usings on. Treat warnings seriously.
- **`async`/`await` end-to-end** for all I/O (feed fetch, downloads, DB). No sync-over-async.
- Built-in DI container; **constructor injection** via **primary constructors** (the norm
  across services).
- **File-scoped namespaces**; types are **`internal sealed`** by default.
- Background download queue is **`Channel`-based**; single-instance via a named **`Mutex`**;
  session tracking via a **`CircuitHandler`** (`OnConnectionUpAsync` / `OnConnectionDownAsync`).
- Timestamps are UTC `DateTime` (see Database).

## Out of scope — do not build (v1)

Background/overnight refresh or downloads · cross-device/cloud sync · authenticated-feed
UI / HTTP Basic auth (token-in-URL feeds work incidentally) · podcast **chapters** (still
deferred even with the in-app player) · podcast directory search (add-by-URL + OPML only) ·
arm64 · native toast notifications · mobile.

## Folder layout (the deployed app)

```
VibeCast/
├─ VibeCast.exe          # backend + launcher, single self-contained exe
├─ wwwroot/              # static web assets (sibling to the exe in published builds)
├─ config.json           # app settings (incl. sticky port preference)
├─ run.lock              # live port for the current run (created on bind, removed on exit)
├─ podcasts.db           # SQLite (WAL): feeds, episodes, state, archive
├─ backups/              # once-per-day pre-migration backups, last 10 of each kept
│  ├─ podcasts-yyyyMMdd.db.bak
│  └─ config-yyyyMMdd.json.bak
├─ downloads/<feed-slug>/<episode-file>   # RSS enclosures + per-feed cover art
└─ logs/vibecast-YYYYMMDD.log
```

## Glossary

- **feed-slug** — filesystem-safe per-feed folder name under `downloads/`, assigned once
  from the resolved title and stable thereafter.
- **dedup_key** — the stored composite identity key (`feed_id`-scoped, source-prefixed).
- **Archive** — the section holding played items; hidden from the active list by default.
- **run.lock** — file holding the live listen port for the current process.
