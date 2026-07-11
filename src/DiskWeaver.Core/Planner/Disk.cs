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
public sealed record Disk(string Id, long SizeBytes, bool IsBlank = true, bool IsLikelySystemDisk = false);
