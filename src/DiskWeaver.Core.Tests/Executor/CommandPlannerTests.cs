using DiskWeaver.Planner;

namespace DiskWeaver.Executor.Tests;

public class CommandPlannerTests
{
    private const long Tb = 1_000_000_000_000L;

    private static Disk[] Disks(params long[] sizesTb) =>
        sizesTb.Select((size, i) => new Disk($"/dev/disk/by-id/disk{i}", size * Tb)).ToArray();

    [Fact]
    public void MixedSizes_Dwr1_EmitsTwoArraysAndOnePoolVolume()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--level=5")
            && s.Arguments.Contains("/dev/md/diskweaver-pool-tier0"));
        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--level=5")
            && s.Arguments.Contains("/dev/md/diskweaver-pool-tier1"));

        var vgStep = Assert.Single(plan.Steps, s => s.Command == "vgcreate");
        Assert.Equal(
            ["--addtag", "diskweaver-managed", "diskweaver-pool", "/dev/md/diskweaver-pool-tier0", "/dev/md/diskweaver-pool-tier1"],
            vgStep.Arguments);

        Assert.Contains(plan.Steps, s => s.Command == "lvcreate"
            && s.Arguments.SequenceEqual(new[] { "-l", "100%FREE", "-n", "data", "diskweaver-pool" }));
    }

    [Fact]
    public void ReservedSegment_EmitsCommentOnly_NoInvocation()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 2, 6), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        var commentStep = Assert.Single(plan.Steps, s => s.Command is null);
        Assert.Contains("reserved", commentStep.Description);
    }

    [Fact]
    public void Dwr2_ThreeWayMirror_UsesLevel1WithThreeDevices()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 2), RedundancyLevel.Dwr2);
        var plan = CommandPlanner.Build(poolPlan);

        Assert.Contains(plan.Steps, s => s.Command == "mdadm"
            && s.Arguments.Contains("--level=1")
            && s.Arguments.Contains("--raid-devices=3"));
    }

    [Fact]
    public void MdadmCreate_AlwaysSpecifiesBitmapExplicitly_NeverPromptsInteractively()
    {
        // mdadm --create prompts "enable write-intent bitmap?" unless --bitmap is given explicitly
        // -- unanswered in a non-interactive script that hangs; answered wrong it corrupts whatever
        // runs next (the same class of bug vgremove's confirmation prompt had). Must never be omitted.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        foreach (var mdadmCreate in plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create")))
        {
            Assert.Contains("--bitmap=internal", mdadmCreate.Arguments);
        }
    }

    [Fact]
    public void MdadmCreate_AlwaysSpecifiesRun_NeverPromptsAboutStaleMemberSuperblocks()
    {
        // A different mdadm --create prompt than the bitmap one above: "<partition> appears to be
        // part of a raid array... Continue creating array?" if any input partition still has an
        // old RAID superblock (e.g. a reused loop-device partition number/offset from a torn-down
        // array that was never zero-superblocked). Non-TTY stdin makes mdadm default to "N" and
        // abort rather than hang, but it's still a script stopping on an unanswerable prompt.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        foreach (var mdadmCreate in plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create")))
        {
            Assert.Contains("--run", mdadmCreate.Arguments);
        }
    }

    [Fact]
    public void EachDiskIsLabeledOnlyOnce_EvenAcrossMultipleTiers()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        var labelSteps = plan.Steps.Where(s => s.Command == "parted" && s.Arguments.Contains("mklabel"));
        Assert.Equal(5, labelSteps.Count());
    }

    [Fact]
    public void PartitionOffsets_AreSequentialPerDisk()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        var disk2Partitions = plan.Steps
            .Where(s => s.Command == "parted" && s.Arguments.Contains("/dev/disk/by-id/disk2") && s.Arguments.Contains("mkpart"))
            .ToList();

        Assert.Equal(2, disk2Partitions.Count);

        const long oneMiB = 1024 * 1024;
        const long segmentBytes = 2 * Tb;

        Assert.Equal($"{oneMiB}", GetStart(disk2Partitions[0]));
        Assert.Equal($"{oneMiB + segmentBytes - 1}", GetEnd(disk2Partitions[0]));
        Assert.Equal($"{oneMiB + segmentBytes}", GetStart(disk2Partitions[1]));

        static string GetStart(ExecutionStep step) => step.Arguments[^2];
        static string GetEnd(ExecutionStep step) => step.Arguments[^1];
    }

    [Fact]
    public void NoProtection_SingleDisk_CreatesDegradedTwoSlotMirrorWithMissingPlaceholder()
    {
        var poolPlan = TieringPlanner.Plan(Disks(4), RedundancyLevel.None);
        var plan = CommandPlanner.Build(poolPlan);

        var createStep = Assert.Single(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--create"));
        Assert.Contains("--level=1", createStep.Arguments);
        Assert.Contains("--raid-devices=2", createStep.Arguments);
        Assert.Equal("missing", createStep.Arguments[^1]);
        Assert.Equal("/dev/disk/by-id/disk0-part1", createStep.Arguments[^2]);
    }

    [Fact]
    public void NoProtection_SingleDisk_TagsThePvAsUnprotected()
    {
        var poolPlan = TieringPlanner.Plan(Disks(4), RedundancyLevel.None);
        var plan = CommandPlanner.Build(poolPlan);

        var tagStep = Assert.Single(plan.Steps, s => s.Command == "pvchange");
        Assert.Equal(["--addtag", "diskweaver-unprotected", "/dev/md/diskweaver-pool-tier0"], tagStep.Arguments);

        // The tag step must come after pvcreate, not before -- pvchange needs the PV to exist.
        var pvcreateIndex = plan.Steps.ToList().FindIndex(s => s.Command == "pvcreate");
        var tagIndex = plan.Steps.ToList().FindIndex(s => s.Command == "pvchange");
        Assert.True(pvcreateIndex >= 0 && tagIndex > pvcreateIndex);
    }

    [Fact]
    public void Protected_MultiDiskTier_IsNotTagged()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        Assert.DoesNotContain(plan.Steps, s => s.Command == "pvchange");
    }

    [Fact]
    public void RescansAndSettlesAfterEachPartition_BeforeMdadmCreate()
    {
        // Regression test: parted writing a partition table doesn't reliably make the kernel
        // expose the new partition device node on its own -- especially for loop devices, where
        // this is a genuinely missing rescan, not just a timing race. mdadm --create needs every
        // partition's device node (e.g. /dev/loop0p1) to already exist.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        var steps = plan.Steps.ToList();
        foreach (var mkpartStep in steps.Where(s => s.Command == "parted" && s.Arguments.Contains("mkpart")))
        {
            var diskId = mkpartStep.Arguments[1];
            var index = steps.IndexOf(mkpartStep);
            Assert.Equal("partprobe", steps[index + 1].Command);
            Assert.Equal(diskId, steps[index + 1].Arguments[0]);
        }

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            if (plan.Steps[i].Command != "mdadm")
            {
                continue;
            }

            Assert.Equal("udevadm", plan.Steps[i - 1].Command);
            Assert.Contains("settle", plan.Steps[i - 1].Arguments);
        }
    }
}
