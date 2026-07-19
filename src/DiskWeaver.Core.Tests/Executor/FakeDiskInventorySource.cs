using DiskWeaver.Core.Inventory.Abstractions;
using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

/// <summary>
/// Canned disk inventory for <see cref="MdadmLvmPoolStateSource"/> tests. Defaults to no disks at
/// all, which is enough for most tests here: DescribeTier's device-path canonicalization simply
/// has nothing to canonicalize against and falls back to the raw kernel id unchanged -- the same
/// behavior it had before that canonicalization existed.
/// </summary>
public sealed class FakeDiskInventorySource : IDiskInventorySource
{
    public IReadOnlyList<Disk> Disks { get; set; } = [];

    public IReadOnlyList<Disk> GetDisks() => Disks;
}
