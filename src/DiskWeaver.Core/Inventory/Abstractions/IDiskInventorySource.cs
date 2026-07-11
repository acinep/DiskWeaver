using DiskWeaver.Planner;

namespace DiskWeaver.Core.Inventory.Abstractions;

/// <summary>A source of real disk inventory. Exists so callers (e.g. the daemon) can substitute a fake for testing.</summary>
public interface IDiskInventorySource
{
    IReadOnlyList<Disk> GetDisks();

    /// <summary>
    /// Maps each disk id to its current partition device paths (e.g. "/dev/loop3p1") -- used by
    /// `POST /disks/wipe` to zero a stale/foreign RAID superblock on each partition before wiping
    /// the parent disk's own signature (see <see cref="DiskWeaver.Executor.CommandPlanner.BuildWipe"/>).
    /// Defaults to reporting no partitions for any disk, which only makes wipe skip the
    /// zero-superblock step and go straight to wiping the parent disk -- safe for fakes/tests that
    /// don't otherwise care about this.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> GetPartitionPaths() =>
        new Dictionary<string, IReadOnlyList<string>>();
}
