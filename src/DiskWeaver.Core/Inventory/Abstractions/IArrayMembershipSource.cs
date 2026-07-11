namespace DiskWeaver.Core.Inventory.Abstractions;

/// <summary>
/// Reports which partitions are currently members of a live (assembled) mdadm array, regardless of
/// whether that array belongs to a known DiskWeaver pool. Exists so `POST /disks/wipe` can refuse a
/// partition still attached to a running array up front, with a clear message naming the array --
/// instead of letting `mdadm --zero-superblock` fail with its own much less actionable "Couldn't
/// open ... for write - not zeroing" (the failure mode a reshape that died partway through leaves
/// behind, e.g. a spare added by a grow that then hit an unsupported RAID-level migration).
/// </summary>
public interface IArrayMembershipSource
{
    /// <summary>Maps each member partition's device path (e.g. "/dev/loop3p1") to its array's device path (e.g. "/dev/md127").</summary>
    IReadOnlyDictionary<string, string> GetArrayMembership();
}
