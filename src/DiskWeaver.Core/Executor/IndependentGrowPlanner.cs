using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Plans growing a pool's existing independent tiers in place -- each existing array picks up more
/// members (and migrates RAID level as it crosses a redundancy boundary, same rule TieringPlanner
/// itself uses) -- rather than merging them into one shared array. This is the alternative
/// <c>CommandPlanner.ClassifyIncremental</c>'s <c>MergeConflicts</c> refusal points at: requesting a
/// blanket redundancy level across new + existing disks makes TieringPlanner want one shared tier
/// per size boundary, which conflicts with tiers that were built independently (e.g. from a
/// RedundancyLevel.None build later completed into separate protected mirrors). There's no mdadm
/// operation to merge two already-built arrays, but each one individually can still grow via the
/// same <c>mdadm --add</c> + <c>--grow</c>/level-migration path already used for a single-tier pool.
/// </summary>
public static class IndependentGrowPlanner
{
    /// <summary>One existing tier's grown-in-place target shape, ready for <c>CommandPlanner</c>.</summary>
    public sealed record TierGrowth(ExistingTier Existing, Tier Desired);

    /// <summary>
    /// <paramref name="TierGrowths"/>: existing tiers that received at least one new disk.
    /// <paramref name="NewIndependentTiers"/>: disk capacity that didn't fit any eligible tier (no
    /// eligible tiers at all, disks left over after every tier's needs were met, or a disk too small
    /// for any eligible tier's segment) -- turned into brand-new independent tiers, same fallback
    /// <see cref="ProtectionPlanner"/> uses for its own leftovers, rather than being wasted or refused.
    /// </summary>
    public sealed record GrowthPlan(
        IReadOnlyList<TierGrowth> TierGrowths,
        IReadOnlyList<Tier> NewIndependentTiers);

    /// <summary>
    /// Distributes <paramref name="newDisks"/> across every eligible existing tier (Mirror, Raid5, or
    /// Raid6 -- any RAID level a member count can still grow into) as evenly as possible: one pass
    /// assigns at most one disk(-chunk) per tier, repeating passes until nothing more fits anywhere.
    /// A disk larger than a tier's segment can end up split across several tiers' passes (its
    /// leftover capacity re-offered each pass), the same "reuse a disk's remaining spare capacity"
    /// pattern <see cref="ProtectionPlanner"/> uses -- already confirmed to need zero
    /// <c>CommandPlanner</c> changes when one physical disk appears in more than one desired tier's
    /// <c>DiskIds</c> within a single Build call. Not a globally optimal bin-packing (first-fit per
    /// pass, not best-fit-after-every-mutation), which is enough for the common case (a handful of
    /// equal- or round-multiple-sized disks against equal-segment-size tiers).
    /// </summary>
    public static GrowthPlan PlanGrowth(IReadOnlyList<ExistingTier> tiers, IReadOnlyList<Disk> newDisks)
    {
        // A tier currently missing a real member (degraded, or an unprotected-by-design tier not
        // yet completed) isn't eligible for this "add another real member" growth -- filling its
        // missing slot is capacity-neutral (mdadm's mirror/parity capacity is fixed by the
        // *configured* member count, not how many are currently real) and is ProtectionPlanner's
        // job, not this one's. Growing it here via RaidLevelForMemberCount would wrongly treat
        // "went from 1 real member to 2" as a level migration needing a real capacity recompute,
        // which it isn't -- confirmed live via CommandPlanner.BuildGrowSteps's own fill-missing-slot
        // branch always being capacity-neutral.
        var eligible = tiers
            .Where(t => t.RaidLevel is RaidLevel.Mirror or RaidLevel.Raid5 or RaidLevel.Raid6)
            .Where(t => t.DiskIds.Count >= t.ConfiguredMemberCountOrDefault)
            .ToList();

        var remaining = newDisks
            .Select(d => (d.Id, SpareBytes: d.SizeBytes - PartitionLayout.TotalReservedBytesPerDisk))
            .ToList();

        var assignedDiskIds = eligible.ToDictionary(t => t.ArrayDevice, _ => new List<string>());

        var keepGoing = eligible.Count > 0;
        while (keepGoing)
        {
            keepGoing = false;
            foreach (var tier in eligible)
            {
                // A disk already a member of this tier (originally, or assigned in an earlier pass)
                // can't become a second member of the same array -- skip it for this tier even if
                // its remaining spare capacity would otherwise fit, but leave it available for
                // other tiers.
                var alreadyUsed = new HashSet<string>(tier.DiskIds.Concat(assignedDiskIds[tier.ArrayDevice]));
                remaining = remaining.OrderBy(x => x.SpareBytes).ToList();
                var matchIndex = remaining.FindIndex(x => !alreadyUsed.Contains(x.Id) && x.SpareBytes >= tier.SegmentSizeBytes);
                if (matchIndex < 0)
                {
                    continue;
                }

                var (diskId, spareBytes) = remaining[matchIndex];
                assignedDiskIds[tier.ArrayDevice].Add(diskId);
                remaining[matchIndex] = (diskId, spareBytes - tier.SegmentSizeBytes);
                keepGoing = true;
            }
        }

        var tierGrowths = new List<TierGrowth>();
        foreach (var tier in eligible)
        {
            var newIds = assignedDiskIds[tier.ArrayDevice];
            if (newIds.Count == 0)
            {
                continue;
            }

            var redundancy = TierRedundancy.InferTier(tier);
            var allDiskIds = tier.DiskIds.Concat(newIds).ToList();
            var raidLevel = TierRedundancy.RaidLevelForMemberCount(redundancy, allDiskIds.Count);
            var usable = raidLevel switch
            {
                RaidLevel.Mirror => tier.SegmentSizeBytes,
                RaidLevel.Raid5 => (allDiskIds.Count - 1) * tier.SegmentSizeBytes,
                RaidLevel.Raid6 => (allDiskIds.Count - 2) * tier.SegmentSizeBytes,
                _ => throw new ArgumentOutOfRangeException(nameof(raidLevel), raidLevel, null),
            };

            tierGrowths.Add(new TierGrowth(tier, new Tier(tier.SegmentSizeBytes, allDiskIds, raidLevel, usable)));
        }

        // Same too-small-to-be-a-usable-PV floor ProtectionPlanner applies to its own leftovers, and
        // the same "group same-size leftovers into a shared protected tier rather than scattering
        // them as separate unprotected ones" rule -- see LeftoverTierPlanner.
        var leftover = remaining.Where(x => x.SpareBytes > PartitionLayout.TotalReservedBytesPerDisk).ToList();
        var newIndependentTiers = LeftoverTierPlanner.Plan(leftover);

        return new GrowthPlan(tierGrowths, newIndependentTiers);
    }
}
