using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class PartitionNamingTests
{
    private const long Tb = 1_000_000_000_000L;

    [Fact]
    public void ByIdPaths_UseDashPartSuffix()
    {
        var poolPlan = TieringPlanner.Plan(
            [new Disk("/dev/disk/by-id/ata-WDC1", 2 * Tb), new Disk("/dev/disk/by-id/ata-WDC2", 2 * Tb)],
            RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("/dev/disk/by-id/ata-WDC1-part1"));
    }

    [Fact]
    public void RawDeviceNameEndingInDigit_GetsPSuffix()
    {
        // Loop devices (and nvme) have no by-id entry and their kernel names end in a digit,
        // so the partition device needs a "p" separator: /dev/loop0p1, not /dev/loop0-part1.
        var poolPlan = TieringPlanner.Plan(
            [new Disk("/dev/loop0", 2 * Tb), new Disk("/dev/loop1", 2 * Tb)],
            RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("/dev/loop0p1"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("/dev/loop1p1"));
    }

    [Fact]
    public void RawDeviceNameNotEndingInDigit_GetsPlainSuffix()
    {
        var poolPlan = TieringPlanner.Plan(
            [new Disk("/dev/sda", 2 * Tb), new Disk("/dev/sdb", 2 * Tb)],
            RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("/dev/sda1"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("/dev/sdb1"));
    }

    [Theory]
    [InlineData("/dev/disk/by-id/ata-WDC1-part1", "/dev/disk/by-id/ata-WDC1")]
    [InlineData("/dev/loop0p1", "/dev/loop0")]
    [InlineData("/dev/nvme0n1p1", "/dev/nvme0n1")]
    [InlineData("/dev/sda1", "/dev/sda")]
    public void ToDiskId_ReversesToPartitionPath(string partitionPath, string expectedDiskId)
    {
        Assert.Equal(expectedDiskId, PartitionNaming.ToDiskId(partitionPath));
    }
}
