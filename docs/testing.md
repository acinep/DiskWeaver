# Validating Phase 2a scripts on real hardware (safely)

Status: Draft v0.1 — **superseded as the primary validation path** by
automated invocation through the daemon (Phase 2b/3) and the Cockpit
plugin (Phase 4), both now built and validated end-to-end — see
execution.md, daemon-api.md, and cockpit-plugin.md for what those
actually exercise (real `mdadm --grow`/RAID-level migrations, teardown
from live state, etc., none of which this hand-run-script workflow
covers). This doc's manual, `--script`-file-only workflow is still
useful for reviewing exactly what a plan would do before trusting the
daemon to run it, or for validating `CommandPlanner` changes without
a daemon in the loop at all.

Phase 2a (see [execution.md](execution.md)) only generates a shell
script — it never runs anything. This doc covers running that generated
script by hand against **loop devices** (block devices backed by sparse
files) on a real Linux box, so you get real `mdadm`/`parted`/LVM
behavior without touching any physical disk. This is manual, human-run
validation, not the automated invocation that's now built (see above).

## 1. Create loopback test disks

`diskweaver testkit` generates the setup script for you — sizes below
reuse the 2,2,4,4,4 TB worked example from algorithm.md at a scale
that's fast to actually run mdadm/mkfs against; adjust as you like.

```bash
diskweaver testkit --disks 2GB,2GB,4GB,4GB,4GB --out setup-loop-devices.sh
bash setup-loop-devices.sh
```

This creates the sparse files, attaches each with `losetup -fP --show`
(the `-P` flag makes the kernel scan for partitions after `parted` later
writes a table — without it `loop0p1` etc. never appear and
`mdadm`/`pvcreate` fail looking for them), prints the
`disk.img -> /dev/loopN` mapping, and — critically — writes
`inventory.json` itself, **scoped to only the loop devices it just
created** (`lsblk ... "${DEV[@]}"`, not a bare `lsblk` over the whole
system).

**Do not replace that with a plain `lsblk --json -b -d -o ... >
inventory.json`.** A system-wide capture picks up every block device on
the box, including your real production disks. This happened during
early testing: `wwn-0x...` and `nvme-eui...` production disks showed up
mixed into the same plan as `/dev/loop0..3`.

That incident is also why disk selection isn't left to "just don't
capture the wrong thing" — `plan --lsblk-json`/`--lsblk` always requires
`--disks` naming the exact disks to use; there is no mode where omitting
it falls back to "use everything found". Review what's in an inventory
file with:

```bash
diskweaver inventory --lsblk-json inventory.json
```

which lists every disk found and suggests the `--disks` syntax. This
matters beyond the test rig too — on a real box, `--disks` is how you
exclude the OS/boot disk from the pool.

Disks that already have a partition table, a mounted filesystem, or a
foreign filesystem/RAID/LVM signature are flagged `[NOT BLANK]` in
that listing — `plan` refuses them outright (`DiskSelector.EnsureBlank`)
rather than silently reusing them. This also catches disks left in a
half-built state, e.g. loop devices that still have spares added to a
live array from a grow that failed partway through (see the RAID-level
migration gotcha below). Clear a flagged disk with:

```bash
diskweaver wipe --disks loop3,loop4 --lsblk-json inventory.json --script wipe.sh
```

which zeroes any RAID superblock on each disk's partitions
(`mdadm --zero-superblock`) before wiping the parent disk's own
signature (`wipefs -a`) — the same order `plan --teardown-script` uses
for a pool's own disks. It does **not** stop or remove a disk from a
currently-*running* array; if a disk is still an active member/spare
left over from a failed reshape, stop or remove it from that array
first (`mdadm --stop <array>` or `mdadm --remove <array> <partition>`
after failing it) — `mdadm --zero-superblock` refuses a live member.

## 2. Generate a plan + script

Loop devices report `"type": "loop"` (not `"disk"`) and have no
`/dev/disk/by-id` entry — `LsblkOutputParser` handles both (see the
`IncludesLoopDevices_ForLoopbackTestRigs` test), falling back to the
raw `/dev/loopN` path as the disk identity.

From your Windows dev machine (or directly on the Linux box if you have
the SDK there too), using the `inventory.json` the setup script wrote:

```bash
diskweaver plan --lsblk-json inventory.json --disks loop0,loop1,loop2,loop3 --redundancy dwr1 --script build-pool.sh
```

**Read the generated script before running it.** Even against loop
devices this creates real mdadm arrays and LVM structures on your test
box — low risk, but not zero (it's still real root-level state on a
real machine).

## 3. Known gotchas when running against loop devices

- **LVM device filter**: many distros' default `lvm.conf` has a
  `global_filter`/`filter` that rejects loop devices, so `pvcreate`/
  `vgcreate` may silently ignore them. If so, run with an explicit
  filter override, e.g.:
  `sudo pvcreate --config 'devices{filter=["a|.*|"]}' /dev/md/diskweaver-pool-tier0`
  (only for this test rig — don't leave a wide-open filter on a real
  system).
- **Partition naming**: `CommandPlanner` emits `/dev/loopNpM` for loop
  devices (kernel names ending in a digit get a `p` separator), vs.
  `/dev/disk/by-id/...-partM` for real disks with stable by-id paths.
  Confirmed by `PartitionNamingTests`.
- **`partprobe`**: `build-pool.sh` already runs `partprobe` after every
  `mkpart` plus a trailing `udevadm settle` before `mdadm --create` (see
  `CommandPlanner`) — this used to be a manual step but is now generated
  automatically, since it turned out to be a real missing kernel rescan
  on loop devices, not just a timing race.

## 4. Tear down

Generate the matching teardown alongside the build script — same
`plan` invocation, one more flag:

```bash
diskweaver plan --lsblk-json inventory.json --disks loop0,loop1,loop2,loop3 --redundancy dwr1 \
  --script build-pool.sh --teardown-script teardown.sh
```

`teardown.sh` reverses exactly what `build-pool.sh` built for that
specific plan: `lvremove` → `vgremove` → per-tier `pvremove`/
`mdadm --stop` → `mdadm --zero-superblock` on every partition (so a
stale array superblock can't get auto-reassembled the next time those
partitions are reused) → `wipefs -a` on every disk → `losetup -d` on any
loop devices involved (skipped for real disks). **Unmount the filesystem
yourself first** if you formatted/mounted it — the script doesn't know
your mountpoint and starts from `lvremove`.

```bash
sudo bash teardown.sh
rm -rf ~/diskweaver-test
```

## What this validates vs. doesn't

Validates: partition offsets/alignment are accepted by real `parted`,
`mdadm --create` accepts the generated flags and array names, `pvcreate`/
`vgcreate`/`lvcreate` chain together correctly, and the incremental
(`BuildIncremental`) path's `vgextend`/`lvextend` work against a pool
built by the fresh (`Build`) path.

Doesn't validate: real disk timing (rebuild/reshape duration on
multi-TB arrays) or real sector-size/alignment edge cases on unusual
physical media. Phase 2b (journaling, crash recovery, automated
invocation) and Phase 4 (Cockpit UI) are validated separately, not by
this hand-run-script workflow — see execution.md, daemon-api.md, and
cockpit-plugin.md.
