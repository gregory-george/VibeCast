# CLAUDE.md — VibeCast

Portable, single-folder podcast client for Windows. A self-contained .NET `.exe` hosts a
Blazor Server site on Kestrel (loopback); the **browser is the renderer, the exe is the
app**. UI state and logic live server-side. Accepts RSS/Atom feeds and YouTube channels.

This file is the stable, cross-phase context. The full phased build plan and per-phase
detail live in `vibecast-requirements-and-build-plan.md` in this repo — work one
phase at a time and read that doc for the active phase.

---

## Stack (fixed)

- **.NET 10**, **Blazor Server**, **Kestrel**. Target framework **`net10.0-windows`**
  (the WinForms tray needs the Windows desktop TFM).
- **Hosting model:** one process hosts *both* the `IHost` (Kestrel + Blazor) **and** a
  Win32 tray icon. The **`IHost` runs on a background thread**; the **WinForms message
  loop owns the main STA thread** (for the `NotifyIcon`). Shutdown is bridged both ways:
  host-initiated stop tears down the message loop, and tray **Quit** calls
  `StopApplication()`. This structure is laid in Phase 0 even though the tray UI itself
  lands in Phase 3.
- **EF Core** over **SQLite** (`Microsoft.Data.Sqlite` provider) in **WAL** mode. EF Core
  is committed, not optional — the auto-migrate-on-startup requirement depends on it.
  Access via **`IDbContextFactory<T>`** only (never a shared `DbContext`).
- **`System.Text.Json`** for `config.json`.
- **Windows x64 only.** Single self-contained single-file publish. No arm64.

## Hard rules — do not break these

These are the things that silently break the app if violated. They apply in every phase.

**Networking**
- Kestrel binds **`127.0.0.1` only — never `0.0.0.0`.** Keeps it off the LAN and skips the
  firewall prompt.
- `http://localhost` is a secure context, so **no HTTPS / no cert.**
- Default port **`5123`**, **auto-fallback** to the next free port if taken.
- On successful bind, persist the live port **two ways**: write `run.lock` (the truly-live
  port for *this* run) and `config.json` (sticky preference for next run).
- The single-instance relaunch path must read the live port from **`run.lock`** — **never
  assume `5123`.** Treat `run.lock` as potentially **stale** (prior run crashed without
  removing it): validate the port is actually live before using it; if not, fall through
  to a fresh launch.
- The single-instance `Mutex` is a **local, non-abandoned** mutex held for the full
  process lifetime.

**Portability**
- Resolve all paths relative to the exe via **`AppContext.BaseDirectory` /
  `Environment.ProcessPath`.**
- **Never use `Assembly.Location`** — it's empty under single-file publish.
- **Never write to `%AppData%`.** The folder is the entire app.

**Database**
- WAL mode; **WAL checkpoint on shutdown.**
- **Never share a `DbContext`** (not thread-safe). Use **`IDbContextFactory<T>`** or a fresh
  connection per unit of work. The download worker and UI circuits write from different
  threads in the same process.
- Set a **`busy_timeout`** and **retry on `SQLITE_BUSY`.** WAL = many-readers / one-writer,
  not free concurrent writes.
- EF migrations **auto-apply on startup**, but **copy `podcasts.db` → `podcasts.db.bak`
  before calling `Migrate()`.**

**Untrusted input (feeds are hostile)**
- **Always sanitize show-note HTML before rendering** (use `HtmlSanitizer`): strip scripts
  and unsafe markup, keep links clickable.
- **Derive each download's filename and extension from the enclosure's media type — never
  from the URL or any feed-supplied name.** Prevents a feed landing a `.exe`/`.bat` in
  `downloads/` that later gets `ShellExecute`d.

## Data model invariants

- **Additive feed model.** Refresh **adds** new items and **never removes** ones that aged
  out of the feed window. The **DB is the source of truth**; the feed is discovery only.
- **Composite de-dup key**, computed once at ingest and stored in a dedicated **`dedup_key`**
  column, always scoped `feed_id + key`:
  - RSS: `<guid>` → normalized enclosure URL (strip query/tracking params) → `hash(title + pubdate)`.
  - YouTube: the `watch?v=` **video ID**.
- **Episode states:** `played`, `archived`, `new`, `downloaded`. Track `played` and
  `archived` as **distinct flags** even though they currently move together.
- **Resume position:** persist a per-episode `playback_position` so the in-app player
  resumes where the user left off. Save periodically during playback and on pause/stop.
  Becomes irrelevant once the item is marked played (RSS file is deleted at that point).

## Behavioral invariants (span all phases)

- **Mark-as-played:** for RSS, **delete the downloaded file immediately** *and* move the
  record to **Archive**. For YouTube it's a read-flag + archive only (nothing on disk to
  delete). Retention/deletion does **not** apply to YouTube.
- **Archive:** kept in the DB forever, hidden from the active/unplayed list. Show/hide
  toggle, **default off**, applies to RSS and YouTube.
- **Unarchive** sets `played = false`. For RSS it **re-downloads** the enclosure (surface a
  clear error if the URL is dead — no half-played ghost record). YouTube is a view-only move.
- **keep-last-100 per feed** backstop, default on. The cap is on **downloaded files on
  disk per feed**, not DB rows — DB records are kept forever (additive model). Policies
  **stack**. Cleanup runs on refresh and on shutdown.
- **Downloads:** auto-download all new by default (per-feed override). **RSS enclosures
  only** — YouTube always streams and is **never** downloaded.
- **90-day auto-download cutoff:** default don't auto-download items older than 90 days
  (per-feed override). Evaluated at **ingest/refresh** against pubdate (unknown date →
  treat as **today**). An item that ages past the cutoff while undownloaded simply never
  auto-downloads — it is **not** retroactively purged (that's keep-last-100's job).
- **Range-resume:** if a resume request returns `200` instead of `206`, **restart from
  zero**. Be ready to **re-resolve through redirector chains** (podtrac/op3/chartable sign
  short-lived URLs that can't be resumed).
- **In-app player is the default.** RSS audio/video play in an HTML5 `<audio>`/`<video>`
  element fed by a **loopback media endpoint that supports HTTP Range** (seek/scrub).
  YouTube video plays via the **YouTube IFrame Player API** (embed). A persistent
  "now playing" component survives navigation between feeds/episodes.
- **RSS plays a local file — download-first.** Hitting play on an undownloaded RSS
  episode **queues the download and plays the local file when ready** — no direct-from-URL
  streaming. The file must exist on disk to play; this is why mark-as-played deletion is
  tied to playback **completion**, not open.
- **Playback speed:** RSS exposes a full **1.0–3.0 in 0.1 steps** control (HTML5
  `playbackRate` with `preservesPitch = true`). YouTube is capped by its embed to its
  **native 0.25–2.0 / 0.25 menu** — surface YouTube's stepped control honestly; never fake
  0.1 granularity or 3x it can't honor.
- **Resume position:** the player resumes each episode from its stored `playback_position`.
- **External handoff is the fallback, not the default.** An "Open in external app" action
  still uses `Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })`: RSS
  hands the **local file** to the default app; YouTube opens the **plain
  `https://www.youtube.com/watch?v=VIDEO_ID`** URL in the default browser — no params, no
  embed. This is the escape hatch for codecs the browser's HTML5 engine can't play.
- **Tray on by default:** running indicator + **Quit** (clean `StopApplication()`, confirm if
  a download is mid-flight — not a force-kill) + **Reopen UI** (opens the live port).
- **Shutdown is "finish, then exit":** call `IHostApplicationLifetime.StopApplication()` only
  when **zero active circuits AND zero active downloads** hold past a ~15–30 s grace window.
  In-flight downloads finish first.

## YouTube specifics

- Resolve `@handle`/`/c/`/`/user/` URLs to the `UC…` channel ID by scraping the channel page
  (`<meta itemprop="channelId">` or canonical `/channel/<id>`). No API key. Always allow a
  raw `channel_id`/feed-URL paste as fallback if scraping breaks.
- Default feed: `…/feeds/videos.xml?channel_id=UC…` (includes Shorts + long-form).
- "Exclude Shorts" swaps to `…/feeds/videos.xml?playlist_id=UULF…` (`UC` → `UULF`).
- Feeds return only ~15 most recent items (incl. `UULF`); the additive model keeps seen
  items but **cannot** recover a pre-subscription back catalog.

## Parsing

- Parse with **`XDocument`** (LINQ-to-XML) for control over enclosures and the `itunes:` /
  `media:` namespaces.
- Extract per episode: GUID/id, title, publish date, enclosure URL + media type, duration,
  description (HTML), artwork, and (YouTube) the `watch?v=` link.

## Code style (defaults — edit to match house conventions)

- Nullable reference types **on**; treat warnings seriously.
- **`async`/`await` end-to-end** for all I/O (feed fetch, downloads, DB). No sync-over-async.
- Use the built-in DI container; prefer constructor injection.
- File-scoped namespaces; primary constructors where natural.
- Background download queue is **`Channel`-based**; single-instance via a named **`Mutex`**;
  session tracking via a **`CircuitHandler`** (`OnConnectionUpAsync` / `OnConnectionDownAsync`).

## Out of scope — do not build (v1)

Background/overnight refresh or downloads · cross-device/cloud sync · authenticated-feed
UI / HTTP Basic auth (token-in-URL feeds work incidentally) · podcast chapters · podcast
directory search (add-by-URL + OPML only) · arm64 · native toast notifications · mobile.

(Note: an in-app audio/video player **is now in scope** — see the playback invariants
above. Chapters remain deferred even with a player.)

## Folder layout

```
VibeCast/
├─ VibeCast.exe          # backend + launcher, single self-contained exe
├─ config.json           # app settings (incl. sticky port preference)
├─ run.lock              # live port for the current run (created on bind, removed on exit)
├─ podcasts.db           # SQLite (WAL): feeds, episodes, state, archive
├─ podcasts.db.bak       # pre-migration backup
├─ downloads/<feed-slug>/<episode-file>
└─ logs/vibecast-YYYYMMDD.log
```

## Glossary

- **feed-slug** — filesystem-safe per-feed folder name under `downloads/`.
- **dedup_key** — the stored composite identity key (`feed_id`-scoped).
- **Archive** — the section holding played items; hidden from the active list by default.
- **run.lock** — file holding the live listen port for the current process.
