namespace DiskWeaver.Planner;

/// <summary>A physical block device considered by the planner.</summary>
/// <param name="Id">Stable identity, e.g. a /dev/disk/by-id path.</param>
/// <param name="SizeBytes">Usable disk size in bytes.</param>
/// <param name="IsBlank">
/// Whether the disk currently has no partitions, no mounted filesystem, and no filesystem/RAID/LVM
/// signature of its own -- i.e. safe to hand to <c>parted mklabel</c>/<c>wipefs</c>. Defaults to
/// true so existing callers (fakes, tests, disks already known to belong to a DiskWeaver pool) don't
/// need to opt in explicitly; real inventory sources must compute it. See <see cref="DiskWeaver.Inventory.DiskSelector.EnsureBlank"/>.
/// </param>
/// <param name="IsLikelySystemDisk">
/// Whether this disk (or one of its partitions) is currently mounted at "/", "/boot", "/boot/efi",
/// or as swap -- a best-effort "don't touch this" signal for the OS/boot disk, not a guarantee (a
/// disk can be critical without being mounted right now, e.g. an unmounted backup target). Defaults
/// to false for the same reason <see cref="IsBlank"/> does; real inventory sources compute it from
/// lsblk's MOUNTPOINTS column. Purely informational today -- a system disk mounted at "/" is
/// already refused by <see cref="IsBlank"/> in virtually every real case, since it has children
/// and/or a filesystem signature; this exists to give that refusal a clearer, scarier label than
/// generic "not blank".
/// </param>
/// <param name="DevicePath">
/// The kernel device node, e.g. "/dev/sdb" -- always the trailing `/dev/{name}` form, even when
/// <see cref="Id"/> resolved to a `/dev/disk/by-id/...` path. Kernel names aren't stable across
/// reboots/replugs, so <see cref="Id"/> remains the identity everything else keys off of; this is
/// purely a display convenience for matching what a user sees in `lsblk`/`dmesg`/a drive bay label
/// against the by-id name shown elsewhere. Null for disks not sourced from a live lsblk capture
/// (fakes, tests).
/// </param>
/// <param name="RaidLvmSignatureOwner">
/// Set only when this disk (or a partition/holder beneath it) carries an mdadm or LVM signature
/// ("linux_raid_member"/"LVM2_member" in lsblk's FSTYPE) -- null whenever there's no such signature
/// at all (a blank disk, or one with only a plain filesystem), since a disk with no RAID/LVM
/// signature can never be a DiskWeaver artifact regardless of anything else. When it IS set, the
/// value is one of:
///   - "unknown" -- lsblk-only inventory sources (<see cref="DiskWeaver.Inventory.LsblkOutputParser"/>)
///     always report this: telling a DiskWeaver-owned signature apart from a foreign one requires
///     reading the LVM VG tag (see <see cref="DiskWeaver.Executor.DiskWeaverPoolTag"/>), which is
///     only readable once the array backing that VG is actually assembled -- a live pvs/vgs/mdadm
///     lookup lsblk alone can't do. DiskWeaver never persists its own record of "used to be my pool"
///     (see docs/state-model.md), so an unassembled array's ownership is genuinely unknown, not
///     guessed either way; reassembling it (POST /arrays/reassemble) may resolve this.
///   - "diskweaver" or "foreign" -- set by <see cref="DiskWeaver.Executor.DiskSignatureOwnership.Annotate"/>
///     once it can see the array is assembled and check its VG's tag. In practice a disk resolving
///     to "diskweaver" this way is unusual: a disk actively claimed by a live, tagged pool is caught
///     earlier by the Cockpit UI's own pool-membership check ("already in &lt;pool&gt;") before this
///     value is even consulted -- this exists for a tagged-but-otherwise-unclaimed edge case (e.g. a
///     multi-tier pool where this disk's tier reads fine but a sibling tier's error keeps the whole
///     pool from resolving cleanly).
/// Only meaningful when <see cref="IsBlank"/> is false and the disk isn't already claimed by a live
/// pool (see DiskInventory.jsx's `busy` map) -- those cases are already unambiguous without this.
/// </param>
public sealed record Disk(
    string Id,
    long SizeBytes,
    bool IsBlank = true,
    bool IsLikelySystemDisk = false,
    string? DevicePath = null,
    string? RaidLvmSignatureOwner = null);
