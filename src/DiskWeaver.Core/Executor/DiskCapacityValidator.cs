using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Validates that a disk has enough unclaimed capacity for a "protect this tier" request --
/// distinct from <see cref="DiskWeaver.Inventory.DiskSelector.EnsureBlank"/>, which assumes a disk
/// is either wholly fresh or wholly foreign. Splitting one larger disk across two separate
/// "protect this tier" operations (explicit or automatic) means a disk that's already a member of
/// one tier in this same pool can legitimately be asked for again, as long as its *remaining*
/// capacity (after subtracting what's already claimed) is enough for the new request.
/// </summary>
public static class DiskCapacityValidator
{
    /// <summary>
    /// Sum of segment sizes across every tier in <paramref name="pool"/> that already includes
    /// <paramref name="diskId"/> -- the same per-disk claimed-capacity computation
    /// <see cref="CommandPlanner.BuildIncremental"/> itself uses to seed partition offsets.
    /// </summary>
    public static long ClaimedBytes(ExistingPoolState pool, string diskId) =>
        pool.Tiers.Where(t => t.DiskIds.Contains(diskId)).Sum(t => t.SegmentSizeBytes);

    /// <summary>
    /// Throws if <paramref name="disk"/> doesn't have at least <paramref name="requiredBytes"/> of
    /// spare capacity once already-claimed tiers (see <see cref="ClaimedBytes"/>) and GPT/alignment
    /// overhead (<see cref="PartitionLayout.TotalReservedBytesPerDisk"/>) are subtracted. A disk with
    /// nothing claimed yet must be blank (same rule as <see cref="DiskWeaver.Inventory.DiskSelector.EnsureBlank"/>);
    /// a disk already partially claimed by this pool is allowed to be claimed further.
    /// </summary>
    public static void EnsureSpareCapacity(ExistingPoolState pool, Disk disk, long requiredBytes)
    {
        var claimed = ClaimedBytes(pool, disk.Id);
        if (claimed == 0 && !disk.IsBlank)
        {
            throw new InvalidOperationException(
                $"Refusing to use disk '{disk.Id}': it isn't blank and isn't already a member of pool "
                + $"'{pool.PoolName}'. Wipe it manually first if you intend to reuse it.");
        }

        var plannableBytes = disk.SizeBytes - PartitionLayout.TotalReservedBytesPerDisk;
        var spareBytes = plannableBytes - claimed;
        if (spareBytes < requiredBytes)
        {
            throw new InvalidOperationException(
                $"Disk '{disk.Id}' doesn't have enough spare capacity: {spareBytes:N0} bytes free "
                + $"({claimed:N0} already claimed by pool '{pool.PoolName}'), but {requiredBytes:N0} "
                + "bytes are needed.");
        }
    }
}
