using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class IndependentGrowPlannerTests
{
    private const long Tb = 1_000_000_000_000L;
    private const long ReservedBytesPerDisk = 2 * 1024 * 1024;

    private static Disk NewDisk(string id, long sizeTb) => new($"/dev/disk/by-id/{id}", sizeTb * Tb);

    private static ExistingTier ProtectedMirror(string arrayDevice, string d0, string d1, long segmentTb) =>
        new(arrayDevice, segmentTb * Tb - ReservedBytesPerDisk, [d0, d1], RaidLevel.Mirror,
            [$"{d0}-part1", $"{d1}-part1"]);

    [Fact]
    public void TwoIndependentMirrors_EachGetOneOfTwoNewDisks_SplitEvenly()
    {
        var tiers = new[]
        {
            ProtectedMirror("/dev/md126", "/dev/disk/by-id/d0", "/dev/disk/by-id/d1", 2),
            ProtectedMirror("/dev/md127", "/dev/disk/by-id/d2", "/dev/disk/by-id/d3", 2),
        };
        var newDisks = new[] { NewDisk("d4", 2), NewDisk("d5", 2) };

        var plan = IndependentGrowPlanner.PlanGrowth(tiers, newDisks);

        Assert.Equal(2, plan.TierGrowths.Count);
        var md126Growth = Assert.Single(plan.TierGrowths, g => g.Existing.ArrayDevice == "/dev/md126");
        var md127Growth = Assert.Single(plan.TierGrowths, g => g.Existing.ArrayDevice == "/dev/md127");

        Assert.Equal(3, md126Growth.Desired.DiskIds.Count);
        Assert.Equal(3, md127Growth.Desired.DiskIds.Count);
        Assert.Equal(RaidLevel.Raid5, md126Growth.Desired.RaidLevel);
        Assert.Equal(RaidLevel.Raid5, md127Growth.Desired.RaidLevel);
        Assert.Empty(plan.NewIndependentTiers);
    }

    [Fact]
    public void OneTierGetsMostNewDisks_MigratesFromMirrorToRaid5ToRaid6AsMembersGrow()
    {
        // A single 2-disk mirror (Dwr1, R=1) picking up enough new disks in one grow request should
        // still only migrate to RAID5 (R+2 members), not further -- RAID6 needs R=2.
        var tiers = new[] { ProtectedMirror("/dev/md127", "/dev/disk/by-id/d0", "/dev/disk/by-id/d1", 2) };
        var newDisks = new[] { NewDisk("d2", 2), NewDisk("d3", 2) };

        var plan = IndependentGrowPlanner.PlanGrowth(tiers, newDisks);

        var growth = Assert.Single(plan.TierGrowths);
        Assert.Equal(4, growth.Desired.DiskIds.Count);
        Assert.Equal(RaidLevel.Raid5, growth.Desired.RaidLevel);
    }

    [Fact]
    public void ThreeWayMirror_Dwr2_MigratesToRaid6OnceEnoughMembersJoin()
    {
        var tiers = new[]
        {
            new ExistingTier("/dev/md127", 2 * Tb - ReservedBytesPerDisk,
                ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"], RaidLevel.Mirror,
                ["/dev/disk/by-id/d0-part1", "/dev/disk/by-id/d1-part1", "/dev/disk/by-id/d2-part1"]),
        };
        var newDisks = new[] { NewDisk("d3", 2) };

        var plan = IndependentGrowPlanner.PlanGrowth(tiers, newDisks);

        var growth = Assert.Single(plan.TierGrowths);
        Assert.Equal(4, growth.Desired.DiskIds.Count);
        Assert.Equal(RaidLevel.Raid6, growth.Desired.RaidLevel);
        Assert.Equal(2 * (2 * Tb - ReservedBytesPerDisk), growth.Desired.UsableBytes);
    }

    [Fact]
    public void DiskTooSmallForAnyEligibleTier_BecomesItsOwnNewIndependentTier()
    {
        var tiers = new[] { ProtectedMirror("/dev/md127", "/dev/disk/by-id/d0", "/dev/disk/by-id/d1", 4) };
        var newDisks = new[] { NewDisk("d2", 2) };

        var plan = IndependentGrowPlanner.PlanGrowth(tiers, newDisks);

        Assert.Empty(plan.TierGrowths);
        var extra = Assert.Single(plan.NewIndependentTiers);
        Assert.Equal(["/dev/disk/by-id/d2"], extra.DiskIds);
        Assert.Equal(RaidLevel.Mirror, extra.RaidLevel);
        Assert.Equal(1, extra.DegradedSlots);
    }

    [Fact]
    public void ExistingRaid5Tier_TooSmallForNewDisk_BecomesIndependentTierInstead()
    {
        var tiers = new[]
        {
            new ExistingTier("/dev/md127", 2 * Tb - ReservedBytesPerDisk,
                ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"], RaidLevel.Raid5,
                ["/dev/disk/by-id/d0-part1", "/dev/disk/by-id/d1-part1", "/dev/disk/by-id/d2-part1"]),
        };

        // Raid5 is itself an eligible RAID level to grow -- but this new disk's spare capacity
        // (1TB) is smaller than the existing tier's segment (2TB), so it can't join it.
        var newDisks = new[] { NewDisk("d3", 1) };

        var plan = IndependentGrowPlanner.PlanGrowth(tiers, newDisks);

        Assert.Empty(plan.TierGrowths);
        var extra = Assert.Single(plan.NewIndependentTiers);
        Assert.Equal(["/dev/disk/by-id/d3"], extra.DiskIds);
    }

    [Fact]
    public void LargerDiskThanTierSegment_SplitsAcrossBothEligibleTiers_WithNoLeftover()
    {
        // A disk twice the size of each tier's segment can fully feed both tiers in successive
        // passes -- exercises the "recycle a disk's remaining spare capacity across passes" path,
        // the same one ProtectionPlanner already relies on for splitting one disk across tiers.
        var tiers = new[]
        {
            ProtectedMirror("/dev/md126", "/dev/disk/by-id/d0", "/dev/disk/by-id/d1", 2),
            ProtectedMirror("/dev/md127", "/dev/disk/by-id/d2", "/dev/disk/by-id/d3", 2),
        };
        var newDisks = new[] { new Disk("/dev/disk/by-id/d4", 4 * Tb) };

        var plan = IndependentGrowPlanner.PlanGrowth(tiers, newDisks);

        Assert.Equal(2, plan.TierGrowths.Count);
        Assert.All(plan.TierGrowths, g => Assert.Contains("/dev/disk/by-id/d4", g.Desired.DiskIds));
        Assert.Empty(plan.NewIndependentTiers);
    }
}
