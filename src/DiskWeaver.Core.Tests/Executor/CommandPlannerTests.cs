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

        // Both newly-created arrays must be persisted to mdadm.conf so the kernel can auto-assemble
        // them at boot -- array device paths are passed as `sh -c` positional parameters, never
        // interpolated into the script text, since poolName (part of the path) is caller-supplied.
        var persistStep = Assert.Single(plan.Steps, s => s.Command == "sh");
        Assert.Equal(
            ["-c", "mdadm --detail --scan \"$@\" >> /etc/mdadm/mdadm.conf", "sh",
                "/dev/md/diskweaver-pool-tier0", "/dev/md/diskweaver-pool-tier1"],
            persistStep.Arguments);
        Assert.Contains(plan.Steps, s => s.Command == "update-initramfs" && s.Arguments.SequenceEqual(new[] { "-u" }));
    }

    [Fact]
    public void ThinProvisioned_CreatesThinPoolWithHeadroom_ThenAThinVolumeSizedToTheFullPool()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan, thinProvisioned: true);

        Assert.DoesNotContain(plan.Steps, s => s.Command == "lvcreate"
            && s.Arguments.SequenceEqual(new[] { "-l", "100%FREE", "-n", "data", "diskweaver-pool" }));

        var thinPoolStep = Assert.Single(plan.Steps, s => s.Command == "lvcreate" && s.Arguments.Contains("thin-pool"));
        Assert.Equal(
            ["--type", "thin-pool", "-l", "90%FREE", "-n", "diskweaver-pool-thin-pool", "diskweaver-pool"],
            thinPoolStep.Arguments);

        var thinVolumeStep = Assert.Single(plan.Steps, s => s.Command == "lvcreate" && s.Arguments.Contains("--thin"));
        Assert.Equal(
            ["--thin", "-V", "100%POOL", "-T", "diskweaver-pool/diskweaver-pool-thin-pool", "-n", "data"],
            thinVolumeStep.Arguments);

        // The thin pool must exist (and be part of the VG) before anything tries to carve a volume
        // from it.
        var vgCreateIndex = plan.Steps.ToList().FindIndex(s => s.Command == "vgcreate");
        var thinPoolIndex = plan.Steps.ToList().FindIndex(s => s == thinPoolStep);
        var thinVolumeIndex = plan.Steps.ToList().FindIndex(s => s == thinVolumeStep);
        Assert.True(vgCreateIndex < thinPoolIndex && thinPoolIndex < thinVolumeIndex);
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
    public void MdadmCreate_AlwaysSpecifiesBitmapOrPplExplicitly_NeverPromptsInteractively()
    {
        // mdadm --create prompts "enable write-intent bitmap?" unless --bitmap (or an equivalent
        // --consistency-policy) is given explicitly -- unanswered in a non-interactive script that
        // hangs; answered wrong it corrupts whatever runs next (the same class of bug vgremove's
        // confirmation prompt had). One of the two must never be omitted.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        foreach (var mdadmCreate in plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create")))
        {
            Assert.True(
                mdadmCreate.Arguments.Contains("--bitmap=internal") || mdadmCreate.Arguments.Contains("--consistency-policy=ppl"),
                $"Expected --bitmap=internal or --consistency-policy=ppl in: {string.Join(' ', mdadmCreate.Arguments)}");
        }
    }

    [Fact]
    public void MdadmCreate_Raid5Tier_DefaultsToBitmap()
    {
        // Bitmap is the recommended default: ppl closes the RAID5 write hole (a stripe torn by an
        // unclean shutdown leaving data and parity silently inconsistent) at the cost of a
        // per-write journal that badly cuts sustained write throughput -- too steep a default for
        // most workloads. See Raid5ConsistencyPolicy's own doc for the full trade-off.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        var raid5Creates = plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create") && s.Arguments.Contains("--level=5")).ToList();
        Assert.NotEmpty(raid5Creates);
        foreach (var mdadmCreate in raid5Creates)
        {
            Assert.Contains("--bitmap=internal", mdadmCreate.Arguments);
            Assert.DoesNotContain("--consistency-policy=ppl", mdadmCreate.Arguments);
            Assert.DoesNotContain("--consistency-policy=resync", mdadmCreate.Arguments);
        }
    }

    [Fact]
    public void MdadmCreate_Raid5Tier_PplRequested_UsesPplInsteadOfBitmap()
    {
        // ppl closes the RAID5 write hole (a stripe torn by an unclean shutdown leaving data and
        // parity silently inconsistent) -- a plain bitmap only narrows the resync window, it
        // doesn't prevent that corruption. RAID5-only: the kernel md driver has no ppl support for
        // RAID6's dual P+Q parity, and mirrors have no parity to protect in the first place.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan, raid5ConsistencyPolicy: Raid5ConsistencyPolicy.Ppl);

        var raid5Creates = plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create") && s.Arguments.Contains("--level=5")).ToList();
        Assert.NotEmpty(raid5Creates);
        foreach (var mdadmCreate in raid5Creates)
        {
            Assert.Contains("--consistency-policy=ppl", mdadmCreate.Arguments);
            Assert.DoesNotContain("--bitmap=internal", mdadmCreate.Arguments);
        }
    }

    [Fact]
    public void MdadmCreate_Raid5Tier_ResyncRequested_OmitsBitmapAndPpl()
    {
        // The cheapest option: no bitmap, no ppl -- fastest steady-state writes, but a full-array
        // resync (and an open write hole) after any unclean shutdown.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan, raid5ConsistencyPolicy: Raid5ConsistencyPolicy.Resync);

        var raid5Creates = plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create") && s.Arguments.Contains("--level=5")).ToList();
        Assert.NotEmpty(raid5Creates);
        foreach (var mdadmCreate in raid5Creates)
        {
            Assert.Contains("--consistency-policy=resync", mdadmCreate.Arguments);
            Assert.DoesNotContain("--bitmap=internal", mdadmCreate.Arguments);
            Assert.DoesNotContain("--consistency-policy=ppl", mdadmCreate.Arguments);
        }
    }

    [Fact]
    public void MdadmCreate_MirrorAndRaid6Tiers_IgnoreRaid5ConsistencyPolicy()
    {
        // Only RAID5 has a choice here -- Mirror/RAID6 always keep the plain internal bitmap
        // regardless of what's requested (ppl is RAID5-only; RAID6's write hole needs a dedicated
        // journal device DiskWeaver doesn't select today).
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 2, 2), RedundancyLevel.Dwr2);
        var plan = CommandPlanner.Build(poolPlan, raid5ConsistencyPolicy: Raid5ConsistencyPolicy.Ppl);

        var raid6Create = Assert.Single(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--create") && s.Arguments.Contains("--level=6"));
        Assert.Contains("--bitmap=internal", raid6Create.Arguments);
        Assert.DoesNotContain("--consistency-policy=ppl", raid6Create.Arguments);
    }

    [Fact]
    public void Build_InvalidRaid5ConsistencyPolicy_Throws()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        Assert.Throws<ArgumentException>(() => CommandPlanner.Build(poolPlan, raid5ConsistencyPolicy: (Raid5ConsistencyPolicy)99));
    }

    [Fact]
    public void MdadmCreate_MirrorTier_StillUsesBitmap_NotPpl()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 2), RedundancyLevel.Dwr2);
        var plan = CommandPlanner.Build(poolPlan);

        var mirrorCreate = Assert.Single(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--create"));
        Assert.Contains("--bitmap=internal", mirrorCreate.Arguments);
        Assert.DoesNotContain("--consistency-policy=ppl", mirrorCreate.Arguments);
    }

    [Fact]
    public void MdadmCreate_Raid6Tier_StillUsesBitmap_NotPpl()
    {
        // ppl has no RAID6 support in the kernel md driver (only RAID5's single-parity scheme is
        // implemented) -- closing RAID6's write hole needs a dedicated journal device instead,
        // which DiskWeaver's planner doesn't select today. RAID6 keeps the plain bitmap.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 2, 2), RedundancyLevel.Dwr2);
        var plan = CommandPlanner.Build(poolPlan);

        var raid6Create = Assert.Single(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--create") && s.Arguments.Contains("--level=6"));
        Assert.Contains("--bitmap=internal", raid6Create.Arguments);
        Assert.DoesNotContain("--consistency-policy=ppl", raid6Create.Arguments);
    }

    [Fact]
    public void MdadmCreate_AssumeCleanFalseByDefault_OmitsFlag()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 2), RedundancyLevel.Dwr2);
        var plan = CommandPlanner.Build(poolPlan);

        var mirrorCreate = Assert.Single(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--create"));
        Assert.DoesNotContain("--assume-clean", mirrorCreate.Arguments);
    }

    [Fact]
    public void MdadmCreate_AssumeCleanRequested_AddsFlagToEveryTier()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan, assumeClean: true);

        foreach (var mdadmCreate in plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create")))
        {
            Assert.Contains("--assume-clean", mdadmCreate.Arguments);
        }
    }

    [Fact]
    public void MdadmCreate_DefaultChunkSize_Is64Kib_OnStripedTiers()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan);

        foreach (var mdadmCreate in plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create")))
        {
            Assert.Contains("--chunk=64", mdadmCreate.Arguments);
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    public void MdadmCreate_ChunkSizeRequested_AppliesToStripedTiers(int chunkSizeKb)
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        var plan = CommandPlanner.Build(poolPlan, chunkSizeKb: chunkSizeKb);

        foreach (var mdadmCreate in plan.Steps.Where(s => s.Command == "mdadm" && s.Arguments.Contains("--create")))
        {
            Assert.Contains($"--chunk={chunkSizeKb}", mdadmCreate.Arguments);
        }
    }

    [Fact]
    public void MdadmCreate_MirrorTier_NeverGetsChunkFlag()
    {
        // RAID1 doesn't stripe, so it has no chunk size -- mdadm ignores/warns on --chunk for
        // RAID1, so this is omitted entirely rather than passed and silently disregarded.
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 2), RedundancyLevel.Dwr2);
        var plan = CommandPlanner.Build(poolPlan, chunkSizeKb: 256);

        var mirrorCreate = Assert.Single(plan.Steps, s => s.Command == "mdadm" && s.Arguments.Contains("--create"));
        Assert.DoesNotContain(mirrorCreate.Arguments, a => a.StartsWith("--chunk="));
    }

    [Fact]
    public void Build_InvalidChunkSize_Throws()
    {
        var poolPlan = TieringPlanner.Plan(Disks(2, 2, 4, 4, 4), RedundancyLevel.Dwr1);
        Assert.Throws<ArgumentException>(() => CommandPlanner.Build(poolPlan, chunkSizeKb: 100));
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
    public void BuildReassemble_AssemblesPersistsThenActivates_InThatOrder()
    {
        var plan = CommandPlanner.BuildReassemble();

        Assert.Equal(4, plan.Steps.Count);
        Assert.Equal("mdadm", plan.Steps[0].Command);
        Assert.Equal(["--assemble", "--scan"], plan.Steps[0].Arguments);
        Assert.Equal("sh", plan.Steps[1].Command);
        Assert.Equal("update-initramfs", plan.Steps[2].Command);
        Assert.Equal(["-u"], plan.Steps[2].Arguments);
        Assert.Equal("vgchange", plan.Steps[3].Command);
        Assert.Equal(["-ay"], plan.Steps[3].Arguments);
    }

    [Fact]
    public void BuildReassemble_MdadmConfPersistStep_IsIdempotent_NeverBlindlyAppends()
    {
        // Unlike AppendMdadmConfPersistSteps (used on the build side, where each array name is
        // only ever persisted once), this action can be re-run against arrays already recorded in
        // mdadm.conf from a previous reassemble -- must dedupe per line, not blindly append.
        var plan = CommandPlanner.BuildReassemble();

        var persistStep = Assert.Single(plan.Steps, s => s.Command == "sh");
        var script = Assert.Single(persistStep.Arguments.Skip(1));
        Assert.Contains("grep -qxF", script);
        Assert.Contains("mdadm --detail --scan", script);
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
