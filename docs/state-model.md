# State model: what gets persisted vs. re-derived

Status: Draft v0.1 — companion to [daemon-api.md](daemon-api.md) and
[execution.md](execution.md)'s journal, addressing "how do we track a
pool once it's provisioned."

## Principle

**Physical reality (mdadm superblocks, LVM metadata, partition tables)
is the only authoritative source of truth for what exists.** DiskWeaver
should never maintain a separate database of "what pools/arrays exist"
that could drift out of sync with reality — that's a classic infra-tool
failure mode (the tool's records say one thing, the hardware says
another, and now every operation is guessing which to trust). If the
daemon crashes and restarts, it should be able to fully reconstruct
`ExistingPoolState` for every pool on the host by querying `mdadm
--detail`, `pvs`/`vgs`/`lvs`, and `lsblk` fresh — never by reading its
own prior notes about what it thinks is there.

This means most of what you'd instinctively reach for a config file for
is actually already stored by the tools themselves and should just be
queried, not duplicated:

| Question | Where the answer already lives |
|---|---|
| What arrays exist, which disks/partitions, what RAID level | `mdadm --detail --export` per array |
| What PVs/VGs/LVs exist, sizes, which PV backs which VG | `pvs`/`vgs`/`lvs --reportformat json` |
| What disks exist, sizes, partition layout | `lsblk --json` |

## The one real gap: ownership

mdadm/LVM don't have a native "created by DiskWeaver" flag. Two options,
not mutually exclusive:

- **Naming convention**: `{poolName}-tier<N>` (`poolName` defaults to
  `diskweaver-pool`, but is caller-chosen -- see execution.md's
  "Multiple pools on one host"). Simple, human-readable, but relies on
  nobody renaming things -- and a name alone doesn't distinguish a
  DiskWeaver-built VG from an unrelated one a human happened to name
  the same thing, so it's not trusted as the authoritative signal.
- **LVM tags** (what's actually authoritative): LVM natively supports
  arbitrary string tags on PVs/VGs/LVs. `CommandPlanner.Build`'s
  `vgcreate` step passes `--addtag diskweaver-managed`, so every VG
  DiskWeaver creates is tagged at creation time — this is the tool's own
  extensible metadata mechanism, not a separate file, and it's queryable
  directly (`vgs -o vg_tags`). `MdadmLvmPoolStateSource.GetPools()`
  queries `vgs --reportformat json -o vg_name,vg_tags` first and only
  builds `ExistingPoolState`s for VGs carrying that tag — an untagged VG
  (manual mdadm+LVM layout, or anything DiskWeaver didn't build) is
  never surfaced via `GET /pools`, and can't be torn down or expanded
  through the daemon, no matter what it's named.

mdadm doesn't have an equivalent tagging mechanism, so array-level
ownership within a tagged pool still relies on convention (the
`{poolName}-tier<N>` name) plus PV membership in a tagged VG — but since
membership in that VG is now gated on the tag rather than the VG's name,
a stray untagged array can no longer masquerade as part of a
DiskWeaver pool.

## What genuinely needs persisting

Two categories, with very different risk profiles:

1. **The Phase 2b execution journal — safety-critical, required.**
   Once real invocation exists, a crash mid-execution needs to be
   recoverable: which steps of an approved plan already ran, which
   didn't, so the daemon can resume or safely report "this plan is
   half-applied, here's exactly where it stopped" rather than guessing
   or blindly re-running everything (which could re-partition an
   already-partitioned disk). This is genuinely not derivable from
   hardware state alone after a crash — you can inspect what *exists*
   but not unambiguously reconstruct *which planned step produced it*
   partway through a multi-step operation. Built: `ExecutionJournal`
   (`DiskWeaver.Executor`), persisted one JSON file per execution id by
   `FileJournalStore` under `/var/lib/diskweaver/journal/`. Resuming
   after a crash is the same operation as running for the first time —
   see execution.md's "Resuming and retrying" section.

2. **User-facing metadata — optional, not safety-critical.** Things
   like a friendly pool name ("Media Storage" instead of
   `diskweaver-pool`), notification preferences, or a hot-spare
   designation (PRD open question). If this file is lost or corrupted,
   the daemon should degrade gracefully — fall back to showing the raw
   `diskweaver-pool` name, no notifications configured, no spare — never
   refuse to operate or misreport what's actually on disk. This is the
   right test for what belongs in this category: **if losing the file
   would make DiskWeaver do something unsafe, it doesn't belong here —
   it either belongs in category 1 (journal) or shouldn't be persisted
   at all, in favor of an LVM tag or re-deriving it.**

Redundancy level (DWR-1 vs DWR-2) deliberately isn't persisted anywhere.
For a brand-new pool, the CLI/daemon require `--redundancy`/`redundancy`
explicitly on every `POST /plan` call — there's no stored value to go
stale. For expanding an *existing* pool (`POST /pools/{poolName}/expand`),
the daemon doesn't ask at all: `InferRedundancy` re-derives it from the
pool's own existing tiers (a qualifying Mirror tier's member count is
always `R+1`; a parity tier's level directly names `R`) every time,
rather than storing or asking for it. This is the same "don't persist,
re-derive from reality" principle taken one step further — redundancy
for an existing pool is itself just a real, already-observable property
of its arrays, not a preference that needs separate storage or
re-confirmation.

## Where it lives

The journal lives at `/var/lib/diskweaver/journal/<execution-id>.json`
(runtime state, not user-edited), overridable via
`DISKWEAVER_JOURNAL_DIR` for dev/testing. Optional user-facing metadata
would go under `/etc/diskweaver/` (config, user-edited) — not designed
or built yet, since nothing currently needs it (redundancy level is
explicitly never persisted; there's no friendly-naming/notification
feature yet to back).
