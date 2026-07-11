using DiskWeaver.Core.Inventory.Abstractions;
using DiskWeaver.Planner;

namespace DiskWeaver.Daemon.Tests;

/// <summary>Canned disk inventory for daemon tests -- no real lsblk/Linux/Docker involved.</summary>
public sealed class FakeDiskInventorySource : IDiskInventorySource
{
    public IReadOnlyList<Disk> Disks { get; set; } =
    [
        new Disk("/dev/disk/by-id/fake-0", 2_000_000_000_000),
        new Disk("/dev/disk/by-id/fake-1", 2_000_000_000_000),
        new Disk("/dev/disk/by-id/fake-2", 4_000_000_000_000),
        new Disk("/dev/disk/by-id/fake-3", 4_000_000_000_000),
    ];

    public IReadOnlyDictionary<string, IReadOnlyList<string>> PartitionPaths { get; set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyList<Disk> GetDisks() => Disks;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetPartitionPaths() => PartitionPaths;
}
