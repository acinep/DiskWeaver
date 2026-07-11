using DiskWeaver.Planner;

namespace DiskWeaver.Executor;

/// <summary>
/// Per-disk space reserved for GPT structures and alignment, and the disk-size
/// adjustment that must happen before real disk sizes are handed to
/// <see cref="TieringPlanner"/>. Discovered the hard way: without this, the
/// bottom tier's segment size equals the smallest disk's exact raw size, and
/// <see cref="CommandPlanner"/> starting the first partition at
/// <see cref="StartAlignmentBytes"/> then needs more space than that disk
/// actually has (`parted` fails with "location is outside of the device").
/// </summary>
public static class PartitionLayout
{
    /// <summary>Space reserved at the start of each disk for the primary GPT header/table and alignment.</summary>
    public const long StartAlignmentBytes = 1024 * 1024; // 1 MiB

    /// <summary>Space reserved at the end of each disk for the backup GPT header/table and alignment.</summary>
    public const long EndReserveBytes = 1024 * 1024; // 1 MiB

    public const long TotalReservedBytesPerDisk = StartAlignmentBytes + EndReserveBytes;

    /// <summary>
    /// Returns disks with their sizes reduced by <see cref="TotalReservedBytesPerDisk"/>, so that
    /// any <see cref="PoolPlan"/> computed from the result is guaranteed partitionable in full by
    /// <see cref="CommandPlanner"/>. Always apply this before <see cref="TieringPlanner.Plan"/> when
    /// the disks came from real hardware (or anything meant to actually be executed).
    /// </summary>
    public static IReadOnlyList<Disk> ForPlanning(IReadOnlyList<Disk> disks)
    {
        return disks.Select(d =>
        {
            var usable = d.SizeBytes - TotalReservedBytesPerDisk;
            if (usable <= 0)
            {
                throw new ArgumentException(
                    $"Disk '{d.Id}' ({d.SizeBytes:N0} bytes) is too small to leave room for "
                    + $"{TotalReservedBytesPerDisk:N0} bytes of GPT/alignment overhead.");
            }

            return d with { SizeBytes = usable };
        }).ToList();
    }

    /// <summary>
    /// <see cref="ForPlanning"/> followed by <see cref="TieringPlanner.Plan"/> -- every real
    /// (non-test) caller needs exactly this pairing and nothing else, so it's collected here
    /// once instead of at each call site. <see cref="TieringPlanner.Plan"/> itself stays callable
    /// directly (and is, extensively, by <c>TieringPlannerTests</c>) for exercising tiering math
    /// against exact round disk sizes, without this reservation's byte offsets in the way.
    /// </summary>
    public static PoolPlan PlanForRealDisks(IReadOnlyList<Disk> disks, RedundancyLevel redundancy) =>
        TieringPlanner.Plan(ForPlanning(disks), redundancy);
}
