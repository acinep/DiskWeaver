using DiskWeaver.Planner;

namespace DiskWeaver.Planner.Tests;

public class TieringPlannerTests
{
    private const long Tb = 1_000_000_000_000L;

    private static Disk[] Disks(params long[] sizesTb) =>
        sizesTb.Select((size, i) => new Disk($"disk{i}", size * Tb)).ToArray();

    [Fact]
    public void TwoTwoFourFourFour_Dwr1_YieldsTwelveTerabytes()
    {
        var plan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);

        Assert.Equal(12 * Tb, plan.PoolCapacityBytes);
        Assert.Equal(0, plan.ReservedBytes);
        Assert.Equal(2, plan.Tiers.Count);

        Assert.Equal(5, plan.Tiers[0].DiskIds.Count);
        Assert.Equal(RaidLevel.Raid5, plan.Tiers[0].RaidLevel);
        Assert.Equal(8 * Tb, plan.Tiers[0].UsableBytes);

        Assert.Equal(3, plan.Tiers[1].DiskIds.Count);
        Assert.Equal(RaidLevel.Raid5, plan.Tiers[1].RaidLevel);
        Assert.Equal(4 * Tb, plan.Tiers[1].UsableBytes);
    }

    [Fact]
    public void TwoTwoTwoSix_Dwr1_LeavesTopSegmentReserved()
    {
        var plan = TieringPlanner.Plan(Disks(2, 2, 2, 6), RedundancyLevel.Dwr1);

        Assert.Equal(6 * Tb, plan.PoolCapacityBytes);
        Assert.Equal(4 * Tb, plan.ReservedBytes);

        var tier = Assert.Single(plan.Tiers);
        Assert.Equal(RaidLevel.Raid5, tier.RaidLevel);
        Assert.Equal(6 * Tb, tier.UsableBytes);

        var reservedSegment = Assert.Single(plan.Reserved);
        Assert.Equal(4 * Tb, reservedSegment.SegmentSizeBytes);
        Assert.Single(reservedSegment.DiskIds);
    }

    [Fact]
    public void TwoTwoTwoTwoSix_Dwr2_LeavesTopSegmentReserved()
    {
        var plan = TieringPlanner.Plan(Disks(2, 2, 2, 2, 6), RedundancyLevel.Dwr2);

        Assert.Equal(6 * Tb, plan.PoolCapacityBytes);
        Assert.Equal(4 * Tb, plan.ReservedBytes);

        var tier = Assert.Single(plan.Tiers);
        Assert.Equal(RaidLevel.Raid6, tier.RaidLevel);
        Assert.Equal(6 * Tb, tier.UsableBytes);
    }

    [Fact]
    public void TwoTwoTwoSix_Dwr2_LeavesTopSegmentReserved()
    {
        var plan = TieringPlanner.Plan(Disks(2, 2, 2, 6), RedundancyLevel.Dwr2);

        Assert.Equal(4 * Tb, plan.PoolCapacityBytes);
        Assert.Equal(4 * Tb, plan.ReservedBytes);

        var tier = Assert.Single(plan.Tiers);
        Assert.Equal(RaidLevel.Raid6, tier.RaidLevel);
    }

    [Fact]
    public void TwoTwoTwo_Dwr2_UsesThreeWayMirror()
    {
        var plan = TieringPlanner.Plan(Disks(2, 2, 2), RedundancyLevel.Dwr2);

        Assert.Equal(2 * Tb, plan.PoolCapacityBytes);
        Assert.Equal(0, plan.ReservedBytes);

        var tier = Assert.Single(plan.Tiers);
        Assert.Equal(RaidLevel.Mirror, tier.RaidLevel);
        Assert.Equal(3, tier.DiskIds.Count);
        Assert.Equal(2 * Tb, tier.UsableBytes);
    }

    [Fact]
    public void TwoDisksSameSize_Dwr1_UsesMirror()
    {
        var plan = TieringPlanner.Plan(Disks(4, 4), RedundancyLevel.Dwr1);

        var tier = Assert.Single(plan.Tiers);
        Assert.Equal(RaidLevel.Mirror, tier.RaidLevel);
        Assert.Equal(4 * Tb, tier.UsableBytes);
    }

    [Fact]
    public void FewerThanTwoDisks_Throws()
    {
        Assert.Throws<ArgumentException>(() => TieringPlanner.Plan(Disks(4), RedundancyLevel.Dwr1));
    }

    [Fact]
    public void SingleDisk_None_SucceedsAsADegradedMirrorTier()
    {
        var plan = TieringPlanner.Plan(Disks(4), RedundancyLevel.None);

        Assert.Equal(4 * Tb, plan.PoolCapacityBytes);
        Assert.Equal(0, plan.ReservedBytes);

        var tier = Assert.Single(plan.Tiers);
        Assert.Equal(RaidLevel.Mirror, tier.RaidLevel);
        Assert.Equal(1, tier.DegradedSlots);
        Assert.Single(tier.DiskIds);
        Assert.Equal(4 * Tb, tier.UsableBytes);
    }

    [Fact]
    public void MultipleDisks_None_YieldsOneIndependentTierPerDisk()
    {
        var plan = TieringPlanner.Plan(Disks(2, 4, 4), RedundancyLevel.None);

        Assert.Equal(10 * Tb, plan.PoolCapacityBytes);
        Assert.Equal(0, plan.ReservedBytes);
        Assert.Equal(3, plan.Tiers.Count);

        Assert.All(plan.Tiers, tier =>
        {
            Assert.Equal(RaidLevel.Mirror, tier.RaidLevel);
            Assert.Equal(1, tier.DegradedSlots);
            Assert.Single(tier.DiskIds);
        });

        // Equal-size disks are NOT grouped into a shared tier the way Dwr1/Dwr2 would --
        // each stays independent, so there are two separate 4TB tiers here, not one.
        Assert.Equal(2, plan.Tiers.Count(t => t.UsableBytes == 4 * Tb));
    }

    [Fact]
    public void ZeroDisks_Throws()
    {
        Assert.Throws<ArgumentException>(() => TieringPlanner.Plan(Disks(), RedundancyLevel.None));
    }
}
