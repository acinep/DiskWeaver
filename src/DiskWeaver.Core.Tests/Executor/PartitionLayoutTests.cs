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
