namespace DiskWeaver.Daemon;

/// <summary>
/// Request body for <c>POST /plan</c>. See docs/daemon-api.md.
/// </summary>
/// <param name="PoolName">
/// The volume group name for a brand-new pool, and the prefix for its tier array names
/// (<c>/dev/md/{PoolName}-tier&lt;N&gt;</c>) -- defaults to <c>diskweaver-pool</c> when omitted/empty,
/// matching the single-pool behavior before multi-pool support existed. Choosing a distinct name
/// per pool is what lets more than one pool coexist on one host without an mdadm/LVM naming
/// collision; see execution.md's "Multiple pools on one host".
/// </param>
public sealed record PlanRequest(string[] DiskIds, string Redundancy, string? PoolName = null);
