using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Executor;
using DiskWeaver.Planner;

namespace DiskWeaver.Daemon.Tests;

/// <summary>Canned pool state for daemon tests -- no real mdadm/lvm/Linux/Docker involved.</summary>
public sealed class FakePoolStateSource : IPoolStateSource
{
    // Segment size mirrors PartitionLayout.ForPlanning exactly -- reserved bytes, then rounded
    // down to an alignment boundary -- a real pool's tier is always this size, since that's how
    // it was actually built. Using the raw 2 TB here would make BuildIncremental see a
    // segment-size mismatch and treat this tier as orphaned instead of recognizing it as
    // unchanged. Calls the real production function rather than a hand-duplicated formula.
    private const long TwoTb = 2_000_000_000_000;
    private static readonly long TwoTbSegment = PartitionLayout.ForPlanning([new Disk("segment-helper", TwoTb)])[0].SizeBytes;

    public IReadOnlyList<ExistingPoolState> Pools { get; set; } =
    [
        new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [
                new ExistingTier(
                    "/dev/md/diskweaver-tier0",
                    TwoTbSegment,
                    ["/dev/disk/by-id/fake-0", "/dev/disk/by-id/fake-1"],
                    RaidLevel.Mirror,
                    ["/dev/disk/by-id/fake-0-part1", "/dev/disk/by-id/fake-1-part1"]),
            ]),
    ];

    public IReadOnlyList<ExistingPoolState> GetPools() => Pools;
}
