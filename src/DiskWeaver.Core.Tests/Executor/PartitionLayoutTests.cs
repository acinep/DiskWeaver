using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class PartitionLayoutTests
{
    [Fact]
    public void ReducesEachDiskByTotalReservedBytes()
    {
        var disks = new[] { new Disk("/dev/loop0", 2_147_483_648L), new Disk("/dev/loop1", 4_294_967_296L) };

        var adjusted = PartitionLayout.ForPlanning(disks);

        Assert.Equal(2_147_483_648L - PartitionLayout.TotalReservedBytesPerDisk, adjusted[0].SizeBytes);
        Assert.Equal(4_294_967_296L - PartitionLayout.TotalReservedBytesPerDisk, adjusted[1].SizeBytes);
    }

    [Fact]
    public void ThrowsForDiskTooSmallToHoldOverhead()
    {
        var disks = new[] { new Disk("/dev/loop0", PartitionLayout.TotalReservedBytesPerDisk) };

        Assert.Throws<ArgumentException>(() => PartitionLayout.ForPlanning(disks));
    }

    [Fact]
    public void RoundsDownToAnAlignmentBoundary_SoLaterPartitionsStayAlignedTooNotJustTheFirst()
    {
        // Regression test for a real parted warning: "The resulting partition is not properly
        // aligned for best performance" -- a real disk's raw manufacturer size is essentially
        // never itself a round StartAlignmentBytes (1 MiB) multiple, so a tier boundary derived
        // directly from it (TieringPlanner.Plan buckets disks by exact SizeBytes) would land a
        // later partition at an arbitrary, unaligned offset. Real WD40EFRX byte count, confirmed
        // not a 1 MiB multiple.
        var disks = new[] { new Disk("/dev/disk/by-id/wd40efrx", 4_000_787_030_016L) };

        var adjusted = PartitionLayout.ForPlanning(disks);

        Assert.Equal(0, adjusted[0].SizeBytes % PartitionLayout.StartAlignmentBytes);

        // Rounding must only ever shrink usable capacity, and by strictly less than one alignment unit.
        var beforeRounding = 4_000_787_030_016L - PartitionLayout.TotalReservedBytesPerDisk;
        Assert.True(adjusted[0].SizeBytes <= beforeRounding);
        Assert.True(beforeRounding - adjusted[0].SizeBytes < PartitionLayout.StartAlignmentBytes);
    }

    [Fact]
    public void MultiTierDisk_EveryPartitionStartAndEndStaysAligned()
    {
        // The real bug this fixes: only the *first* partition on a disk is aligned by construction
        // (it always starts at exactly StartAlignmentBytes) -- every later partition's start/end is
        // the previous tier's cumulative byte offset, which lands wherever an unrounded, real-world
        // disk size happened to end unless ForPlanning rounds every disk down to an alignment
        // boundary first. Real WD40EFRX/WD80EFAX byte counts, neither a clean 1 MiB multiple.
        var disks = new[]
        {
            new Disk("/dev/disk/by-id/d0", 4_000_787_030_016L),
            new Disk("/dev/disk/by-id/d1", 4_000_787_030_016L),
            new Disk("/dev/disk/by-id/d2", 8_001_563_222_016L),
        };

        var planned = PartitionLayout.PlanForRealDisks(disks, RedundancyLevel.Dwr1);
        var executionPlan = CommandPlanner.Build(planned);

        var mkpartSteps = executionPlan.Steps.Where(s => s.Command == "parted" && s.Arguments.Contains("mkpart")).ToList();
        Assert.NotEmpty(mkpartSteps);

        foreach (var step in mkpartSteps)
        {
            var start = long.Parse(step.Arguments[^2]);
            var endInclusive = long.Parse(step.Arguments[^1]);
            Assert.Equal(0, start % PartitionLayout.StartAlignmentBytes);
            // CommandPlanner's own end argument is the inclusive last byte (endBytes - 1), so it's
            // the *next* partition's start (endInclusive + 1) that must land on the boundary.
            Assert.Equal(0, (endInclusive + 1) % PartitionLayout.StartAlignmentBytes);
        }
    }

    [Fact]
    public void PlanningThenPartitioning_NeverExceedsTheDisksActualSize()
    {
        // Regression test for the real failure: a disk whose raw size exactly equals the bottom
        // tier's segment size used to get a final partition ending 1 MiB past its actual capacity.
        const long rawSize = 2_147_483_648L; // 2 GiB, matches the loop device that failed
        var disks = new[] { new Disk("/dev/loop0", rawSize), new Disk("/dev/loop1", rawSize) };

        // Calls the same PlanForRealDisks production code calls (not a hand-reconstructed
        // ForPlanning+Plan pairing) so this regression test tracks the real code path.
        var planned = PartitionLayout.PlanForRealDisks(disks, RedundancyLevel.Dwr1);
        var executionPlan = CommandPlanner.Build(planned);

        var partitionStep = executionPlan.Steps.Single(s =>
            s.Command == "parted" && s.Arguments.Contains("/dev/loop0") && s.Arguments.Contains("mkpart"));
        var endByte = long.Parse(partitionStep.Arguments[^1]);

        Assert.True(endByte < rawSize, $"partition end {endByte} must be strictly less than the disk's raw size {rawSize}");
    }
}
