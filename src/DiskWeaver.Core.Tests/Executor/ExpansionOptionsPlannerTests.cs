using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class ExpansionOptionsPlannerTests
{
    private const long Tb = 1_000_000_000_000L;
    private const long ReservedBytesPerDisk = 2 * 1024 * 1024;

    private static Disk NewDisk(string id, long sizeTb) => new($"/dev/disk/by-id/{id}", sizeTb * Tb);

    private static string[] PartitionPaths(params string[] diskIds) =>
        diskIds.Select(id => PartitionNaming.ToPartitionPath(id, 1)).ToArray();

    private static ExistingTier UnprotectedTier(string arrayDevice, string diskId, long segmentTb) =>
        new(arrayDevice, segmentTb * Tb - ReservedBytesPerDisk, [diskId], RaidLevel.Mirror,
            PartitionPaths(diskId), ConfiguredMemberCount: 2, IsUnprotectedByDesign: true);

    private static ExistingTier ProtectedMirror(string arrayDevice, string d0, string d1, long segmentTb) =>
        new(arrayDevice, segmentTb * Tb - ReservedBytesPerDisk, [d0, d1], RaidLevel.Mirror, PartitionPaths(d0, d1));

    private static ExistingTier ProtectedRaid5(string arrayDevice, string[] diskIds, long segmentTb) =>
        new(arrayDevice, segmentTb * Tb - ReservedBytesPerDisk, diskIds, RaidLevel.Raid5, PartitionPaths(diskIds));

    [Fact]
    public void ProtectionOnly_CompletingADegradedTier_AddsNoCapacity_SoNoSpaceOption()
    {
        // The user's real scenario: a 2-slot degraded mirror gets its missing slot filled by a
        // matching disk -- strictly more protection (0 real members short -> fully populated), but
        // mdadm's mirror capacity never changes with member count, so growing "independently" would
        // reach the exact same capacity -- not a real space option.
        var pool = new ExistingPoolState("diskweaver-pool", "data", [UnprotectedTier("/dev/md127", "/dev/disk/by-id/d0", 2)]);
        var newDisks = new[] { NewDisk("d1", 2) };
        var allDisks = new[] { NewDisk("d0", 2), NewDisk("d1", 2) };

        var options = ExpansionOptionsPlanner.ComputeOptions(pool, allDisks, newDisks, RedundancyLevel.Dwr1);

        Assert.NotNull(options.Protection);
        Assert.Equal(ExpansionOptionsPlanner.ProtectionIntent, options.Protection!.Intent);
        Assert.Null(options.Space);
    }

    [Fact]
    public void SpaceOnly_PoolAlreadyFullyProtected_DiskGrowsCapacity()
    {
        // Pool's already a healthy 2-disk mirror -- nothing to protect, but a matching disk still
        // grows it (mirror -> RAID5), which is a real capacity increase.
        var pool = new ExistingPoolState("diskweaver-pool", "data",
            [ProtectedMirror("/dev/md127", "/dev/disk/by-id/d0", "/dev/disk/by-id/d1", 2)]);
        var newDisks = new[] { NewDisk("d2", 2) };
        var allDisks = new[] { NewDisk("d0", 2), NewDisk("d1", 2), NewDisk("d2", 2) };

        var options = ExpansionOptionsPlanner.ComputeOptions(pool, allDisks, newDisks, RedundancyLevel.Dwr1);

        Assert.Null(options.Protection);
        Assert.NotNull(options.Space);
        Assert.Equal(ExpansionOptionsPlanner.SpaceIntent, options.Space!.Intent);
        Assert.True(options.Space.AchievedCapacityBytes > 2 * Tb - ReservedBytesPerDisk);
    }

    [Fact]
    public void BothAvailable_OneDiskCompletesADegradedTier_AnotherGrowsAHealthyOne()
    {
        var pool = new ExistingPoolState("diskweaver-pool", "data",
            [
                UnprotectedTier("/dev/md127", "/dev/disk/by-id/d0", 2),
                ProtectedMirror("/dev/md126", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2", 2),
            ]);
        var newDisks = new[] { NewDisk("d3", 2), NewDisk("d4", 2) };
        var allDisks = new[]
        {
            NewDisk("d0", 2), NewDisk("d1", 2), NewDisk("d2", 2), NewDisk("d3", 2), NewDisk("d4", 2),
        };

        var options = ExpansionOptionsPlanner.ComputeOptions(pool, allDisks, newDisks, RedundancyLevel.Dwr1);

        Assert.NotNull(options.Protection);
        Assert.NotNull(options.Space);
    }

    [Fact]
    public void NeitherAvailable_DiskTooSmallToHelpAnyTierOrFormItsOwn()
    {
        // The "hot spare" case: the pool's fully protected already (nothing to complete), the
        // offered disk is far too small to grow the existing tier's segment, and it's too small
        // even to clear its own GPT/alignment reservation for a brand-new JBOD tier -- there is
        // genuinely no plan that does anything useful with it.
        var pool = new ExistingPoolState("diskweaver-pool", "data",
            [ProtectedMirror("/dev/md127", "/dev/disk/by-id/d0", "/dev/disk/by-id/d1", 4)]);
        var tinyDisk = new Disk("/dev/disk/by-id/d2", (long)(PartitionLayout.TotalReservedBytesPerDisk * 1.5));
        var newDisks = new[] { tinyDisk };
        var allDisks = new[] { NewDisk("d0", 4), NewDisk("d1", 4), tinyDisk };

        var options = ExpansionOptionsPlanner.ComputeOptions(pool, allDisks, newDisks, RedundancyLevel.Dwr1);

        Assert.Null(options.Protection);
        Assert.Null(options.Space);
    }

    [Fact]
    public void ProtectionUpgrade_NoMergeConflict_RaisesRedundancyOnASingleExistingTier()
    {
        // A single already-healthy 2-disk (Dwr1) mirror, asked to reach Dwr2 -- no auto-protect
        // work to do (nothing's degraded), so this falls to the pool-wide redundancy-upgrade
        // fallback. Only one existing tier is involved, so growing it to a 3-way mirror is a plain
        // grow candidate, not a merge conflict.
        var pool = new ExistingPoolState("diskweaver-pool", "data",
            [ProtectedMirror("/dev/md127", "/dev/disk/by-id/d0", "/dev/disk/by-id/d1", 2)]);
        var newDisks = new[] { NewDisk("d2", 2) };
        var allDisks = new[] { NewDisk("d0", 2), NewDisk("d1", 2), NewDisk("d2", 2) };

        var options = ExpansionOptionsPlanner.ComputeOptions(pool, allDisks, newDisks, RedundancyLevel.Dwr2);

        Assert.NotNull(options.Protection);
        Assert.Equal(RedundancyLevel.Dwr2, options.Protection!.AchievedRedundancy);
        var tier = Assert.Single(options.Protection.Desired.Tiers);
        Assert.Equal(RaidLevel.Mirror, tier.RaidLevel);
        Assert.Equal(3, tier.DiskIds.Count);
    }

    [Fact]
    public void NoProtectionOption_WhenNewDisksWouldOnlyFormTheirOwnFreshTier_NotCompleteAnythingExisting()
    {
        // Real user question: a healthy 3-disk RAID5 tier (already Dwr1, nothing degraded) gets 2
        // new same-size disks offered. Grouping those 2 brand-new disks into their own protected
        // mirror is NOT "increasing protection" -- they were never part of the pool before, so
        // there's nothing of theirs to protect; it's just an alternate (less space-efficient)
        // capacity layout, which the space option already covers via IndependentGrowPlanner. Only a
        // real 0->1 (or 1->2) increase on something that already existed should ever be offered as
        // the protection candidate -- matching how this HHR model would only ever surface a
        // protection upgrade when it's a genuine increase.
        var pool = new ExistingPoolState("diskweaver-pool", "data",
            [ProtectedRaid5("/dev/md127", ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"], 2)]);
        var newDisks = new[] { NewDisk("d3", 4), NewDisk("d4", 4) };
        var allDisks = new[] { NewDisk("d0", 2), NewDisk("d1", 2), NewDisk("d2", 2), NewDisk("d3", 4), NewDisk("d4", 4) };

        var options = ExpansionOptionsPlanner.ComputeOptions(pool, allDisks, newDisks, RedundancyLevel.Dwr1);

        Assert.Null(options.Protection);
        Assert.NotNull(options.Space);
    }

    [Fact]
    public void ProtectionUpgrade_WouldRequireMergingIndependentTiers_IsOmittedNotAnError()
    {
        // Real incident this generalizes: 2 independent JBOD tiers can't be merged into one shared
        // Dwr1 tier without a rebuild (no mdadm "merge two arrays" operation) -- the protection
        // option should simply be absent, not throw.
        var pool = new ExistingPoolState("diskweaver-pool", "data",
            [
                UnprotectedTier("/dev/md127", "/dev/disk/by-id/d0", 2),
                UnprotectedTier("/dev/md126", "/dev/disk/by-id/d1", 2),
            ]);
        var newDisks = Array.Empty<Disk>();
        var allDisks = new[] { NewDisk("d0", 2), NewDisk("d1", 2) };

        var options = ExpansionOptionsPlanner.ComputeOptions(pool, allDisks, newDisks, RedundancyLevel.Dwr1);

        Assert.Null(options.Protection);
        Assert.Null(options.Space);
    }
}
