using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Infers a <see cref="RedundancyLevel"/> from an already-built tier's shape, and (the reverse
/// direction) picks the RAID level TieringPlanner itself would choose for a given redundancy level
/// and member count -- shared by the daemon's expand-preview inference and
/// <see cref="IndependentGrowPlanner"/>, which needs to know what an existing tier will become once
/// grown, not just what it is today.
/// </summary>
public static class TierRedundancy
{
    /// <summary>
    /// A RedundancyLevel.None tier and a really-degraded protected mirror are indistinguishable from
    /// the array's own shape alone (both are 2-slot-configured, 1-real-member arrays) -- that's why
    /// <see cref="ExistingTier.IsUnprotectedByDesign"/> (from the durable "diskweaver-unprotected" PV
    /// tag) is checked first here, rather than inferring intent from ConfiguredMemberCount.
    /// </summary>
    public static RedundancyLevel InferTier(ExistingTier tier) => tier.RaidLevel switch
    {
        RaidLevel.Raid5 => RedundancyLevel.Dwr1,
        RaidLevel.Raid6 => RedundancyLevel.Dwr2,
        RaidLevel.Mirror when tier.IsUnprotectedByDesign => RedundancyLevel.None,
        RaidLevel.Mirror when tier.ConfiguredMemberCountOrDefault - 1 is 1 or 2 => (RedundancyLevel)(tier.ConfiguredMemberCountOrDefault - 1),
        _ => throw new InvalidOperationException(
            $"Can't infer redundancy from tier '{tier.ArrayDevice}' ({tier.RaidLevel}, {tier.ConfiguredMemberCountOrDefault} configured members)."),
    };

    /// <summary>
    /// Redundancy used to be assumed pool-wide (any one tier reveals it), but a pool can now have
    /// independent RedundancyLevel.None tiers sitting alongside protected ones (e.g. an explicit
    /// "add as unprotected tier" expand onto an already-protected pool) -- inspect every tier and
    /// require them to agree, rather than trusting just pool.Tiers[0] and silently guessing wrong
    /// for a mixed pool.
    /// </summary>
    public static RedundancyLevel Infer(ExistingPoolState pool)
    {
        if (pool.Tiers.Count == 0)
        {
            throw new InvalidOperationException($"Pool '{pool.PoolName}' has no tiers -- can't infer its redundancy level.");
        }

        var perTier = pool.Tiers.Select(InferTier).Distinct().ToList();
        if (perTier.Count > 1)
        {
            throw new InvalidOperationException(
                $"Pool '{pool.PoolName}' has tiers at different redundancy levels ({string.Join(", ", perTier)}) -- "
                + "expand must specify redundancy explicitly (none/dwr1/dwr2) for the new disks rather than relying on inference.");
        }

        return perTier[0];
    }

    /// <summary>
    /// Same rule TieringPlanner.Plan uses internally to pick a RAID level for a boundary segment's
    /// participating disk count -- exposed here so growing an *existing* tier in place (see
    /// IndependentGrowPlanner) lands on the same level a fresh build would have chosen.
    /// </summary>
    public static RaidLevel RaidLevelForMemberCount(RedundancyLevel redundancy, int memberCount)
    {
        var r = (int)redundancy;
        if (memberCount == r + 1)
        {
            return RaidLevel.Mirror;
        }

        if (memberCount >= r + 2)
        {
            return r == 1 ? RaidLevel.Raid5 : RaidLevel.Raid6;
        }

        throw new InvalidOperationException(
            $"{memberCount} member(s) isn't enough to sustain {redundancy} redundancy (needs at least {r + 1}).");
    }
}
