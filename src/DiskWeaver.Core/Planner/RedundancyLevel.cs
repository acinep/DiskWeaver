namespace DiskWeaver.Planner;

/// <summary>
/// Pool-wide fault tolerance target. The numeric value is the number of
/// simultaneous disk failures the resulting pool must tolerate.
/// </summary>
public enum RedundancyLevel
{
    /// <summary>No protection: each disk is its own independent, unprotected segment.</summary>
    None = 0,

    /// <summary>DWR-1: tolerate 1 disk failure.</summary>
    Dwr1 = 1,

    /// <summary>DWR-2: tolerate 2 disk failures.</summary>
    Dwr2 = 2,
}
