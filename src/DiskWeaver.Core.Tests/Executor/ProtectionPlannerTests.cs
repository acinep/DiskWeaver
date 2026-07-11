using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class ProtectionPlannerTests
{
    private const long Tb = 1_000_000_000_000L;
    private const long ReservedBytesPerDisk = 2 * 1024 * 1024;

    private static Disk NewDisk(string id, long sizeTb) => new($"/dev/disk/by-id/{id}", sizeTb * Tb);

    // A disk's spare capacity is its own raw size minus its own GPT/alignment reservation,
    // independent of any other disk's reservation -- so a new disk sized to exactly cover N
    // existing tiers' segments (each computed against a *different* disk's own reservation) needs
    // its raw size adjusted for its single reservation, not naively N times a tier's segment size.
    private static Disk NewDiskCoveringExactly(string id, long totalSegmentBytes) =>
        new($"/dev/disk/by-id/{id}", totalSegmentBytes + ReservedBytesPerDisk);

    private static ExistingTier UnprotectedTier(string arrayDevice, string diskId, long segmentTb) =>
        new(arrayDevice, segmentTb * Tb - ReservedBytesPerDisk, [diskId], RaidLevel.Mirror,
            [$"{diskId}-part1"], ConfiguredMemberCount: 2, IsUnprotectedByDesign: true);

    [Fact]
    public void OneDiskExactlyCoveringOneTier_CompletesIt()
    {
        var tiers = new[] { UnprotectedTier("/dev/md127", "/dev/disk/by-id/d0", 2) };
        var newDisks = new[] { NewDisk("d1", 2) };

        var plan = ProtectionPlanner.PlanAutoProtect(tiers, newDisks);

        var assignment = Assert.Single(plan.TierAssignments);
        Assert.Equal("/dev/md127", assignment.ArrayDevice);
        Assert.Equal(["/dev/disk/by-id/d1"], assignment.DiskIds);
        Assert.Empty(plan.NewTiers);
    }

    [Fact]
    public void OneLargerDiskCoveringTwoIndependentTiers_CompletesBothWithNoLeftover()
    {
        // The user's exact repro: 2x2GB unprotected tiers, one 4GB disk arrives -- should split into
        // two 2GB slices, one per tier, completing both mirrors with nothing wasted.
        var tiers = new[]
        {
            UnprotectedTier("/dev/md127", "/dev/disk/by-id/d0", 2),
            UnprotectedTier("/dev/md126", "/dev/disk/by-id/d1", 2),
        };
        var tierSegmentBytes = 2 * Tb - ReservedBytesPerDisk;
        var newDisks = new[] { NewDiskCoveringExactly("d2", 2 * tierSegmentBytes) };

        var plan = ProtectionPlanner.PlanAutoProtect(tiers, newDisks);

        Assert.Equal(2, plan.TierAssignments.Count);
        Assert.Contains(plan.TierAssignments, a => a.ArrayDevice == "/dev/md127" && a.DiskIds.SequenceEqual(new[] { "/dev/disk/by-id/d2" }));
        Assert.Contains(plan.TierAssignments, a => a.ArrayDevice == "/dev/md126" && a.DiskIds.SequenceEqual(new[] { "/dev/disk/by-id/d2" }));
        Assert.Empty(plan.NewTiers);
    }

    [Fact]
    public void DiskTooSmallForAnyEligibleTier_BecomesItsOwnNewUnprotectedTier()
    {
        var tiers = new[] { UnprotectedTier("/dev/md127", "/dev/disk/by-id/d0", 4) };
        var newDisks = new[] { NewDisk("d1", 2) };

        var plan = ProtectionPlanner.PlanAutoProtect(tiers, newDisks);

        Assert.Empty(plan.TierAssignments);
        var extra = Assert.Single(plan.NewTiers);
        Assert.Equal(["/dev/disk/by-id/d1"], extra.DiskIds);
        Assert.Equal(RaidLevel.Mirror, extra.RaidLevel);
    }

    [Fact]
    public void MultipleDisksAndTiers_MatchesSmallestFit_NotJustFirstInOrder()
    {
        // Two eligible tiers (2GB, 4GB) and two disks (4GB, 2GB) offered in an order that would
        // trip up a naive "just take them in order" match -- the 2GB tier should get the 2GB disk
        // and the 4GB tier the 4GB disk, not the other way around wasting the 4GB disk on the
        // smaller tier and leaving the 4GB tier unmatched.
        var tiers = new[]
        {
            UnprotectedTier("/dev/md-small", "/dev/disk/by-id/small0", 2),
            UnprotectedTier("/dev/md-big", "/dev/disk/by-id/big0", 4),
        };
        var newDisks = new[] { NewDisk("big1", 4), NewDisk("small1", 2) };

        var plan = ProtectionPlanner.PlanAutoProtect(tiers, newDisks);

        Assert.Equal(2, plan.TierAssignments.Count);
        Assert.Contains(plan.TierAssignments, a => a.ArrayDevice == "/dev/md-small" && a.DiskIds.SequenceEqual(new[] { "/dev/disk/by-id/small1" }));
        Assert.Contains(plan.TierAssignments, a => a.ArrayDevice == "/dev/md-big" && a.DiskIds.SequenceEqual(new[] { "/dev/disk/by-id/big1" }));
        Assert.Empty(plan.NewTiers);
    }

    [Fact]
    public void AlreadyProtectedTiers_AreNeverTargeted()
    {
        var tiers = new[]
        {
            new ExistingTier("/dev/md127", 2 * Tb - ReservedBytesPerDisk,
                ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1"], RaidLevel.Mirror,
                ["/dev/disk/by-id/d0-part1", "/dev/disk/by-id/d1-part1"]),
        };
        var newDisks = new[] { NewDisk("d2", 2) };

        var plan = ProtectionPlanner.PlanAutoProtect(tiers, newDisks);

        Assert.Empty(plan.TierAssignments);
        var extra = Assert.Single(plan.NewTiers);
        Assert.Equal(["/dev/disk/by-id/d2"], extra.DiskIds);
    }

    [Fact]
    public void MultipleLeftoverDisks_FormTheirOwnProtectedTier_NotSeparateUnprotectedOnes()
    {
        // Real incident: a pool with 2 already-protected tiers (no eligible completion targets) got
        // 3 new same-size disks offered via auto-protect. None are needed to complete anything, but
        // all 3 are the same size -- the default should be to protect them together (a 3-disk
        // mirror -> RAID5, tolerating 1 failure) rather than leaving all 3 as separate single-disk
        // unprotected tiers just because none of them individually completed an existing one.
        var tiers = new[]
        {
            new ExistingTier("/dev/md127", 2 * Tb - ReservedBytesPerDisk,
                ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1"], RaidLevel.Mirror,
                ["/dev/disk/by-id/d0-part1", "/dev/disk/by-id/d1-part1"]),
        };
        var newDisks = new[] { NewDisk("d2", 4), NewDisk("d3", 4), NewDisk("d4", 4) };

        var plan = ProtectionPlanner.PlanAutoProtect(tiers, newDisks);

        Assert.Empty(plan.TierAssignments);
        var newTier = Assert.Single(plan.NewTiers);
        Assert.Equal(RaidLevel.Raid5, newTier.RaidLevel);
        Assert.Equal(3, newTier.DiskIds.Count);
        Assert.Equal(0, newTier.DegradedSlots);
    }

    [Fact]
    public void TwoLeftoverDisksOfDifferentSizes_ProtectTheSharedPortion_SmallerDiskCompletelyUsed()
    {
        // 2 leftover disks of different sizes still form a real protected tier over their shared
        // (smaller) size -- the larger disk's excess has no size-matched partner, so it becomes its
        // own single-disk unprotected tier instead of being wasted or blocking protection entirely.
        var tiers = new[]
        {
            new ExistingTier("/dev/md127", 2 * Tb - ReservedBytesPerDisk,
                ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1"], RaidLevel.Mirror,
                ["/dev/disk/by-id/d0-part1", "/dev/disk/by-id/d1-part1"]),
        };
        var newDisks = new[] { NewDisk("d2", 2), NewDisk("d3", 4) };

        var plan = ProtectionPlanner.PlanAutoProtect(tiers, newDisks);

        Assert.Equal(2, plan.NewTiers.Count);
        var protectedTier = Assert.Single(plan.NewTiers, t => t.DegradedSlots == 0);
        Assert.Equal(RaidLevel.Mirror, protectedTier.RaidLevel);
        Assert.Equal(2, protectedTier.DiskIds.Count);

        var unprotectedExcess = Assert.Single(plan.NewTiers, t => t.DegradedSlots > 0);
        Assert.Equal(["/dev/disk/by-id/d3"], unprotectedExcess.DiskIds);
    }

    [Fact]
    public void RealDegradedMirror_IsEligibleJustLikeAnUnprotectedTier()
    {
        var tiers = new[]
        {
            new ExistingTier("/dev/md127", 2 * Tb - ReservedBytesPerDisk, ["/dev/disk/by-id/d0"],
                RaidLevel.Mirror, ["/dev/disk/by-id/d0-part1"], ConfiguredMemberCount: 2),
        };
        var newDisks = new[] { NewDisk("d1", 2) };

        var plan = ProtectionPlanner.PlanAutoProtect(tiers, newDisks);

        var assignment = Assert.Single(plan.TierAssignments);
        Assert.Equal("/dev/md127", assignment.ArrayDevice);
        Assert.Equal(["/dev/disk/by-id/d1"], assignment.DiskIds);
    }
}
