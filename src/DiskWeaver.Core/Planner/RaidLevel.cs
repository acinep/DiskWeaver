namespace DiskWeaver.Planner;

/// <summary>The mdadm array type chosen for a single tier/segment.</summary>
public enum RaidLevel
{
    /// <summary>RAID1 mirror across all disks in the tier.</summary>
    Mirror,

    /// <summary>RAID5 — single parity.</summary>
    Raid5,

    /// <summary>RAID6 — dual parity.</summary>
    Raid6,
}
