# Daemon API (Phase 2b/3)

Status: `DiskWeaver.Daemon` implements every endpoint below, including
real invocation (`POST /plan/{id}/execute`) and the crash-recoverable
journal (`GET /execute/{id}/status`) — tested in-process against fakes,
and validated end-to-end in WSL2 against real loop devices/mdadm/lvm2.

## Why this exists

A Cockpit plugin is HTML/JS running in a browser — it can't call our C#
libraries directly. It needs something to talk to: `cockpit.spawn()`, a
D-Bus service, or (per the PRD's decision) a small HTTP/JSON API over a
Unix socket that a root-owned `diskweaverd` process exposes. The CLI
should become a thin client of the same API rather than duplicating
logic, so Cockpit and the CLI can never drift apart in behavior.

## Endpoints

All read-only today, backed directly by existing domain code in
`DiskWeaver.Core` (`DiskWeaver.Inventory`, `DiskWeaver.Planner`,
`DiskWeaver.Executor` namespaces) — no new logic, just exposing what's
already built over HTTP.

- `GET /inventory` → live disk list, wrapping
  `LsblkDiskInventorySource.GetDisks()`. Replaces needing `--lsblk-json`
  file hand-offs once the daemon runs on the actual target host.
- `GET /pools` → existing pool state on this host (arrays, PVs, VG/LV),
  the `ExistingPoolState` list needed for `BuildIncremental`. Built:
  `MdadmLvmPoolStateSource` (in `DiskWeaver.Executor`) groups `pvs`/`lvs
  --reportformat json` output by volume group, then describes each
  tier's array via `mdadm --detail --export` (parsed by the pure
  `MdadmDetailParser`) and `blockdev --getsize64` for its size. Member
  partition paths are mapped back to disk ids via `PartitionNaming.
  ToDiskId` — the exact inverse of the naming `CommandPlanner` uses when
  building the array in the first place.
- `POST /plan` → body: `{ diskIds: [...], redundancy: "none"|"dwr1"|"dwr2",
  poolName?, thinProvisioned?, assumeClean? }` (optionally `{ existingPool: ... }` for
  the incremental path) → `thinProvisioned` (default `false`) makes the
  eventual `Build`/`BuildTeardown` create a thin pool (with headroom, see
  `CommandPlanner.ThinPoolHeadroomPercent`) plus one thin `data` volume
  instead of the default single thick LV — see execution.md's "Multiple
  logical volumes (thin pools)". `assumeClean` (default `false`) makes
  each tier's `mdadm --create` pass `--assume-clean`, skipping the
  initial full-array resync/parity-build — safe here specifically
  because every disk was just verified blank, so there's no real data
  whose parity could be silently wrong to begin with. `PlanCache` stores
  both alongside the plan itself, keyed into the plan id, so
  `/plan/{id}/script` and `/plan/{id}/execute` build exactly the layout
  this request described.
  →
  returns a `PoolPlan` plus a **plan id** (see below). `"none"` builds
  each disk as its own independent, unprotected tier — a degraded 2-slot
  RAID1 (`--raid-devices=2 <partition> missing`, PV-tagged
  `diskweaver-unprotected`) rather than a bare partition, so a disk added
  to it later (see `/expand` below) just fills the missing slot (`mdadm
  --add`, no `--grow`). See `Tier.DegradedSlots`'s doc and algorithm.md.
  Upgrading such a
  pool to a shared DWR-1/DWR-2 tier isn't supported incrementally if it
  would require merging several independent existing tiers into one —
  refused with a message pointing at tearing down and rebuilding fresh.
- `GET /plan/{id}/script` → the `ExecutionPlan` rendered as text via
  `ShellScriptEmitter`, for review — today's `--script`/
  `--teardown-script` CLI output, just served over the API instead of
  written to a file.
- `POST /plan/{id}/execute?kind=build|teardown` → runs the plan's steps
  as real subprocesses via `ProcessStepRunner`, persisting an
  `ExecutionJournal` after each step via `FileJournalStore`. Runs
  synchronously to completion or first failure within the request
  (each step is individually fast; RAID resync continues in the
  background after `mdadm --create` returns). Re-`POST`ing always
  starts a fresh `ExecutionRunner.AdvanceOneStep` run from scratch — the
  journal from a previous request is never read back to decide what to
  skip (see execution.md's "Resuming and retrying" for why). Validated
  end-to-end in WSL2 against real loop devices/mdadm/lvm2.
- `GET /execute/{id}/status` → returns the persisted `ExecutionJournal`
  (id is `"{planId}-{kind}"`), for the Cockpit UI to poll/show a
  progress view, or just to check the outcome of a prior execute call
  later.
- `POST /pools/{poolName}/teardown` → tears down a pool found via
  `GET /pools`, using its actual on-disk state
  (`CommandPlanner.BuildTeardownFromExisting`) rather than a
  regenerated `PoolPlan` — deliberately separate from
  `POST /plan/{id}/execute?kind=teardown`, which only works for a plan
  still cached from the same planning session. Same synchronous
  run/journal model, execution id `"pool-{poolName}-teardown"`.
  Validated end-to-end through the Cockpit UI.
- `POST /pools/{poolName}/expand` → body `{ diskIds: [...],
  targetProtection?: "none"|"dwr1"|"dwr2", redundancy?: "none"|"dwr1"|"dwr2",
  targetArrayDevice?: string, assumeClean?: boolean }`, disks to add to a pool found via
  `GET /pools`. `assumeClean` (default `false`) is passed straight through
  to `CommandPlanner.BuildIncremental` — same `--assume-clean` behavior as
  `POST /plan`'s field of the same name, but only affects any brand-new
  tier this expansion creates; growing an existing tier in place
  (`mdadm --grow`) always resyncs regardless, since mdadm has no
  equivalent skip for that operation. Three tenets drive this endpoint's design (see
  algorithm.md's "Expand tenets" for the full rationale):
  1. A fresh build (`POST /plan`) always picks the max-capacity layout
     for the requested redundancy — nothing new here, just the baseline.
  2. Given a pool, new disks, and a target protection level, expand
     computes **0, 1, or 2 candidate plans** — one that adds protection,
     one that adds space — rather than requiring the caller to already
     know which internal mechanism fits this pool's current shape.
     Either can be absent (e.g. a disk too small to help any tier at all
     — the "hot spare" case yields both absent).
  3. A from-scratch rebuild being more space-efficient is always a
     "btw" note (`HypotheticalRebuildCapacityBytes`), never a blocking
     error and never a third option.

  `targetProtection`, `redundancy`, and `targetArrayDevice` are mutually
  exclusive (`400` if more than one is set):
  - `targetProtection` omitted or set (default two-option mode): the
    daemon calls `ExpansionOptionsPlanner.ComputeOptions` (`DiskWeaver.Executor`)
    and returns up to two entries in `Options` below.
    - **Protection candidate**: only ever offered for a genuine increase
      in something that already existed in the pool — matching this same
      HHR model, where a protection upgrade is never suggested just
      because new disks *could* form their own separately-protected tier
      (those disks weren't part of the pool before, so there's nothing of
      theirs to "increase the protection of"; that's the space
      candidate's territory). First tries completing any currently
      degraded/unprotected tier with the offered disks
      (`ProtectionPlanner.PlanAutoProtect` — splits a larger disk across
      more than one eligible tier if it's big enough; leftover capacity
      from *that* completion is still grouped into a new protected tier
      wherever 2+ same-size pieces make that possible, `LeftoverTierPlanner`,
      rather than scattered as separate single-disk unprotected tiers, but
      leftover-only grouping with zero real completions doesn't count as
      a protection gain on its own). Only if no tier was actually
      completed does it try raising the pool's own redundancy to
      `targetProtection` — which defaults to **one level above** the
      pool's current inferred redundancy (capped at `dwr2`), not the same
      level: defaulting to "same as current" would trivially never offer
      an upgrade at all. This is what lets a healthy `dwr1` pool with
      room to reach `dwr2` (e.g. enough same-size disks and only one
      existing tier to grow) surface that as an option without the caller
      having to already know to ask for it. The upgrade is computed via a
      full `TieringPlanner.Plan` across every disk, kept only if it
      doesn't require merging 2+ independent existing tiers
      (`CommandPlanner.HasMergeConflicts`; no mdadm operation does that).
      Absent entirely if neither applies.
    - **Space candidate**: grows every eligible *already fully-populated*
      existing tier in place at its own current redundancy — never
      `targetProtection`, which is the protection candidate's job —
      splitting the offered disks across all eligible tiers as evenly as
      possible (`IndependentGrowPlanner.PlanGrowth`; same `mdadm --add`/
      `--grow`/level-migration mechanism `BuildGrowSteps` uses for a
      single-tier pool). Leftover capacity is grouped the same way as the
      protection candidate's. Absent if nothing actually grows achieved
      capacity (e.g. a disk that only completes a degraded tier, which is
      capacity-neutral — filling a missing RAID1/5/6 slot never changes
      an array's size regardless of real vs. configured member count).
  - `redundancy: "none"|"dwr1"|"dwr2"` ("advanced"/manual mode): bypasses
    the two-option computation entirely, returning exactly one `"manual"`
    entry. `"none"`: the added disk(s) become a brand-new, independent
    unprotected tier — planned separately via `TieringPlanner.Plan(newDisks,
    RedundancyLevel.None)` and combined with the existing tiers' own
    (otherwise-unchanged) desired plan. `"dwr1"`/`"dwr2"`: recomputes the
    whole pool's tiering at that redundancy from every disk (existing +
    new) together — a genuine `409`/`400` if that's not achievable
    incrementally (a caller's specific ask that can't be honored, unlike
    the default mode's silent per-tier best-effort).
  - `targetArrayDevice: "/dev/md/..."` ("advanced"/manual mode): bypasses
    the two-option computation entirely, returning exactly one `"manual"`
    entry. Sends every disk in `diskIds` toward completing that one named
    tier into a real mirror (it must currently have exactly 1 real member
    — unprotected-by-design or degraded from a disk failure), instead of
    recomputing tiering from scratch. Splitting one larger disk across
    several tiers means calling this once per tier — a disk already
    claimed by another tier in this pool is allowed again as long as its
    *remaining* capacity is enough (`DiskCapacityValidator.EnsureSpareCapacity`,
    `DiskWeaver.Executor`), unlike the usual whole-disk blank requirement.

  Every candidate/manual plan is an ordinary `desired` `PoolPlan` handed
  to `CommandPlanner.BuildIncremental`, cached separately under its own
  content-addressed id in `ExecutionPlanCache`, so
  `GET /pools/{poolName}/expand/{id}/script` and
  `POST /pools/{poolName}/expand/{id}/execute` (below) work identically
  regardless of which candidate/mode produced the plan. Returns an
  `ExpansionOptionsResponse { Options: ExpansionOption[], HypotheticalRebuildCapacityBytes }`,
  where each `ExpansionOption` is
  `{ Intent: "protection"|"space"|"manual", PlanId, DesiredPlan,
  AchievedCapacityBytes, AchievedRedundancy }` — note `DesiredPlan`'s own
  `PoolCapacityBytes` and `AchievedCapacityBytes` can legitimately differ:
  `DesiredPlan.PoolCapacityBytes` assumes any grow-candidate tier has
  already been manually reshaped (which `BuildIncremental` never
  automates), while `AchievedCapacityBytes` (`CommandPlanner.AchievedCapacityBytes`)
  is what actually results from executing this specific plan.
  `HypotheticalRebuildCapacityBytes` (`CommandPlanner.HypotheticalFullRebuildCapacityBytes`,
  nullable, one value for the whole response not per-option) is purely
  informational and never executed: what tearing down and rebuilding
  fresh from every disk in the pool plus the ones being offered would
  achieve at the pool's own (inferred) redundancy level — often
  meaningfully higher than any candidate's `AchievedCapacityBytes` for
  the exact same disks, since several independent tiers each separately
  pay a mirror's redundancy overhead where one shared rebuild would
  amortize it across every disk at once.
- `GET /pools/{poolName}/expand/{id}/script` /
  `POST /pools/{poolName}/expand/{id}/execute` → same script-preview
  and synchronous execute/journal model as the `/plan/{id}/...`
  endpoints, execution id `"{id}-expand"`. Validated end-to-end through
  the Cockpit UI against real loop devices: added two matching-size
  disks to an existing pool, got a genuine new tier via real
  `vgextend`/`lvextend`, confirmed against real `mdadm`/`vgs`/`lvs`.

- `POST /disks/wipe` → body `{ diskIds: [...] }`, disks to clear so they
  pass `Disk.IsBlank` afterward (`CommandPlanner.BuildWipe`) —
  independent of any pool/plan, unlike teardown. For each requested
  disk, zeroes any RAID superblock on its current partitions
  (`IDiskInventorySource.GetPartitionPaths`, backed by
  `LsblkOutputParser.ParsePartitionPaths`) before wiping the parent
  disk's own signature (`wipefs -a`) — same order teardown already uses
  for a pool's own disks. Same synchronous run/journal model as
  teardown, execution id `"wipe-{diskNames joined by _}"` (disk ids'
  trailing path component, since a full `/dev/...` id isn't a safe
  journal file name as-is). Does **not** stop or remove a disk from a
  currently-*running* array — `mdadm --zero-superblock` refuses a live
  member, so a disk left mid-reshape (e.g. a grow that failed partway
  through) needs `mdadm --stop`/`--remove` first. The Cockpit UI never
  offers this for a disk flagged `isLikelySystemDisk` (see below), but
  the endpoint itself doesn't check that — it trusts `DiskSelector`'s
  usual not-a-typo/no-duplicates validation only.

Not yet built:

- An explicit `/abort` — see execution.md's open items for why this is
  more subtle than it sounds for a sequential subprocess pipeline.

## Plan identity — built

`PlanCache` (for `POST /plan`) and `ExecutionPlanCache` (for
`POST /pools/{poolName}/expand`) both compute a SHA256 content hash
over the plan's inputs (resolved disk ids + sizes + redundancy, or
pool name + added disk ids) and store the resulting `PoolPlan`/
`ExecutionPlan` keyed by that hash's first 16 hex chars, in memory,
until the daemon restarts. This is what makes re-`POST`ing the same
`/plan` or `/expand` preview call return the same id for the same
inputs — a caching/display convenience, not a claim about what's
already been executed.

**Formerly a known sharp edge, now fixed:** because the id is purely a
function of inputs, reusing the same disk ids/sizes (e.g. the same loop
device numbers across independent test rig rebuilds) could produce the
same execution id as a previous, already-`Succeeded` attempt. The
daemon used to treat that stale journal as still valid and silently
skip execution on replay — a real incident, not theoretical (see
execution.md's "Resuming and retrying" for the full story). Fixed by no
longer reading any persisted journal back to decide what to run: every
execute call always starts fresh and re-verifies its own preconditions
against live state instead.

**Staleness detection at Execute time:** a plan can sit reviewed for a
while (`GET /plan/{id}/script`, a browser tab left open) before Execute
actually runs, so every execute endpoint re-checks state immediately
before running a single command — unconditionally, on every call, not
just the first:

- `POST /plan/{id}/execute?kind=build` re-fetches live inventory
  (`PlanCache.TryGetSelectedDisks`) and refuses (409 Conflict) if any
  disk the plan was computed against is now missing, changed size, or
  is no longer blank (`Disk.IsBlank` — see below).
- `POST /plan/{id}/execute?kind=teardown` confirms a DiskWeaver-owned
  pool (`GET /pools`) still exists whose disks exactly match the plan's.
- `POST /pools/{poolName}/expand/{id}/execute` re-fetches the pool and
  compares `ExecutionPlanCache.ComputeFingerprint` against the value
  captured at `/expand` time (covers the array/tier state
  `BuildIncremental` planned against), and re-confirms the newly-added
  disks are still blank.

This now also correctly handles a rebuilt test rig that looks identical
to the daemon (same disk ids/sizes) — since reality genuinely matches
what the plan expects, these checks pass and a fresh execution runs for
real, rather than a stale journal short-circuiting it.

## Disk eligibility: `Disk.IsBlank`

`GET /inventory` reports every disk/loop device lsblk sees, including
ones that already have a partition table, a mounted filesystem, or a
foreign filesystem/RAID/LVM signature — `Disk.IsBlank` (computed by
`LsblkOutputParser` from lsblk's per-device `children`/`fstype`/
`mountpoints`, since `-d` is no longer passed) says which. Selecting a
non-blank disk is refused (`DiskSelector.EnsureBlank`, `400 Bad
Request`) at both `POST /plan` (all requested disks) and `POST
/pools/{poolName}/expand` (only the newly-requested disks — a pool's
existing disks are legitimately non-blank, and are trusted because they
came from `GET /pools`, not directly from user input).

This same check applies to the CLI's `diskweaver plan --lsblk-json`/
`--lsblk`, not just the daemon — but a captured `inventory.json` file
can be arbitrarily old, predating `fstype`/`mountpoints` entirely (e.g.
one taken via a pre-eligibility-checking `lsblk --json -b -d -o
NAME,SIZE,TYPE,ID-LINK`). `LsblkOutputParser` treats a **missing**
`fstype`/`mountpoints` column as unknown, not blank — it throws
`FormatException` naming the offending device rather than silently
defaulting every disk in a stale capture to "safe to wipe." Re-capture
with `lsblk --json -b -o NAME,SIZE,TYPE,ID-LINK,FSTYPE,MOUNTPOINTS` (no
`-d`) to fix it; `diskweaver testkit`'s generated setup script already
captures this way.

## Kernel device path: `Disk.DevicePath`

`GET /inventory` also reports `devicePath`, e.g. `"/dev/sdb"` — always
the trailing `/dev/{name}` form lsblk reports, even when `id` resolved
to a `/dev/disk/by-id/...` path (the common case: most real disks have
one). Kernel names aren't stable across reboots/replugs, so `id`
remains the identity everything else (plans, wipe requests, pool
membership) keys off of — `devicePath` is purely a display convenience
in the Cockpit inventory table, for matching what a user sees here
against `lsblk`/`dmesg`/a drive bay label elsewhere.

## Best-effort system-disk flag: `Disk.IsLikelySystemDisk`

`GET /inventory` also reports `isLikelySystemDisk`: true if the disk or
any of its partitions is currently mounted at `/`, `/boot`, `/boot/efi`,
or as swap (computed by walking lsblk's nested `children`/`mountpoints`
recursively, same data `IsBlank` already reads). This is informational
only — the daemon does not refuse to select a disk based on it, and it
is not exhaustive (a disk can be important without being mounted right
now, e.g. an unmounted backup target). In practice a real system/boot
disk already fails `IsBlank` (it has partitions and/or a filesystem
signature), so this exists to give that refusal a clearer, scarier
label in the Cockpit UI ("likely system/boot disk -- do not use") and
to keep the `WipeButton` from ever being offered for it, rather than to
add a new blocking mechanism.

## Whose RAID/LVM signature: `Disk.RaidLvmSignatureOwner`

`GET /inventory` also reports `raidLvmSignatureOwner`: `null` when the
disk has no mdadm/LVM signature at all (a blank disk, or one with only
a plain filesystem — never a DiskWeaver artifact, so no further check
is needed), otherwise one of `"diskweaver"`, `"foreign"`, or
`"unknown"`. This is what lets the Cockpit inventory table say *why* a
non-blank disk isn't blank — leftover DiskWeaver pool metadata that was
never wiped, versus some other system's RAID/LVM, versus "can't tell
yet."

Getting past lsblk's `linux_raid_member`/`LVM2_member` FSTYPE (which
only proves *some* RAID/LVM signature exists) to a specific owner needs
the LVM VG's `diskweaver-managed` tag (`DiskWeaverPoolTag`), and that
tag is only readable while the array backing the VG's PV is actually
assembled. So this is computed in two passes:

1. `LsblkOutputParser` (pure, lsblk-only) sets `"unknown"` for any disk
   with a signature — it has no way to shell out to `pvs`/`vgs`.
2. `DiskSignatureOwnership.Annotate`, run by the `/inventory` handler
   after fetching inventory, upgrades each `"unknown"` disk it can:
   cross-references the disk's partitions against
   `IArrayMembershipSource` (live `/proc/mdstat`, so only currently
   *assembled* arrays show up) to find its array device, then checks
   whether that array is a PV in a VG tagged `diskweaver-managed` via
   `vgs`/`pvs`. Found and tagged → `"diskweaver"`; found and untagged
   (or belongs to no VG) → `"foreign"`; array not currently assembled
   at all → stays `"unknown"`, since DiskWeaver never persists its own
   record of "used to be my pool" (see docs/state-model.md) — there is
   no data to consult once the array is torn down. Reassembling it
   (`POST /arrays/reassemble`) may resolve the "unknown" into a real
   answer on the next `GET /inventory`.

In practice a disk actively claimed by a live, tagged pool is already
caught earlier by the Cockpit UI's own "already in `<pool>`" check
(`poolNameByDiskId` in `DiskInventory.jsx`, sourced from `GET /pools`)
before `raidLvmSignatureOwner` is even consulted — this field only
matters for the disks that check doesn't cover.

## Transport decision: HTTP/JSON (Kestrel + Native AOT), not D-Bus

Explicitly considered and rejected: a D-Bus system service (the more
idiomatic Linux pattern — NetworkManager, udisks2, systemd, and Cockpit's
own built-in modules all integrate this way via `cockpit.dbus()`), and
no persistent daemon at all (Cockpit spawning the CLI per action via
`cockpit.spawn()`, deferring the question).

Decision: keep HTTP/JSON over a Unix socket, matching how `dockerd`
exposes a REST API to multiple clients over a Unix socket. Reasoning:

- It's already built and tested (see `DiskWeaver.Daemon`/
  `DiskWeaver.Daemon.Tests` — 21 passing tests, fully in-process, no
  Linux/Docker needed to test the routing/caching logic).
- Plain JSON is far less work to marshal from C# than D-Bus's own
  type/signature system, and trivially debuggable with `curl`.
- Native AOT publish (see below) keeps the "Kestrel is built for
  internet-facing HTTP" overhead in check for a NAS-class host — this
  needs to actually be validated, not just assumed, which is why it's
  the immediate next step rather than a settled fact.
- Spawn-per-action was rejected because Phase 2b's execution/journal
  tracking (§ above) genuinely needs a long-running process — deferring
  the daemon decision would just mean building it later anyway, on a
  tighter timeline.

If Native AOT publish turns out not to deliver an acceptably small/fast
daemon in practice, D-Bus is the fallback to revisit — this isn't
treated as irreversible.

## What's NOT decided yet

- Auth/privilege model beyond "the socket is only reachable by
  Cockpit's already-privileged bridge" — validated so far only with a
  `chmod 666` test shortcut; the real superuser/socket-permission model
  (see cockpit-plugin.md's open items) still needs deciding against a
  real Cockpit superuser-reauth session.
