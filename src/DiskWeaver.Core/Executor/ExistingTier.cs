using DiskWeaver.Planner;
using System.IO.Compression;

namespace DiskWeaver.Executor;

/// <summary>An already-built mdadm array backing one tier of a pool that already exists on disk.</summary>
/// <param name="ArrayDevice">The real device path, e.g. /dev/md/diskweaver-pool-tier0.</param>
/// <param name="DiskIds">Member disk ids, same order as <paramref name="PartitionPaths"/>.</param>
/// <param name="PartitionPaths">
/// Each member's real partition device path (e.g. /dev/loop3p2), same order as
/// <paramref name="DiskIds"/>. Carried through from <c>mdadm --detail --export</c> rather than
/// re-derived from a partition-numbering convention -- after an incremental same-level grow, a
/// disk's partition for this tier isn't necessarily "partition 1" just because this is the
/// smallest-segment tier (partition numbers follow creation order, not segment-size order, once
/// a tier is grown into after other tiers already exist on the same disks).
/// </param>
/// <param name="ConfiguredMemberCount">
/// The array's configured member slot count (mdadm --detail --export's MD_DEVICES), which can
/// exceed <paramref name="DiskIds"/>.Count when one or more slots are currently "missing" --
/// either a RedundancyLevel.None tier deliberately built degraded (see Tier.DegradedSlots), or an
/// ordinary array that's degraded from a real disk failure. Defaults to DiskIds.Count for the
/// (overwhelmingly common) case of a fully-populated, healthy array.
/// </param>
/// <param name="IsUnprotectedByDesign">
/// Whether this tier's PV carries the "diskweaver-unprotected" LVM tag
/// (`MdadmLvmPoolStateSource` reads it from `pvs`'s `pv_tags` field) -- the durable signal that
/// distinguishes a RedundancyLevel.None tier deliberately built degraded from an ordinary tier
/// that's degraded from a real disk failure. The two are indistinguishable from the array's own
/// shape alone (both are 2-slot-configured, 1-real-member arrays), which is exactly why this is a
/// separate, explicitly-tagged fact rather than inferred from <see cref="ConfiguredMemberCount"/>.
/// </param>
/// <param name="SyncOperation">
/// mdadm's name for whatever's currently running against this array's data -- "recovery" (rebuilding
/// a replaced/re-added member), "resync" (routine consistency pass, also used for "not yet started"),
/// "reshape", or "check". Null means fully in sync, not "unknown" -- <c>/proc/mdstat</c> only reports
/// a progress line while something is actually running.
/// </param>
public sealed record ExistingTier(
    string ArrayDevice,
    long SegmentSizeBytes,
    IReadOnlyList<string> DiskIds,
    RaidLevel RaidLevel,
    IReadOnlyList<string> PartitionPaths,
    int? ConfiguredMemberCount = null,
    bool IsUnprotectedByDesign = false,
    string? SyncOperation = null,
    double? SyncPercentComplete = null,
    double? SyncSpeedKBps = null,
    double? SyncEtaMinutes = null)
{
    public int ConfiguredMemberCountOrDefault => ConfiguredMemberCount ?? DiskIds.Count;

    /// <summary>Capacity this tier actually contributes today -- same (m-r)*segment math as <see cref="Tier.UsableBytes"/>.</summary>
    public long UsableBytes => RaidLevel switch
    {
        RaidLevel.Mirror => SegmentSizeBytes,
        RaidLevel.Raid5 => (DiskIds.Count - 1) * SegmentSizeBytes,
        RaidLevel.Raid6 => (DiskIds.Count - 2) * SegmentSizeBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(RaidLevel), RaidLevel, null),
    };
}
