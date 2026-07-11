using DiskWeaver.Planner;

namespace DiskWeaver.Cli;

/// <summary>Renders a <see cref="PoolPlan"/> as human-readable console output.</summary>
public static class PlanFormatter
{
    public static void Print(PoolPlan plan)
    {
        Console.WriteLine("Tiers:");
        for (var i = 0; i < plan.Tiers.Count; i++)
        {
            var tier = plan.Tiers[i];
            Console.WriteLine(
                $"  [{i}] {tier.RaidLevel} across {tier.DiskIds.Count} disks "
                + $"({string.Join(", ", tier.DiskIds)}), "
                + $"segment {ByteSizeFormatter.Format(tier.SegmentSizeBytes)}, "
                + $"usable {ByteSizeFormatter.Format(tier.UsableBytes)}");
        }

        if (plan.Reserved.Count > 0)
        {
            Console.WriteLine("Reserved (unprotected, not included in pool):");
            foreach (var reserved in plan.Reserved)
            {
                Console.WriteLine(
                    $"  {string.Join(", ", reserved.DiskIds)}: "
                    + $"segment {ByteSizeFormatter.Format(reserved.SegmentSizeBytes)}, "
                    + $"total {ByteSizeFormatter.Format(reserved.TotalRawBytes)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Pool capacity: {ByteSizeFormatter.Format(plan.PoolCapacityBytes)}");
        if (plan.ReservedBytes > 0)
        {
            Console.WriteLine($"Reserved:      {ByteSizeFormatter.Format(plan.ReservedBytes)}");
        }
    }
}
