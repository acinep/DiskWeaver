namespace DiskWeaver.Planner;

/// <summary>
/// Computes the HHR-style segment tiering plan for a set of disks. See
/// docs/algorithm.md for the derivation and worked examples.
/// </summary>
public static class TieringPlanner
{
    public static PoolPlan Plan(IReadOnlyList<Disk> disks, RedundancyLevel redundancy)
    {
        if (disks.Count == 0)
        {
            throw new ArgumentException("At least 1 disk is required.", nameof(disks));
        }

        if (redundancy == RedundancyLevel.None)
        {
            // JBOD-style: each disk is its own independent, unprotected segment -- not grouped
            // with the others the way Dwr1/Dwr2 tiers pool disks together for fault tolerance,
            // since there's no shared fault tolerance to compute here. Each is built as a
            // degraded 2-slot mirror (1 real disk + 1 "missing") so a disk added to it later
            // needs only a plain `mdadm --add`, not an array rebuild -- see Tier.DegradedSlots.
            var unprotectedTiers = disks
                .Select(d => new Tier(d.SizeBytes, [d.Id], RaidLevel.Mirror, d.SizeBytes, DegradedSlots: 1))
                .ToList();
            return new PoolPlan(unprotectedTiers, []);
        }

        if (disks.Count < 2)
        {
            throw new ArgumentException("At least 2 disks are required.", nameof(disks));
        }

        int r = (int)redundancy;
        var sortedDisks = disks.OrderBy(d => d.SizeBytes).ToList();
        var boundaries = sortedDisks.Select(d => d.SizeBytes).Distinct().OrderBy(s => s).ToList();

        var tiers = new List<Tier>();
        var reserved = new List<ReservedSegment>();
        long previousBoundary = 0;

        foreach (var boundary in boundaries)
        {
            long segmentSize = boundary - previousBoundary;
            var participating = sortedDisks
                .Where(d => d.SizeBytes >= boundary)
                .Select(d => d.Id)
                .ToList();
            int m = participating.Count;

            if (m < r + 1)
            {
                reserved.Add(new ReservedSegment(segmentSize, participating));
            }
            else if (m == r + 1)
            {
                tiers.Add(new Tier(segmentSize, participating, RaidLevel.Mirror, segmentSize));
            }
            else
            {
                var raidLevel = r == 1 ? RaidLevel.Raid5 : RaidLevel.Raid6;
                long usable = (m - r) * segmentSize;
                tiers.Add(new Tier(segmentSize, participating, raidLevel, usable));
            }

            previousBoundary = boundary;
        }

        return new PoolPlan(tiers, reserved);
    }
}
