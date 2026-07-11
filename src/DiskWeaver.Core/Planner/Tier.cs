namespace DiskWeaver.Planner;

/// <summary>
/// One same-size segment shared across a group of disks, protected as a
/// single mdadm array. See docs/algorithm.md.
/// </summary>
/// <param name="SegmentSizeBytes">Size of the slice contributed by each participating disk.</param>
/// <param name="DiskIds">Disks participating in this tier's array.</param>
/// <param name="RaidLevel">The array type chosen for this tier.</param>
/// <param name="UsableBytes">Capacity this tier contributes to the pool.</param>
/// <param name="DegradedSlots">
/// Extra mdadm "missing" member slots to create the array with beyond <see cref="DiskIds"/> --
/// non-zero only for a RedundancyLevel.None tier, which is built as a degraded 2-slot RAID1
/// (one real disk, one "missing") rather than a bare partition, so a disk added later can fill
/// the slot via a plain `mdadm --add` and resync into a real mirror. A genuine single-member
/// (`--raid-devices=1`) array was tried instead and confirmed broken: `mdadm --add` refuses to
/// add a first spare to a true 1-device array ("cannot load array metadata"), verified live —
/// the degraded-2-slot approach is the only one that actually works for growing. The resulting
/// ambiguity with a really-degraded protected mirror (both are 2-slot-configured, 1-real-member)
/// is resolved with a separate LVM PV tag, not by the array's own shape — see
/// `ExistingTier.IsUnprotectedByDesign` and docs/algorithm.md.
/// </param>
public sealed record Tier(
    long SegmentSizeBytes,
    IReadOnlyList<string> DiskIds,
    RaidLevel RaidLevel,
    long UsableBytes,
    int DegradedSlots = 0);
