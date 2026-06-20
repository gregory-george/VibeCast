# VibeCast — Requirements & Build Plan

A portable, single-folder podcasting client (gPodder-style) for Windows. The UI is a
modern, responsive design that runs in the default browser, served by a local .NET
backend that the user launches by double-clicking one `.exe`. Accepts standard podcast
RSS feeds and YouTube channels.

---

## Decisions at a glance

| Area | Decision |
|---|---|
| UI model | Browser UI served by a local Blazor Server backend (not a desktop-native shell) |
| Stack | .NET 10 (`net10.0-windows`), Blazor Server, Kestrel on loopback, EF Core + SQLite, JSON config |
| Hosting | One process: `IHost` on a background thread, WinForms tray message loop on the main STA thread; shutdown bridged both ways |
| Target | Windows x64 |
| Portability | Everything lives in one folder, relative to the `.exe` |
| Launch | Double-click `.exe` → starts backend → opens UI in **default browser, normal tab** |
| Lifecycle | Session-bounded — backend runs only while the app is open |
| Shutdown | "Finish, then exit": waits for zero UI sessions **and** zero active downloads |
| Background work | None overnight; refresh/downloads happen only during sessions |
| Tray icon | **On by default** — running indicator + Quit + reopen UI |
| Port conflict | **Auto-fallback** from default `5123` to next free port; persisted; live port in a lockfile |
| Player | **In-app player** is the default — HTML5 `<audio>`/`<video>` for RSS, YouTube IFrame embed for YouTube; persistent "now playing" across navigation |
| RSS playback | In-app HTML5 player off a **local file** (download-first); a loopback media endpoint serves the file with HTTP Range for seeking |
| YouTube playback | In-app **YouTube IFrame** embed of the `watch?v=` video |
| Playback speed | RSS: full **1.0–3.0 in 0.1 steps** (HTML5 `playbackRate`, pitch-preserved). YouTube: its **native 0.25–2.0 / 0.25 menu** (embed limit) |
| Resume position | **Tracked per episode** — player resumes where you left off |
| External handoff | **Kept as a fallback** ("Open in external app") for codecs the browser can't play: RSS local file / plain YouTube `watch?v=` URL via `ShellExecute` |
| Feed refresh | Default **on open only**; 30 min / 60 min / manual selectable in settings; manual button always present |
| Feed model | **Additive** — refresh adds new items and **never removes** ones that aged out of the feed window. The DB is the source of truth. |
| Episode identity | **Composite de-dup key** — RSS: GUID → normalized enclosure URL → `hash(title+pubdate)`; YouTube: `watch?v=` video ID; scoped per feed |
| Downloads | Default **auto-download all new**; Default **don't download anything older than 90 days** (ingest-time gate, not retroactive); per-feed override; RSS enclosures only |
| Retention | **Mark-as-played deletes the RSS file immediately**; **keep-last-100 per feed** backstop (default on) — caps **files on disk**, not DB rows |
| Played state | Marking played moves the item to an **Archive** section (de-clutters the unplayed list) |
| Archive | **Unarchive** returns an item to unplayed; for RSS this **re-downloads** the enclosure; YouTube is a view-only move |
| Archive visibility | Toggle to show archived items; default **off**; applies to RSS and YouTube |
| YouTube Shorts | **Included** by default; per-feed "exclude Shorts" toggle (`UULF` playlist) |
| Show notes | Render **sanitized HTML**; chapters out of scope |
| Authenticated feeds | **Not supported** in v1 (token-in-URL feeds work incidentally; HTTP Basic auth does not) |
| OPML | **Import and export** — subscription migration only, **not** a backup |
| Backup | Copy the folder (`podcasts.db` + `config.json` hold all state) |
| Updates | **Manual `.exe` swap**; EF Core **auto-migrate on startup** (DB backed up first) |
| Sync | Single machine only, no cross-device sync |

---

## 1. Platform & stack

The whole app is a single self-contained .NET executable that hosts a Blazor Server
site on Kestrel. The browser is the renderer; the `.exe` is the application.

- **.NET 10** (LTS). Target framework **`net10.0-windows`** — the WinForms tray
  (`NotifyIcon`) requires the Windows desktop TFM.
- **Blazor Server** — UI state and logic live in the backend process, so the server
  has full OS access (file system, `Process.Start`, etc.). The browser only renders.
- **Hosting/threading model.** A single process hosts both the `IHost` (Kestrel +
  Blazor) and the Win32 tray icon. The **`IHost` runs on a background thread** while the
  **WinForms message loop owns the main STA thread** (required for `NotifyIcon`).
  Shutdown is bridged in both directions: a host-initiated stop tears down the message
  loop, and tray **Quit** calls `StopApplication()`. This `Main` structure is established
  in Phase 0; the tray UI itself lands in Phase 3 (§11).
- **Kestrel bound to `127.0.0.1` only** (never `0.0.0.0`). Loopback skips the Windows
  Firewall prompt and keeps the app off the LAN. Default port `5123`, with auto-fallback
  if taken (see §2). `http://localhost` is a secure context in browsers, so no HTTPS
  cert is needed.
- **SQLite** for feeds, episodes, and state, via **EF Core** (over the
  `Microsoft.Data.Sqlite` provider). Run in **WAL** mode. EF Core is **committed, not
  optional** — the auto-migrate-on-startup flow (§2/§11) depends on EF Migrations.
  - **Concurrency is real, if modest.** The download worker and UI circuits write state
    from different threads in the same process. WAL gives many-readers / one-writer —
    not unlimited concurrent writes. **Do not share a single `DbContext`** (it isn't
    thread-safe); use `IDbContextFactory<T>` (or a fresh connection per unit of work),
    set a `busy_timeout`, and expect the occasional `SQLITE_BUSY` to retry.
- **JSON config** (`System.Text.Json`) for app settings.
- **Target: Windows x64.**, so a single arch artifact.
- **Portable:** all paths resolved relative to the executable using
  `AppContext.BaseDirectory` / `Environment.ProcessPath`. **Do not** use
  `Assembly.Location` (it's empty under single-file publish) and **do not** write to
  `%AppData%`. The folder is the entire app.

---

## 2. Runtime & lifecycle model

### Launch sequence

1. User double-clicks `VibeCast.exe` (or a pinned shortcut).
2. **Single-instance check** via a named `Mutex` — a **local, non-abandoned** mutex held
   for the full process lifetime.
   - **Already running** → the second instance reads the **live port** (from the
     lockfile, see below) and `ShellExecute`s the localhost URL to open a fresh tab
     against the live backend, then exits. No IPC needed.
   - **Not running** → continue. Note `run.lock` can be **stale** (a prior run crashed
     without removing it): validate the port is actually live before trusting it, and
     fall through to a fresh launch if it isn't.
3. **Bind Kestrel on `127.0.0.1`, port `5123`.** If the bind fails (port in use), walk
   to the next free port (`5124`, `5125`, …) until one binds.
4. **Persist the working port** two ways as soon as the bind succeeds:
   - Write it to a small **`run.lock`** (or `port`) file — this is the *truly-live* port
     for **this** run, and what a second instance reads to open the right tab. The race
     this avoids: a second instance launching mid-startup and reading a stale port.
   - Persist it to `config.json` as the **sticky preference** for the next run.
5. **Wait for `ApplicationStarted`** (host actually listening) **before** opening the
   browser. Opening too early loads the tab against a dead socket and forces a manual
   refresh.
6. `Process.Start(new ProcessStartInfo("http://localhost:<port>") { UseShellExecute = true })`
   → UI opens as a normal tab in the **default browser**. (Same `ShellExecute` call
   used everywhere else in the app.)
7. **Tray icon starts (on by default):** a "yes, it's running" indicator with **Quit**
   and **Reopen UI** (which `ShellExecute`s the live localhost URL into a new tab).

### Tray icon (on by default)

- Minimal but always present while the backend lives.
- **Reopen UI** — opens the live port in a new tab. This is the canonical way back in,
  and it sidesteps stale bookmarks (the port can float — see below).
- **Quit** — a **clean** `StopApplication()` honoring finish-then-exit semantics, *not*
  a force-kill. If a download is mid-flight, prompt for confirmation before quitting
  (cancel-and-exit vs. stay).
- The tray also covers the **"download finishing with no open tab"** state: after the
  last tab closes, the tray remains visible while a download wraps up, so there's always
  a visible handle and an escape hatch instead of a silent Task-Manager-only process.

### Shutdown — "finish, then exit"

The backend lives only while the app is in use, and it shuts itself down cleanly.

- A `CircuitHandler` tracks active Blazor circuits (`OnConnectionUpAsync` /
  `OnConnectionDownAsync`). Closing the tab drops the circuit.
- Shutdown fires only when **both** conditions hold past a short grace window
  (~15–30 s, to survive a refresh or reconnect blip):
  1. **Zero active circuits**, and
  2. **Zero active downloads.**
- When both are satisfied, call `IHostApplicationLifetime.StopApplication()`. The
  process exits, the `Mutex` releases, the port frees, the `run.lock` is removed, and
  SQLite flushes (WAL checkpoint on shutdown).
- **In-flight downloads complete first.** If an episode is mid-download when the tab
  closes, the backend stays alive until that download finishes, then shuts down.

### Consequences (intentional)

- **No overnight / background fetching.** Refresh and downloads only run during a
  session. This is the tradeoff for not running an always-on tray daemon. (The tray
  is a presence/control indicator, not a background fetcher.)
- **Circuit churn is expected.** Sleep/wake, page nav, and multiple stale tabs each add
  or drop circuits, and each open tab pins one — so an idle open tab keeps the backend
  alive indefinitely (intended). The grace window + zero-downloads check must survive
  all of this without premature exit or a lingering process.
- **Port can float.** Because of auto-fallback, a saved browser bookmark can go stale if
  the usual port is taken on some launch. The tray's **Reopen UI** always targets the
  live port; bookmarks are best-effort.
- **Browser session restore:** if the browser reopens last session's tabs, it may try
  to restore `localhost:<port>` before the `.exe` is launched and show a dead "can't
  connect" tab until the app starts. Not interceptable (the server isn't up to answer).
- **No tab focusing:** reliably focusing an already-open tab across arbitrary browsers
  isn't feasible, so relaunch opens a new tab rather than jumping to the existing one.
- **First-run discoverability:** portable means no Start-menu entry by default. First
  run can offer to drop a desktop/taskbar shortcut.

---

## 3. Feeds & subscriptions

### Sources

- **Standard podcast RSS/Atom feeds** — added by URL.
- **YouTube channels** — added by channel URL (any of `/@handle`, `/channel/UC…`,
  `/c/custom`, `/user/name`).

### YouTube channel resolution

- The feed endpoint requires the `UC…` **channel ID**; `@handle` URLs do not work
  directly. Resolve by fetching the channel page and parsing the channel ID out of the
  HTML (`<meta itemprop="channelId">` or the canonical `/channel/<id>` link). No API
  key, no quota.
- Store the resolved feed URL. **Default** is `…/feeds/videos.xml?channel_id=UC…`
  (includes Shorts + long-form).
- **Per-feed "exclude Shorts"** toggle swaps to the long-form playlist:
  `…/feeds/videos.xml?playlist_id=UULF…` (the `UC` prefix replaced with `UULF`).
- **Fallback:** allow pasting a raw `channel_id` or feed URL directly, in case page
  scraping breaks on a YouTube markup change.

### Feed model — additive, DB is the source of truth

- **Refresh is purely additive.** It pulls the current feed contents, adds any items
  not already stored, and **never removes** items that have since fallen out of the
  feed's window. The local SQLite DB — not the feed — is the source of truth.
- This is what makes YouTube usable despite the **~15-most-recent cap** on
  `videos.xml` (and on the `UULF` long-form feed): items seen once are kept forever.
  It applies equally to RSS feeds that only carry recent episodes.
- **Known limit:** additive accumulation only captures items the app has *seen at least
  once*. A back catalog that aged out **before** you subscribed is unreachable — there's
  no way to recover items the feed no longer lists and the app never saw.

### Episode identity / de-duplication

A reliable identity key is load-bearing here: because refresh never removes items, a bad
key means duplicates accumulate forever and never age out.

- **Resolution order (RSS):**
  1. `<guid>` if present.
  2. Else the **enclosure URL, normalized** — strip the query string / per-request
     tracking params so the same episode doesn't look new every refresh.
  3. Last resort: `hash(title + pubdate)`.
- **YouTube:** the `watch?v=` **video ID** is the stable key; no fallback needed.
- **Scope:** the key is always combined with the feed (`feed_id + key`), so the same
  GUID appearing in two feeds stays distinct.
- **Storage:** persist a dedicated **`dedup_key`** (a.k.a. `external_id`) column,
  computed once at ingest, separate from whatever the feed literally sent. The rest of
  the app trusts that column.
- **Accepted cost:** if a feed legitimately *changes* a GUID on an item (some do when
  re-cutting audio), the fallback can't always tell, and it may show as a new entry —
  an occasional non-aging dupe. This is the known tradeoff of the additive model,
  matching the YouTube "recent only" tradeoff.

### Parsing

- Parse with `XDocument` (LINQ-to-XML) for full control over enclosures and the
  `itunes:` / `media:` namespaces. `System.ServiceModel.Syndication` is an acceptable
  base if extension elements are read for the namespaced bits.
- Extract per episode: GUID/id, title, publish date, enclosure URL + media type,
  duration, description (HTML), artwork, and (YouTube) the `watch?v=` link.

### Refresh modes

- **Default: refresh all feeds on app open only.**
- Settings expose alternatives: **every 30 min**, **every 60 min**, or **manual only**
  (while the app is open).
- A manual **"Refresh now"** button is always available regardless of mode.
- Add-by-URL is the only discovery path in v1 (no podcast directory search). OPML
  import covers bulk add.

---

## 4. Downloads

- **Default: auto-download all new episodes.** Each feed can override this in its
  settings (e.g., on-click only, or no auto-download).
- **Default: don't auto-download anything older than 90 days.** Each feed can override this in its
  settings (e.g., x number of days, and download all). If a date is unknown for a feed item,
  assume a date of today. The cutoff is a **gate evaluated at ingest/refresh** against
  pubdate; an item that ages past the cutoff while still undownloaded simply **never
  auto-downloads** — it is **not** retroactively purged (that is keep-last-N's job, §6).
- **RSS enclosures only.** YouTube items always stream in the browser and are never
  downloaded.
- **Worker:** a `Channel`-based background download queue. `HttpClient` streams to disk
  with progress reporting and **Range-based resume** for interrupted downloads.
  - **Not every host honors Range.** If a resume request gets a `200` (full body)
    instead of a `206` (partial), restart the download from zero rather than appending.
  - **Redirector chains.** Enclosure URLs often pass through trackers (podtrac, op3,
    chartable) that `302` to a **signed, short-lived** URL. A resume across that chain
    can break — be ready to **re-resolve from the original enclosure URL** rather than
    reusing a stale signed link.
- **Filename safety.** Derive the saved filename and **extension from the enclosure's
  media type**, not from the URL or any feed-supplied name. Feeds are untrusted input;
  this prevents a feed from landing a `.exe` / `.bat` in `downloads/` that later gets
  `ShellExecute`d.
- A visible download queue with progress, plus per-episode cancel.
- Files land under `downloads/<feed-slug>/`.

---

## 5. Playback

The app ships with an **in-app player** as the default playback path, with the
external-app handoff retained as a fallback. There are two playback engines because the
two sources are fundamentally different media.

### RSS audio/video — HTML5 player

- Plays in a browser-side HTML5 `<audio>` / `<video>` element. Because Blazor Server keeps
  the file server-side, a **loopback media endpoint on Kestrel serves the local file with
  HTTP Range support** so the player can seek/scrub. Filename-safety rules (§4) still apply
  to whatever the endpoint serves.
- **Download-first.** Hitting play on an episode that isn't downloaded yet **queues the
  download and starts playback from the local file once it's ready** — there is no
  direct-from-enclosure-URL streaming path. The file must be on disk to play, which is why
  mark-as-played deletion is tied to playback **completion**, not opening (§6).
- **Playback speed: full 1.0–3.0 in 0.1 increments**, via HTML5 `playbackRate`. Modern
  browsers default `preservesPitch = true`, so higher speeds don't pitch-shift the voice.

### YouTube — IFrame embed

- Plays in-app via the **YouTube IFrame Player API** (an embed of
  `https://www.youtube.com/watch?v=VIDEO_ID`). No raw-stream extraction (that would violate
  YouTube's ToS and is brittle).
- **Playback speed is capped by YouTube** to its native menu — **0.25, 0.5, 0.75, 1, 1.25,
  1.5, 1.75, 2** (queryable at runtime via `getAvailablePlaybackRates()`). The UI surfaces
  YouTube's **stepped control honestly**; it does **not** fake 0.1 granularity or 3x, which
  the embed cannot honor. (This is the deliberate asymmetry vs. RSS.)

### Resume position

- The player persists a **per-episode playback position** and resumes from it. Position is
  saved periodically during playback and on pause/stop. Once an item is marked played the
  position is moot (the RSS file is deleted; YouTube just archives).

### "Now playing" UI

- A persistent player component lives in the layout and **survives navigation** between
  feeds and episodes, so changing the list view doesn't tear down playback.

### External-app handoff (fallback)

- An **"Open in external app"** action is retained as an escape hatch for media the
  browser's HTML5 engine can't play. It uses the same `ShellExecute` call as elsewhere:
  `Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })` — RSS hands the
  **local file** to the default Windows app; YouTube opens the **plain
  `https://www.youtube.com/watch?v=VIDEO_ID`** URL in the default browser (no params, no
  embed).

---

## 6. Retention & Archive

The retention and "played" models are intertwined, so they're described together.

### Marking played

- **"Mark as played" is the trigger.** It does two things at once:
  1. **For RSS: delete the downloaded file immediately.**
  2. **Move the record to the Archive** (see below) so it leaves the unplayed list.
- **Optional setting:** auto-mark on **playback completion** (when the in-app player
  reaches the end, delete the RSS file and archive). With an in-app player, "auto-mark on
  *open*" no longer makes sense — opening an episode is how you start playing it, and it
  would delete the file out from under the player — so the completion event is the trigger.
- **YouTube:** nothing is downloaded, so "played" is purely a read-flag plus the move to
  Archive. **Retention/deletion does not apply to YouTube items** — there's no file to
  reclaim. (Stated explicitly so this isn't mistaken for a bug.)
- **Accepted consequence:** because the RSS file is deleted on the button press, there's
  no built-in "undo / give the file back" short of unarchiving (which re-downloads —
  see below). Deliberate, not a gap. A trash folder or grace window could be added later
  if an undo is ever wanted.

### Archive

- Archived items are kept in the DB forever (consistent with the additive feed model)
  but moved out of the active/unplayed list into an **Archive** section.
- **Archive visibility toggle**, default **off**, applies to both RSS and YouTube. The
  unplayed list stays decluttered until the user opts to see archived items.
- **Unarchive** returns an item to the active/unplayed list:
  - **For RSS:** since the file was deleted on archive, unarchive **re-downloads** the
    enclosure. It depends on the original (or re-resolvable) enclosure URL still being
    valid; if the re-download 404s or the URL is dead, **surface a clear error** rather
    than leaving a half-played ghost record.
  - **For YouTube:** unarchive is just a view move — nothing to fetch.
  - Unarchive implies **played → false** (re-downloading while staying archived would be
    pointless).

### keep-last-N backstop

- **Default: keep the last 100 items per feed.** This is the runaway-disk backstop,
  important because auto-download-all is on by default and items accumulate forever.
- **The cap is on downloaded files on disk per feed, not DB rows.** DB records are kept
  forever (additive model, §3); the backstop only reclaims disk by evicting files.
- The backstop evicts old downloaded files even if they were never marked played.
- **Policies stack** and can be set globally or per feed. The default global policy is
  delete-on-played (RSS) plus the keep-last-100 backstop.
- Cleanup runs on refresh and on shutdown.

---

## 7. State, show notes & UI

### Episode / feed state

- Track **played / unplayed**, **archived**, **new**, and **downloaded** states.
  "Archived" is the home for played items; "played" and "archived" move together under
  the current rules but are tracked as distinct flags for clarity and future flexibility.
- Track a per-episode **`playback_position`** (resume point) for the in-app player, saved
  periodically during playback and on pause/stop.

### Show notes

- Render episode descriptions as **sanitized HTML** (links clickable, scripts and
  unsafe markup stripped — feeds are untrusted input; use a library such as
  `HtmlSanitizer`).
- **Chapters are out of scope (deferred).** Even with an in-app player, chapter parsing
  and a chapter UI are deferred for v1; revisit later.

### UI shape

- Browser layout: feed list, episode list (unplayed/active by default), and an episode
  detail pane with the sanitized show notes, download/play controls, and a
  played/unplayed toggle.
- **Persistent in-app player** ("now playing") that survives navigation: transport
  controls, scrubber, speed control (full 1.0–3.0/0.1 slider for RSS; YouTube's stepped
  native menu for YouTube), and an **"Open in external app"** fallback action.
- **Archive section** with a show/hide toggle (default off), covering RSS and YouTube;
  per-item **Unarchive** (re-downloads for RSS).
- A visible **download queue** with progress and per-episode cancel.
- Per-feed settings UI (download override, Shorts toggle for YouTube, retention
  override, refresh inheritance).
- Global settings UI (port, refresh mode, default download mode, retention defaults
  including keep-last-N, concurrent download limit, tray on/off, grace window,
  auto-mark-on-completion, default playback speed).

---

## 8. OPML

- **Import and export.**
- **Subscription migration only — not a backup.** OPML round-trips the *list of feed
  URLs* (and since YouTube channels are stored as feed URLs, it covers podcasts and
  YouTube uniformly). It does **not** carry per-feed settings (download override, Shorts
  toggle, retention override, refresh inheritance), played/archive state, or the
  accumulated episode history.
- Import pulls existing subscriptions in (easy migration off gPodder); export provides
  a subscription backup and a migration path out.
- For a **full** backup, copy the folder — see §9.

---

## 9. Suggested folder layout

```
VibeCast/
├─ VibeCast.exe               # the backend + launcher, single self-contained exe
├─ config.json                # app settings (incl. sticky port preference)
├─ run.lock                   # live port for the current run (created on bind, removed on exit)
├─ podcasts.db                # SQLite (WAL): feeds, episodes, state, archive
├─ backups/                   # timestamped pre-migration backups, last 10 kept (see §11, Phase 0 / startup)
│  └─ podcasts-yyyyMMddHHmmss.db.bak
├─ downloads/                 # downloaded RSS enclosures
│  └─ <feed-slug>/
│     └─ <episode-file>
└─ logs/
   └─ vibecast-YYYYMMDD.log
```

Everything is relative to `VibeCast.exe`. The whole folder is xcopy-portable; a
single-file publish may extract native bits to a temp dir on first run, which is fine —
app data still resolves via `AppContext.BaseDirectory`.

**Backup = copy the folder.** All durable state lives in `podcasts.db` + `config.json`,
so copying the folder (or just those two files) is the complete backup/restore story.
There is no separate backup format to maintain.

---

## 10. Out of scope (v1)

- Cross-device / cloud sync.
- Overnight / background refresh and downloads.
- **Authenticated feeds.** No username/password (HTTP Basic auth) UI. *Token-in-URL*
  private feeds happen to work, since they're just normal URLs that get stored and
  fetched — but Basic-auth-protected feeds are not supported, which also keeps the
  portable folder free of stored cleartext credentials.
- Podcast chapter support.
- Podcast directory search (add-by-URL and OPML import only).
- Recovering a back catalog that aged out of a feed before subscribing (the additive
  model only keeps items it has seen at least once).
- Native toast notifications (see risks — deferred pending a decision).
- Mobile.

---

## 11. Build plan (phased)

**Phase 0 — Scaffolding & runtime shell**
Blazor Server project; Kestrel loopback bind on `5123` **with auto-fallback to next free
port**; **persist live port to `run.lock` + `config.json`**; single-instance `Mutex`
(second instance reads `run.lock` to open the live tab); launch → `ApplicationStarted`
gate → open default browser; graceful `StopApplication()`; SQLite (WAL) +
`IDbContextFactory` setup + `busy_timeout`; **EF Core auto-migrate on startup, preceded
by a `podcasts.db` → `backups/podcasts-yyyyMMddHHmmss.db.bak` copy, keeping the last
10 backups**; config.json load; portable path resolution.

**Phase 1 — Subscriptions & feed parsing**
Add feed by URL; YouTube handle→channelId resolution (+ raw channel_id/feed-URL
fallback); RSS/Atom + `itunes:`/`media:` parsing; **composite de-dup key** (`dedup_key`
column) with the RSS fallback chain and YouTube video ID; **additive persist** of feeds
+ episodes (never remove aged-out items); manual refresh and on-open refresh.

**Phase 2 — Episode/feed UI & state**
Feed list, episode list, detail pane; sanitized HTML show notes; played/unplayed/new/
archived state; "Mark as played"; **Archive section + show/hide toggle (default off)**;
**Unarchive** (view move for YouTube; re-download stub wired up in Phase 3).

**Phase 3 — Downloads (+ minimal tray)**
`Channel`-based background worker; `HttpClient` streaming with progress + Range-resume
(**handle `200` vs `206`; re-resolve through redirector chains**); **media-type-derived
filenames** (no feed-supplied names); auto-download-all default with per-feed override;
download queue UI + cancel; **wire Unarchive re-download for RSS** (with clear error on
dead URL). **Minimal tray pulled in here** so the "download finishing, no tab" state has
a visible handle from the moment downloads exist.

**Phase 4 — In-app player & playback**
Loopback **media endpoint with HTTP Range** serving local files; HTML5 `<audio>`/`<video>`
player for RSS with **download-first** play and **1.0–3.0/0.1 speed**; **YouTube IFrame
embed** with its native stepped speed menu; persistent "now playing" component surviving
navigation; **per-episode resume position** (save + restore); **"Open in external app"
fallback** (`ShellExecute` of local file / plain `watch?v=` URL).

**Phase 5 — Retention & Archive policy**
Mark-as-played → **delete RSS file immediately** + move to Archive; **keep-last-100**
backstop (default on); policy stacking; auto-mark-on-completion option; cleanup on refresh
and on exit. (YouTube: read-flag + archive only, no deletion.)

**Phase 6 — Shutdown & tray polish**
`CircuitHandler` session tracking; finish-then-exit waiting on zero circuits + zero
active downloads; grace window; **full tray** (Reopen UI, clean Quit with mid-download
confirm). Harden against circuit churn (sleep/wake, stale tabs).

**Phase 7 — OPML & settings**
OPML import/export (subscription-only); settings UI exposing refresh modes,
download/retention defaults (incl. keep-last-N and auto-mark-on-completion), Shorts
default, default playback speed, port, concurrency, tray, grace window.

**Phase 8 — Polish & packaging**
Error handling and feed-health display; logging; first-run shortcut offer;
self-contained single-file publish for win-x64. Document the **manual `.exe` swap**
update flow (app must be closed first — Windows locks a running executable).

---

## 12. Risks & things to watch

- **Runaway disk vs. additive model** — items accumulate forever and auto-download is
  on, so the **keep-last-100 backstop** is the primary guard. Watch that cleanup
  actually fires on refresh/shutdown and that the per-feed cap is honored.
- **De-dup key drift** — a feed that changes a GUID can produce a non-aging duplicate;
  accepted cost of additive-never-remove. The normalized-URL fallback reduces, but
  doesn't eliminate, this.
- **Unarchive re-download** — depends on the enclosure URL still resolving; redirector
  links usually keep working, but a dead/moved enclosure must surface a clear error,
  not a ghost record.
- **DB concurrency** — worker + UI write from different threads; use `IDbContextFactory`
  (never a shared `DbContext`), `busy_timeout`, and `SQLITE_BUSY` retry. WAL is
  many-readers/one-writer, not free concurrent writes.
- **Download resume** — `200` vs `206` handling; signed/short-lived URLs behind tracking
  redirectors that can't be resumed and must be re-resolved.
- **Filename safety** — always derive name/extension from media type; never trust
  feed-supplied names (avoid landing executables in `downloads/`).
- **Port float / stale bookmarks** — auto-fallback can move the URL; the second-instance
  path and tray Reopen UI must read the live port from `run.lock`, not assume `5123`.
- **Migration on a years-deep DB** — auto-migrate runs against a long-lived, accumulating
  library; the **timestamped `backups/podcasts-yyyyMMddHHmmss.db.bak` copy before
  `Migrate()`** (last 10 kept) is the safety net.
- **Update swap locking** — the `.exe` can't be replaced while running; finish-then-exit
  + tray Quit make closing clean, but document it.
- **YouTube speed ceiling** — the IFrame embed only honors YouTube's native menu
  (0.25–2.0 / 0.25 steps); no 0.1 granularity and no 3x. The UI must reflect this stepped
  set honestly rather than implying RSS-style control. Confirm the allowed set at runtime
  via `getAvailablePlaybackRates()`.
- **Media endpoint** — the loopback file-serving endpoint must support HTTP **Range**
  (for seek/scrub) and respect the same filename/extension-safety rules as downloads. It
  is loopback-only, consistent with the `127.0.0.1` bind.
- **Codec coverage** — the browser's HTML5 engine covers mp3/m4a-aac/mp4-h264/webm (i.e.
  ~all podcasts), but odd codecs may not play; the **"Open in external app" fallback** is
  the escape hatch.
- **Resume vs. delete** — `playback_position` is only meaningful while the file exists;
  mark-as-played (manual or on-completion) deletes the RSS file and retires the position.
  Ensure auto-mark fires on **completion**, never on open, or it deletes mid-playback.
- **YouTube feed limits** — ~15-most-recent cap (incl. `UULF`); the additive model keeps
  seen items but can't recover a pre-subscription back catalog. `UULF` playlist feeds can
  occasionally be flaky.
- **Handle resolution brittleness** — page scraping depends on YouTube's HTML; the raw
  `channel_id` / feed-URL fallback is the safety valve.
- **Single-file path handling** — use `AppContext.BaseDirectory` /
  `Environment.ProcessPath`, never `Assembly.Location`.
- **Circuit churn** — sleep/wake, nav, and stale tabs add/drop circuits and each open tab
  pins one; the grace window must avoid both premature exit and a lingering process.
- **Browser session-restore dead tab** and **no tab focusing** — minor, documented UX
  warts inherent to the normal-tab launch choice.
- **Native notifications** — the local-backend model has no package identity, so OS
  toast notifications would need a separate mechanism. Deferred; revisit if wanted.
- **Feed HTML safety** — always sanitize before rendering.
- **SQLite** — WAL mode; checkpoint on shutdown.
