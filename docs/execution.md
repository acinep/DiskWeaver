# Executor: command generation and invocation

Status: Draft v0.1 — companion to [PRD.md](PRD.md) §4a/§6, Phase 2 scope.

Phase 2 turns an approved `PoolPlan` (from [algorithm.md](algorithm.md))
into the actual disk-layer changes. Like disk inventory in Phase 1, this
is split into a pure transform and an OS-specific backend:

- **2a — command generation + script emission.** `PoolPlan` →
  `ExecutionPlan` (ordered `ExecutionStep`s) → shell script text. No
  side effects, no root, no real disks — fully unit-testable anywhere,
  including off-target during development.
- **2b — invocation + journal.** Actually run each `ExecutionStep` as a
  subprocess, persisting a journal entry after each step so a crash
  mid-run is recoverable. Built: `ExecutionRunner.AdvanceOneStep` (pure,
  unit-tested with a fake `IStepRunner`) advances a plan exactly one
  step at a time; `ProcessStepRunner` is the real subprocess-invoking
  implementation; `FileJournalStore` persists the resulting
  `ExecutionJournal` as one JSON file per execution id
  (`/var/lib/diskweaver/journal/<id>.json`, overridable via
  `DISKWEAVER_JOURNAL_DIR`), written via a temp-file-then-rename so a
  crash mid-write never corrupts it. Validated end-to-end in WSL2
  against real loop devices/mdadm/lvm2 via the daemon's
  `POST /plan/{id}/execute`: full build, idempotent re-execute (no
  steps re-run once succeeded), and teardown all completed correctly.

## Partition layout invariant

A useful property of `PoolPlan` falls out of the tiering algorithm: since
segment `j`'s qualifying-disk count `m_j` is computed from disks whose
size `>= sj`, and boundaries are ascending, the set of qualifying disks
can only shrink (or stay the same) as `j` increases — it's monotonically
non-increasing. That means **any reserved (excluded) segments are always
the topmost/last segments on a disk**, never sandwiched between two
protected tiers. So partitioning a disk is just: walk `PoolPlan.Tiers` in
order, and for each tier the disk belongs to, append the next partition;
any reserved segment at the end is simply left unpartitioned (free space,
reclaimed automatically once a matching disk arrives and the plan is
recomputed).

## Command generation (`CommandPlanner`)

For each disk, partitions are created sequentially starting at a 1MiB
aligned offset (GPT + alignment reserve). For each tier (in boundary
order):

**Every partition boundary is 1MiB-aligned, not just the first.**
`PartitionLayout.ForPlanning` rounds each disk's adjusted size (after
subtracting `TotalReservedBytesPerDisk`) *down* to a `StartAlignmentBytes`
(1 MiB) multiple, before `TieringPlanner.Plan` buckets disks into
boundaries by that adjusted size. Real disks' raw manufacturer byte
counts are essentially never themselves a round 1MiB multiple, so
without this, only the very first partition on a disk (which always
starts at exactly `StartAlignmentBytes` by construction) ended up
aligned — every later partition's start was wherever the previous
tier's unrounded byte size happened to end, which `parted` then flags:
`Warning: The resulting partition is not properly aligned for best
performance`. Confirmed live. Costs at most just under 1 MiB of usable
capacity per disk.

**This changes a tier's exact `SegmentSizeBytes` slightly** — which
matters because `BuildIncremental`'s `ClassifyIncremental` matches an
existing tier to a freshly-recomputed desired tier by *exact*
`SegmentSizeBytes` equality (not "close enough"). A pool built before
this rounding existed has segment sizes that won't exactly match a
post-fix recompute, so expanding it will fail with "existing tier(s)
don't correspond to any tier in the new plan" even though nothing is
actually wrong — a full teardown + rebuild is required first to get
segment sizes consistent with the new alignment. This is an inherent
consequence of `ClassifyIncremental`'s exact-match design (not unique
to this change — any future refinement of `PartitionLayout`'s math
would hit the same wall for already-built pools), just the first
change to actually trigger it in practice.

1. First time a disk is touched: `parted --script <disk> mklabel gpt`.
2. `parted --script <disk> unit B mkpart primary <start> <end>` for that
   disk's slice of the tier, followed by `partprobe <disk>` (see below).
3. Once every disk in the tier has its partition and a trailing
   `udevadm settle`: `mdadm --create /dev/md/diskweaver-tier<N>
   --level=<1|5|6> --raid-devices=<m> --metadata=1.2
   --consistency-policy=ppl|--bitmap=internal <partitions...>`, then
   `pvcreate` on the resulting array device. RAID5 tiers get
   `--consistency-policy=ppl`; Mirror and RAID6 tiers get
   `--bitmap=internal` (see below for why they differ).

After all tiers: `vgcreate diskweaver-pool <all array devices>`, then
`lvcreate -l 100%FREE -n data diskweaver-pool`.

**Every mdadm/LVM command that can prompt interactively is given
whatever flag makes it non-interactive, always, no exceptions** —
discovered as a real bug three times during hardware/loop-device
validation: `vgremove` prompts "Do you really want to remove volume
group..." without `-f` (the next script line got swallowed as the
prompt's answer, corrupting teardown); `mdadm --create` prompts
"enable write-intent bitmap?" without `--bitmap` specified explicitly
(same failure mode: hangs unanswered, or a wrong/unintended answer
corrupts whatever runs next); and `mdadm --create` separately prompts
"`<partition>` appears to be part of a raid array... Continue creating
array?" if any input partition still has an old RAID superblock on it
(e.g. a reused loop-device partition number/offset from a torn-down
array whose superblock was never zeroed) — non-TTY stdin makes mdadm
default to "N" and abort rather than hang, but it's still a script
stopping on a prompt it can never answer, so `--run` is given
explicitly to proceed. Explicitly specifying one of `--bitmap` /
`--consistency-policy` is also the right call on its own merits, not
just to silence the prompt — see the ppl note below for why the choice
between them isn't just "make it non-interactive." `lvremove` and
`vgremove` both get `-f` for the same reason.

**RAID5 gets ppl instead of a plain bitmap; Mirror and RAID6 don't.**
A write-intent bitmap only narrows an unclean-shutdown resync to the
regions that were dirty — it doesn't stop the RAID5 write hole itself:
a stripe update torn by power loss can leave data and parity silently
inconsistent, and a bitmap resync won't detect or fix that. `ppl`
(Partial Parity Log, `--consistency-policy=ppl`) logs each stripe's
pre-write parity, so recovery can correctly reconstruct exactly the
stripes that were mid-write — a real correctness improvement, not just
a faster resync. It's RAID5-only: the kernel md driver never
implemented it for RAID6's dual P+Q parity (closing RAID6's write hole
needs a dedicated write-intent journal device instead — a real,
separate gap DiskWeaver's planner doesn't cover, since it has no
concept of selecting a disk for that role today), and mirrors have no
parity to protect in the first place, so both keep the plain bitmap.

ppl and an internal bitmap are mutually exclusive on the same array,
which matters for `BuildGrowSteps`' RAID-level migrations
(`CommandPlanner.cs`'s `PlanLevelHops`): a Mirror always has the
bitmap it was created with, and mdadm won't let ppl be enabled until
the array is genuinely RAID5, so a Mirror → RAID5 hop keeps that
bitmap through the reshape itself and only switches to ppl (`--grow
--bitmap=none` then `--grow --consistency-policy=ppl`) once the
reshape finishes. Going the other way, RAID6 has no ppl support at
all, so a RAID5 (ppl) tier migrating up to RAID6 sheds ppl (`--grow
--consistency-policy=bitmap`) *before* that reshape starts — there's
no valid intermediate state where a RAID6 array runs with ppl active.
A Mirror → RAID5 → RAID6 double hop (jumping straight to a RAID6
target) never touches ppl at all in either direction: the bitmap
present since Mirror creation just carries through both hops
untouched, since the *final* level is RAID6, not the transient RAID5
it passes through.

**`parted` writing a partition table doesn't reliably make the kernel
expose the new partition device node on its own** — especially
unreliable for loop devices. This isn't a timing race (confirmed by
manually running commands one at a time and still hitting it); it's a
genuinely missing rescan. `partprobe <disk>` after every `mkpart`
forces it, and a trailing `udevadm settle` before `mdadm --create`
flushes the resulting udev events so every partition device node is
guaranteed to exist by the time it's referenced.

Partition device paths are assumed to follow the `<disk-by-id>-partN`
udev convention. Filesystem creation is deliberately **not** emitted —
per the PRD, the planner/executor are filesystem-agnostic; `mkfs` is
left as a manual, optional last step for the user.

Tier array names are `/dev/md/{poolName}-tier<N>` — derived from the
pool's own name (`POST /plan`'s `poolName`, defaulting to
`diskweaver-pool`), not a fixed `diskweaver-tier` prefix, specifically
so two differently-named pools on one host don't collide on the same
`mdadm` array name (`vgcreate`/`vgextend`'s volume group name is the
same `poolName`, so it's already unique per pool; `data` as the LV name
never collides either, since an LV's full identity is
`{poolName}/{volumeName}`). `POST /plan` also checks the requested
`poolName` against `GET /pools` up front and refuses (`400 Bad
Request`) if a DiskWeaver-owned pool with that exact name already
exists — this catches the collision before a single `parted`/`mdadm`
command runs, rather than failing partway through `Execute` on
`mdadm: Array name ... is in use already` the way an unnamed/colliding
plan used to.

## Planning scenarios

Fresh pool creation (`CommandPlanner.Build`) is only one of the situations
the executor needs to plan for. Brainstorming the rest surfaced a
non-obvious invariant and a genuinely hard case worth flagging early
rather than discovering in production.

**Discovered invariant: the bottom tier always spans every disk in the
pool.** Its segment boundary is the pool's minimum disk size, which every
disk trivially satisfies (`size >= min size` is always true). So **there
is no such thing as adding a disk that leaves every existing tier
untouched** — the bottom tier's member count changes on every single
addition. In practice this means most "add a disk" plans include at
least one grow-flagged tier (the bottom one) alongside whatever new tier
the addition also creates.

`CommandPlanner.BuildIncremental(ExistingPoolState current, PoolPlan desired)`
handles this by classifying each desired tier against the existing ones:

| desired tier vs. existing tiers | outcome |
|---|---|
| exact match (same disk set, same segment size) | unchanged, no steps |
| existing tier's disks are a proper subset of desired's, same segment size | **grow candidate — automated** (same-level device-count grow or RAID-level migration, see below) |
| no existing tier matches by disk-set/segment-size at all | genuinely new tier — full creation steps, added via `vgextend`/`lvextend` instead of `vgcreate`/`lvcreate` |
| an existing tier matches none of the above once all desired tiers are considered | **orphaned** — throws `InvalidOperationException` rather than guessing |

### Grows are automated — same-level and RAID-level migrations alike

When an existing tier just needs more members, `BuildIncremental`
emits real steps via `BuildGrowSteps`: partition the new disk(s) for
that tier's segment, `mdadm --add <array> <partition>` (adds as a
spare), `mdadm --grow --raid-devices=<N> <array>` (triggers the
reshape), `mdadm --wait <array>` (blocks until the reshape finishes),
then `pvresize <array>` so LVM sees the new size, and a single
`lvextend -l +100%FREE` at the end (shared with any genuinely new
tiers in the same plan). If the desired tier's RAID level differs from
the existing one (e.g. a 2-disk mirror picking up a 3rd disk and
needing to become RAID5, since `m` crossed from `R+1` to `R+2`
members), the same `mdadm --grow` invocation also gets `--level=<X>`
— mdadm supports changing level and device count together in one
reshape, *for the level transitions it actually supports*. This was
validated for real against loop devices, including a same-level
RAID5 grow (3→4→5→6 disks across several sessions) and a
Mirror→RAID5 migration, both confirmed against real
`mdadm --detail`/`vgs`/`lvs` output afterward.

mdadm's level-migration support is narrower than "any level to any
level in one `--grow`", though: a RAID1 array can only migrate away
from RAID1 (to RAID5) while it still has exactly 2 members, and
there's no direct RAID1 → RAID6 reshape at all — attempting one fails
at runtime with mdadm's own `Impossibly level change request for
RAID1` (this was hit for real on an DWR-2 pool's 3-disk mirror tier
growing to 5 disks). `PlanLevelHops` in `CommandPlanner` encodes this:
a 2-disk mirror needing to reach RAID6 is decomposed into two grow+wait
steps (mirror → RAID5, jumping straight to the final device count in
that first hop since the combined change is legal from a 2-disk
source, then RAID5 → RAID6 in place); a mirror that already has 3+
members has no reshape path to RAID5 or RAID6 at all (the RAID1→RAID5
hop itself requires exactly 2 members) and is refused up front with
`InvalidOperationException` rather than emitting a plan mdadm will
reject partway through, after spares have already been added.

**This can make a single `Execute` call take a long time on real
hardware.** `mdadm --wait` blocks for as long as the reshape takes —
seconds on loop devices (which is how this was validated), but
potentially hours on large real arrays. The daemon's `POST
/plan/{id}/execute` / `POST /pools/{poolName}/expand/{id}/execute`
still run synchronously within one HTTP request today (see
daemon-api.md) — a long real-hardware reshape would hold that request
(and the browser tab) open for the whole duration. This is the same
already-flagged "needs background execution + polling" gap noted
elsewhere, just now with a concrete step that would actually trigger
it in practice; not solved in this pass.

Covered by tests in `CommandPlannerIncrementalTests`:

- **Add a disk that also creates a new top tier, growing the bottom
  tier in place** — the new tier's disks get labeled/partitioned/arrayed
  and added via `vgextend`; the bottom tier (same RAID level, more
  members) is grown automatically via `mdadm --add`/`--grow`/`--wait`/
  `pvresize` rather than silently left alone, recreated, or merely
  flagged.
- **Add a disk that reclaims a previously-reserved segment** — the
  existing disk's *already-partitioned* prefix is untouched (no re-label,
  no re-partition); its new partition for the reclaimed range starts
  exactly where the old one ended, tracked via `ExistingPoolState`'s
  per-disk offset/partition-count bookkeeping.
- **Add a disk matching an existing tier's exact size, same RAID
  level** — grown in place automatically (see above), no `vgextend`
  needed since no new PV was created, but `lvextend` still runs since
  the existing array (and its PV) got bigger.
- **Add a disk that migrates an existing tier's RAID level** (e.g.
  mirror → RAID5) — same `mdadm --add`/`--grow`/`--wait`/`pvresize`
  shape, with `--level=<X>` added to the grow command.
- **Add a disk whose size falls strictly between two existing tier
  boundaries** — this inserts a new boundary in the middle of what used
  to be one segment, which would require splitting/resizing a *live*
  array. There's no safe incremental command for this, so it's refused
  outright (`InvalidOperationException` naming the orphaned tier) rather
  than attempting anything. The safe fallback is a fresh pool rebuild.

Scenarios noted but explicitly out of scope for now:

- **Disk replacement** (swap for a larger disk) decomposes into a live
  rebuild (2b/hardware, not yet built) followed by a re-plan that's just
  one of the add-disk cases above.
- **Degraded-pool repair** needs live array-state detection (which
  degraded, which partition needs `mdadm --add`) — that's inventory-like
  work belonging to the Phase 3 daemon's state collector, not the
  planner.
- **Hot spares**, **disk removal/shrink**, and **redundancy-level changes
  on an existing pool** (DWR-1 → DWR-2) are non-goals / deferred, per
  the PRD's open questions. The one exception: completing a
  `RedundancyLevel.None` tier's degraded mirror by adding its missing
  disk is supported (`POST /pools/{poolName}/expand`) — it isn't a
  redundancy-level *change* in the reshape sense, since the tier was
  always architecturally a mirror (just short a member); filling the
  slot is a plain `mdadm --add`, not a reshape. Unlike the grows above,
  this never emits `lvextend`: mdadm reports the same "Array Size" for
  a degraded mirror as a fully-populated one (capacity is set by the
  configured member count, not how many are currently active), so
  filling the slot gains zero free VG extents — an unconditional
  `lvextend -l +100%FREE` here fails outright ("no size change", exit
  5) even though nothing actually went wrong, confirmed live.
  `BuildIncremental` only counts a grow candidate toward the final
  `lvextend` step when it's a real device-count/level-migration grow.
- **Multiple pools on one host** — now supported: `POST /plan` takes a
  `poolName`, tier array names derive from it (`/dev/md/{poolName}-tier<N>`,
  see "Command generation" above), and a colliding name is refused up
  front rather than failing mid-`Execute`. What's still a placeholder:
  the LV name is always `data` inside whichever VG (harmless, since LV
  identity is per-VG), and there's no CLI/Cockpit affordance yet for
  discovering what pool names are already taken beyond reading `GET
  /pools` yourself.

## Resuming and retrying (2b)

`ExecutionRunner.AdvanceOneStep(executionId, kind, plan, journal, runner)`
takes whatever journal has been built up *within the current request* (or
`null` for the first step) and runs exactly the one step past the last one
recorded as `Succeeded`. Comment-only steps (`Command is null`) are marked
`Succeeded` immediately without invoking the runner. A step that fails halts
the loop — later steps stay `Pending` rather than being attempted out of
order, since each step generally depends on the disk-layer changes the
previous ones made. Each `POST .../execute` endpoint loops calling this and
saving after each call until the journal reaches a terminal
(`Succeeded`/`Failed`) status, all synchronously within that one request.

**Deliberately not** used across separate requests: every execute-style
endpoint always starts this loop with `journal = null`, never seeded from
whatever a previous `POST` persisted. A persisted journal is a *record* of a
past attempt, not a trustworthy claim about current reality — trusting it
to decide what to skip is a shadow-config bug (a second, independent copy of
"what state is the system in" that nothing keeps in sync with the one real
source, the actual mdadm/LVM/partition state). A real incident hit this
directly: an `/expand` executed successfully, something external reset the
pool back to its pre-expand state, and re-`POST`ing the same execute
silently replayed the old `Succeeded` journal without running a single
command.

Instead, every execute call re-verifies its own preconditions against
**live state**, unconditionally, every time — `StaleDisks` (are the selected
disks still blank?), `TeardownTargetStillExists` (does the teardown target
still exist?), the pool-fingerprint match plus `EnsureBlank` on added disks
(expand), and the array-membership check (wipe). These ask reality directly,
so they naturally refuse a genuinely-partial retry (a disk that got
partitioned is no longer blank) while correctly allowing a fresh run when
reality has reverted to exactly the state a plan was computed against — that
isn't staleness, it's confirmation that re-running is exactly right. Build's
"already done" case is decided the same way: a pool named `poolName` already
existing in `GetPools()` right now, not a persisted journal.

The journal itself is now a **write-only record of the most recent attempt
only** — `GET /execute/{id}/status` reads it back for operator visibility,
but no execute-style endpoint ever reads one back to decide what to do. Its
`Id` is still `"{planId}-{kind}"` (e.g. `e0e1cfa5-build`), kept for
organizing/naming journal files per operation, not for correctness.

One accepted gap: `POST /pools/{poolName}/teardown`'s own target (the pool,
as seen via `GetPools()`) can vanish from view *before* its own teardown
finishes — `lvremove`/`vgremove` are early steps, `wipefs`/
`mdadm --zero-superblock`/`losetup -d` come after. A teardown that fails
after the VG is gone but before disk-level cleanup finishes can't be cleanly
resumed by this endpoint once the pool disappears from `GetPools()`. This
predates the redesign above and isn't made worse by it; fixing it for real
would need a live-inspection mechanism independent of `GetPools()` (see
"Open items" below).

## Open items / hardening still needed

- **Sector alignment**: byte-precise offsets need to respect the actual
  device's logical/physical sector size, not just assume 1MiB alignment
  is always correct.
- **GPT partition type GUIDs**: currently untyped/default; may want the
  Linux RAID GUID explicitly.
- **mdadm array naming stability**: `/dev/md/{poolName}-tier<N>` is
  collision-free across pools now that it's derived from `poolName`
  rather than a fixed prefix (see "Multiple pools on one host" above).
  `POST /plan` restricts `poolName` to `[A-Za-z0-9][A-Za-z0-9_.-]*`
  (alphanumeric, starting with a letter/digit, only `-`/`_`/`.`
  otherwise) so it can't itself introduce a collision, an invalid LVM/
  mdadm name, or (since it flows into `ExecutionStep.Arguments`, never
  a shell string, but still worth restricting defensively) anything
  that looks like a command flag.
- **Idempotency / re-planning**: if a disk is added later and the plan
  is recomputed, `BuildIncremental` must recognize and leave existing
  tiers/arrays alone — this is about *planning* against real state
  (`IPoolStateSource`), same principle as "Resuming and retrying" above
  (always ask live state, never a persisted record) but a separate
  mechanism (`ClassifyIncremental`'s tier diffing, not the journal). This
  was already sound; the terminal-journal-trust problem above was the
  actual gap, now closed.
- **Teardown target visibility gap**: see "Resuming and retrying" above —
  `POST /pools/{poolName}/teardown` can't cleanly resume a partial teardown
  once its own target pool disappears from `GetPools()` partway through
  (its early steps are what make it disappear). Not fixed here; would need
  live inspection independent of `GetPools()`.
- **Journal retention/cleanup**: `FileJournalStore` never deletes old
  journal files — fine for now, but a long-lived daemon will
  accumulate one file per execution indefinitely.
- **`abort`**: there's no `POST /execute/{id}/abort`. A step that's
  actually running (e.g. `mdadm --create`) can't be safely interrupted
  mid-command anyway; what's missing is a way to mark a journal that
  will never be retried (e.g. the user fixed the problem by hand) as
  terminal instead of perpetually `Failed`-and-retriable.
- **No subprocess timeout/cancellation**: `ProcessStepRunner`/
  `ProcessCommandRunner` drain stdout/stderr concurrently (fixing a
  real pipe-buffer deadlock — see their doc comments), but nothing
  bounds how long a command is allowed to run, and there's no way to
  cancel one already in flight. Not an oversight: `mdadm --wait` is a
  real step (see "Grows are automated" above) that can legitimately
  take hours on a large real array, so no fixed timeout is simultaneously
  short enough to catch a genuinely stuck command and long enough to
  never abort a healthy reshape. Since the daemon's execute endpoints
  already run synchronously within one HTTP request regardless (noted
  above), a real fix needs background execution + polling + the
  `abort` API above, all together — not a timeout bolted onto the
  current synchronous model.

## Multiple logical volumes (thin pools)

By default `Build`/`BuildIncremental` still only ever create one thick LV
named `data` per pool. But a user is free to convert a pool's VG into a
thin pool with several thin volumes on top of it afterward (e.g. one
Btrfs LV for a fileshare, one or two raw LVs as iSCSI backstores — see the
SHR-style layering discussed when this was designed), and DiskWeaver's
discovery and teardown paths have to cope with that real state:

- `ExistingPoolState.VolumeNames` is a list, not a single name. It's
  populated from `lvs -o vg_name,lv_name,pool_lv`, ordered so any thin
  volume (non-empty `pool_lv`) sorts before the thin pool/thick LV it
  depends on.
- `CommandPlanner.BuildTeardownFromExisting` emits an `lvremove` for
  every entry in that order, before `vgremove` — removing a thin pool
  while a thin volume still references it fails, so the order matters.
- `CommandPlanner.BuildIncremental`'s auto-`lvextend` on expand only
  fires when there's exactly one LV (the plain `data` case). With more
  than one, there's no way to tell which LV is the thin pool that should
  absorb the newly-added space, so this is left as an explicit comment
  step (`lvextend -l +100%FREE <vg>/<thin-pool-lv>`) rather than guessed
  at.

### Thin pool sizing (`Build(..., thinProvisioned: true)`)

`CommandPlanner.Build` can create the pool's VG with a thin pool
(`<poolName>-thin-pool`) instead of a thick LV, sized to
`100 - ThinPoolHeadroomPercent` (10) percent of the VG, plus one thin
volume named `data` with virtual size `100%POOL` (i.e. not overcommitted
by default — same "one volume, ready to format, using all the pool has"
result as the non-thin default, just backed by a thin pool so headroom
exists and further thin volumes can be carved later).

The headroom is deliberate: an LVM thin pool at literally 100% of its VG
has no free extents left for its own metadata growth, and a thin pool
that fills up puts every thin LV on it at risk of write stalls/failures —
see the pvs/lvs `data%`/`metadata%` fields, which is what real monitoring
of a thin pool needs to watch (not yet wired into DiskWeaver's own
`/pools` reporting).

This is opt-in and not yet wired into the daemon's `/plan`/`/build` API,
the CLI, or Cockpit — today it's a `CommandPlanner`-level building block
only. Also not yet done: `BuildIncremental` growing a DiskWeaver-created
thin pool + its `data` volume together on expand (it currently falls
into the generic ">1 LV, leave a comment" path above, same as any
manually-thin-provisioned pool) — the `lvextend -l N%FREE`/`-l 100%POOL`
sequence needed for that hasn't been verified against a real LVM install
yet, so it's deliberately not automated. Individual *additional* thin
volumes on top of the pool (fileshare/iSCSI/etc.) remain intentionally
out of scope regardless — filesystem choice, LUN sizing, and snapshot
policy are workload decisions DiskWeaver has no visibility into.
