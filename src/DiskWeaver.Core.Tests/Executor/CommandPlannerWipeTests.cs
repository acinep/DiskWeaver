namespace DiskWeaver.Executor.Tests;

public class CommandPlannerWipeTests
{
    [Fact]
    public void ZeroesEachPartitionSuperblockBeforeWipingTheParentDisk()
    {
        var partitionPathsByDisk = new Dictionary<string, IReadOnlyList<string>>
        {
            ["/dev/loop3"] = ["/dev/loop3p1"],
        };

        var steps = CommandPlanner.BuildWipe(["/dev/loop3"], partitionPathsByDisk).Steps.ToList();

        var zeroSuperblock = steps.FindIndex(s =>
            s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--zero-superblock", "/dev/loop3p1" }));
        var wipefs = steps.FindIndex(s =>
            s.Command == "wipefs" && s.Arguments.SequenceEqual(new[] { "-a", "/dev/loop3" }));

        Assert.True(zeroSuperblock >= 0, "expected a --zero-superblock step for the partition");
        Assert.True(wipefs >= 0, "expected a wipefs step for the parent disk");
        Assert.True(zeroSuperblock < wipefs, "the partition's superblock must be cleared before the parent disk is wiped");
    }

    [Fact]
    public void DiskWithNoPartitions_OnlyGetsWipefs_NoSpuriousZeroSuperblockStep()
    {
        var partitionPathsByDisk = new Dictionary<string, IReadOnlyList<string>>
        {
            ["/dev/loop4"] = [],
        };

        var steps = CommandPlanner.BuildWipe(["/dev/loop4"], partitionPathsByDisk).Steps.ToList();

        Assert.DoesNotContain(steps, s => s.Command == "mdadm");
        Assert.Contains(steps, s => s.Command == "wipefs" && s.Arguments.SequenceEqual(new[] { "-a", "/dev/loop4" }));
    }

    [Fact]
    public void NoPartitionMapProvided_StillWipesEachDisk()
    {
        var steps = CommandPlanner.BuildWipe(["/dev/loop3", "/dev/loop4"]).Steps.ToList();

        Assert.Contains(steps, s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop3"));
        Assert.Contains(steps, s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop4"));
    }

    [Fact]
    public void RescansEachDiskAfterWipe()
    {
        // Same missing-rescan bug as CommandPlannerTeardownTests: wipefs alone doesn't refresh the
        // kernel's cached partition-table view, so a disk wiped this way can still read back as
        // "not blank" on the very next plan/build request without this rescan.
        var steps = CommandPlanner.BuildWipe(["/dev/loop3", "/dev/loop4"]).Steps.ToList();

        var wipefsLoop3 = steps.FindIndex(s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop3"));
        var partprobeLoop3 = steps.FindIndex(s => s.Command == "partprobe" && s.Arguments.Contains("/dev/loop3"));
        var settle = steps.FindIndex(s => s.Command == "udevadm" && s.Arguments.Contains("settle"));

        Assert.True(wipefsLoop3 >= 0 && wipefsLoop3 < partprobeLoop3, "wipefs must run before its disk's rescan");
        Assert.True(partprobeLoop3 < settle, "the rescan must be waited on via udevadm settle");
    }
}
