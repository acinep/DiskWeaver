# Planner algorithm: segment tiering

Status: Draft v0.1 — companion to [PRD.md](PRD.md) §7/§8, Phase 1 scope.

This document specifies the core algorithm the planner uses to turn a set
of disks (arbitrary, mixed sizes) plus a redundancy target into a set of
mdadm arrays and their usable capacity — the HHR tiering behavior. It's
deliberately kept separate from the PRD since it's implementation-level
detail that will map directly onto planner unit tests.

## Inputs

- A list of disks, each with a size in bytes.
- A redundancy target `R`: the number of simultaneous disk failures the
  resulting pool must tolerate. `R=1` ("DWR-1"), `R=2` ("DWR-2").

## Step 1 — find size breakpoints

Sort disks ascending by size. Take the distinct sizes present:
`s1 < s2 < ... < sk` (k ≤ number of disks, k = 1 if all disks are the
same size). These define segment boundaries `(0,s1], (s1,s2], ...,
(s(k-1),sk]`.

## Step 2 — assign disks to segments

Segment `j`, spanning `(s(j-1), sj]`, is only present on disks whose size
`>= sj` (any such disk can supply a slice of exactly `sj - s(j-1)` bytes
at that offset). Segment `j`'s disk count is `m_j`.

## Step 3 — pick an array per segment (unified rule)

Given a segment with `m_j` disks and the pool's redundancy target `R`:

| condition | array | usable capacity |
|---|---|---|
| `m_j < R+1` | none — can't tolerate R failures with fewer than R+1 disks | **excluded**: raw bytes reserved/unallocated, optionally offered as a separate unprotected volume (never silently added to the protected pool) |
| `m_j == R+1` | `(R+1)`-way mirror (RAID1) | `1 × segment_size` |
| `m_j >= R+2` | parity RAID: RAID5 if `R=1`, RAID6 if `R=2` | `(m_j - R) × segment_size` |

Rationale: the pool's overall guarantee is the *minimum* over all
included segments, so each segment should use the cheapest (highest
capacity) array that still meets `R` — never more redundancy than
requested, never less.

- `R=1, m_j=2` → 2-way mirror, `1×`.
- `R=1, m_j>=3` → RAID5, `(m_j-1)×`. (A 3-way+ mirror is never used here
  — it would give the same 1-disk guarantee at less capacity than RAID5.)
- `R=2, m_j<=2` → excluded (2 disks can supply at most 1 redundant copy
  between them — 2-disk tolerance is impossible regardless of RAID level).
- `R=2, m_j=3` → 3-way mirror, `1×`. (RAID6 needs `m_j>=4`; the mirror is
  the only way to include this segment at all under R=2.)
- `R=2, m_j>=4` → RAID6, `(m_j-2)×`.

## Step 4 — sum

- Pool capacity = sum of usable capacity across all included segments.
- Reserved/unallocated = sum of raw bytes in excluded segments.

**`PoolPlan.PoolCapacityBytes` is a theoretical upper bound, not a
byte-exact final guarantee.** It's computed before any RAID/LVM/
filesystem metadata overhead: mdadm's per-array write-intent bitmap
(see [execution.md](execution.md)) and superblock, each LVM PV's
metadata area and extent-boundary rounding, and the filesystem's own
overhead (inode tables, journal, and e.g. ext4's default 5%
reserved-for-root blocks) all further reduce real usable space.
Confirmed against real hardware: a planned 10,731,126,784-byte pool
came out to a 10,712,252,416-byte LV — 18 MiB less, fully accounted for
by mdadm bitmap + LVM PV/extent overhead (see git history/session notes
around the Phase 2a hardware validation for the full arithmetic) — and
then further reduced by ext4 overhead and its 5% root reservation at
the filesystem layer. None of that is a planner bug; it's real overhead
this algorithm deliberately doesn't attempt to model exactly, since it
varies by mdadm/LVM/filesystem choices made below this layer.

## Step 5 — emit domain objects

Each segment becomes a **Tier** (§7 of the PRD): `{ sizeBytes, diskIds,
raidLevel, usableBytes }`. Each Tier becomes one **Array**. All Arrays'
resulting block devices become PVs in the **Pool**'s single VG.

Partitioning implication: each disk gets one partition per segment it
qualifies for, laid out sequentially. A disk that's uniquely largest and
whose top segment gets excluded simply has that trailing range left
unpartitioned (reserved, visible in the plan, reclaimed automatically
once a matching disk is added and the plan is recomputed).

## Worked examples

**Disks 2,2,4,4,4 TB, R=1:**
Breakpoints 2,4. Segment A `(0,2]`, m=5 → RAID5, `4×2=8TB`. Segment B
`(2,4]`, m=3 → RAID5, `2×2=4TB`. **Pool = 12TB** (vs. 8TB for a naive
single 5-disk RAID5 capped at the 2TB minimum).

**Disks 2,2,2,6 TB, R=1:**
Breakpoints 2,6. Segment A `(0,2]`, m=4 → RAID5, `3×2=6TB`. Segment B
`(2,6]`, m=1 → excluded (`1 < R+1=2`). **Pool = 6TB, 4TB reserved**
until a second large disk is added.

**Disks 2,2,2,2,6 TB, R=2:**
Breakpoints 2,6. Segment A `(0,2]`, m=5 → RAID6, `3×2=6TB`. Segment B
`(2,6]`, m=1 → excluded (`1 < R+1=3`). **Pool = 6TB, 4TB reserved.**

**Disks 2,2,2,6 TB, R=2:**
Breakpoints 2,6. Segment A `(0,2]`, m=4 → RAID6, `2×2=4TB`. Segment B
`(2,6]`, m=1 → excluded. **Pool = 4TB, 4TB reserved.**

**Disks 2,2,2 TB, R=2:**
One breakpoint. Segment `(0,2]`, m=3 = `R+1` → 3-way mirror, `1×2=2TB`.
**Pool = 2TB** (vs. 0 if segments below the RAID6 minimum were always
excluded outright).

## Open items for implementation

- Confirm mdadm actually supports arbitrary N-way RAID1 (`--level=1
  --raid-devices=N`) for N>2 on the target distros — expected yes, but
  needs a smoke test in Phase 2.
- Decide the exact shape of the "reserved" leftover reporting in the
  Plan object (bytes + which disk(s) + what additional disk size would
  reclaim it) — needed for the Cockpit UI later but should be modeled
  now since it's part of the planner's output, not an afterthought.
- ~~Optional unprotected-volume offer~~ — implemented, but as an
  explicit `RedundancyLevel.None` choice rather than an offer
  constructed from the § Step 3 exclusion/reserved case: picking "no
  protection" makes `TieringPlanner.Plan` bypass the shared
  boundary-grouping algorithm entirely and return one independent
  single-disk `Tier` per disk (`RaidLevel.Mirror`). Each is built as a
  degraded 2-slot RAID1 (`mdadm --create --level=1 --raid-devices=2
  <partition> missing`), not a genuine single-member array — a genuine
  single-member (`--raid-devices=1 --force`) array was tried instead
  specifically to avoid ambiguity with a real Dwr1/Dwr2 mirror degraded
  by an actual disk failure (both being "2 configured slots, 1 real
  member" under the degraded-2-slot representation), but was confirmed
  broken live: `mdadm --add` refuses to add the first spare to a true
  1-device array ("cannot load array metadata"), even though `mdadm
  --create --raid-devices=1 --force` itself succeeds — the array simply
  can't be grown afterward, which defeats the entire point of building
  an unprotected tier. The disambiguation problem is instead solved with
  a durable **LVM PV tag** (`pvchange --addtag diskweaver-unprotected`,
  applied right after `pvcreate`): `MdadmLvmPoolStateSource` reads it back
  via `pvs -o pv_tags` into `ExistingTier.IsUnprotectedByDesign`, and
  `InferRedundancy`/`ProtectionPlanner` key off that field instead of
  inferring intent from array shape. Growing an unprotected tier into a
  real mirror later is then a plain `mdadm --add` (no `--grow` — the slot
  count was already configured as 2 at creation, so filling it doesn't
  change the array's raid-devices count). See `RedundancyLevel.None`,
  `Tier.DegradedSlots`'s doc, `ExistingTier.IsUnprotectedByDesign`, and
  daemon-api.md's `POST /plan`/`POST /pools/{poolName}/expand` redundancy
  parameter.
## Expand tenets

Three tenets govern `POST /pools/{poolName}/expand` (see daemon-api.md for
the full request/response shape):

1. A fresh build (`POST /plan`) always picks the max-capacity layout for
   the requested redundancy — the boundary-grouping algorithm above.
2. Given a pool, new disks, and a target protection level, expand returns
   **0, 1, or 2 candidate plans** — one that adds protection, one that
   adds space (`ExpansionOptionsPlanner`, `DiskWeaver.Executor`) — rather
   than requiring the caller to already know which internal mechanism
   (completing a degraded tier, raising the pool's redundancy, growing an
   existing tier's member count) fits this pool's current shape. Either
   can be absent: a disk too small to help any tier at all (the "hot
   spare" case) yields both absent. The protection candidate specifically
   only exists for a genuine 0→1 or 1→2 increase in something that
   already existed (completing a degraded/unprotected tier, or raising
   the pool's redundancy level) — matching this same HHR model. Grouping
   brand-new disks into their own protected tier when nothing existing
   needed completing doesn't count: those disks were never part of the
   pool before, so there's nothing of theirs to "increase the protection
   of" — that's just an alternate (and less space-efficient) capacity
   layout, which the space candidate already covers.
3. A from-scratch rebuild being more space-efficient than either
   candidate is always a "btw" note (`HypotheticalRebuildCapacityBytes`),
   never a blocking error and never a third option.

This replaced an earlier design with four separate, mutually-exclusive
request modes (`redundancy`, `autoProtect`, `targetArrayDevice`,
`growIndependently`) plus a silent server-side fallback between two of
them — each added to fix one live bug in isolation, but the accretion
left the caller needing to already know which mode applied before making
a request. `targetArrayDevice` and an explicit `redundancy` remain as
advanced/manual escape hatches (bypassing the two-option computation
entirely, always returning exactly one plan) for the precision cases they
were built for — completing one specific named tier, or deliberately
building an unprotected tier — but the common "just add these disks"
path is now always the two-candidate computation below.

- **Can't merge independent tiers incrementally**: upgrading a JBOD
  pool's several independent single-disk tiers to a shared DWR-1/DWR-2
  tier would require merging multiple already-built mdadm arrays into
  one — not a reshape mdadm supports (it would need real data
  migration). `CommandPlanner.ClassifyIncremental` detects this (several
  independent existing tiers each a proper subset of one desired tier)
  and refuses with a message naming the specific arrays, pointing at
  tearing down and rebuilding fresh with the desired redundancy instead
  — the same "not supported, fresh pool rebuild required" refusal
  `execution.md` already documents for the analogous split-tier case.
  Real incident: a pool built from 2 independent `RedundancyLevel.None`
  tiers, each later completed into its own protected 2-disk mirror via
  `autoProtect`, then hit this refusal on a plain (redundancy-inferred)
  expand with more disks — the two mirrors are legitimately independent
  arrays, and a blanket redundancy request wants to unify them into one
  shared tier. The alternative that doesn't require a rebuild — and is
  now the default expand path's own "space" candidate, not a separate
  flag the caller has to ask for — is `IndependentGrowPlanner.PlanGrowth`
  (`DiskWeaver.Executor`): grows each existing tier in place instead,
  splitting the offered disks across all of them as evenly as possible
  (a large enough disk can split across more than one tier's growth, the
  same spare-capacity-reuse pattern as the protection candidate's) — less
  space-efficient than a fresh rebuild across the full disk set would be
  (independent tiers can't share parity/spare capacity the way one bigger
  shared array could), but doesn't touch existing data. An explicit
  `redundancy` request that itself conflicts still gets the real error
  (`ThrowIfMergeConflicts`), since that's a caller's specific ask that
  genuinely can't be honored — only the *default* two-option computation
  treats a merge conflict as "no protection candidate available" rather
  than an error. See daemon-api.md's `POST /pools/{poolName}/expand`.
- **Completing independent unprotected/degraded tiers with a new disk**:
  the actual point of building an unprotected tier is completing it into
  a real mirror later, which *is* supported without a rebuild — it's
  just filling the array's already-configured missing slot (`mdadm --add`,
  no `--grow` needed since the raid-devices count isn't changing), not a
  merge, since each independent tier stays independent (never
  combined with any other tier). Two ways to do it, both producing an
  ordinary `desired` `PoolPlan` with the target tier's own `DiskIds`
  extended (see daemon-api.md's `POST /pools/{poolName}/expand`): the
  explicit/manual `targetArrayDevice` mode (one tier at a time — used
  repeatedly to split one physical disk across several tiers), and the
  default expand path's own "protection" candidate
  (`ProtectionPlanner.PlanAutoProtect` greedily matches every eligible
  tier against the offered disks' capacity, splitting a larger disk
  across more than one same-size tier in a single request when it's big
  enough — e.g. one 4TB disk completing two independent 2TB unprotected
  tiers into two separate 2-disk mirrors, confirmed via
  `CommandPlanner.BuildIncremental`'s already-shared partition-offset
  tracking needing zero changes to support one disk appearing in two
  different tiers' desired `DiskIds` within one call).
