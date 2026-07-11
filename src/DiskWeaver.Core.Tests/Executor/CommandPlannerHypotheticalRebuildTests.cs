using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class CommandPlannerHypotheticalRebuildTests
{
    private const long Tb = 1_000_000_000_000L;

    private static Disk Disk(string id, long sizeTb) => new($"/dev/disk/by-id/{id}", sizeTb * Tb);

    [Fact]
    public void FiveDisks_TwoSmallAndThreeBig_ExceedsWhatThreeIndependentTiersWouldAchieve()
    {
        // Real scenario: a pool grew incrementally into 3 independent 2-disk mirrors (2 protected
        // via completing RedundancyLevel.None tiers, 1 from grouping leftover auto-protect disks)
        // achieving ~8TB total. Rebuilding all 5 disks together at Dwr1 instead amortizes the
        // mirror's ~50%-overhead across a much wider array, achieving noticeably more from the
        // exact same disks -- this is the number that should be shown as "what a rebuild could get."
        var allDisks = new[] { Disk("d0", 2), Disk("d1", 2), Disk("d2", 4), Disk("d3", 4), Disk("d4", 4) };

        var hypothetical = CommandPlanner.HypotheticalFullRebuildCapacityBytes(allDisks, RedundancyLevel.Dwr1);

        // Segment (0,2]: all 5 disks -> RAID5, (5-1)*(2TB - reservation) = ~8TB.
        // Segment (2,4]: the three 4TB disks -> RAID5, (3-1)*2TB = 4TB (reservation already paid
        // for by the first segment, since PartitionLayout.ForPlanning subtracts it once per disk).
        var reserved = PartitionLayout.TotalReservedBytesPerDisk;
        Assert.Equal(4 * (2 * Tb - reserved) + 2 * (2 * Tb), hypothetical);

        var incrementalTotal = 2 * Tb + 2 * Tb + 4 * Tb; // 3 independent 2-disk mirrors, no shared parity
        Assert.True(hypothetical > incrementalTotal);
    }

    [Fact]
    public void MatchesTieringPlannerDirectly_ForASingleFreshBuild()
    {
        var allDisks = new[] { Disk("d0", 2), Disk("d1", 2), Disk("d2", 2) };

        var hypothetical = CommandPlanner.HypotheticalFullRebuildCapacityBytes(allDisks, RedundancyLevel.Dwr1);
        var freshPlan = TieringPlanner.Plan(PartitionLayout.ForPlanning(allDisks), RedundancyLevel.Dwr1);

        Assert.Equal(freshPlan.PoolCapacityBytes, hypothetical);
    }
}
