# DiskWeaver — Product Requirements Document

Status: Draft v0.1
Owner: Noel Phillips

## 1. Summary

DiskWeaver brings Heterogeneous Hybrid RAID (HHR)-style storage pooling to
mainline Linux. It lets a user combine disks of mixed, arbitrary sizes into
a single redundant, expandable storage pool — without wasting capacity to
the smallest disk in the set, and without requiring manual mdadm/LVM
wrangling.

DiskWeaver is a native-AOT compiled C#/.NET application composed of:

- A **planner** that inspects current disk/array/volume-group state and
  produces a declarative, human-reviewable plan for any requested change
  (create pool, add disk, replace disk, grow pool, repair).
- An **executor** that carries out an approved plan by driving `mdadm`,
  LVM2 CLI tools (`pvcreate`, `vgextend`, `lvextend`, ...), and partitioning
  tools (`sfdisk`/`parted`), journaling progress so a crash mid-execution
  is recoverable.
- A **daemon** (root, systemd-managed) that owns all disk state and exposes
  the planner/executor over a local Unix socket API.
- A **Cockpit plugin** (thin HTML/JS client) and a **CLI** as two interfaces
  to the same daemon API.

## 2. Background: how HHR works

Heterogeneous Hybrid RAID solves the "mixed disk size" problem in classic
RAID by, conceptually:

1. Splitting every physical disk into partitions such that disks are
   grouped into same-size "chunks" across all members.
2. Building one mdadm RAID array per same-size chunk group (so a set of
   4TB, 4TB, 2TB, 2TB disks becomes a 2-disk-chunk array using all 4
   partitions at the 2TB size, plus a 2-disk-chunk array using the
   remaining 2TB from just the two 4TB disks).
3. Combining those arrays into a single LVM volume group / logical volume,
   presented as one pool.
4. Preserving single- or dual-disk fault tolerance across the whole pool
   (equivalent to RAID5/RAID6 semantics) despite the underlying
   heterogeneous array structure.

This is the behavior DiskWeaver reimplements on stock Linux using
`mdadm` + LVM2, rather than shipping a custom RAID/volume manager.

## 3. Goals

- Create a new HHR-style pool from 2+ disks of arbitrary/mixed sizes with
  single-disk (DWR-1) or dual-disk (DWR-2) fault tolerance.
- Expand an existing pool by adding a disk, or by replacing a disk with a
  larger one, and grow usable capacity accordingly — online, without data
  loss.
- Detect and guide recovery from a degraded/failed disk.
- Present a clear, reviewable plan before any destructive/risky operation,
  and execute it safely with resumable state.
- Ship as a Cockpit plugin so it's usable from a browser on a headless
  NAS-like box, plus a CLI for scripting/headless use.
- Be distributable as a single native-AOT binary (daemon) with minimal
  runtime dependencies beyond mdadm/lvm2/util-linux.

## 4. Non-goals (v1)

- Btrfs/ZFS-style checksumming or self-healing filesystem semantics —
  DiskWeaver manages block-layer redundancy (mdadm/LVM), not the
  filesystem on top. The planner is filesystem-agnostic: it plans
  disks → slices → arrays → volume group → logical volume, and
  `mkfs` (if run at all) is a trivial optional final step, not a
  decision that feeds back into planning.
- Network/remote storage (iSCSI targets, distributed pools).
- Migrating an *existing*, non-DiskWeaver-managed mdadm/LVM layout into
  management automatically (may be a stretch goal — see §9).
- A GUI beyond the Cockpit plugin (no separate Electron/desktop app).
- Multi-node / clustering support.

## 4a. Phased delivery

- **Phase 1 (current focus): Planner only.** Given an inventory of disks
  (real or fixture/mocked) and a requested intent (create pool, add
  disk, replace disk), produce a correct, serializable `Plan` — no
  execution, no daemon, no Cockpit plugin, no root privileges required.
  This is where the HHR tiering algorithm gets built and tested in
  isolation, with unit tests driven by disk-size fixtures rather than
  real hardware. Ships as a library + CLI command that prints/dumps a
  Plan (e.g. as JSON) for inspection.
- **Phase 2: Executor.** Take an approved `PoolPlan` and turn it into the
  actual `mdadm`/partitioning/LVM2 commands needed to build it — split
  into two sub-phases using the same pure-transform / OS-specific-backend
  split that Phase 1's inventory sourcing used (`LsblkOutputParser` vs
  `LsblkDiskInventorySource`):
  - **2a — Command generation + script emission (current focus).** A
    pure, platform-independent step that turns a `PoolPlan` into an
    ordered list of `ExecutionStep`s (partition, mdadm create, pvcreate,
    vgcreate, lvcreate) and renders them as a reviewable shell script.
    No root privileges, no real disk access, fully testable on Windows —
    same reasoning as why the lsblk *parser* could be built and tested
    off-target while the lsblk *process invocation* couldn't.
  - **2b — Invocation + journal.** Actually run those steps as
    subprocesses against real disks, with the journaled, resumable
    execution described in §6/§8.5 (plan-hash validation, per-step
    journal persistence, crash recovery). Linux-only at runtime, needs
    root, needs real hardware (or a Linux VM) to validate.
- **Phase 3: Daemon + transport.** Wrap planner+executor in the
  long-running root `diskweaverd` service exposed over a Unix socket.
  Built: `DiskWeaver.Daemon` (Kestrel, Native AOT, Unix socket via
  `DISKWEAVER_SOCKET`) — see daemon-api.md for the full endpoint list,
  including pool expansion (`POST /pools/{poolName}/expand`) and
  teardown (`POST /pools/{poolName}/teardown`) driven by real on-disk
  state, not just fresh-build plans.
- **Phase 4: Cockpit plugin.** Thin HTML/JS client on top of the
  daemon API. Built and validated against a real Cockpit instance (see
  cockpit-plugin.md): create/expand/teardown a pool, view
  build/teardown scripts, and watch the execution journal, all through
  the browser, confirmed end-to-end against real `mdadm`/LVM state.
  **Not yet done**: the CLI is still a direct client of the planner
  library (not yet repointed to call the daemon over the socket) — it
  and the Cockpit plugin currently don't share a backend, contrary to
  this phase's original intent.

Sections below describe the eventual full architecture; only §7 and the
planning-related parts of §8 are in scope for Phase 1. Phases 2-4 are
now built and validated (see execution.md, daemon-api.md,
cockpit-plugin.md for what's actually true today, since those docs are
kept current turn-by-turn while this PRD stays at the plan/intent
level).

## 5. Users & use cases

Primary user: a self-hoster / homelab operator running bare-metal Linux
(Debian/Ubuntu/Fedora-family) who wants NAS-appliance-like "just add a disk"
storage flexibility without a proprietary NAS OS.

Key use cases:

1. **Fresh pool creation** — user has N blank/wiped disks of varying
   sizes, wants one redundant volume, mounted and ready.
2. **Capacity expansion** — user adds a new disk (possibly a different
   size than existing members) to grow the pool.
3. **Disk replacement / upgrade** — user swaps a smaller disk for a
   larger one to grow effective capacity.
4. **Degraded pool recovery** — a disk fails; DiskWeaver surfaces
   degraded state and walks the user through replacement/rebuild.
5. **Status / observability** — at-a-glance health of arrays, volume
   group, capacity used/available, rebuild progress.

## 6. Architecture

```
┌─────────────────────┐      ┌──────────────────────┐
│   Cockpit plugin     │      │   diskweaver CLI      │
│   (HTML/JS, thin)    │      │   (native-AOT exe)    │
└──────────┬───────────┘      └──────────┬────────────┘
           │ cockpit http-stream1 bridge  │
           │            (unix socket, JSON-RPC/HTTP)     │
           └───────────────┬──────────────┘
                            ▼
                 ┌─────────────────────┐
                 │  diskweaverd (root)  │
                 │  systemd service      │
                 │  native-AOT daemon    │
                 ├─────────────────────┤
                 │ State/Inventory       │  udev, sysfs, lsblk --json,
                 │ collector             │  mdadm --detail, lvs/vgs --reportformat json
                 ├─────────────────────┤
                 │ Planner               │  pure, side-effect-free;
                 │                       │  current state + intent -> Plan
                 ├─────────────────────┤
                 │ Executor              │  runs Plan step-by-step,
                 │ + journal             │  writes journal before/after
                 │                       │  each step for crash recovery
                 ├─────────────────────┤
                 │ Command drivers       │  mdadm, sfdisk/parted,
                 │                       │  pvcreate/vgextend/lvextend, mkfs
                 └─────────────────────┘
```

Key design decisions (confirmed for v1):

- **Command execution**: shell out to `mdadm`, LVM2, and `util-linux`
  CLI tools as subprocesses, parsing `--export`/JSON output where
  available (e.g. `lsblk --json`, `mdadm --detail --export`,
  `vgs/lvs --reportformat json`). Chosen over a libblockdev/udisks2
  D-Bus binding for simplicity, transparency (every action maps to a
  loggable, reproducible shell command), and parity with how DSM itself
  operates.
- **Daemon + transport**: a single long-running root daemon
  (`diskweaverd`) is the only process that touches disks. It exposes an
  HTTP+JSON API over a Unix domain socket (settled — see daemon-api.md's
  "Transport decision"). The Cockpit plugin talks to it via Cockpit's
  `cockpit.http()`; the CLI is intended to talk to it directly over the
  same socket, though that repointing hasn't happened yet (see §4a).
  Chosen over "Cockpit spawns CLI per action" so multi-step, long-running
  operations (rebuilds, growth) have a persistent process to track
  progress/state and so the CLI and Cockpit UI share identical behavior.
- **Safety model**: every mutating request is a two-phase
  `Plan` → `Execute` call. `Plan` is pure/read-only and returns a
  serializable `Plan` object (ordered list of steps, each with the exact
  command line, purpose, and risk annotation, plus a summary diff of
  before/after disk layout and capacity). `Execute` requires the plan's
  id/hash back (guards against stale plans if state changed) and runs
  steps sequentially, persisting a journal entry per step so a crash or
  reboot mid-execution can be inspected and resumed or safely aborted.

## 7. Core concepts / domain model (draft)

- **Disk** — a physical block device (`/dev/sdX`), identified stably by
  `/dev/disk/by-id`.
- **Slice** — a partition on a Disk sized to match a chunk-size tier
  shared across multiple disks.
- **Tier** — a group of same-size Slices, one per participating Disk,
  backing a single mdadm array.
- **Array** — an mdadm RAID device (RAID1/5/6 depending on redundancy
  level and member count) built from one Tier's Slices.
- **Pool** — the LVM Volume Group spanning all Arrays' Physical Volumes.
- **Volume** — a Logical Volume carved from the Pool (v1: likely one
  LV = the whole Pool, minus reserve; multiple LVs may be a later
  feature).
- **Plan** — an ordered, reviewable list of Steps to move current state
  to desired state.
- **Step** — one atomic action (e.g. "create partition sdb1 2048s-4.2TB",
  "mdadm --create /dev/md1 --level=5 --raid-devices=3 ...").
- **Journal** — persisted record of Plan execution progress.

## 8. Functional requirements

### 8.1 Inventory / state collection
- Enumerate block devices, existing partitions, mdadm arrays, LVM PVs/VGs/LVs.
- Detect disks already in use / foreign arrays and exclude/warn.
- Stable disk identity via `/dev/disk/by-id` (never raw `/dev/sdX`) for
  all persisted state and generated commands.

### 8.2 Pool creation
- Input: set of disks, redundancy level (None / DWR-1 / DWR-2), optional
  filesystem choice for initial volume. "None" builds each disk as its
  own independent, unprotected tier — a degraded 2-slot RAID1 mirror
  that a later expand can complete with a second disk, rather than a
  bare partition (see algorithm.md's `RedundancyLevel.None`).
- Output: a Plan detailing partition layout per disk (tiers), array(s) to
  create, PV/VG/LV creation, optional mkfs.
- Compute usable capacity per the HHR tiering algorithm and show it
  up front (before execution) so the user knows what they'll get.

### 8.3 Expansion (add disk / replace disk)
- Add disk: recompute tiers, grow/migrate existing arrays in place
  (`mdadm --add`/`--grow`, including RAID-level migrations like mirror
  → RAID5) or create new ones, `vgextend`/`pvresize`, then `lvextend`.
  Built and validated for real — see execution.md. **Filesystem grow is
  deliberately NOT automated** (unlike this section's original wording)
  — per the planner/executor's filesystem-agnostic design (§4 Non-goals),
  resizing the filesystem on top (e.g. `resize2fs`) is left as a manual
  step, same as `mkfs` itself.
- Replace disk: guided flow — mark disk for removal, wait for/trigger
  rebuild onto new disk, then treat as add-disk if new disk is larger.
  Not yet built.

### 8.4 Health & repair
- Surface array/pool state (clean/degraded/rebuilding/failed) and map
  degraded arrays back to the physical disk(s) responsible in
  user-facing terms.
- Guided replace-failed-disk flow.

### 8.5 Execution safety
- Plan objects are content-addressed (hash of inputs) so re-executing
  identical inputs is idempotent. Built (`PlanCache`/`ExecutionPlanCache`
  — see daemon-api.md). **Not yet built**: rejecting a plan whose inputs
  no longer match current disk state (e.g. a disk was unplugged since
  planning) — today a stale plan just gets re-executed against whatever
  is actually there.
- Journal persisted to disk after each step (not before — a step is
  recorded once its outcome is known); resuming after a crash is the
  same operation as running for the first time (see execution.md's
  "Resuming and retrying"). **Not yet built**: no automatic surfacing of
  in-progress/failed plans on daemon restart — resuming requires
  re-`POST`ing the same execute call yourself.
- Explicit confirmation required for any step that destroys existing
  data — built in the Cockpit UI via `window.confirm()` before any
  Execute/Teardown call (e.g. `mdadm --create`); `mkfs` is a manual step
  the tool never runs at all (filesystem-agnostic by design).

### 8.6 Interfaces
- CLI: still a direct client of the planner library (`DiskWeaver.Cli`),
  not yet repointed to the daemon — see §4a. Current commands:
  `diskweaver inventory`, `diskweaver plan --disks ... --redundancy dwr1
  --script ... --teardown-script ...`, `diskweaver testkit`.
- Cockpit plugin: built (see cockpit-plugin.md) — disk picker with
  create/expand target selection, capacity preview (achieved vs. full
  end-state), script preview, execution journal view, per-pool
  teardown. Health/degraded-pool dashboard not yet built (§8.4 not
  started).
- The Cockpit plugin is a pure client of the daemon's socket API; the
  CLI is not yet, so CLI and Cockpit UI can currently drift in behavior
  contrary to this section's original intent (see §4a).

## 9. Open questions / stretch goals

- Import/adopt an existing hand-built mdadm+LVM layout into DiskWeaver's
  model without destroying data — needed for users migrating from a
  manual setup.
- Multiple Logical Volumes per Pool (v1 assumes one).
- SMART monitoring integration to proactively flag failing disks before
  mdadm reports degraded.
- Notification channels (email/webhook) for degraded/rebuild events.
- Encryption (LUKS) integration in the layer stack.
- Filesystem choice/support matrix (ext4 vs btrfs vs xfs) for the
  initial volume — btrfs would give bitrot protection but adds
  complexity; needs a decision.
- Packaging/distribution: systemd unit + Cockpit package layout, target
  distros for v1 (Debian/Ubuntu first?).

## 10. Success criteria (v1)

- A user with 3+ mixed-size disks can create a redundant pool via the
  Cockpit plugin in under 5 minutes, without touching a terminal. **Met**
  — validated through a real Cockpit session against real loop devices
  (build, expand across multiple rounds including a RAID-level
  migration, teardown).
- Adding a disk to grow the pool is a single guided flow with an
  accurate capacity preview before commit. **Met** for the automated
  cases (same-level grow, RAID-level migration, new tier); no guided
  flow exists yet for the disk-replacement case (§8.3).
- A simulated disk failure (fail + remove a member) is clearly surfaced
  as degraded, and guided replacement brings the pool back to healthy.
  Not yet built (§8.4).
- No operation destroys data without an explicit, plan-reviewed
  confirmation step. **Met** in the Cockpit UI (`window.confirm()`
  before every Execute/Teardown); not yet true for direct daemon API
  callers (e.g. `curl`), which have no such gate — that's expected for
  a scriptable API, but worth being clear the guarantee is a UI-layer
  one, not an API-layer one.
