# CLAUDE.md — VibeCast

Portable, single-folder podcast client for Windows. A self-contained .NET `.exe` hosts a
Blazor Server site on Kestrel (loopback); the **browser is the renderer, the exe is the
app**. UI state and logic live server-side. Subscribes to RSS/Atom feeds and to YouTube
channels and playlists.

VibeCast is **shipped** (all features built). This file is the durable context for
*maintaining* it: the invariants that silently break the app if violated, and the
behavioral rules that aren't obvious from any single file. It is the source of truth over
older docs — `vibecast-requirements-and-build-plan.md` is the original design rationale,
kept for history, not an active spec.

---

## Stack (fixed)

- **.NET 10**, **Blazor Server**, **Kestrel**. Target framework **`net10.0-windows`** (the
  WinForms tray needs the Windows desktop TFM).
- **Hosting model:** one process hosts *both* the `IHost` (Kestrel + Blazor) **and** a Win32
  tray icon. The **`IHost` runs on a background thread** (`AppHost/HostRunner`); the
  **WinForms message loop owns the main STA thread** (`Program.Main`, for the `NotifyIcon`).
  Shutdown is bridged both ways: the host's `WaitForShutdownAsync` completing (success *or*
  failure) posts `ExitThread()` back to the UI thread, and tray **Quit** calls
  `StopApplication()`.
- **EF Core** over **SQLite** (`Microsoft.EntityFrameworkCore.Sqlite`) in **WAL** mode. EF
  Core is committed, not optional — auto-migrate-on-startup depends on it. Access via the
  **pooled `IDbContextFactory<AppDbContext>`** (`AddPooledDbContextFactory`) only — **never a
  shared `DbContext`.**
- **`System.Text.Json`** for `config.json` (`AppHost/AppConfig`).
- **`HtmlSanitizer`** for show-note sanitizing; **`SQLitePCLRaw.lib.e_sqlite3`** for the
  native SQLite engine.
- **Windows x64 only.** Single self-contained single-file publish (with a sibling
  `wwwroot/`). No arm64.

## Build & run

- Source lives under `src/VibeCast/`. Release publish:
  `dotnet publish src/VibeCast/VibeCast.csproj -c Release -p:PublishProfile=win-x64-singlefile`
  → a single `VibeCast.exe` plus a sibling `wwwroot/` (static web assets are **not** embedded
  into the single file).
- **`publish.ps1`** (repo root) is the publish-*and-deploy* script: it runs the same
  single-file publish (properties passed explicitly, without the profile's
  `EnableCompressionInSingleFile`) and robocopy-**overlays** the output onto
  `\\Highlands\Applications\VibeCast`. That destination is the **live app folder** —
  `podcasts.db`, `config.json`, `downloads/`, `logs/` all live there at runtime — so only
  `wwwroot/` is mirrored; **never mirror/delete the destination root.**
- Migrations are authored with the pinned local `dotnet-ef` tool (`.config/dotnet-tools.json`):
  `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/VibeCast`
  (`Data/AppDbContextFactory` is the design-time factory the CLI uses).
- **Live-testing a build:** the single-instance mutex is shared with any installed copy running
  in the same login session — a second `VibeCast.exe` silently defers to the running instance
  (opens its tab and exits). Quit the real app before launching a test build.
- Console window is suppressed at runtime via `FreeConsole()` P/Invoke, **not**
  `OutputType=WinExe` — setting `WinExe` breaks the SDK's static-web-asset / Razor JS pipeline
  (`blazor.web.js` 404s, no interactivity). Don't "fix" this back to `WinExe`.
- Tests: `dotnet test` (`tests/VibeCast.Tests`, **xUnit v3**, same `net10.0-windows` TFM,
  same pinned `RuntimeIdentifier=win-x64` so a RID-less run can still load the app assembly).
  The app grants it `InternalsVisibleTo` — keep types `internal sealed`, never widen
  visibility for testability. Downloader integration tests run EF against SQLite in-memory;
  `FeedRefreshService` takes an injectable retry-delay `Func` so retry tests don't sleep real
  seconds — keep delays injectable in new retrying code. (`xUnit1051`, the
  ambient-cancellation-token advisory, is suppressed project-wide by design.)

## Source layout (namespaces mirror folders)

- **`Program.cs`** (project root, namespace `VibeCast`) — the STA/tray **entry point**
  (`Main`): the sole file outside a subfolder. Everything below is foldered.
- **`AppHost/`** — process bootstrap: `HostRunner` (background host thread, DI wiring,
  launch sequence), `PortBinder`, `RunLock`, `SingleInstance`, `AppConfig`, `AppPaths`,
  `TrayApplicationContext`, `DesktopShortcut`.
- **`Data/`** — EF Core: `AppDbContext`, entities (`Feed`, `Episode`, `FeedType`),
  `DatabaseLifecycle` (backup + migrate + checkpoint), `AppDbContextFactory` (design-time,
  for the `dotnet ef` CLI only), `Migrations/`.
- **`Feeds/`** — fetch/parse/subscribe/refresh, dedup, slugs, artwork, and the YouTube
  resolvers/scrapers.
- **`Downloads/`** — the `Channel`-based queue, worker, `EpisodeDownloader`, file naming,
  progress/cancellation tracking, the `AutoDownloadGate`.
- **`Episodes/`**, **`Retention/`**, **`Playback/`**, **`Opml/`**, **`Shutdown/`**,
  **`Logging/`** — one concern each, named for it.
- **`Components/`** — the Blazor Server UI (`.razor`). This is where the front end lives:
  - `Components/Pages/` — routable pages: **`Home`** (the library: sidebar feed list +
    episode list), `Feeds`, `Downloads`, `Settings`, plus `Error`/`NotFound`.
  - `Components/Layout/` — `MainLayout`, **`NowPlaying`** (the persistent now-playing player,
    outside the router so it survives navigation), and **`ReconnectModal`** (custom-styled
    Blazor Server reconnect/resume dialog).
  - `App.razor` sets `<html data-theme>` from `AppConfig.Theme` (see UI below); `Routes.razor`
    wires the router and default layout.
- **`wwwroot/js/`** — the JS interop modules: `audioPlayer.js` (HTML5), `youtubePlayer.js`
  (IFrame API), `nowPlayingUi.js` (arrow-key skip + Esc listeners, video resize drag), and
  `theme.js` (applies a Settings theme change live). `ReconnectModal` keeps its script
  collocated (`ReconnectModal.razor.js`).

## Hard rules — do not break these

These are the things that silently break the app if violated.

**Networking**
- Kestrel binds **`127.0.0.1` only — never `0.0.0.0`.** Keeps it off the LAN and skips the
  firewall prompt. (The browser is opened against `http://localhost:<port>` — `localhost` is
  the secure-context host.)
- `http://localhost` is a secure context, so **no HTTPS / no cert** (no HTTPS redirection or
  HSTS anywhere).
- Default port **`5123`**, **auto-fallback** to the next free port (up to 50 attempts) if
  taken.
- On successful bind, persist the live port **two ways**: write `run.lock` (the truly-live
  port for *this* run) and `config.json` (sticky preference for next run).
- The single-instance relaunch path reads the live port from **`run.lock`** — **never assume
  `5123`.** Treat `run.lock` as potentially **stale** (prior run crashed without removing it):
  validate the port is actually live (TCP connect) before opening a browser against it. If it
  isn't live, the second process just exits — the running instance opens its own tab once
  bound, and a stale file from a crashed run is overwritten on the next fresh launch.
- The single-instance `Mutex` is a **local, non-abandoned** mutex held for the full process
  lifetime.

**Portability**
- Resolve all paths relative to the exe via **`AppContext.BaseDirectory`** (see
  `AppHost/AppPaths`) **/ `Environment.ProcessPath`.**
- **Never use `Assembly.Location`** — it's empty under single-file publish.
- **Never write to `%AppData%`.** The folder is the entire app.
- **All `config.json` access goes through `AppHost/AppConfig.Load()/Save()`** — a static lock
  serializes readers/writers (circuits, the player resize handle, and host startup all save
  concurrently) and `Save` is an atomic temp-file + move, so a crash can't truncate the file.
  A corrupt/unreadable config falls back to defaults rather than failing startup. `AppConfig`
  is also the catalog of every user setting and its default.

**Database**
- WAL mode; **WAL checkpoint on shutdown** (`PRAGMA wal_checkpoint(TRUNCATE)`).
- **Never share a `DbContext`** (not thread-safe). Use the pooled
  `IDbContextFactory<AppDbContext>` (fresh context per unit of work). The download worker and
  UI circuits write from different threads in the same process.
- The connection string sets **`Default Timeout=30`** so a transient `SQLITE_BUSY` waits
  rather than throwing immediately. WAL = many-readers / one-writer, not free concurrent
  writes.
- **All timestamps are `DateTime` in UTC** — the `Utc` suffix on each timestamp property is
  load-bearing, not decorative. A value converter re-stamps `Kind=Utc` on read because SQLite
  drops `Kind` on round-trip. **Don't switch to `DateTimeOffset`:** the SQLite provider can't
  translate `ORDER BY` on it.
- EF migrations **auto-apply on startup**. Immediately before, a **once-per-calendar-day**
  backup copies `podcasts.db → backups/podcasts-yyyyMMdd.db.bak` **and** `config.json →
  backups/config-yyyyMMdd.json.bak` (skipped if today's already exists), keeping only the
  **last 10 of each**.

**Untrusted input (feeds are hostile)**
- **Always sanitize show-note HTML before rendering** (`Episodes/ShowNotesSanitizer`, built on
  `HtmlSanitizer`): strip scripts and unsafe markup, keep links clickable (rewritten to open
  in a new tab with `rel="noopener noreferrer"`). Store the **raw** HTML; sanitize at render
  time, never store sanitized.
- **Derive each download's extension from the enclosure's media type — never from the URL or
  any feed-supplied name** (`Downloads/DownloadFileNaming`; the saved name is
  `yyyy-MM-dd-<sanitized-title-slug>-<episode id><ext>`). Prevents a feed landing
  a `.exe`/`.bat` in `downloads/` that later gets `ShellExecute`d. Unknown media types get
  `.bin` (safe, never executable). The media endpoint serves each file with its stored
  `EnclosureMediaType` (falling back to `application/octet-stream` when blank).
- **Feed fetches stream through a hard 20 MB cap** (`Feeds/FeedFetcher`) — reject, never buffer
  unbounded, whether or not the server declares a Content-Length. Parsing loads `XDocument`
  from the raw bytes (the XML declaration's own encoding is honored) with DTD processing
  prohibited — no XXE / billion-laughs.

## Data model invariants

- **Additive feed model.** Refresh **adds** new items and **never removes** ones that aged out
  of the feed window. The **DB is the source of truth**; the feed is discovery only. (Deleting
  a feed is the one deliberate, user-confirmed exception — it wipes the rows and the
  `downloads/<slug>` folder.)
- **Composite de-dup key**, computed once at ingest (`Feeds/DedupKeyComputer`) and stored in a
  dedicated **`DedupKey`** column, enforced by a unique index scoped `(FeedId, DedupKey)`.
  Keys are source-prefixed for debuggability:
  - RSS: `guid:<guid>` → `url:<enclosure URL, query+fragment stripped>` → `hash:<sha256(title|pubdate)>`.
  - YouTube: `yt:<watch?v= video ID>`.
- **Persisted episode flags:** `IsPlayed`, `IsArchived`, `IsDownloaded`. `played` and
  `archived` are tracked as **distinct flags** even though they currently move together. There
  is **no stored `new` flag** — "new" is the derived default (none of the above).
- **Resume position:** `PlaybackPositionSeconds` per episode so the in-app player resumes where
  the user left off. Saved by a ~5 s periodic timer while an episode is open — there is **no**
  explicit flush on pause/stop/close, so the last few seconds can be lost. Becomes irrelevant
  once the item is played (RSS file is deleted at that point).

## Behavioral invariants

### Launch & refresh

- **Launch sequence** (`HostRunner`): refresh-on-open (`RefreshOnOpen`, default **on**)
  refreshes all feeds fire-and-forget, and a **startup download sweep** re-queues RSS
  enclosures that should be on disk but aren't — downloads interrupted by a prior exit resume
  from their `.partial` file via Range. The sweep is gated by the same `AutoDownloadGate` as
  ingest, so items past the age cutoff stay skipped. Logs older than 30 days are pruned
  (`Logging/LogRetention`, keyed off the date in the filename). First run only (`config.json`
  doesn't exist yet): offer to create a desktop shortcut, once ever.
- **Periodic auto-refresh while running:** `Feeds/PeriodicFeedRefreshService` (a hosted
  `BackgroundService`) refreshes all feeds every `AutoRefreshIntervalMinutes` (default **60**,
  clamped **30–180**). The interval is re-read from `AppConfig` before each wait, so a Settings
  change applies on the next cycle without a restart; the first timed refresh fires one full
  interval after startup (launch-time refresh stays `RefreshOnOpen`'s job).
- **Refresh is concurrent but bounded** (4 feeds at a time — WAL is single-writer; the 30 s
  busy timeout absorbs the write contention). Each fetch retries **transient** failures only
  (timeout / 408 / 429 / 5xx / transport; 3 attempts, exponential backoff) — other 4xx and
  parse errors don't retry. Failures are recorded per feed (`LastRefreshError`, cleared on
  success) and surfaced on the Feeds page; cancellation from shutdown/navigation is never
  recorded as a feed failure.
- **Initial subscribe (RSS):** only the newest `InitialActiveEpisodeCount` (default **15**)
  episodes stay active; older back-catalog items are **pre-archived** (`played + archived`) at
  ingest so auto-download-all doesn't flood disk on a deep feed on day one. YouTube is **not**
  pre-archived (its feed already returns only ~15).

### Downloads

- **Auto-download all new by default** (per-feed `AutoDownloadEnabled` override). **RSS
  enclosures only** — YouTube always streams and is **never** downloaded.
  `ConcurrentDownloadLimit` (default **1**) sets how many workers drain the `Channel`-based
  queue; read once at startup.
- **90-day auto-download cutoff** (`DefaultAutoDownloadMaxAgeDays` default **90**, per-feed
  `AutoDownloadMaxAgeDays`; `null` = no limit). Evaluated at **ingest/refresh** against pubdate
  (unknown date → treat as **today**). An item that ages past the cutoff while undownloaded
  simply never auto-downloads — it is **not** retroactively purged (that's keep-last-N's job).
  Note the default is **stamped onto the feed row at subscribe time** — changing the global
  setting later only affects newly-added feeds (unlike keep-last-N, where a feed without an
  override follows the live global value).
- **Range-resume:** the downloader always re-requests the **original enclosure URL** (never a
  cached redirect target), so a resume naturally **re-resolves through redirector chains**
  (podtrac/op3/chartable sign short-lived URLs). If a resume request returns `200` instead of
  `206`, **restart from zero**. Downloads deliberately have **no overall HTTP timeout** (a large
  enclosure legitimately outlives any fixed limit); instead a 30 s connect timeout, a **100 s
  per-read stall window**, and a truncation check (fewer bytes than Content-Length ⇒ fail, keep
  the `.partial` for the next resume) guard the stream.

### Retention & the lifecycle of a played item

- **Mark-as-played:** for RSS, **delete the downloaded file immediately** *and* move the record
  to **Archive**. If the file is **locked** (still open in the player), the flags still move now
  and the deletion is **deferred** — the retention sweep retries it on the next refresh/shutdown.
  For YouTube it's a read-flag + archive only (nothing on disk). Retention/deletion does **not**
  apply to YouTube.
- **Archive:** kept in the DB forever, hidden from the active/unplayed list. Show/hide toggle,
  **default off**, applies to RSS and YouTube.
- **Unarchive** sets `played = false`. For RSS it **re-downloads the enclosure only if the file
  is actually gone** (avoids a redundant re-fetch of one still on disk); surface a clear error
  if the URL is dead — no half-played ghost record. YouTube is a view-only move.
- **keep-last-N per feed** backstop (`DefaultKeepLastCount` default **100**, per-feed
  `KeepLastCount` override; `0` disables). Caps **downloaded files on disk per feed**, not DB
  rows — DB records are kept forever (additive model). RSS only. Policies **stack**. Cleanup
  runs on refresh (per feed) and on shutdown (all feeds), and the same sweep retries any
  deferred locked-file deletions left by mark-as-played.

### Playback & player UI

- **In-app player is the default.** RSS audio/video play in an HTML5 `<audio>`/`<video>` element
  fed by the loopback media endpoint `/media/episodes/{id}`, which serves the local file with
  ASP.NET Core `enableRangeProcessing` (seek/scrub). YouTube plays via the **YouTube IFrame
  Player API** (embed). The `NowPlaying` component is a persistent "now playing" surface that
  survives navigation between feeds/episodes.
- **RSS plays a local file — download-first.** Hitting play on an undownloaded RSS episode
  **queues the download and plays the local file when ready** — no direct-from-URL streaming. If
  that download fails or is canceled, the pending-play state clears and the error is surfaced —
  never a "plays when ready" banner waiting forever. The file must exist on disk to play; that's
  why auto-mark-on-completion (`AutoMarkOnCompletion`, default **off**) fires at playback
  **completion**, never on open — mark-as-played deletes the file.
- **Playback speed:** RSS exposes a full **1.0–3.0 in 0.1 steps** control (HTML5 `playbackRate`
  with `preservesPitch = true`). YouTube is capped to its **native 0.25–2.0 / 0.25 menu** —
  surface YouTube's stepped control honestly; never fake 0.1 granularity or 3x it can't honor.
  `DefaultPlaybackSpeed` seeds each newly-opened episode and snaps to the nearest step the active
  player actually offers. (Keyboard skip is `SkipSeconds`, default 10; YouTube captions default
  on, RSS has none.)
- **External handoff is the fallback, not the default.** An "Open in external app" action uses
  `Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })`: RSS hands the **local
  file** to the default app; YouTube opens the **plain `https://www.youtube.com/watch?v=VIDEO_ID`**
  URL in the default browser — no params, no embed. Escape hatch for codecs the browser's HTML5
  engine can't play.

### General UI

- **Theme** (`AppConfig.Theme`: `"Light"` / `"Dark"` / `"System"`, default `"System"`).
  `App.razor` stamps `data-theme` on `<html>`; `"System"` leaves it unset so the stylesheet
  follows the OS via `prefers-color-scheme`. A Settings change re-stamps it live via
  `js/theme.js` (no reload). Other library-view preferences also live in
  `AppConfig`: `HideEmptyFeeds` (hide feeds with zero active episodes, default off),
  `EpisodeSortDescending` (newest-first, default on), and `VideoPlayerHeightPx` (sticky height
  of the small video player, set by dragging its resize handle — that JS resize handle is one of
  the concurrent `config.json` writers the `AppConfig` lock guards).
- **Reconnect:** `ReconnectModal` is a custom-styled replacement for Blazor Server's default
  reconnect/resume overlay (rejoin / paused-session / retry states).
- **OPML:** export is a loopback endpoint (`GET /opml/export`); import runs in-browser
  (Settings → file upload) through the normal add-feed path, so duplicate detection and per-feed
  defaults apply.
- Feed cover art is served from its own loopback endpoint, `/media/feeds/{id}/artwork` (sibling
  to `/media/episodes/{id}`).

### Shutdown & tray

- **Tray on by default** (`TrayEnabled`): running indicator + **Quit** (clean
  `StopApplication()`; confirm if a download is mid-flight, then cancel it — not a force-kill; a
  ~10 s watchdog hard-exits only if graceful shutdown hangs) + **Reopen UI** (opens the live
  port).
- **Shutdown is "finish, then exit":** call `IHostApplicationLifetime.StopApplication()` only
  when **zero active circuits AND zero active downloads** hold past a grace window
  (`GraceWindowSeconds`, **default 10**, configurable). In-flight downloads finish first.
  Evaluation is **event-driven only** (circuit and download-progress changes), so it normally
  can't run before the first circuit connects — but that's emergent, not an explicit guard: a
  startup-sweep download emitting progress before the browser's circuit connects can start the
  countdown early.

## YouTube specifics

- Resolve `@handle`/`/c/`/`/user/` URLs to the `UC…` channel ID by scraping the channel page
  (`<meta itemprop="channelId">` or canonical `/channel/<id>`). No API key. A raw `channel_id`
  or feed-URL paste is always accepted as a fallback if scraping breaks.
- **Channel feed (default):** `…/feeds/videos.xml?channel_id=UC…` (includes Shorts + long-form).
- **"Exclude Shorts"** swaps to `…/feeds/videos.xml?playlist_id=UULF…` (`UC` → `UULF`).
- **User playlists:** a `youtube.com/playlist?list=PL…` URL (or a raw `playlist_id=PL…` feed URL)
  subscribes to that playlist's `videos.xml`. A distinct kind from channel feeds:
  **Exclude-Shorts does not apply** (`PL…` ≠ `UULF…`), and artwork comes from the playlist page's
  `og:image` rather than a channel avatar.
- YouTube `videos.xml` carries no duration; it's scraped separately
  (`Feeds/YouTubeDurationService`). It also carries no channel-level artwork — the cover is
  scraped from the channel/playlist page's `og:image` (`Feeds/YouTubeChannelResolver`,
  `Feeds/FeedArtworkService`).
- **Scheduled premieres/live streams** appear in `videos.xml` before they air but their watch
  page reports `lengthSeconds:"0"`. That zero is treated as **unknown, not a real zero-length
  episode** — the duration stays `null` and the page's `scheduledStartTime` is stored in
  **`Episode.ScheduledStartUtc`** (drives the "Upcoming" badge; a non-null value with null
  duration = not yet aired). The refresh path re-scrapes recent YouTube episodes still missing a
  duration (`BackfillFeedAsync`, bounded to a 45-day pubdate window), so the real length lands
  once the video airs and the scheduled-start clears. Don't skip these items at ingest — the
  ~15-item feed window means a premiere can age out **before** it airs and the additive model
  can't recover it.
- Feeds return only ~15 most recent items (incl. `UULF`); the additive model keeps seen items
  but **cannot** recover a pre-subscription back catalog.

## Parsing

- Parse with **`XDocument`** (LINQ-to-XML) for control over enclosures and the `itunes:` /
  `media:` namespaces (`Feeds/FeedDocumentParser`).
- Extract per episode: GUID/id, title, publish date, enclosure URL + media type, duration,
  description (HTML), artwork, and (YouTube) the `watch?v=` video ID.
- All outbound HTTP (feed fetches, page scrapes, downloads) sends a desktop-Chrome **browser
  User-Agent** (`HostRunner`) — stock `HttpClient` UAs get blocked by some hosts, YouTube scrapes
  especially.

## Code style (established conventions)

- Nullable reference types **on**; implicit usings on. Treat warnings seriously.
- **`async`/`await` end-to-end** for all I/O (feed fetch, downloads, DB). No sync-over-async.
- Built-in DI container; **constructor injection** via **primary constructors** (the norm across
  services).
- **File-scoped namespaces**; types are **`internal sealed`** by default.
- Background download queue is **`Channel`-based**; single-instance via a named **`Mutex`**;
  session tracking via a **`CircuitHandler`** (`OnConnectionUpAsync` / `OnConnectionDownAsync`).
- Timestamps are UTC `DateTime` (see Database).

## Out of scope — do not build (v1)

Scheduled/background **downloads** (the launch-time download sweep plus downloads triggered by
refresh are the deliberate extent; refresh itself now also runs on the in-process periodic timer
above — but no OS-scheduled work while the app is closed) · cross-device/cloud sync · authenticated-feed UI / HTTP
Basic auth (token-in-URL feeds work incidentally) · podcast **chapters** (still deferred even with
the in-app player) · podcast directory search (add-by-URL + OPML only) · arm64 · native toast
notifications · mobile.

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
└─ logs/vibecast-YYYYMMDD.log             # daily log, pruned after 30 days
```

## Glossary

- **feed-slug** — filesystem-safe per-feed folder name under `downloads/`, assigned once from
  the resolved title and stable thereafter.
- **DedupKey** — the stored composite identity key (feed-scoped, source-prefixed).
- **Archive** — the section holding played items; hidden from the active list by default.
- **run.lock** — file holding the live listen port for the current process.
