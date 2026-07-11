using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Computes up to two candidate expansion plans for a pool given newly-offered disks: one that
/// adds protection, one that adds space -- see docs/algorithm.md's expand tenets. Either can come
/// back null (e.g. a disk too small to help any tier at all -- the "hot spare" case yields both
/// null), and a caller picks whichever option(s) are actually present rather than one of several
/// request modes it has to already know applies to this pool's shape.
/// </summary>
public static class ExpansionOptionsPlanner
{
    public const string ProtectionIntent = "protection";
    public const string SpaceIntent = "space";

    public sealed record Option(string Intent, PoolPlan Desired, long AchievedCapacityBytes, RedundancyLevel? AchievedRedundancy);

    public sealed record Options(Option? Protection, Option? Space);

    /// <param name="pool">The pool's current on-disk state.</param>
    /// <param name="allDisks">
    /// Every disk in the resulting pool if it were fully rebuilt from scratch -- the pool's
    /// existing disks plus <paramref name="newDisks"/>, resolved to real (not planning-adjusted)
    /// sizes. Only used for the protection option's pool-wide redundancy-upgrade fallback; see
    /// <see cref="PartitionLayout.ForPlanning"/>, applied internally here.
    /// </param>
    /// <param name="newDisks">The disks being offered to the pool right now.</param>
    /// <param name="targetProtection">
    /// The redundancy level the caller wants, if achievable without a rebuild. Only relevant when
    /// no currently degraded/unprotected tier can be completed -- see
    /// <see cref="ComputeProtectionOption"/>.
    /// </param>
    public static Options ComputeOptions(
        ExistingPoolState pool, IReadOnlyList<Disk> allDisks, IReadOnlyList<Disk> newDisks, RedundancyLevel targetProtection)
    {
        var currentCapacityBytes = pool.Tiers.Sum(t => t.UsableBytes);

        return new Options(
            ComputeProtectionOption(pool, allDisks, newDisks, targetProtection),
            ComputeSpaceOption(pool, newDisks, currentCapacityBytes));
    }

    /// <summary>
    /// Only offers a protection candidate when something is genuinely more protected afterward than
    /// before -- a real 0-&gt;1 or 1-&gt;2 increase, matching the HHR model this whole endpoint
    /// is aiming for. Prefers completing currently degraded/unprotected tiers (<see cref="ProtectionPlanner"/>)
    /// -- cheap, no rebuild, works regardless of <paramref name="targetProtection"/>, since going from
    /// "missing a member" to "fully populated" is strictly more protected no matter the target. Only
    /// when that yields no real completion (pool's already fully protected -- note this deliberately
    /// ignores <see cref="ProtectionPlanner.AutoProtectPlan.NewTiers"/> when it's just leftover
    /// *brand-new* disks grouped into their own protected tier: those disks weren't part of the pool
    /// before, so turning them into a mirror isn't increasing anything's protection, it's just an
    /// alternate capacity layout -- the space option already covers that) does a pool-wide redundancy
    /// upgrade get attempted, and only if it doesn't need to merge independent existing tiers (no
    /// mdadm operation does that -- <see cref="CommandPlanner.HasMergeConflicts"/>); a rebuild being
    /// needed for that isn't an error here, it just means no protection option is available.
    /// </summary>
    private static Option? ComputeProtectionOption(
        ExistingPoolState pool, IReadOnlyList<Disk> allDisks, IReadOnlyList<Disk> newDisks, RedundancyLevel targetProtection)
    {
        var autoPlan = ProtectionPlanner.PlanAutoProtect(pool.Tiers, newDisks);

        if (autoPlan.TierAssignments.Count > 0)
        {
            var assignedByArrayDevice = autoPlan.TierAssignments.ToDictionary(a => a.ArrayDevice, a => a.DiskIds);
            var grownTiers = pool.Tiers.Select(t =>
                assignedByArrayDevice.TryGetValue(t.ArrayDevice, out var assignedDiskIds)
                    ? new Tier(t.SegmentSizeBytes, [.. t.DiskIds, .. assignedDiskIds], t.RaidLevel, t.SegmentSizeBytes)
                    : new Tier(t.SegmentSizeBytes, t.DiskIds, t.RaidLevel, t.UsableBytes))
                .ToList();

            var desired = new PoolPlan([.. grownTiers, .. autoPlan.NewTiers], []);
            return new Option(ProtectionIntent, desired, desired.PoolCapacityBytes, HighestAchievedRedundancy(desired));
        }

        RedundancyLevel? existingRedundancy;
        try
        {
            existingRedundancy = TierRedundancy.Infer(pool);
        }
        catch (InvalidOperationException)
        {
            // A mixed pool (tiers at different redundancy levels) doesn't have one redundancy to
            // compare targetProtection against -- treat as "unknown", not as "already met", so the
            // upgrade attempt below still gets a chance.
            existingRedundancy = null;
        }

        if (existingRedundancy is not null && targetProtection <= existingRedundancy)
        {
            return null;
        }

        PoolPlan upgraded;
        try
        {
            upgraded = PartitionLayout.PlanForRealDisks(allDisks, targetProtection);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (CommandPlanner.HasMergeConflicts(pool, upgraded))
        {
            return null;
        }

        return new Option(ProtectionIntent, upgraded, upgraded.PoolCapacityBytes, targetProtection);
    }

    /// <summary>
    /// Grows every eligible existing tier in place at its own current redundancy (never the
    /// requested <c>targetProtection</c> -- raising redundancy is the protection option's job, not
    /// this one's) via <see cref="IndependentGrowPlanner"/>, kept only when it actually raises
    /// achieved capacity above what the pool has today -- a disk that can't add space to anything
    /// (e.g. only completes an already-eligible mirror, which is capacity-neutral) yields no space
    /// option rather than an empty no-op one.
    /// </summary>
    private static Option? ComputeSpaceOption(ExistingPoolState pool, IReadOnlyList<Disk> newDisks, long currentCapacityBytes)
    {
        var growthPlan = IndependentGrowPlanner.PlanGrowth(pool.Tiers, newDisks);

        // A grown existing tier is always a real space gain. A brand-new *unprotected* leftover
        // tier (a lone disk with no size-matched partner) isn't -- the same disk could instead
        // complete a degraded tier (the protection option's job) or, if truly unusable elsewhere,
        // isn't a meaningful "grow this pool's space" outcome to present as a competing candidate;
        // only a genuinely protected new leftover tier (2+ matching leftovers grouped) counts.
        var hasRealSpaceGain = growthPlan.TierGrowths.Count > 0 || growthPlan.NewIndependentTiers.Any(t => t.DegradedSlots == 0);
        if (!hasRealSpaceGain)
        {
            return null;
        }

        var desiredByArrayDevice = growthPlan.TierGrowths.ToDictionary(g => g.Existing.ArrayDevice, g => g.Desired);

        var grownTiers = pool.Tiers.Select(t =>
            desiredByArrayDevice.TryGetValue(t.ArrayDevice, out var desiredTier)
                ? desiredTier
                : new Tier(t.SegmentSizeBytes, t.DiskIds, t.RaidLevel, t.UsableBytes))
            .ToList();

        var desired = new PoolPlan([.. grownTiers, .. growthPlan.NewIndependentTiers], []);
        if (desired.PoolCapacityBytes <= currentCapacityBytes)
        {
            return null;
        }

        return new Option(SpaceIntent, desired, desired.PoolCapacityBytes, null);
    }

    private static RedundancyLevel HighestAchievedRedundancy(PoolPlan plan) =>
        plan.Tiers.Select(TierRedundancyOf).DefaultIfEmpty(RedundancyLevel.None).Max();

    private static RedundancyLevel TierRedundancyOf(Tier tier)
    {
        if (tier.DegradedSlots > 0)
        {
            return RedundancyLevel.None;
        }

        return tier.RaidLevel switch
        {
            RaidLevel.Raid6 => RedundancyLevel.Dwr2,
            RaidLevel.Raid5 or RaidLevel.Mirror => RedundancyLevel.Dwr1,
            _ => RedundancyLevel.None,
        };
    }
}
