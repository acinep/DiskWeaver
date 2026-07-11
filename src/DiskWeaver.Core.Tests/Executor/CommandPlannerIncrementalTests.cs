using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class CommandPlannerIncrementalTests
{
    private const long Tb = 1_000_000_000_000L;

    private static Disk Disk(string id, long sizeTb) => new($"/dev/disk/by-id/{id}", sizeTb * Tb);

    // These fixtures only exercise BuildIncremental's classification/grow logic, which reads
    // DiskIds/SegmentSizeBytes/RaidLevel, not PartitionPaths -- "-part1" for every disk here is a
    // placeholder, not a claim about real on-disk numbering (see CommandPlannerTeardownFromExistingTests
    // for tests that actually exercise PartitionPaths usage).
    private static string[] PartitionPaths(params string[] diskIds) =>
        diskIds.Select(id => PartitionNaming.ToPartitionPath(id, 1)).ToArray();

    [Fact]
    public void OneDiskNamedInTwoSeparateTargetTiers_SplitsIntoSequentialPartitionsCorrectly()
    {
        // Validates the key claim behind ProtectionPlanner/the daemon's "protect this tier"
        // modes: a hand-constructed desired PoolPlan naming the SAME new disk in two independent
        // existing tiers' DiskIds (rather than TieringPlanner recomputing everything) needs zero
        // CommandPlanner changes -- BuildIncremental's shared nextPartitionNumber/diskOffsetBytes
        // dictionaries already thread correctly across multiple grow candidates in one call.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [
                new ExistingTier("/dev/md127", 2 * Tb, ["/dev/disk/by-id/d0"], RaidLevel.Mirror,
                    PartitionPaths("/dev/disk/by-id/d0")),
                new ExistingTier("/dev/md126", 2 * Tb, ["/dev/disk/by-id/d1"], RaidLevel.Mirror,
                    PartitionPaths("/dev/disk/by-id/d1")),
            ]);

        // Hand-constructed exactly the way the daemon's auto/targeted protect modes build it:
        // current tiers verbatim, except each gets the new disk "/dev/disk/by-id/d2" appended.
        var desired = new PoolPlan(
            [
                new Tier(2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d2"], RaidLevel.Mirror, 2 * Tb),
                new Tier(2 * Tb, ["/dev/disk/by-id/d1", "/dev/disk/by-id/d2"], RaidLevel.Mirror, 2 * Tb),
            ],
            []);

        var plan = CommandPlanner.BuildIncremental(existing, desired);

        // d2 must be labeled (GPT) only once, then get two sequential partitions -- one per tier.
        Assert.Single(plan.Steps, s => s.Command == "parted" && s.Arguments.Contains("mklabel") && s.Arguments.Contains("/dev/disk/by-id/d2"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.SequenceEqual(new[] { "--add", "/dev/md127", "/dev/disk/by-id/d2-part1" }));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.SequenceEqual(new[] { "--add", "/dev/md126", "/dev/disk/by-id/d2-part2" }));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--grow") && s.Arguments.Contains("/dev/md127"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--grow") && s.Arguments.Contains("/dev/md126"));
    }

    [Fact]
    public void AddingTwoLargerDisks_CreatesNewTopTier_AndAutomaticallyGrowsBottomTierInPlace()
    {
        // Existing pool: three 2TB disks, RAID5. Note the bottom tier always spans every disk in the
        // pool (its boundary is the pool's minimum size), so adding any disk always touches it -- there's
        // no such thing as an addition that leaves every existing tier fully alone.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier(
                "/dev/md/diskweaver-tier0",
                2 * Tb,
                ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"],
                RaidLevel.Raid5,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"))]);

        // Add two new 4TB disks -- large enough to also form a genuine new top tier together.
        var desired = TieringPlanner.Plan(
            [Disk("d0", 2), Disk("d1", 2), Disk("d2", 2), Disk("d3", 4), Disk("d4", 4)], RedundancyLevel.Dwr1);

        var plan = CommandPlanner.BuildIncremental(existing, desired);

        // Bottom tier stays RAID5 (3 -> 5 members, still >= R+2) -- a same-level grow, which is
        // automated: the new disks' bottom-segment partitions are added as spares and the array
        // is grown/reshaped in place, then LVM is told about the new size.
        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.SequenceEqual(new[] { "--add", "/dev/md/diskweaver-tier0", "/dev/disk/by-id/d3-part1" }));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.SequenceEqual(new[] { "--add", "/dev/md/diskweaver-tier0", "/dev/disk/by-id/d4-part1" }));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--grow") && s.Arguments.Contains("/dev/md/diskweaver-tier0") && s.Arguments.Contains("--raid-devices=5"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--wait", "/dev/md/diskweaver-tier0" }));
        Assert.Contains(plan.Steps, s => s.Command == "pvresize" && s.Arguments.Contains("/dev/md/diskweaver-tier0"));

        // The two new disks ALSO form a genuine new top tier from their (2,4] excess.
        Assert.Contains(plan.Steps, s => s.Command == "parted" && s.Arguments.Contains("mklabel") && s.Arguments.Contains("/dev/disk/by-id/d3"));
        Assert.Contains(plan.Steps, s => s.Command == "parted" && s.Arguments.Contains("mklabel") && s.Arguments.Contains("/dev/disk/by-id/d4"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--create") && s.Arguments.Contains("/dev/md/diskweaver-pool-tier1"));

        var vgextend = Assert.Single(plan.Steps, s => s.Command == "vgextend");
        Assert.Equal(["diskweaver-pool", "/dev/md/diskweaver-pool-tier1"], vgextend.Arguments);

        Assert.Contains(plan.Steps, s => s.Command == "lvextend");
    }

    [Fact]
    public void AddingMatchingDisk_ReclaimsReservedSegment_WithoutTouchingExistingTier()
    {
        // Existing pool: 2,2,2 disks -- tier0 covers all three; the 6TB disk's excess sat reserved.
        // Model: 2,2,2,6 existing where only (0,2] is built, (2,6] reserved.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md/diskweaver-tier0", 2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"], RaidLevel.Raid5,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"))]);

        // A second 6TB-class disk arrives, reclaiming the (2,6] segment.
        var desired = TieringPlanner.Plan(
            [Disk("d0", 2), Disk("d1", 2), Disk("d2", 6), Disk("d3", 6)], RedundancyLevel.Dwr1);

        var plan = CommandPlanner.BuildIncremental(existing, desired);

        // d2 already owns partition 1 (from tier0); its new partition for the reclaimed segment must be part 2,
        // starting where the first partition left off (1MiB + 2TB), not restart at 1MiB.
        var d2SecondPartition = Assert.Single(plan.Steps, s =>
            s.Command == "parted" && s.Arguments.Contains("mkpart") && s.Description.Contains("d2-part2"));
        const long oneMiB = 1024 * 1024;
        Assert.Equal($"{oneMiB + 2 * Tb}", d2SecondPartition.Arguments[^2]);

        // d2 must NOT be re-labeled (it already has a GPT table from tier0).
        Assert.DoesNotContain(plan.Steps, s =>
            s.Command == "parted" && s.Arguments.Contains("mklabel") && s.Arguments.Contains("/dev/disk/by-id/d2"));

        // The new disk d3 does get labeled.
        Assert.Contains(plan.Steps, s =>
            s.Command == "parted" && s.Arguments.Contains("mklabel") && s.Arguments.Contains("/dev/disk/by-id/d3"));

        Assert.Contains(plan.Steps, s => s.Command == "vgextend");
        Assert.Contains(plan.Steps, s => s.Command == "lvextend");
    }

    [Fact]
    public void AddingSameSizeDisk_ToExistingTier_GrowsInPlaceAutomatically()
    {
        // Existing pool: 2,2,2 RAID5.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md/diskweaver-tier0", 2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"], RaidLevel.Raid5,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"))]);

        // A 4th 2TB disk arrives -- same segment, one more member, same RAID level (still RAID5).
        var desired = TieringPlanner.Plan([Disk("d0", 2), Disk("d1", 2), Disk("d2", 2), Disk("d3", 2)], RedundancyLevel.Dwr1);

        var plan = CommandPlanner.BuildIncremental(existing, desired);

        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.SequenceEqual(new[] { "--add", "/dev/md/diskweaver-tier0", "/dev/disk/by-id/d3-part1" }));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--grow") && s.Arguments.Contains("--raid-devices=4"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--wait", "/dev/md/diskweaver-tier0" }));
        Assert.Contains(plan.Steps, s => s.Command == "pvresize" && s.Arguments.Contains("/dev/md/diskweaver-tier0"));

        // No new tier/PV was created, so no vgextend -- but the array did grow, so the LV still needs extending.
        Assert.DoesNotContain(plan.Steps, s => s.Command == "vgextend");
        Assert.Contains(plan.Steps, s => s.Command == "lvextend");
    }

    [Fact]
    public void AddingADisk_MigratesAnExistingMirrorToRaid5Automatically()
    {
        // Existing pool: 2-disk mirror. Adding one matching-size (2TB) disk makes the bottom
        // segment have 3 qualifying disks -- a RAID level migration (Mirror -> RAID5), not just a
        // device-count grow. mdadm supports this via --grow --level=X --raid-devices=Y in one
        // invocation (after --add-ing the new disk's partition as a spare), same shape as a
        // same-level grow otherwise.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1"], RaidLevel.Mirror,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1"))]);

        var desired = TieringPlanner.Plan([Disk("d0", 2), Disk("d1", 2), Disk("d2", 2)], RedundancyLevel.Dwr1);

        var plan = CommandPlanner.BuildIncremental(existing, desired);

        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.SequenceEqual(new[] { "--add", "/dev/md127", "/dev/disk/by-id/d2-part1" }));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--grow") && s.Arguments.Contains("--raid-devices=3") && s.Arguments.Contains("--level=5"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--wait", "/dev/md127" }));
        Assert.Contains(plan.Steps, s => s.Command == "pvresize" && s.Arguments.Contains("/dev/md127"));
        Assert.Contains(plan.Steps, s => s.Command == "lvextend");
        Assert.DoesNotContain(plan.Steps, s => s.Command is null && s.Description.Contains("Not automated"));
    }

    [Fact]
    public void AchievedCapacityBytes_IncludesLevelMigrationGrowCandidates_SinceThatIsNowAutomatedToo()
    {
        // Existing pool: 2-disk mirror (fake DWR-1 pool). Adding one larger disk alone can't form
        // a new redundant tier on its own -- it's a grow candidate for the existing tier (member
        // count 2->3, Mirror->RAID5). BuildIncremental now automates this level migration the same
        // way as a same-level grow, so achieved should reflect the full 3-disk RAID5 capacity.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1"], RaidLevel.Mirror,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1"))]);

        var desired = TieringPlanner.Plan([Disk("d0", 2), Disk("d1", 2), Disk("d2", 4)], RedundancyLevel.Dwr1);

        var achieved = CommandPlanner.AchievedCapacityBytes(existing, desired);

        Assert.Equal(2 * 2 * Tb, achieved); // (3-1)*2TB
    }

    [Fact]
    public void AchievedCapacityBytes_IncludesSameLevelGrowCandidates_SinceThatIsNowAutomated()
    {
        // Existing pool: 2,2,2 RAID5 (usable = (3-1)*2TB = 4TB). A 4th matching disk arrives --
        // same level (still RAID5), so BuildIncremental automates the grow, and the achieved
        // capacity should reflect the desired 4-disk RAID5's usable bytes, not the old 3-disk one.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md/diskweaver-tier0", 2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"], RaidLevel.Raid5,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"))]);

        var desired = TieringPlanner.Plan([Disk("d0", 2), Disk("d1", 2), Disk("d2", 2), Disk("d3", 2)], RedundancyLevel.Dwr1);

        var achieved = CommandPlanner.AchievedCapacityBytes(existing, desired);

        Assert.Equal(3 * 2 * Tb, achieved); // (4-1)*2TB
    }

    [Fact]
    public void AchievedCapacityBytes_IncludesGenuinelyNewTiers_AndGrownExistingTier()
    {
        // Existing pool: 2-disk mirror. Two more matching-size (4TB) disks arrive together: the
        // bottom segment now has 4 qualifying disks, migrating the existing tier from Mirror to
        // RAID5 (automated), and the two 4TB disks' excess forms a genuine new top tier (Mirror).
        // Both are achieved, since both are automated.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1"], RaidLevel.Mirror,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1"))]);

        var desired = TieringPlanner.Plan(
            [Disk("d0", 2), Disk("d1", 2), Disk("d2", 4), Disk("d3", 4)], RedundancyLevel.Dwr1);

        var achieved = CommandPlanner.AchievedCapacityBytes(existing, desired);

        // Bottom tier migrated to 4-disk RAID5 ((4-1)*2TB = 6TB) + new top tier (2TB mirror).
        Assert.Equal(desired.PoolCapacityBytes, achieved);
        Assert.Equal(3 * 2 * Tb + 2 * Tb, achieved);
    }

    [Fact]
    public void AddingADisk_MigratesA2DiskMirrorToRaid6ViaAnIntermediateRaid5Hop()
    {
        // A 2-disk mirror needs to become RAID6 directly (e.g. jumping straight to a RAID6 target
        // tier). mdadm has no direct RAID1 -> RAID6 reshape -- "mdadm: Impossibly level change
        // request for RAID1" is what real mdadm says if you try. It has to go via an intermediate
        // RAID5 hop instead (RAID5 -> RAID6 is directly supported), so BuildGrowSteps should emit
        // two grow+wait pairs: mirror -> 4-disk RAID5 (jumping straight to the final device count,
        // since that combined change is legal from a 2-disk source), then RAID5 -> RAID6 in place.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1"], RaidLevel.Mirror,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1"))]);

        var desired = new Tier(2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2", "/dev/disk/by-id/d3"], RaidLevel.Raid6, 2 * 2 * Tb);
        var desiredPlan = new PoolPlan([desired], []);

        var plan = CommandPlanner.BuildIncremental(existing, desiredPlan);

        var growSteps = plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--grow")).ToList();
        Assert.Equal(2, growSteps.Count);

        Assert.Contains(growSteps, s => s.Arguments.Contains("--level=5") && s.Arguments.Contains("--raid-devices=4"));
        Assert.Contains(growSteps, s => s.Arguments.Contains("--level=6") && !s.Arguments.Any(a => a.StartsWith("--raid-devices", StringComparison.Ordinal)));

        // Two reshapes means two waits, one after each hop.
        Assert.Equal(2, plan.Steps.Count(s => s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--wait", "/dev/md127" })));
        Assert.Contains(plan.Steps, s => s.Command == "pvresize" && s.Arguments.Contains("/dev/md127"));
    }

    [Fact]
    public void AddingDisksToA3DiskMirror_NeedingRaid6_ThrowsInsteadOfEmittingAnImpossibleReshape()
    {
        // Reproduces a real failure: an DWR-2 pool's minimum-redundancy tier is a 3-disk mirror
        // (m == r+1 == 3), not a 2-disk one. mdadm's RAID1 -> RAID5 hop only works from an
        // exactly-2-disk RAID1, so a 3-disk mirror has no reshape path to RAID5 or RAID6 at all --
        // this must be refused up front rather than emitting a plan that fails mid-execution with
        // "mdadm: Impossibly level change request for RAID1" after spares have already been added.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2 * Tb,
                ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"], RaidLevel.Mirror,
                PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2"))]);

        var desired = TieringPlanner.Plan(
            [Disk("d0", 2), Disk("d1", 2), Disk("d2", 2), Disk("d3", 2), Disk("d4", 2)], RedundancyLevel.Dwr2);

        var ex = Assert.Throws<InvalidOperationException>(() => CommandPlanner.BuildIncremental(existing, desired));
        Assert.Contains("/dev/md127", ex.Message);
        Assert.Contains("3-disk mirror", ex.Message);
    }

    [Fact]
    public void AddingADisk_ToARealDegradedMirror_FillsTheMissingSlotInsteadOfGrowing()
    {
        // Existing pool: a real Dwr1 mirror that lost a disk to failure -- ConfiguredMemberCount=2
        // (the array is still configured for 2 members) even though only 1 real disk is present
        // now. Adding a matching-size replacement disk should just fill the missing slot via a
        // plain `mdadm --add` (mdadm auto-resyncs it), NOT a `--grow --raid-devices`/level change,
        // since the array was already configured for 2 members from the start.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2 * Tb, ["/dev/disk/by-id/d0"], RaidLevel.Mirror,
                PartitionPaths("/dev/disk/by-id/d0"), ConfiguredMemberCount: 2)]);

        var desired = TieringPlanner.Plan([Disk("d0", 2), Disk("d1", 2)], RedundancyLevel.Dwr1);

        var plan = CommandPlanner.BuildIncremental(existing, desired);

        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.SequenceEqual(new[] { "--add", "/dev/md127", "/dev/disk/by-id/d1-part1" }));
        Assert.DoesNotContain(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--grow"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--wait", "/dev/md127" }));
        Assert.Contains(plan.Steps, s => s.Command == "pvresize" && s.Arguments.Contains("/dev/md127"));

        // Filling a missing slot adds resilience, not capacity (mdadm reports the same Array Size
        // whether a mirror is degraded or fully populated) -- lvextend -l +100%FREE would find zero
        // free extents and fail outright ("no size change"), confirmed live, so it must be omitted.
        Assert.DoesNotContain(plan.Steps, s => s.Command == "lvextend");
    }

    [Fact]
    public void AddingTwoDisks_ToARealDegradedMirror_ThrowsRatherThanFillAndGrowInOneStep()
    {
        // Filling the missing slot AND growing further (e.g. straight to a 3-disk RAID5) in the
        // same expand isn't a legal single mdadm operation -- mdadm's RAID1->RAID5 migration
        // requires the source array to already have exactly 2 real members, not 1 real + 1
        // resyncing. Must be refused up front (two separate expands instead), not emit a plan
        // that fails mid-execution.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2 * Tb, ["/dev/disk/by-id/d0"], RaidLevel.Mirror,
                PartitionPaths("/dev/disk/by-id/d0"), ConfiguredMemberCount: 2)]);

        var desired = TieringPlanner.Plan([Disk("d0", 2), Disk("d1", 2), Disk("d2", 2)], RedundancyLevel.Dwr1);

        var ex = Assert.Throws<InvalidOperationException>(() => CommandPlanner.BuildIncremental(existing, desired));
        Assert.Contains("/dev/md127", ex.Message);
        Assert.Contains("degraded", ex.Message);
    }

    [Fact]
    public void UpgradingAllJbodPoolToSharedRedundancy_ThrowsMergeConflictInsteadOfOrphaningExtraTiers()
    {
        // Real incident: a pool built as 2 independent RedundancyLevel.None (JBOD) tiers, then
        // expanded requesting Dwr1 explicitly -- Dwr1's tiering groups ALL matching-size disks
        // (existing + new) into one shared tier, which both independent existing tiers are each a
        // proper subset of. There's no mdadm operation that merges two already-built arrays into
        // one; this must be refused with a clear message naming both, not silently keep one as a
        // grow candidate and orphan the other with a confusing "doesn't correspond to any tier" error.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [
                new ExistingTier("/dev/md127", 2 * Tb, ["/dev/disk/by-id/d0"], RaidLevel.Mirror,
                    PartitionPaths("/dev/disk/by-id/d0")),
                new ExistingTier("/dev/md126", 2 * Tb, ["/dev/disk/by-id/d1"], RaidLevel.Mirror,
                    PartitionPaths("/dev/disk/by-id/d1")),
            ]);

        var desired = TieringPlanner.Plan(
            [Disk("d0", 2), Disk("d1", 2), Disk("d2", 4), Disk("d3", 4)], RedundancyLevel.Dwr1);

        var ex = Assert.Throws<InvalidOperationException>(() => CommandPlanner.BuildIncremental(existing, desired));
        Assert.Contains("/dev/md127", ex.Message);
        Assert.Contains("/dev/md126", ex.Message);
        Assert.Contains("merging", ex.Message);
        Assert.Contains("rebuild", ex.Message);
    }

    [Fact]
    public void AddingDisk_WithSizeBetweenExistingBoundaries_Throws()
    {
        // Existing pool: 2,2,4,4 -- tier0 (0,2] all four disks mirror-ish RAID5, tier1 (2,4] mirror on the two 4TB disks.
        var existing = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [
                new ExistingTier("/dev/md/diskweaver-tier0", 2 * Tb, ["/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2", "/dev/disk/by-id/d3"], RaidLevel.Raid5,
                    PartitionPaths("/dev/disk/by-id/d0", "/dev/disk/by-id/d1", "/dev/disk/by-id/d2", "/dev/disk/by-id/d3")),
                new ExistingTier("/dev/md/diskweaver-tier1", 2 * Tb, ["/dev/disk/by-id/d2", "/dev/disk/by-id/d3"], RaidLevel.Mirror,
                    PartitionPaths("/dev/disk/by-id/d2", "/dev/disk/by-id/d3")),
            ]);

        // A new 3TB disk splits the old (2,4] segment into (2,3] and (3,4].
        var desired = TieringPlanner.Plan(
            [Disk("d0", 2), Disk("d1", 2), Disk("d2", 4), Disk("d3", 4), Disk("d4", 3)], RedundancyLevel.Dwr1);

        var ex = Assert.Throws<InvalidOperationException>(() => CommandPlanner.BuildIncremental(existing, desired));
        Assert.Contains("diskweaver-tier1", ex.Message);
    }
}
