namespace DiskWeaver.Planner;

/// <summary>The output of <see cref="TieringPlanner"/>: the full set of tiers making up a pool.</summary>
/// <param name="Tiers">Protected tiers, each backing one mdadm array.</param>
/// <param name="Reserved">Segments excluded from the protected pool.</param>
public sealed record PoolPlan(IReadOnlyList<Tier> Tiers, IReadOnlyList<ReservedSegment> Reserved)
{
    /// <summary>Total usable pool capacity across all protected tiers.</summary>
    public long PoolCapacityBytes => Tiers.Sum(t => t.UsableBytes);

    /// <summary>Total raw bytes left unallocated across all reserved segments.</summary>
    public long ReservedBytes => Reserved.Sum(r => r.TotalRawBytes);
}
