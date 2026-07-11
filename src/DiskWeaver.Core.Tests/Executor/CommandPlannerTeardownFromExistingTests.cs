using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class CommandPlannerTeardownFromExistingTests
{
    [Fact]
    public void UsesRealArrayDeviceAndPoolNamesInsteadOfDefaults()
    {
        var pool = new ExistingPoolState(
            "custom-pool",
            "custom-volume",
            [new ExistingTier("/dev/md127", 2_000_000_000_000, ["/dev/loop0", "/dev/loop1"], RaidLevel.Mirror, ["/dev/loop0p1", "/dev/loop1p1"])]);

        var steps = CommandPlanner.BuildTeardownFromExisting(pool).Steps;

        Assert.Contains(steps, s => s.Command == "lvremove" && s.Arguments.Contains("custom-pool/custom-volume"));
        Assert.Contains(steps, s => s.Command == "vgremove" && s.Arguments.Contains("custom-pool"));
        Assert.Contains(steps, s => s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--stop", "/dev/md127" }));
        Assert.Contains(steps, s => s.Command == "pvremove" && s.Arguments.Contains("/dev/md127"));
    }

    [Fact]
    public void UsesEachTiersRealPartitionPaths_NotASegmentSizeOrderingGuess()
    {
        // Regression test: md1 (mirror, bigger segment) was built FIRST on loop2/loop3, taking
        // partition 1 on each; md0 (RAID5, smaller/bottom segment) was only grown into loop2/loop3
        // AFTER that, via BuildIncremental's same-level grow automation, taking partition 2. The
        // "smallest segment = partition 1" assumption this method used to make would target the
        // wrong partition for md0's superblock wipe -- using PartitionPaths directly avoids that.
        var pool = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [
                new ExistingTier("/dev/md1", 4_000_000_000_000, ["/dev/loop2", "/dev/loop3"], RaidLevel.Mirror, ["/dev/loop2p1", "/dev/loop3p1"]),
                new ExistingTier("/dev/md0", 2_000_000_000_000, ["/dev/loop0", "/dev/loop1", "/dev/loop2", "/dev/loop3"], RaidLevel.Raid5, ["/dev/loop0p1", "/dev/loop1p1", "/dev/loop2p2", "/dev/loop3p2"]),
            ]);

        var steps = CommandPlanner.BuildTeardownFromExisting(pool).Steps;

        Assert.Contains(steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--zero-superblock") && s.Arguments.Contains("/dev/loop2p1"));
        Assert.Contains(steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--zero-superblock") && s.Arguments.Contains("/dev/loop2p2"));
        Assert.Contains(steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--zero-superblock") && s.Arguments.Contains("/dev/loop3p1"));
        Assert.Contains(steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--zero-superblock") && s.Arguments.Contains("/dev/loop3p2"));
    }

    [Fact]
    public void WipesEachDiskAndDetachesLoopDevicesOnly()
    {
        var pool = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2_000_000_000_000, ["/dev/loop0", "/dev/disk/by-id/wwn-real"], RaidLevel.Mirror, ["/dev/loop0p1", "/dev/disk/by-id/wwn-real-part1"])]);

        var steps = CommandPlanner.BuildTeardownFromExisting(pool).Steps;

        Assert.Contains(steps, s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop0"));
        Assert.Contains(steps, s => s.Command == "wipefs" && s.Arguments.Contains("/dev/disk/by-id/wwn-real"));
        Assert.Contains(steps, s => s.Command == "losetup" && s.Arguments.Contains("/dev/loop0"));
        Assert.DoesNotContain(steps, s => s.Command == "losetup" && s.Arguments.Contains("/dev/disk/by-id/wwn-real"));
    }

    [Fact]
    public void RescansEachDiskAfterWipe_BeforeDetachingLoopDevices()
    {
        // Same missing-rescan bug as CommandPlannerTeardownTests, but for the real "tear down
        // whatever GET /pools sees" path Cockpit actually drives -- confirmed live: a disk wiped
        // via this method still read back as "not blank" on the very next plan/build request.
        var pool = new ExistingPoolState(
            "diskweaver-pool",
            "data",
            [new ExistingTier("/dev/md127", 2_000_000_000_000, ["/dev/loop0", "/dev/loop1"], RaidLevel.Mirror,
                ["/dev/loop0p1", "/dev/loop1p1"])]);

        var steps = CommandPlanner.BuildTeardownFromExisting(pool).Steps.ToList();

        var wipefsLoop0 = steps.FindIndex(s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop0"));
        var partprobeLoop0 = steps.FindIndex(s => s.Command == "partprobe" && s.Arguments.Contains("/dev/loop0"));
        var settle = steps.FindIndex(s => s.Command == "udevadm" && s.Arguments.Contains("settle"));
        var losetupLoop0 = steps.FindIndex(s => s.Command == "losetup" && s.Arguments.Contains("/dev/loop0"));

        Assert.True(wipefsLoop0 >= 0 && wipefsLoop0 < partprobeLoop0, "wipefs must run before its disk's rescan");
        Assert.True(partprobeLoop0 < settle, "the rescan must be waited on via udevadm settle");
        Assert.True(settle < losetupLoop0, "the rescan must finish before detaching the loop device");
    }
}
