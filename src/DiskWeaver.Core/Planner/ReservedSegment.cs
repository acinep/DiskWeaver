namespace DiskWeaver.Planner;

/// <summary>
/// A segment that couldn't meet the requested redundancy level and is
/// therefore left out of the protected pool. See docs/algorithm.md.
/// </summary>
/// <param name="SegmentSizeBytes">Size of the slice contributed by each participating disk.</param>
/// <param name="DiskIds">Disks whose slice in this range sits unallocated.</param>
public sealed record ReservedSegment(long SegmentSizeBytes, IReadOnlyList<string> DiskIds)
{
    /// <summary>Total raw bytes left unallocated across all disks in this segment.</summary>
    public long TotalRawBytes => SegmentSizeBytes * DiskIds.Count;
}
