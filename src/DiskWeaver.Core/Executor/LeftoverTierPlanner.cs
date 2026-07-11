using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Groups leftover disk capacity (bytes not needed to complete/grow any eligible existing tier)
/// into new tiers, protecting it wherever 2+ same-size leftover pieces make that possible -- the
/// default should be the most protected storage the leftover capacity can provide, not scattered
/// single-disk unprotected tiers just because each piece didn't happen to complete an existing
/// tier. Shared by <see cref="ProtectionPlanner"/> (auto-protect leftovers) and
/// <see cref="IndependentGrowPlanner"/> (grow leftovers) -- same leftover-capacity shape, same
/// grouping rule either way.
/// </summary>
public static class LeftoverTierPlanner
{
    public static List<Tier> Plan(List<(string Id, long SpareBytes)> leftover)
    {
        if (leftover.Count == 0)
        {
            return [];
        }

        if (leftover.Count == 1)
        {
            var (id, spareBytes) = leftover[0];
            return [new Tier(spareBytes, [id], RaidLevel.Mirror, spareBytes, DegradedSlots: 1)];
        }

        var leftoverPlan = TieringPlanner.Plan(
            leftover.Select(x => new Disk(x.Id, x.SpareBytes)).ToList(), RedundancyLevel.Dwr1);

        // A segment TieringPlanner reserved (fewer than 2 disks reach it, so it can't even form a
        // mirror) is still real, unwasted spare capacity -- same principle as the single-leftover-
        // disk case above, just per reserved segment instead of per disk, since a reserved segment
        // can itself span more than one disk if none of them individually reach the next boundary.
        var reservedAsUnprotected = leftoverPlan.Reserved
            .SelectMany(r => r.DiskIds.Select(diskId => new Tier(r.SegmentSizeBytes, [diskId], RaidLevel.Mirror, r.SegmentSizeBytes, DegradedSlots: 1)));

        return [.. leftoverPlan.Tiers, .. reservedAsUnprotected];
    }
}
