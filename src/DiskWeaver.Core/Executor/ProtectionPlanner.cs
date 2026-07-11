using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Figures out how to use newly-added disks to complete currently under-protected independent
/// tiers (RedundancyLevel.None tiers built as a degraded 2-slot mirror, or ordinary mirrors
/// degraded by a real disk failure -- both the same on-disk shape, disambiguated via
/// <see cref="ExistingTier.IsUnprotectedByDesign"/>) -- the automatic counterpart to explicitly
/// picking one target tier and a disk for it. Lives here (not alongside
/// <see cref="TieringPlanner"/> in DiskWeaver.Planner) because it needs <see cref="ExistingTier"/>,
/// which only exists in this project.
/// </summary>
public static class ProtectionPlanner
{
    /// <summary>One or more disks assigned to complete an existing tier's missing member(s).</summary>
    public sealed record TierAssignment(string ArrayDevice, IReadOnlyList<string> DiskIds);

    /// <summary>
    /// <paramref name="TierAssignments"/>: which new disk(s) go toward completing which existing
    /// tier. <paramref name="NewTiers"/>: disk capacity left over after every eligible tier is
    /// satisfied (whole unmatched disks, or the unused remainder of a disk that helped complete a
    /// smaller tier) -- protected wherever 2+ same-size leftover pieces make that possible (the
    /// default should be the most protected storage the offered disks can provide, not leftover
    /// disks scattered as separate single-disk unprotected tiers just because they didn't happen to
    /// complete an existing one), falling back to an independent unprotected tier only for a piece
    /// that has no size-matched partner to protect it with.
    /// </summary>
    public sealed record AutoProtectPlan(
        IReadOnlyList<TierAssignment> TierAssignments,
        IReadOnlyList<Tier> NewTiers);

    /// <summary>
    /// Greedy first-fit match: eligible tiers (currently missing one or more real members versus
    /// their configured slot count) smallest-segment-first, against new disks' spare capacity
    /// smallest-first -- so a disk exactly the right size for the smallest tier is used for it
    /// first, and a disk large enough for more than one tier (e.g. one 4x-sized disk completing
    /// two independent same-size tiers) gets reused across tiers via its remaining spare capacity,
    /// not treated as "already spoken for" after its first assignment. Not a globally optimal
    /// bin-packing (first-fit, not best-fit-after-every-mutation) -- re-sorts remaining spare
    /// capacity between tiers, which is enough for the common case (a handful of equal- or
    /// round-multiple-sized disks) without the complexity of a real packing solver.
    /// </summary>
    public static AutoProtectPlan PlanAutoProtect(IReadOnlyList<ExistingTier> tiers, IReadOnlyList<Disk> newDisks)
    {
        // A tier is eligible whenever it's genuinely missing a real member versus its configured
        // slot count -- covers both a RedundancyLevel.None tier deliberately built degraded (see
        // Tier.DegradedSlots) and an ordinary mirror degraded by a real disk failure uniformly,
        // since both are the exact same on-disk shape (a genuine single-member array turned out to
        // be unsupported by mdadm for this -- see BuildTierCreationSteps). Deliberately not
        // generalized to "missing 2+ members" (e.g. a 3-way Dwr2 mirror down to 1 real member),
        // which is rare enough to be out of scope for this pass.
        var eligible = tiers
            .Where(t => t.RaidLevel == RaidLevel.Mirror && t.DiskIds.Count < t.ConfiguredMemberCountOrDefault)
            .OrderBy(t => t.SegmentSizeBytes)
            .ToList();

        var remaining = newDisks
            .Select(d => (d.Id, SpareBytes: d.SizeBytes - PartitionLayout.TotalReservedBytesPerDisk))
            .ToList();

        var assignments = new List<TierAssignment>();

        foreach (var tier in eligible)
        {
            var neededMembers = tier.ConfiguredMemberCountOrDefault - tier.DiskIds.Count;
            var assignedDiskIds = new List<string>();

            for (var i = 0; i < neededMembers; i++)
            {
                remaining = remaining.OrderBy(x => x.SpareBytes).ToList();
                var matchIndex = remaining.FindIndex(x => x.SpareBytes >= tier.SegmentSizeBytes);
                if (matchIndex < 0)
                {
                    break; // No disk (or disk remainder) big enough is left -- tier stays as-is.
                }

                var (diskId, spareBytes) = remaining[matchIndex];
                assignedDiskIds.Add(diskId);
                remaining[matchIndex] = (diskId, spareBytes - tier.SegmentSizeBytes);
            }

            if (assignedDiskIds.Count > 0)
            {
                assignments.Add(new TierAssignment(tier.ArrayDevice, assignedDiskIds));
            }
        }

        // A leftover sliver that doesn't even clear one disk's own partition/alignment overhead
        // isn't a usable tier -- e.g. splitting a disk evenly across N same-size eligible tiers can
        // leave exactly one reservation's worth of bytes over, which LVM's pvcreate then refuses
        // outright ("device is too small (pv_min_size)"), confirmed live.
        var leftover = remaining.Where(x => x.SpareBytes > PartitionLayout.TotalReservedBytesPerDisk).ToList();
        var newTiers = LeftoverTierPlanner.Plan(leftover);

        return new AutoProtectPlan(assignments, newTiers);
    }
}
