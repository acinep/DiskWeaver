using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class CommandPlannerTeardownTests
{
    private const long Tb = 1_000_000_000_000L;

    private static Disk[] Disks(params (string Id, long SizeTb)[] disks) =>
        disks.Select(d => new Disk(d.Id, d.SizeTb * Tb)).ToArray();

    [Fact]
    public void RemovesLvBeforeVgBeforePvBeforeArray()
    {
        var poolPlan = TieringPlanner.Plan(
            Disks(("/dev/loop0", 2), ("/dev/loop1", 2)), RedundancyLevel.Dwr1);
        var steps = CommandPlanner.BuildTeardown(poolPlan).Steps.ToList();

        var lvremove = steps.FindIndex(s => s.Command == "lvremove");
        var vgremove = steps.FindIndex(s => s.Command == "vgremove");
        var pvremove = steps.FindIndex(s => s.Command == "pvremove");
        var mdadmStop = steps.FindIndex(s => s.Command == "mdadm" && s.Arguments.Contains("--stop"));

        Assert.True(lvremove < vgremove, "lvremove must run before vgremove");
        Assert.True(vgremove < pvremove, "vgremove must run before pvremove");
        Assert.True(pvremove < mdadmStop, "pvremove must run before mdadm --stop");
    }

    [Fact]
    public void ThinProvisioned_RemovesTheThinVolumeBeforeTheThinPoolBeforeTheVg()
    {
        var poolPlan = TieringPlanner.Plan(
            Disks(("/dev/loop0", 2), ("/dev/loop1", 2)), RedundancyLevel.Dwr1);
        var steps = CommandPlanner.BuildTeardown(poolPlan, thinProvisioned: true).Steps.ToList();

        var dataRemove = steps.FindIndex(s => s.Command == "lvremove" && s.Arguments.Contains("diskweaver-pool/data"));
        var thinPoolRemove = steps.FindIndex(s => s.Command == "lvremove" && s.Arguments.Contains("diskweaver-pool/diskweaver-pool-thin-pool"));
        var vgremove = steps.FindIndex(s => s.Command == "vgremove");

        Assert.True(dataRemove >= 0 && thinPoolRemove >= 0);
        Assert.True(dataRemove < thinPoolRemove, "the thin volume must be removed before its thin pool");
        Assert.True(thinPoolRemove < vgremove, "every LV must be removed before the VG itself");
    }

    [Fact]
    public void LvremoveAndVgremove_AreForced_NoInteractivePrompt()
    {
        // A script running non-interactively can't answer lvm's "Do you really want to..."
        // confirmation prompts -- without -f, the next script line gets swallowed as the answer.
        var poolPlan = TieringPlanner.Plan(
            Disks(("/dev/loop0", 2), ("/dev/loop1", 2)), RedundancyLevel.Dwr1);
        var steps = CommandPlanner.BuildTeardown(poolPlan).Steps;

        Assert.Contains(steps, s => s.Command == "lvremove" && s.Arguments.Contains("-f"));
        Assert.Contains(steps, s => s.Command == "vgremove" && s.Arguments.Contains("-f"));
    }

    [Fact]
    public void ZeroesSuperblockOnEveryPartition()
    {
        var poolPlan = TieringPlanner.Plan(
            Disks(("/dev/loop0", 2), ("/dev/loop1", 2), ("/dev/loop2", 2)), RedundancyLevel.Dwr1);
        var teardown = CommandPlanner.BuildTeardown(poolPlan);

        Assert.Contains(teardown.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--zero-superblock") && s.Arguments.Contains("/dev/loop0p1"));
        Assert.Contains(teardown.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--zero-superblock") && s.Arguments.Contains("/dev/loop1p1"));
        Assert.Contains(teardown.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--zero-superblock") && s.Arguments.Contains("/dev/loop2p1"));
    }

    [Fact]
    public void WipesEachDiskAndDetachesLoopDevicesOnly()
    {
        var poolPlan = TieringPlanner.Plan(
            Disks(("/dev/loop0", 2), ("/dev/disk/by-id/wwn-real", 2)), RedundancyLevel.Dwr1);
        var teardown = CommandPlanner.BuildTeardown(poolPlan);

        Assert.Contains(teardown.Steps, s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop0"));
        Assert.Contains(teardown.Steps, s => s.Command == "wipefs" && s.Arguments.Contains("/dev/disk/by-id/wwn-real"));

        Assert.Contains(teardown.Steps, s => s.Command == "losetup" && s.Arguments.Contains("/dev/loop0"));
        Assert.DoesNotContain(teardown.Steps, s => s.Command == "losetup" && s.Arguments.Contains("/dev/disk/by-id/wwn-real"));
    }

    [Fact]
    public void RescansEachDiskAfterWipe_BeforeDetachingLoopDevices()
    {
        // Regression test: wipefs writes the blank signature to the underlying storage, but the
        // kernel's own cached partition-table view of the device isn't refreshed by that write
        // alone -- confirmed live: a disk wiped this way still read back as "not blank" on the very
        // next plan/build request. Needs the same partprobe+udevadm settle rescan as the build side.
        var poolPlan = TieringPlanner.Plan(
            Disks(("/dev/loop0", 2), ("/dev/loop1", 2)), RedundancyLevel.Dwr1);
        var steps = CommandPlanner.BuildTeardown(poolPlan).Steps.ToList();

        Assert.Contains(steps, s => s.Command == "partprobe" && s.Arguments.Contains("/dev/loop0"));
        Assert.Contains(steps, s => s.Command == "partprobe" && s.Arguments.Contains("/dev/loop1"));

        var wipefsLoop0 = steps.FindIndex(s => s.Command == "wipefs" && s.Arguments.Contains("/dev/loop0"));
        var partprobeLoop0 = steps.FindIndex(s => s.Command == "partprobe" && s.Arguments.Contains("/dev/loop0"));
        var settle = steps.FindIndex(s => s.Command == "udevadm" && s.Arguments.Contains("settle"));
        var losetupLoop0 = steps.FindIndex(s => s.Command == "losetup" && s.Arguments.Contains("/dev/loop0"));

        Assert.True(wipefsLoop0 < partprobeLoop0, "wipefs must run before its disk's rescan");
        Assert.True(partprobeLoop0 < settle, "the rescan must be waited on via udevadm settle");
        Assert.True(settle < losetupLoop0, "the rescan must finish before detaching the loop device");
    }

    [Fact]
    public void HandlesMultipleTiersUsingSameArrayNumberingAsBuild()
    {
        var poolPlan = TieringPlanner.Plan(
            Disks(("/dev/loop0", 2), ("/dev/loop1", 2), ("/dev/loop2", 4), ("/dev/loop3", 4), ("/dev/loop4", 4)),
            RedundancyLevel.Dwr1);
        var teardown = CommandPlanner.BuildTeardown(poolPlan);

        Assert.Contains(teardown.Steps, s => s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--stop", "/dev/md/diskweaver-pool-tier0" }));
        Assert.Contains(teardown.Steps, s => s.Command == "mdadm" && s.Arguments.SequenceEqual(new[] { "--stop", "/dev/md/diskweaver-pool-tier1" }));
    }
}
