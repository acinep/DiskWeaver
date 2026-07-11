# Cockpit plugin (Phase 4)

Status: `DiskWeaver.Cockpit` is a thin HTML/JS client over the daemon's
HTTP API (see [daemon-api.md](daemon-api.md)) — no logic of its own,
same principle as the CLI. **Validated against a real Cockpit instance**
(WSL2 already had `cockpit`/`cockpit-ws`/`cockpit-bridge` installed and
running): symlinked this directory into
`~/.local/share/cockpit/diskweaver`, confirmed `cockpit-bridge
--packages` lists it correctly, started the daemon on
`/run/diskweaverd.sock`, and confirmed in a real browser session at
`https://localhost:9090` that the **DiskWeaver** entry appears in the
nav and the page renders live disk inventory fetched over
`cockpit.http()`/the Unix socket. Went on to click an actual **Execute
build** through the browser against three real loop devices — it
genuinely partitioned them, built a RAID5 array, and created the
VG/LV, confirmed against real `mdadm`/`vgs`/`lvs` output afterward. What
remains open: the proper (non-`chmod 666`) socket permission/superuser
model — see below.

This work surfaced eight real bugs that only showed up by actually
clicking through the UI rather than just unit-testing the backend:

1. **`MdadmDetailParser` assumed the wrong `mdadm --detail --export`
   format.** Real output keys each member device by its own name
   (`MD_DEVICE_dev_loop0p1_DEV`/`_ROLE`), not a plain numeric index
   (`MD_DEVICE_dev0_DEV`) — the parser silently matched nothing and
   `GET /pools` 500'd. Fixed to key off `ROLE` for ordering; regression
   test added from the real captured output.
2. **The UI silently did nothing if `Execute`/`View script` was
   clicked without a plan having actually been created** (e.g. after a
   page reload, or if plan creation itself failed) — no error, just an
   inert button. Fixed to show "Create a plan first."
3. **Teardown was wired to a regenerated plan, not the pool you'd
   actually see in `GET /pools`** — see below, now fixed.
4. **`MdadmLvmPoolStateSource` queried `blockdev --getsize64` on the
   array device, not a member partition** — for RAID5/6 this returns
   the array's usable *(n-r)×segment* capacity, not the per-disk
   segment size `ExistingTier.SegmentSizeBytes` is supposed to hold.
   Silently inflated the reported segment size, which made
   `BuildIncremental` refuse a real expansion as "orphaned tier" the
   first time expansion was tried against a real pool. Fixed to query
   a member partition instead; caught with a real regression test (see
   `MdadmLvmPoolStateSourceTests` — a new `ICommandRunner` seam, mirroring
   `IStepRunner`, makes the exact command+arguments assertable without a
   real subprocess).
5. **The expansion preview showed the full desired end-state capacity
   as if a grow-candidate tier had already been manually reshaped** —
   at the time, `BuildIncremental` didn't automate growing an existing
   tier's member count at all, so clicking Execute only ever delivered
   new-tier capacity plus unchanged tiers' *current* capacity. Showing
   the desired-state number as "what you get" was actively misleading.
   Fixed first by computing the real achieved number
   (`CommandPlanner.AchievedCapacityBytes`, sharing tier classification
   with `BuildIncremental` via `ClassifyIncremental` so the two can't
   drift apart) and having the UI show it as the headline; then, once
   grows were automated for real (see execution.md — both same-level
   device-count grows and RAID-level migrations are now automated),
   achieved and desired capacity converge back to the same number for
   any plan that's fully automatable, and the UI's "reaching the full
   X GB requires manually growing..." note correctly stops appearing.
6. **`app.js` fetched `GET /inventory` exactly once, on page load, and
   never again.** The "Refresh" button only re-fetched `/pools`. If
   disks were attached/created after the page loaded (very much the
   normal case when driving loop-device tests from outside the
   browser), the disk checklist silently kept showing stale, missing,
   or wrong disks — so checking every visible box would submit fewer
   disks than intended, without any error, since selecting fewer disks
   is a perfectly valid request on its own. This combined with
   content-addressed plan/execution ids to produce a genuinely
   confusing failure mode: submitting the same (stale, smaller) disk
   set repeatedly hashes to the same id every time, so once that id had
   ever succeeded, every later attempt looked like an instant "success"
   without doing anything, no matter how many boxes were actually
   checked in the browser. Fixed with a dedicated **Refresh disks**
   button (`loadInventory()`), separate from the pools refresh.
7. **`BuildTeardownFromExisting` guessed partition numbers by ordering
   tiers by segment size** ("smallest segment = partition 1 on every
   disk"), which only holds for freshly-built pools. Once a tier could
   be grown into *after* another tier already existed on the same disk
   (once grows were automated, see below), a disk's partition for one
   tier could legitimately be `p2`, not `p1` — a real teardown hit
   exactly this, guessing `/dev/loop4p1` for a RAID5 tier when that
   partition actually belonged to a still-active mirror tier, so
   `mdadm` correctly refused to zero it (busy device) and teardown
   halted partway, orphaning the mirror array. Fixed: `ExistingTier`
   now carries the real `PartitionPaths` straight from `mdadm --detail
   --export` (already parsed, previously discarded down to bare disk
   ids) instead of re-deriving them from a numbering convention;
   regression test `UsesEachTiersRealPartitionPaths_NotASegmentSizeOrderingGuess`
   reproduces the exact swapped-partition scenario.
8. **`mdadm --create` has a second interactive prompt beyond the
   bitmap one** — "`<partition>` appears to be part of a raid array...
   Continue creating array?" if any input partition still has an old
   RAID superblock (e.g. a reused loop-device partition number/offset
   from a torn-down array that was never zero-superblocked). Non-TTY
   stdin makes mdadm default to "N" and abort rather than hang, but
   it's still a script stopping on a prompt it can never answer — hit
   for real during testing. Fixed: `--run` added to `mdadm --create`,
   same "every prompt gets a flag" principle as `--bitmap=internal`.
9. **A successful `/expand` silently no-op'd on retry after the test rig
   was reset** — an expand ran for real (confirmed via `dmesg`/`mdadm
   --detail`: the array genuinely grew), then something external reset
   the test pool back to its pre-expand state, and retrying the *same*
   expand through the Cockpit UI reported success again without running
   a single command. Root cause: the daemon trusted a persisted
   `ExecutionJournal` from a previous request as authoritative for "is
   this already done," instead of only ever asking live system state —
   a shadow-config bug (a second, independent record of "what state is
   the system in" that nothing kept in sync with the one real source).
   Fixed: every execute-style endpoint now always starts a fresh
   execution and re-verifies its own preconditions against live state
   unconditionally; the journal is now a write-only record of the most
   recent attempt, never read back to decide what to run. See
   execution.md's "Resuming and retrying" for the full design.

Also encountered, not a code bug: mdadm's udev auto-assembly rule will
grab a partition the instant it reappears if a stale `linux_raid_member`
superblock is still on it from a previous test run that wasn't torn
down through `wipefs`/`--zero-superblock` first (manual `losetup`/
`truncate` reuse of loop devices does **not** clear this). Shows up as
`mdadm: ... Device or resource busy` on `--create`. This is exactly why
`CommandPlanner`'s teardown paths always zero superblocks before
`wipefs` — a real trap for anyone re-testing against reused loop
devices outside of a full teardown.

Also encountered: WSL2's `/tmp` is a `tmpfs` mount (RAM-backed, fixed
size, 23G in this session) — it filled up entirely from accumulated
loop-device image files (each `truncate -s 2G/4G` looks cheap since
it's sparse, but RAID/LVM writing real superblock/metadata bytes into
them adds up fast across many test generations) plus old `dotnet
publish` output directories that were never cleaned up. This surfaced
as `System.IO.IOException: No space left on device` from
`FileJournalStore.Save` on a real `POST /pools/{poolName}/teardown`
call — a genuine 500, but purely environmental, not a logic bug.
Resolved by deleting old loop-image directories and superseded publish
output; no code change needed. Worth checking `df -h /tmp` if a real
subprocess step ever fails with a vague I/O error during repeated
loop-device testing.

## Files

As of the Phase 5 UI refit, this is an esbuild + React + PatternFly
(`@patternfly/react-core`) app — a deliberate reversal of the original
"no build step, no bundler, no dependencies beyond `cockpit.js`" design
recorded here previously. That earlier decision optimized for a project
with zero JS toolchain; it traded away native Cockpit look-and-feel and
made a guided create/expand flow awkward to build in vanilla JS/DOM.
Mirrors the `cockpit-project/starter-kit` pattern (esbuild, not
Vite/Webpack — no dev-server is useful here since Cockpit pages are
served from disk by cockpit-ws/cockpit-bridge, not a dev proxy).

- `manifest.json` — registers the plugin in Cockpit's tools/menu. Uses
  both the older `tools` and newer `menu` keys for compatibility across
  Cockpit versions.
- `index.html` — loads Cockpit's own `cockpit.js` (provided by the
  Cockpit web server at `../base1/cockpit.js`, not vendored) plus the
  built `dist/app.js`/`dist/app.css`. Mounts React into a bare `<div id="app">`.
- `package.json` / `esbuild.config.mjs` — build (`npm run build`) and
  watch (`npm run watch`) scripts. Fonts referenced by PatternFly's
  `base.css` are emitted as separate hashed files (`file` loader); small
  background SVGs are inlined as data URLs (`dataurl` loader) — see
  `esbuild.config.mjs`'s `loader` map.
- `src/api.js` — `apiRequest`/`apiGetJson`/`apiPostJson`, unchanged from
  the original vanilla-JS version: still a single `cockpit.http({ unix:
  DISKWEAVER_SOCKET })` channel, still builds full paths (query string
  included) itself rather than relying on `cockpit.http()`'s `params` option.
- `src/format.js` — `formatBytes` plus the enum label maps (see "How it
  talks to the daemon" below).
- `src/app.jsx` — React root; imports PatternFly's `base.css` and mounts `<App/>`.
- `src/components/` — `App.jsx` (top-level layout/state), `ErrorBanner.jsx`,
  `PoolsTable.jsx` (+ `TeardownButton.jsx`), `DiskInventory.jsx`,
  `CreateExpandWizard.jsx` (+ `wizard/DiskPickerStep.jsx`,
  `wizard/ConfigureStep.jsx`, `wizard/ReviewPlanStep.jsx`), `JournalView.jsx`.
  Manual `escapeHtml` is gone entirely — JSX auto-escapes every
  interpolated value, so there's nothing left to reimplement.
- `dist/` — build output (gitignored). Never symlinked as a directory
  into Cockpit's package tree — see the node_modules gotcha below.

## Build

```
cd src/DiskWeaver.Cockpit
npm ci
npm run build      # one-shot
npm run watch      # rebuild on change, for iterative dev against a live Cockpit session
```

No JS test framework exists and none was added — the established
verification pattern (see "Validated against a real Cockpit instance"
above) is manual, end-to-end, against a real Cockpit session and real
loop devices. That's how all bugs in this file, before and after the UI
refit, were actually caught; none were unit-test-catchable at the JS layer.

## WSL2 dev/deploy workflow

Three scripts under `scripts/` (repo root), meant to be run from inside
WSL2:

- `scripts/wsl-dev/setup-wsl.sh` — one-time, idempotent. Installs daemon
  build/runtime dependencies (`mdadm`, `lvm2`, `dotnet-sdk`, etc.), syncs
  the repo to a native WSL filesystem copy (`~/diskweaver` — loop-device
  work is unreliable on the Windows-mounted `/mnt/c`, see docker/README.md),
  creates the `diskweaver` group and adds the current user to it (see
  socket permissions below), installs the systemd unit, and symlinks the
  Cockpit plugin (individual files only — see the node_modules gotcha).
- `scripts/wsl-dev/deploy-wsl.sh` — run after every daemon code change. Re-syncs
  the native copy, `dotnet publish`es the daemon (AOT, `linux-x64`), and
  installs+restarts it via a scoped root helper.
- `scripts/wsl-dev/sync-cockpit-dist.sh` — run after every `npm run build`.
  Re-creates per-file symlinks for `dist/`'s current contents (font
  filenames are content-hashed, so this can't be a one-time setup step).

Scripts are invoked as `bash /mnt/c/.../scripts/foo.sh`, not executed
directly — the executable bit over the `/mnt/c` 9p mount has been
unreliable in practice.

## Known gotcha: the browser caches dist/app.js/app.css, not Cockpit

Real symptom, hit more than once during development: edit source, rebuild,
reload the page in the browser — and the *old* behavior is still there,
with no error. This is **not** Cockpit caching anything server-side.
Confirmed directly against `cockpit/packages.py` (the Python bridge,
`/usr/lib/python3/dist-packages/cockpit/packages.py` on a Debian/Ubuntu
install): `Package.load_file()` calls `path.open('rb')` fresh on every
single request — file *content* is never cached in the bridge process —
and nothing in the bridge sets a `Cache-Control` or `ETag` header on
package responses at all. With no caching directive from the server,
it's entirely up to the browser's own heuristics whether to reuse a
previous response for the same URL, and since `dist/app.js`/`dist/app.css`
are always requested at the exact same URL, browsers frequently do.

Two mitigations, not mutually exclusive:

- **`?v=N` cache-busting** (in place in `index.html`): `dist/app.css?v=N`
  and `dist/app.js?v=N`. Bump the number on both tags after every
  `npm run build` — this forces a new URL, so the browser can't serve a
  stale response for it. Still manual (deliberately not switched to
  esbuild's content-hashed filenames, which would need `index.html`
  regenerated by the build — extra tooling not worth it for two files),
  so it's only as reliable as remembering to bump it.
- **Hard refresh** (Ctrl+Shift+R / Cmd+Shift+R) or fully logging out
  and back in to Cockpit: guaranteed to work regardless of the `?v=`
  number, since it bypasses the browser cache entirely rather than
  relying on the URL having changed. This is also the only fix for
  `index.html` itself being served stale (its own URL never changes,
  so `?v=` bumps inside it don't help until the browser actually
  re-fetches it) — reach for this first if a `?v=` bump alone doesn't
  seem to help.

## Known gotcha: never symlink node_modules/ into Cockpit's package directory

Real symptom, cost a full day to isolate: after the Phase 5 UI refit added
an esbuild/React/PatternFly toolchain (see below), the *entire* Cockpit
shell started failing intermittently — not just this plugin's page.
Logging into Cockpit would spin, then `cockpit-ws` would log
`/shell/shell.js: external channel failed: terminated` (core Cockpit's own
JS, unrelated to this plugin) roughly 35-45 seconds after every login. It
was consistently reproducible across multiple from-scratch WSL2
reinstalls and even a Windows host reboot, which made it look like
environment corruption (WSL2 networking, TLS/certificate issues, the
daemon's Unix socket permissions) rather than anything to do with this
plugin — all of which were investigated and ruled out (raw HTTP/TLS
throughput over the same network path was solid at 50+ seconds; the
daemon was idle and had 0 restarts; a completely fresh WSL install with
Cockpit alone, untouched, was stable across many logins).

The actual cause: `scripts/wsl-dev/setup-wsl.sh` used to symlink the *whole*
`DiskWeaver.Cockpit/` directory into `~/.local/share/cockpit/diskweaver`.
Once that directory also contained `node_modules/` (52,000+ files, 186MB,
from the esbuild/React/PatternFly toolchain) and `src/`, cockpit-bridge's
per-session package scan was walking all of it — over the Windows-mounted
9p filesystem (`/mnt/c/...`), where per-file latency is much higher than
on a native Linux filesystem. Walking 50k+ small files there stalled the
whole bridge session long enough to look exactly like a broken
environment, including killing unrelated core Cockpit resources, not just
this plugin's own files.

Fixed by symlinking only the three things Cockpit actually needs to
serve — `index.html`, `manifest.json`, `dist/` (9 files) — as individual
symlinks inside `~/.local/share/cockpit/diskweaver/`, never the project
root. See `scripts/wsl-dev/setup-wsl.sh`. If this plugin's build output ever grows
new top-level files that need serving, add them to that symlink list
explicitly rather than reverting to a single whole-directory symlink.

## How it talks to the daemon

`cockpit.http({ unix: "/run/diskweaverd.sock" })` opens an HTTP channel
over that Unix socket via the Cockpit bridge (the plugin's JS never
touches the socket directly — the bridge, running server-side, does).
This is the same transport decision recorded in daemon-api.md: HTTP/JSON
over a Unix socket, chosen over D-Bus.

Every call goes through a single `apiRequest(method, path, jsonBody)`
helper that builds the full path (including query string, e.g.
`/plan/{id}/script?kind=teardown`) itself rather than relying on
`cockpit.http()`'s `params` option — done to avoid depending on exact
query-string-building behavior that hasn't been checked against a real
Cockpit build yet.

Enum fields (`raidLevel`, step/journal `status`) come back from
`System.Text.Json`'s default enum converter as **integers**, not
strings (confirmed against real daemon output during Phase 2b testing:
`"raidLevel":1` for a RAID5 tier). `src/format.js` keeps small label maps
(`RAID_LEVEL_LABELS`, `STEP_STATUS_LABELS`, `JOURNAL_STATUS_LABELS`)
matching the C# enum declarations' declaration order — if a new enum
value is ever inserted rather than appended on the C# side, these maps
would silently go stale, since nothing enforces the mapping stays in
sync.

## Flow implemented

1. `GET /pools` and `GET /inventory` load on page open.
2. User checks disks, picks DWR-1/DWR-2, clicks **Create plan** →
   `POST /plan`. The returned plan id is held in memory
   (`currentPlanId`) for the rest of the session — it's never
   persisted client-side, consistent with state-model.md's "don't
   duplicate state" principle; a page reload just means re-planning.
3. **View build/teardown script** → `GET /plan/{id}/script?kind=...`,
   shown verbatim in a `<pre>` — the same script the CLI's
   `--script`/`--teardown-script` would produce.
4. **Execute build** → `window.confirm()` first (this runs real
   mdadm/parted/lvm2 commands), then `POST /plan/{id}/execute?kind=build`.
   The request blocks until the daemon finishes (or hits the first
   failed step) and returns the final `ExecutionJournal`, rendered as a
   per-step table.
5. **Teardown**, per pool row in the Existing pools panel →
   `POST /pools/{poolName}/teardown`. This is deliberately a separate
   action from the plan flow above — see below.

## Tearing down a pool: two different actions, on purpose

Tearing down *the plan you just built* (`POST /plan/{id}/execute?
kind=teardown`, still reachable via **View teardown script** for
preview) and tearing down *a pool you see in `GET /pools`* are
different operations with different inputs, and the UI now reflects
that split rather than blurring it into one "Execute teardown" button:

- `CommandPlanner.BuildTeardown(PoolPlan)` needs the *original disk
  selection and redundancy* that produced the plan — fine right after
  building, useless for a pool from a previous session or one this
  daemon didn't build.
- `CommandPlanner.BuildTeardownFromExisting(ExistingPoolState)` (new)
  needs none of that — it reads the pool's actual array devices,
  segment sizes, disk ids, and (per bug 7 above) real partition paths
  straight from `GET /pools`, rather than reconstructing partition
  numbering from a convention that can be wrong once grows have
  happened out of creation order. This is what the **Teardown** button
  per pool row now calls, via `POST /pools/{poolName}/teardown`.

Both ultimately produce an `ExecutionPlan` run through the same
`ExecutionRunner`/journal machinery — this is purely about which one
correctly answers "teardown *what*," not a difference in execution
safety.

## Open items before this is real

- **Socket permissions / superuser model.** The daemon needs root
  (mdadm/lvm2/parted); Cockpit's bridge normally runs as the logged-in
  user unless a "superuser" reauth elevates it (the same mechanism
  udisks2/NetworkManager rely on for their root-owned D-Bus calls).
  `packaging/diskweaverd.service` doesn't restrict the socket's
  permissions yet — this needs deciding once there's a real Cockpit
  session to test the superuser flow against, not guessed at now.
- **No polling/streaming for long executions.** `POST /execute` blocks
  for the whole plan today (matches the daemon's synchronous design —
  see execution.md); if a future step genuinely takes a long time,
  the UI would need to move to background execution + polling
  `GET /execute/{id}/status`, which the daemon already exposes but
  `app.js` doesn't currently poll.
