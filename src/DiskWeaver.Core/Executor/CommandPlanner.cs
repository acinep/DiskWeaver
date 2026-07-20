using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Turns a <see cref="PoolPlan"/> into the ordered mdadm/partitioning/LVM
/// commands needed to build or grow it. Pure and platform-independent —
/// see docs/execution.md for the layout invariant this relies on, the
/// scenarios it covers, and those it deliberately refuses.
/// </summary>
public static class CommandPlanner
{
    /// <summary>
    /// Plans a brand-new pool from scratch. All disks are assumed blank. Each tier's mdadm array is
    /// named <c>/dev/md/{poolName}-tier{N}</c> -- deriving it from <paramref name="poolName"/>
    /// (rather than a fixed "diskweaver-tier" prefix) is what lets two differently-named pools
    /// coexist on one host without an mdadm "array name already in use" collision.
    /// </summary>
    public static ExecutionPlan Build(
        PoolPlan plan,
        string poolName = "diskweaver-pool",
        string volumeName = "data")
    {
        var labeledDisks = new HashSet<string>();
        var nextPartitionNumber = new Dictionary<string, int>();
        var diskOffsetBytes = new Dictionary<string, long>();
        var steps = new List<ExecutionStep>();
        var arrayDevices = new List<string>();
        var unprotectedArrayDevices = new List<string>();

        for (var tierIndex = 0; tierIndex < plan.Tiers.Count; tierIndex++)
        {
            var tier = plan.Tiers[tierIndex];
            var (tierSteps, arrayDevice) = BuildTierCreationSteps(
                tier, tierIndex, poolName, labeledDisks, nextPartitionNumber, diskOffsetBytes);
            steps.AddRange(tierSteps);
            arrayDevices.Add(arrayDevice);
            if (tier.DegradedSlots > 0)
            {
                unprotectedArrayDevices.Add(arrayDevice);
            }
        }

        AppendReservedNotice(steps, plan);

        if (arrayDevices.Count > 0)
        {
            steps.Add(new ExecutionStep(
                $"Create volume group {poolName}, tagged {DiskWeaverPoolTag.Value} so DiskWeaver can recognize it as its own later",
                "vgcreate",
                ["--addtag", DiskWeaverPoolTag.Value, poolName, ..arrayDevices]));

            AppendUnprotectedTagSteps(steps, unprotectedArrayDevices);

            steps.Add(new ExecutionStep(
                $"Create logical volume {volumeName} using all pool space",
                "lvcreate",
                ["-l", "100%FREE", "-n", volumeName, poolName]));

            AppendMdadmConfPersistSteps(steps, arrayDevices);
        }

        return new ExecutionPlan(steps);
    }

    /// <summary>
    /// Whether <see cref="BuildIncremental"/> would refuse <paramref name="desired"/> because
    /// reaching it needs to merge 2+ independent existing tiers into one shared array (mdadm has no
    /// such operation) -- checked up front so a caller (the daemon's default expand path) can fall
    /// back to a different desired plan (e.g. <see cref="IndependentGrowPlanner"/>'s grow-each-tier-
    /// in-place alternative) instead of surfacing the refusal as an error the user has to react to.
    /// </summary>
    public static bool HasMergeConflicts(ExistingPoolState current, PoolPlan desired) =>
        ClassifyIncremental(current, desired).MergeConflicts.Count > 0;

    /// <summary>
    /// Plans the incremental changes needed to grow an existing pool to match
    /// <paramref name="desired"/> (typically recomputed after adding a disk).
    /// Existing tiers are never touched: unchanged tiers are left alone, new
    /// tiers are created and added to the pool via vgextend/lvextend, and
    /// scenarios that would require resizing/splitting/reshaping an existing
    /// array are refused outright rather than guessed at (see docs/execution.md).
    /// </summary>
    public static ExecutionPlan BuildIncremental(ExistingPoolState current, PoolPlan desired)
    {
        var labeledDisks = new HashSet<string>(current.Tiers.SelectMany(t => t.DiskIds));
        var nextPartitionNumber = new Dictionary<string, int>();
        var diskOffsetBytes = new Dictionary<string, long>();

        foreach (var diskId in labeledDisks)
        {
            var existingTiersForDisk = current.Tiers.Where(t => t.DiskIds.Contains(diskId)).ToList();
            nextPartitionNumber[diskId] = existingTiersForDisk.Count;
            diskOffsetBytes[diskId] = PartitionLayout.StartAlignmentBytes + existingTiersForDisk.Sum(t => t.SegmentSizeBytes);
        }

        var classification = ClassifyIncremental(current, desired);
        ThrowIfMergeConflicts(classification.MergeConflicts);
        ThrowIfOrphaned(classification.Orphaned);

        var steps = new List<ExecutionStep>();
        var grewAnyTierInPlace = false;

        foreach (var growCandidate in classification.GrowCandidates)
        {
            // Covers both a same-level device-count grow (e.g. an existing RAID5 tier picking up
            // a 4th disk) and a RAID-level migration (e.g. a 2-disk mirror needing to become a
            // 3-disk RAID5) -- mdadm --grow handles both via the same --raid-devices (+ --level
            // when it changes) invocation, so both are automated the same way.
            var (growSteps, capacityChanged) = BuildGrowSteps(
                growCandidate.Existing, growCandidate.Desired, labeledDisks, nextPartitionNumber, diskOffsetBytes);
            steps.AddRange(growSteps);
            grewAnyTierInPlace |= capacityChanged;
        }

        var newArrayDevices = new List<string>();
        var newUnprotectedArrayDevices = new List<string>();
        var nextTierIndex = current.Tiers.Count;
        foreach (var newTier in classification.NewTiers)
        {
            var (tierSteps, arrayDevice) = BuildTierCreationSteps(
                newTier, nextTierIndex++, current.PoolName, labeledDisks, nextPartitionNumber, diskOffsetBytes);
            steps.AddRange(tierSteps);
            newArrayDevices.Add(arrayDevice);
            if (newTier.DegradedSlots > 0)
            {
                newUnprotectedArrayDevices.Add(arrayDevice);
            }
        }

        AppendReservedNotice(steps, desired);

        if (newArrayDevices.Count > 0)
        {
            steps.Add(new ExecutionStep(
                $"Add new tier(s) to volume group {current.PoolName}",
                "vgextend",
                [current.PoolName, ..newArrayDevices]));

            AppendUnprotectedTagSteps(steps, newUnprotectedArrayDevices);

            AppendMdadmConfPersistSteps(steps, newArrayDevices);
        }

        if (newArrayDevices.Count > 0 || grewAnyTierInPlace)
        {
            steps.Add(new ExecutionStep(
                $"Grow logical volume {current.VolumeName} to use the added space",
                "lvextend",
                ["-l", "+100%FREE", $"{current.PoolName}/{current.VolumeName}"]));
        }

        return new ExecutionPlan(steps);
    }

    /// <summary>
    /// Adds new disk(s) to an existing array in place via mdadm --add + --grow --raid-devices
    /// (plus --level when the RAID level itself needs to change, e.g. a mirror becoming a RAID5
    /// once it has enough members) -- e.g. a 3-disk RAID5 tier picking up a 4th disk, or a 2-disk
    /// mirror picking up a 3rd disk and becoming RAID5. Unlike <see cref="BuildTierCreationSteps"/>,
    /// no new array/PV is created; the existing array and PV just get bigger (and possibly change
    /// type). The reshape runs in the background on real hardware (potentially for hours on large
    /// arrays); `mdadm --wait` blocks until it finishes before LVM is told to notice the new size,
    /// which is why this can make a single Execute call take a long time -- see docs/execution.md's
    /// note on this needing background execution for production use.
    /// </summary>
    private static (List<ExecutionStep> Steps, bool CapacityChanged) BuildGrowSteps(
        ExistingTier existing,
        Tier desired,
        HashSet<string> labeledDisks,
        Dictionary<string, int> nextPartitionNumber,
        Dictionary<string, long> diskOffsetBytes)
    {
        var steps = new List<ExecutionStep>();
        var newPartitionPaths = new List<string>();

        foreach (var diskId in desired.DiskIds.Except(existing.DiskIds))
        {
            if (labeledDisks.Add(diskId))
            {
                steps.Add(new ExecutionStep(
                    $"Create GPT partition table on {diskId}",
                    "parted",
                    ["--script", diskId, "mklabel", "gpt"]));
            }

            var startBytes = diskOffsetBytes.GetValueOrDefault(diskId, PartitionLayout.StartAlignmentBytes);
            var endBytes = startBytes + desired.SegmentSizeBytes;
            diskOffsetBytes[diskId] = endBytes;

            var partitionNumber = nextPartitionNumber.GetValueOrDefault(diskId, 0) + 1;
            nextPartitionNumber[diskId] = partitionNumber;
            var partitionPath = PartitionNaming.ToPartitionPath(diskId, partitionNumber);
            newPartitionPaths.Add(partitionPath);

            steps.Add(new ExecutionStep(
                $"Partition {diskId} to grow {existing.ArrayDevice} ({partitionPath})",
                "parted",
                ["--script", diskId, "unit", "B", "mkpart", "primary", $"{startBytes}", $"{endBytes - 1}"]));

            steps.Add(new ExecutionStep($"Rescan {diskId} so the kernel exposes {partitionPath}", "partprobe", [diskId]));
        }

        steps.Add(new ExecutionStep("Wait for partition device nodes to appear", "udevadm", ["settle"]));

        // A tier can already be configured with more slots than it has real members -- either a
        // RedundancyLevel.None tier deliberately built degraded (Tier.DegradedSlots) or an
        // ordinary array missing a member from a real disk failure (indistinguishable at the
        // mdadm level; ExistingTier.IsUnprotectedByDesign is how callers tell them apart). Filling
        // the missing slot is a plain `mdadm --add` (mdadm auto-detects the missing slot and
        // resyncs into it): no --grow, no level change, because the slot count itself isn't
        // changing -- confirmed live as the *only* mechanism that actually works for this (a
        // genuine single-member array's first spare-add fails outright, see BuildTierCreationSteps).
        var missingSlots = Math.Max(0, existing.ConfiguredMemberCountOrDefault - existing.DiskIds.Count);
        var fillCount = Math.Min(missingSlots, newPartitionPaths.Count);

        if (fillCount > 0)
        {
            if (newPartitionPaths.Count > fillCount)
            {
                throw new InvalidOperationException(
                    $"{existing.ArrayDevice} is degraded (missing {missingSlots} slot(s)) and this expansion "
                    + $"adds more disks than needed to fill it. Fill the missing slot with exactly {missingSlots} "
                    + "disk(s) first (a separate expand), let it finish resyncing, then expand again to add the rest.");
            }

            foreach (var partitionPath in newPartitionPaths)
            {
                steps.Add(new ExecutionStep(
                    $"Add {partitionPath} to {existing.ArrayDevice}, filling its missing slot (was degraded)",
                    "mdadm",
                    ["--add", existing.ArrayDevice, partitionPath]));
            }

            steps.Add(WaitForReshapeStep(existing.ArrayDevice));
            steps.Add(new ExecutionStep(
                $"Resize {existing.ArrayDevice}'s LVM physical volume to its new size",
                "pvresize",
                [existing.ArrayDevice]));

            if (existing.IsUnprotectedByDesign)
            {
                // The tier just became a genuine, fully-populated mirror -- drop the marker so it's
                // never again mistaken for still-unprotected (e.g. by a later ProtectionPlanner run).
                steps.Add(new ExecutionStep(
                    $"Remove {existing.ArrayDevice}'s unprotected-by-design tag now that it's a real mirror",
                    "pvchange",
                    ["--deltag", "diskweaver-unprotected", existing.ArrayDevice]));
            }

            // Filling a missing RAID1/5/6 slot adds resilience, not capacity: mdadm reports a
            // degraded array's "Array Size" the same as a fully-populated one (RAID1's capacity is
            // one member's size regardless of how many legs are missing; RAID5/6's is (n-1)/(n-2)
            // members' worth based on the *configured* device count, not how many are currently
            // active) -- so the VG genuinely gains zero free extents here. Confirmed live: an
            // unconditional lvextend -l +100%FREE after this fails with "New size ... matches
            // existing size" (LVM treats a true no-op resize as an error, exit 5), even though
            // nothing actually went wrong -- the caller must not treat this as capacity growth.
            return (steps, false);
        }

        foreach (var partitionPath in newPartitionPaths)
        {
            steps.Add(new ExecutionStep(
                $"Add {partitionPath} to {existing.ArrayDevice} as a spare",
                "mdadm",
                ["--add", existing.ArrayDevice, partitionPath]));
        }

        var levelHops = PlanLevelHops(existing, desired);

        if (levelHops.Count == 0)
        {
            steps.Add(new ExecutionStep(
                $"Grow {existing.ArrayDevice} to {desired.DiskIds.Count} devices ({existing.RaidLevel}, reshapes in place)",
                "mdadm",
                ["--grow", $"--raid-devices={desired.DiskIds.Count}", existing.ArrayDevice]));
            steps.Add(WaitForReshapeStep(existing.ArrayDevice));
        }
        else
        {
            // ppl (see BuildTierCreationSteps) is mutually exclusive with an internal bitmap, and
            // mdadm only allows enabling it once an array is genuinely RAID5 -- a Mirror (which
            // always has --bitmap=internal from creation) migrating up to RAID5 has to keep that
            // bitmap through the level-change reshape itself, then switch to ppl afterward, once
            // it's confirmed RAID5. Going the other way, RAID6 has no ppl support at all, so a
            // RAID5 tier (already on ppl) migrating up to RAID6 has to shed ppl *before* that
            // reshape starts -- there's no valid intermediate state where a RAID6 array runs with
            // ppl still active. Existing is only ever RAID5 here (never RAID6 -- see
            // PlanLevelHops's own doc comment on the reshape paths mdadm actually supports), so
            // "migrating away from RAID5" always means "up to RAID6," never back down.
            if (existing.RaidLevel == RaidLevel.Raid5 && desired.RaidLevel != RaidLevel.Raid5)
            {
                steps.Add(new ExecutionStep(
                    $"Switch {existing.ArrayDevice} off ppl (RAID6 doesn't support it) before migrating level",
                    "mdadm",
                    ["--grow", "--consistency-policy=bitmap", existing.ArrayDevice]));
            }

            var fromLevel = existing.RaidLevel;
            for (var hopIndex = 0; hopIndex < levelHops.Count; hopIndex++)
            {
                var toLevel = levelHops[hopIndex];

                // Only the first hop carries --raid-devices: mdadm's raid1->raid5 level change is
                // only legal while the array is still at its original (2-disk) device count, but it
                // allows combining that change with jumping straight to the final device count in
                // the same command (validated for real: a 2-disk mirror picking up a 3rd disk and
                // becoming RAID5 in one --grow). Later hops (e.g. raid5->raid6) just change level in
                // place -- the device count from the first hop already stands.
                var growArgs = new List<string> { "--grow", $"--level={ToMdadmLevel(toLevel)}" };
                if (hopIndex == 0)
                {
                    growArgs.Add($"--raid-devices={desired.DiskIds.Count}");
                }
                growArgs.Add(existing.ArrayDevice);

                var growDescription = levelHops.Count > 1
                    ? $"Grow {existing.ArrayDevice} to {desired.DiskIds.Count} devices, migrating {fromLevel} to {toLevel} "
                        + $"(step {hopIndex + 1} of {levelHops.Count} toward {desired.RaidLevel})"
                    : $"Grow {existing.ArrayDevice} to {desired.DiskIds.Count} devices, migrating {fromLevel} to {toLevel}";
                steps.Add(new ExecutionStep(growDescription, "mdadm", growArgs));
                steps.Add(WaitForReshapeStep(existing.ArrayDevice));

                fromLevel = toLevel;
            }

            if (desired.RaidLevel == RaidLevel.Raid5)
            {
                // Only reachable here via a Mirror -> RAID5 hop -- a same-level RAID5 device-count
                // grow never has any level hops at all (levelHops.Count would be 0, handled by the
                // branch above), so the array still carries the internal bitmap every Mirror tier
                // is created with; ppl requires that gone first.
                steps.Add(new ExecutionStep(
                    $"Remove {existing.ArrayDevice}'s bitmap so ppl can be enabled",
                    "mdadm",
                    ["--grow", "--bitmap=none", existing.ArrayDevice]));
                steps.Add(new ExecutionStep(
                    $"Enable ppl on {existing.ArrayDevice} to close the RAID5 write hole",
                    "mdadm",
                    ["--grow", "--consistency-policy=ppl", existing.ArrayDevice]));
            }
        }

        steps.Add(new ExecutionStep(
            $"Resize {existing.ArrayDevice}'s LVM physical volume to its new size",
            "pvresize",
            [existing.ArrayDevice]));

        return (steps, true);
    }

    private static ExecutionStep WaitForReshapeStep(string arrayDevice) => new(
        $"Wait for {arrayDevice}'s reshape to finish (can take a long time on real disks)",
        "mdadm",
        ["--wait", arrayDevice]);

    /// <summary>
    /// Works out which single-step mdadm level changes (if any) are needed to get
    /// <paramref name="existing"/> from its current RAID level to <paramref name="desired"/>'s, and
    /// throws if there's no legal reshape path at all. mdadm's real level-migration support is much
    /// narrower than "any level to any level in one --grow": a RAID1 array can only migrate away
    /// from RAID1 (to RAID5) while it still has exactly 2 members, and there's no direct RAID1 ->
    /// RAID6 reshape -- attempting one fails at runtime with mdadm's own
    /// "Impossibly level change request for RAID1". A 2-disk mirror reaching RAID6 has to go via an
    /// intermediate RAID5 hop instead (RAID5 -> RAID6 is directly supported); a mirror with 3+
    /// members already (e.g. an DWR-2 pool's minimum-redundancy tier) has no reshape path to RAID5
    /// or RAID6 at all, since the RAID1-to-RAID5 hop itself requires exactly 2 members -- that case
    /// is refused outright rather than emitting a plan mdadm will reject partway through.
    /// </summary>
    private static IReadOnlyList<RaidLevel> PlanLevelHops(ExistingTier existing, Tier desired)
    {
        if (existing.RaidLevel == desired.RaidLevel)
        {
            return [];
        }

        if (existing.RaidLevel == RaidLevel.Mirror)
        {
            if (existing.DiskIds.Count != 2)
            {
                throw new InvalidOperationException(
                    $"{existing.ArrayDevice} is a {existing.DiskIds.Count}-disk mirror and would need to become "
                    + $"{desired.RaidLevel} to fit the new disk layout, but mdadm can only migrate a RAID1 array "
                    + "away from RAID1 when it has exactly 2 members (and only to RAID5) -- there is no reshape "
                    + $"path for a {existing.DiskIds.Count}-disk mirror. A fresh pool rebuild would be required.");
            }

            switch (desired.RaidLevel)
            {
                case RaidLevel.Raid5:
                    return [RaidLevel.Raid5];
                case RaidLevel.Raid6:
                    // No direct RAID1 -> RAID6 reshape exists; go via RAID5 first.
                    return [RaidLevel.Raid5, RaidLevel.Raid6];
            }
        }
        else if (existing.RaidLevel == RaidLevel.Raid5 && desired.RaidLevel == RaidLevel.Raid6)
        {
            return [RaidLevel.Raid6];
        }

        throw new InvalidOperationException(
            $"{existing.ArrayDevice} would need to migrate from {existing.RaidLevel} to {desired.RaidLevel}, "
            + "but there's no supported mdadm reshape path between those levels. A fresh pool rebuild would be required.");
    }

    /// <summary>
    /// The pool's usable capacity once <see cref="BuildIncremental"/>'s plan is actually executed.
    /// Since <see cref="BuildGrowSteps"/> automates every grow candidate (same-level device-count
    /// grows and RAID-level migrations alike), this is currently always equal to
    /// <paramref name="desired"/>'s own <c>PoolCapacityBytes</c> -- kept as a separate, explicit
    /// computation (rather than callers just reading <c>desired.PoolCapacityBytes</c> directly)
    /// because that equivalence is a property of what's automated today, not a guarantee: if a
    /// future scenario (e.g. splitting a segment) is refused/deferred again, this is where that
    /// would show up as a real difference, same as it did before level migrations were automated.
    /// </summary>
    public static long AchievedCapacityBytes(ExistingPoolState current, PoolPlan desired)
    {
        var classification = ClassifyIncremental(current, desired);
        ThrowIfMergeConflicts(classification.MergeConflicts);
        ThrowIfOrphaned(classification.Orphaned);

        return classification.Unchanged.Sum(t => t.UsableBytes)
            + classification.GrowCandidates.Sum(g => g.Desired.UsableBytes)
            + classification.NewTiers.Sum(t => t.UsableBytes);
    }

    /// <summary>
    /// Purely informational (never executed): what <see cref="TieringPlanner"/> would produce if
    /// every disk currently in the pool, plus whichever ones are being offered right now, were wiped
    /// and rebuilt fresh together at <paramref name="redundancy"/>, instead of grown incrementally.
    /// Lets a user weigh an incremental grow/auto-protect/independent-grow against tearing down and
    /// rebuilding -- often a meaningfully higher number even for the exact same disks, because
    /// redundancy overhead is paid once per array: several independent tiers (e.g. from a
    /// RedundancyLevel.None pool later protected tier-by-tier) each separately pay a mirror's ~50%
    /// overhead, while one shared boundary-grouped rebuild amortizes it across every disk at once
    /// (a 5-disk RAID5 "loses" only 1 disk's worth of capacity total, not 1-of-2 per tier).
    /// </summary>
    public static long HypotheticalFullRebuildCapacityBytes(IReadOnlyList<Disk> allDisks, RedundancyLevel redundancy) =>
        PartitionLayout.PlanForRealDisks(allDisks, redundancy).Tiers.Sum(t => t.UsableBytes);

    /// <summary>
    /// Brings up any mdadm array/LVM volume group that already exists on this host's disks (its
    /// on-disk superblock/metadata is intact) but isn't currently assembled/active -- the situation
    /// left behind by installing a fresh OS onto disks that already hold a DiskWeaver pool (see
    /// docs/state-model.md's ownership section: the `diskweaver-managed` VG tag lives in LVM's own
    /// metadata on disk, so it survives an OS reinstall untouched, but the kernel still has to be
    /// told to reassemble the arrays and activate the VG before <see cref="MdadmLvmPoolStateSource"/>
    /// can see them). Safe to run unconditionally, including when nothing needs it -- both commands
    /// are no-ops (exit 0) against arrays/VGs that are already assembled/active, and neither one
    /// creates, destroys, or modifies data.
    /// </summary>
    public static ExecutionPlan BuildReassemble() => new([
        new ExecutionStep(
            "Assemble any inactive mdadm arrays found on this host's disks",
            "mdadm",
            ["--assemble", "--scan"]),

        // Assembling above only takes effect for the current boot -- without this, an array that
        // was foreign a moment ago (e.g. right after installing a fresh OS onto disks that already
        // held a pool) goes right back to inactive on the next reboot, same underlying problem
        // AppendMdadmConfPersistSteps exists for on the build side. Unlike that helper, the array
        // names here aren't known in advance (they're whatever --assemble --scan just found, not
        // ones this plan created), and this action can be run repeatedly -- so a plain `>>` append
        // would duplicate ARRAY lines on every re-run. Each line `mdadm --detail --scan` reports is
        // only appended if it isn't already present (grep -qxF), making this idempotent. No
        // caller-supplied values reach the shell text, so this is safe as a literal -c string.
        //
        // Whether a line was already present *is* the "was this foreign" signal, and worth
        // surfacing rather than swallowing: a line already in mdadm.conf means this OS install
        // already knew about that array (DiskWeaver wrote it at build time, or a previous
        // reassemble did); a line that's missing means this is the first time this install has
        // ever seen it -- exactly the case of a pool built under a previous OS on the same disks.
        // Stdout here lands in the step's ExecutionStepRecord.Output, visible in the journal.
        new ExecutionStep(
            "Persist any newly-assembled array(s) in mdadm.conf so they auto-assemble at boot",
            "sh",
            ["-c",
                "before=$(grep -c '' /etc/mdadm/mdadm.conf 2>/dev/null || echo 0); "
                + "mdadm --detail --scan | while IFS= read -r line; do "
                + "grep -qxF \"$line\" /etc/mdadm/mdadm.conf 2>/dev/null || { "
                + "echo \"$line\" >> /etc/mdadm/mdadm.conf; "
                + "echo \"Newly discovered (not previously known to this OS install -- likely from a previous install): $line\"; "
                + "}; done; "
                + "after=$(grep -c '' /etc/mdadm/mdadm.conf 2>/dev/null || echo 0); "
                + "[ \"$after\" = \"$before\" ] && echo 'No new arrays found -- everything already known to this OS install.'; true"]),

        new ExecutionStep(
            "Rebuild the initramfs so early boot can see mdadm.conf's new entries",
            "update-initramfs",
            ["-u"]),

        new ExecutionStep(
            "Activate any inactive LVM volume groups now backed by those arrays",
            "vgchange",
            ["-ay"]),
    ]);

    private static void ThrowIfOrphaned(IReadOnlyList<ExistingTier> orphaned)
    {
        if (orphaned.Count > 0)
        {
            throw new InvalidOperationException(
                $"{orphaned.Count} existing tier(s) ({string.Join(", ", orphaned.Select(t => t.ArrayDevice))}) "
                + "don't correspond to any tier in the new plan. This usually means a new disk's size falls "
                + "strictly between two existing tier boundaries, which would require splitting or resizing an "
                + "existing array — not supported. A fresh pool rebuild would be required.");
        }
    }

    /// <summary>
    /// A requested redundancy change (e.g. upgrading a JBOD pool's independent single-disk tiers
    /// to a shared DWR-1/DWR-2 tier) can require several already-built, independent existing
    /// arrays to become one new shared array. mdadm has no "merge two arrays into one" operation --
    /// that needs real data migration -- so this is refused outright rather than silently keeping
    /// only one of the competing existing tiers and orphaning the rest (which is what naively
    /// picking the first match in <see cref="ClassifyIncremental"/> would otherwise do).
    /// </summary>
    private static void ThrowIfMergeConflicts(IReadOnlyList<(Tier Desired, IReadOnlyList<ExistingTier> Conflicting)> mergeConflicts)
    {
        if (mergeConflicts.Count > 0)
        {
            var (_, conflicting) = mergeConflicts[0];
            throw new InvalidOperationException(
                $"Reaching the requested redundancy would require merging {conflicting.Count} independent "
                + $"existing tiers ({string.Join(", ", conflicting.Select(t => t.ArrayDevice))}) into one array "
                + "-- not supported as an incremental operation (mdadm has no way to merge two already-built "
                + "arrays). Tear down the pool and rebuild it fresh with the desired redundancy instead.");
        }
    }

    private readonly record struct GrowCandidate(ExistingTier Existing, Tier Desired);

    private readonly record struct IncrementalClassification(
        IReadOnlyList<ExistingTier> Unchanged,
        IReadOnlyList<GrowCandidate> GrowCandidates,
        IReadOnlyList<Tier> NewTiers,
        IReadOnlyList<ExistingTier> Orphaned,
        IReadOnlyList<(Tier Desired, IReadOnlyList<ExistingTier> Conflicting)> MergeConflicts);

    private static IncrementalClassification ClassifyIncremental(ExistingPoolState current, PoolPlan desired)
    {
        var unchanged = new List<ExistingTier>();
        var growCandidates = new List<GrowCandidate>();
        var newTiers = new List<Tier>();
        var mergeConflicts = new List<(Tier, IReadOnlyList<ExistingTier>)>();
        var matchedExisting = new HashSet<ExistingTier>();

        foreach (var desiredTier in desired.Tiers)
        {
            var exactMatch = current.Tiers.FirstOrDefault(e =>
                e.SegmentSizeBytes == desiredTier.SegmentSizeBytes && SameDisks(e.DiskIds, desiredTier.DiskIds));
            if (exactMatch is not null)
            {
                matchedExisting.Add(exactMatch);
                unchanged.Add(exactMatch);
                continue;
            }

            var growTargets = current.Tiers.Where(e =>
                e.SegmentSizeBytes == desiredTier.SegmentSizeBytes && IsProperSubset(e.DiskIds, desiredTier.DiskIds)).ToList();
            if (growTargets.Count == 1)
            {
                matchedExisting.Add(growTargets[0]);
                growCandidates.Add(new GrowCandidate(growTargets[0], desiredTier));
                continue;
            }

            if (growTargets.Count > 1)
            {
                // Several independent existing tiers would each need to become part of this one
                // desired tier -- a merge, not a grow. Mark all of them handled (by this conflict)
                // so they don't also show up as generic "orphaned" noise alongside the real error.
                foreach (var target in growTargets)
                {
                    matchedExisting.Add(target);
                }

                mergeConflicts.Add((desiredTier, growTargets));
                continue;
            }

            newTiers.Add(desiredTier);
        }

        var orphaned = current.Tiers.Where(e => !matchedExisting.Contains(e)).ToList();
        return new IncrementalClassification(unchanged, growCandidates, newTiers, orphaned, mergeConflicts);
    }

    /// <summary>
    /// Reverses a pool built by <see cref="Build"/>: removes the LV, VG, each tier's PV and
    /// array, clears RAID superblocks (so stale metadata isn't auto-reassembled next time those
    /// partitions are reused), wipes each disk's partition-table signature, and detaches any
    /// loop devices involved. Assumes standard fresh-build tier numbering (0..N-1); not meant for
    /// pools that went through <see cref="BuildIncremental"/> with a different numbering offset.
    /// </summary>
    public static ExecutionPlan BuildTeardown(
        PoolPlan plan,
        string poolName = "diskweaver-pool",
        string volumeName = "data")
    {
        var steps = new List<ExecutionStep>
        {
            ExecutionStep.Comment("Unmount the filesystem first if it's mounted, e.g.: umount <mountpoint>"),
            new($"Remove logical volume {volumeName}", "lvremove", ["-f", $"{poolName}/{volumeName}"]),
            new($"Remove volume group {poolName}", "vgremove", ["-f", poolName]),
        };

        var nextPartitionNumber = new Dictionary<string, int>();

        for (var tierIndex = 0; tierIndex < plan.Tiers.Count; tierIndex++)
        {
            var tier = plan.Tiers[tierIndex];
            var arrayDevice = $"/dev/md/{poolName}-tier{tierIndex}";
            var partitionPaths = new List<string>();

            foreach (var diskId in tier.DiskIds)
            {
                var partitionNumber = nextPartitionNumber.GetValueOrDefault(diskId, 0) + 1;
                nextPartitionNumber[diskId] = partitionNumber;
                partitionPaths.Add(PartitionNaming.ToPartitionPath(diskId, partitionNumber));
            }

            steps.Add(new ExecutionStep($"Remove {arrayDevice} as a physical volume", "pvremove", [arrayDevice]));
            steps.Add(new ExecutionStep($"Stop {arrayDevice}", "mdadm", ["--stop", arrayDevice]));

            foreach (var partitionPath in partitionPaths)
            {
                steps.Add(new ExecutionStep(
                    $"Clear RAID superblock on {partitionPath} (prevents stale auto-reassembly)",
                    "mdadm",
                    ["--zero-superblock", partitionPath]));
            }
        }

        var disks = plan.Tiers.SelectMany(t => t.DiskIds).Distinct().ToList();
        AppendWipeAndDetachSteps(steps, disks);

        return new ExecutionPlan(steps);
    }

    /// <summary>
    /// Reverses a pool using its actual on-disk state (from <see cref="IPoolStateSource"/>)
    /// rather than a regenerated <see cref="Planner.PoolPlan"/> -- unlike <see cref="BuildTeardown"/>,
    /// this doesn't need the disk selection/redundancy that originally produced the pool, and
    /// uses each tier's real array device rather than assuming fresh-build tier numbering. This is
    /// what "tear down this pool I see in /pools" means; <see cref="BuildTeardown"/> is for
    /// tearing down a plan still cached from the same planning session.
    /// </summary>
    public static ExecutionPlan BuildTeardownFromExisting(ExistingPoolState pool)
    {
        var steps = new List<ExecutionStep>
        {
            ExecutionStep.Comment("Unmount the filesystem first if it's mounted, e.g.: umount <mountpoint>"),
            new($"Remove logical volume {pool.VolumeName}", "lvremove", ["-f", $"{pool.PoolName}/{pool.VolumeName}"]),
            new($"Remove volume group {pool.PoolName}", "vgremove", ["-f", pool.PoolName]),
        };

        // Uses each tier's real PartitionPaths (from mdadm --detail --export) rather than
        // re-deriving them from a partition-numbering convention -- after an incremental
        // same-level grow (BuildIncremental), a disk's partition for the bottom (smallest-segment)
        // tier isn't necessarily "partition 1": partition numbers follow creation order, and a
        // disk can pick up a tier's partition well after another tier's partition already exists
        // on it. Guessing "smallest segment = partition 1" here would target the wrong partition.
        foreach (var tier in pool.Tiers)
        {
            steps.Add(new ExecutionStep($"Remove {tier.ArrayDevice} as a physical volume", "pvremove", [tier.ArrayDevice]));
            steps.Add(new ExecutionStep($"Stop {tier.ArrayDevice}", "mdadm", ["--stop", tier.ArrayDevice]));

            foreach (var partitionPath in tier.PartitionPaths)
            {
                steps.Add(new ExecutionStep(
                    $"Clear RAID superblock on {partitionPath} (prevents stale auto-reassembly)",
                    "mdadm",
                    ["--zero-superblock", partitionPath]));
            }
        }

        var disks = pool.Tiers.SelectMany(t => t.DiskIds).Distinct().ToList();
        AppendWipeAndDetachSteps(steps, disks);

        return new ExecutionPlan(steps);
    }

    /// <summary>
    /// Wipes each disk's signature, then rescans it before anything downstream (a subsequent
    /// <see cref="DiskWeaver.Inventory.DiskSelector.EnsureBlank"/> check, or this same call's own
    /// loop-device detach below) queries it again. <c>wipefs -a</c> writes the blank signature to
    /// the underlying storage, but the kernel's own cached partition-table view of the block device
    /// isn't refreshed by that write alone -- confirmed live: a disk wiped this way still read back
    /// as "not blank" on the very next plan/build request. Same class of missing-rescan bug already
    /// fixed on the build side (<c>parted mkpart</c> -&gt; <c>partprobe</c> -&gt; <c>udevadm
    /// settle</c>, see <see cref="BuildTierCreationSteps"/>); needed here for the same reason.
    /// </summary>
    private static void AppendWipeAndDetachSteps(List<ExecutionStep> steps, IReadOnlyList<string> disks)
    {
        foreach (var diskId in disks)
        {
            steps.Add(new ExecutionStep(
                $"Wipe filesystem/partition-table signatures on {diskId}",
                "wipefs",
                ["-a", diskId]));

            steps.Add(new ExecutionStep($"Rescan {diskId} so the kernel notices the wipe", "partprobe", [diskId]));
        }

        if (disks.Count > 0)
        {
            steps.Add(new ExecutionStep("Wait for the rescan to finish", "udevadm", ["settle"]));
        }

        foreach (var diskId in disks.Where(IsLoopDevice))
        {
            steps.Add(new ExecutionStep($"Detach loop device {diskId}", "losetup", ["-d", diskId]));
        }
    }

    /// <summary>
    /// Clears a stale/foreign filesystem, RAID, or LVM signature off disks so they pass
    /// <see cref="DiskWeaver.Inventory.DiskSelector.EnsureBlank"/> and can be selected for a new
    /// plan. Unlike <see cref="BuildTeardown"/>, this doesn't assume the disks belong to any
    /// DiskWeaver-built pool or array -- it takes whatever partition paths lsblk currently reports
    /// for each disk (<paramref name="partitionPathsByDisk"/>) and, for each one, zeroes any RAID
    /// superblock before wiping the parent disk's own signature, same order/reasoning as teardown.
    /// Does NOT stop or remove disks from a currently-*running* array -- if a disk is still an
    /// active member/spare of an assembled array (e.g. left behind by a grow that failed partway
    /// through), `mdadm --zero-superblock` will refuse it until the array is stopped or the member
    /// is removed first; that's a separate recovery step, not something this blindly overrides.
    /// </summary>
    public static ExecutionPlan BuildWipe(
        IReadOnlyList<string> diskIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? partitionPathsByDisk = null)
    {
        var steps = new List<ExecutionStep>();

        foreach (var diskId in diskIds)
        {
            if (partitionPathsByDisk is not null && partitionPathsByDisk.TryGetValue(diskId, out var partitionPaths))
            {
                foreach (var partitionPath in partitionPaths)
                {
                    steps.Add(new ExecutionStep(
                        $"Clear RAID superblock on {partitionPath} (in case it's a stale/foreign array member)",
                        "mdadm",
                        ["--zero-superblock", partitionPath]));
                }
            }

            steps.Add(new ExecutionStep(
                $"Wipe filesystem/partition-table/RAID/LVM signatures on {diskId}",
                "wipefs",
                ["-a", diskId]));

            // See AppendWipeAndDetachSteps's doc: wipefs alone doesn't refresh the kernel's cached
            // partition-table view of the device, so a disk wiped this way can still read back as
            // "not blank" on the very next request without this rescan.
            steps.Add(new ExecutionStep($"Rescan {diskId} so the kernel notices the wipe", "partprobe", [diskId]));
        }

        if (diskIds.Count > 0)
        {
            steps.Add(new ExecutionStep("Wait for the rescan to finish", "udevadm", ["settle"]));
        }

        return new ExecutionPlan(steps);
    }

    private static bool IsLoopDevice(string diskId) =>
        diskId.StartsWith("/dev/loop", StringComparison.Ordinal)
        && diskId.Length > "/dev/loop".Length
        && diskId["/dev/loop".Length..].All(char.IsDigit);

    private static (List<ExecutionStep> Steps, string ArrayDevice) BuildTierCreationSteps(
        Tier tier,
        int tierIndex,
        string poolName,
        HashSet<string> labeledDisks,
        Dictionary<string, int> nextPartitionNumber,
        Dictionary<string, long> diskOffsetBytes)
    {
        var steps = new List<ExecutionStep>();
        var partitionPaths = new List<string>();

        foreach (var diskId in tier.DiskIds)
        {
            if (labeledDisks.Add(diskId))
            {
                steps.Add(new ExecutionStep(
                    $"Create GPT partition table on {diskId}",
                    "parted",
                    ["--script", diskId, "mklabel", "gpt"]));
            }

            var startBytes = diskOffsetBytes.GetValueOrDefault(diskId, PartitionLayout.StartAlignmentBytes);
            var endBytes = startBytes + tier.SegmentSizeBytes;
            diskOffsetBytes[diskId] = endBytes;

            var partitionNumber = nextPartitionNumber.GetValueOrDefault(diskId, 0) + 1;
            nextPartitionNumber[diskId] = partitionNumber;
            var partitionPath = PartitionNaming.ToPartitionPath(diskId, partitionNumber);
            partitionPaths.Add(partitionPath);

            steps.Add(new ExecutionStep(
                $"Partition {diskId} for tier {tierIndex} ({partitionPath})",
                "parted",
                ["--script", diskId, "unit", "B", "mkpart", "primary", $"{startBytes}", $"{endBytes - 1}"]));

            // parted writing a partition table doesn't reliably make the kernel expose the new
            // partition device node on its own -- this is especially unreliable for loop devices.
            // partprobe forces the rescan; without it the node (e.g. /dev/loop1p1) may simply never
            // appear, which is a missing rescan, not a timing race that waiting alone would fix.
            steps.Add(new ExecutionStep(
                $"Rescan {diskId} so the kernel exposes {partitionPath}",
                "partprobe",
                [diskId]));
        }

        // Flushes the udev events partprobe just triggered, so the partition device nodes are
        // guaranteed to exist by the time mdadm --create below references them.
        steps.Add(new ExecutionStep(
            "Wait for partition device nodes to appear",
            "udevadm",
            ["settle"]));

        var arrayDevice = $"/dev/md/{poolName}-tier{tierIndex}";
        // A RedundancyLevel.None tier is created degraded on purpose: DegradedSlots "missing"
        // placeholders fill out --raid-devices beyond the real partitions, so a disk added to
        // this tier later needs only a plain `mdadm --add` (which fills the missing slot and
        // resyncs) rather than a `--grow`/array rebuild. See Tier.DegradedSlots.
        //
        // A genuine single-member (`--raid-devices=1 --force`) array was tried instead (to avoid
        // the resulting ambiguity with a really-degraded protected mirror) and confirmed broken
        // via live testing: `mdadm --add` refuses to add a first spare to a true 1-device array
        // ("cannot load array metadata"), even with --force, even via the stable /dev/md/name
        // symlink instead of the kernel device path. The degraded-2-slot approach is the only one
        // that actually works for growing -- the disambiguation is instead handled by tagging the
        // tier's PV (see AppendUnprotectedTagSteps), not by the array's own shape.
        var raidDevices = partitionPaths.Count + tier.DegradedSlots;
        var missingSlots = Enumerable.Repeat("missing", tier.DegradedSlots);
        var descriptionSuffix = tier.DegradedSlots > 0
            ? $"{partitionPaths.Count} of {raidDevices} disks, degraded -- add a disk later to complete the mirror"
            : $"{partitionPaths.Count} disks";
        steps.Add(new ExecutionStep(
            $"Create tier {tierIndex} array ({tier.RaidLevel}, {descriptionSuffix})",
            "mdadm",
            [
                "--create", arrayDevice,
                $"--level={ToMdadmLevel(tier.RaidLevel)}",
                $"--raid-devices={raidDevices}",
                "--metadata=1.2",
                // Explicit, not left to mdadm's interactive "enable write-intent bitmap?" prompt --
                // that prompt is a script-safety hazard (unanswered it hangs; answered wrong it
                // corrupts whatever runs next, same class of bug as vgremove's confirmation prompt).
                //
                // RAID5 gets ppl (Partial Parity Log) instead of a plain bitmap: unlike a bitmap
                // (which only narrows an unclean-shutdown resync to the regions that were dirty),
                // ppl logs each stripe's pre-write parity, so it actually closes the RAID5 write
                // hole (a stripe torn by power loss leaving data and parity silently inconsistent)
                // rather than just speeding up recovery from one. Mirror and RAID6 keep the plain
                // bitmap: ppl is a RAID5-only mdadm feature (no RAID1 use case for it -- mirrors
                // have no parity to protect -- and the kernel md driver has never implemented it
                // for RAID6's dual P+Q parity). Closing RAID6's write hole needs a dedicated
                // write-intent journal device instead, which DiskWeaver's planner has no concept
                // of selecting today -- a real, separate gap, not something this covers.
                tier.RaidLevel == RaidLevel.Raid5 ? "--consistency-policy=ppl" : "--bitmap=internal",
                // A different interactive prompt than the bitmap one above: mdadm --create asks
                // "appears to be part of a raid array... Continue creating array?" if any input
                // partition still has an old RAID superblock on it (e.g. a partition number/offset
                // reused from a torn-down array whose superblock was never zeroed). Same failure
                // mode as every other mdadm/LVM confirmation prompt in this codebase -- non-TTY
                // stdin makes mdadm default to "N" and abort, not hang, but it's still a script
                // stopping on a prompt it can never answer. --run tells mdadm to proceed anyway;
                // safe here because by the time this step runs, DiskWeaver has just partitioned
                // these devices itself in this same plan, so any such leftover metadata is
                // necessarily stale, not a live array actually in use.
                "--run",
                ..partitionPaths,
                ..missingSlots,
            ]));

        steps.Add(new ExecutionStep(
            $"Mark {arrayDevice} as an LVM physical volume",
            "pvcreate",
            [arrayDevice]));

        return (steps, arrayDevice);
    }

    /// <summary>
    /// Tags each given array's PV as intentionally unprotected -- a durable marker distinguishing
    /// "built unprotected on purpose" from a real disk-failure degradation, which looks identical
    /// at the mdadm level (both are 2-slot-configured, 1-real-member arrays); see
    /// ExistingTier.IsUnprotectedByDesign and InferRedundancy. Must run after the PV has joined a
    /// VG (vgcreate/vgextend), not right after pvcreate -- LVM stores PV tags in the VG's metadata,
    /// so `pvchange --addtag` on an orphan PV fails outright ("not in volume group"), confirmed live.
    /// </summary>
    private static void AppendUnprotectedTagSteps(List<ExecutionStep> steps, IReadOnlyList<string> arrayDevices)
    {
        foreach (var arrayDevice in arrayDevices)
        {
            steps.Add(new ExecutionStep(
                $"Tag {arrayDevice}'s physical volume as an intentionally-unprotected tier",
                "pvchange",
                ["--addtag", "diskweaver-unprotected", arrayDevice]));
        }
    }

    /// <summary>
    /// Appends the newly-created array(s) to /etc/mdadm/mdadm.conf and rebuilds the initramfs, so the
    /// kernel/udev can auto-assemble them at boot instead of racing incremental assembly with no
    /// config to guide it -- confirmed live to otherwise leave an array stuck "inactive" after a
    /// reboot until a member happens to enumerate late, with mdadm refusing to auto-start what then
    /// looks like a dirty degraded array. Array device paths are passed as `sh -c` positional
    /// parameters ($@), not interpolated into the script text -- they're derived from the caller-
    /// supplied pool name, so string-building the script itself would be a command injection hole.
    /// </summary>
    private static void AppendMdadmConfPersistSteps(List<ExecutionStep> steps, IReadOnlyList<string> arrayDevices)
    {
        steps.Add(new ExecutionStep(
            "Persist new array(s) in mdadm.conf so they auto-assemble at boot",
            "sh",
            ["-c", "mdadm --detail --scan \"$@\" >> /etc/mdadm/mdadm.conf", "sh", ..arrayDevices]));

        steps.Add(new ExecutionStep(
            "Rebuild the initramfs so early boot can see mdadm.conf's new entries",
            "update-initramfs",
            ["-u"]));
    }

    private static void AppendReservedNotice(List<ExecutionStep> steps, PoolPlan plan)
    {
        if (plan.Reserved.Count == 0)
        {
            return;
        }

        var reservedDisks = plan.Reserved.SelectMany(r => r.DiskIds).Distinct();
        steps.Add(ExecutionStep.Comment(
            $"{plan.ReservedBytes:N0} bytes reserved/unallocated on: {string.Join(", ", reservedDisks)} "
            + "(add a matching-size disk and re-plan to reclaim)"));
    }

    private static bool SameDisks(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.ToHashSet().SetEquals(b);

    private static bool IsProperSubset(IReadOnlyList<string> smaller, IReadOnlyList<string> larger) =>
        larger.Count > smaller.Count && smaller.All(larger.Contains);

    private static int ToMdadmLevel(RaidLevel level) => level switch
    {
        RaidLevel.Mirror => 1,
        RaidLevel.Raid5 => 5,
        RaidLevel.Raid6 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}
