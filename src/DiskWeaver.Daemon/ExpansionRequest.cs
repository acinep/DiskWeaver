namespace DiskWeaver.Daemon;

/// <summary>Disks to add to an existing pool via <c>POST /pools/{poolName}/expand</c>.</summary>
/// <param name="DiskIds">Disks to add.</param>
/// <param name="TargetProtection">
/// Optional; the redundancy level (none/dwr1/dwr2) the caller wants, if achievable without a
/// rebuild. Omit to keep the pool's current (inferred) redundancy target -- the default response
/// then just reflects whatever's naturally available: completing any currently degraded/
/// unprotected tier (always attempted regardless of this value), and/or growing existing tiers'
/// capacity in place. Passing a higher level than the pool currently has only changes whether the
/// <c>protection</c> option's pool-wide redundancy-upgrade fallback gets attempted; it has no
/// effect on the <c>space</c> option. Mutually exclusive with <see cref="Redundancy"/> and
/// <see cref="TargetArrayDevice"/> (both bypass the two-option computation entirely).
/// </param>
/// <param name="Redundancy">
/// Optional; advanced/manual mode, bypasses the two-option (<c>protection</c>/<c>space</c>)
/// computation entirely and returns a single plan built exactly as requested. Pass "none" to make
/// the added disk(s) a brand-new, independent unprotected tier; pass "dwr1"/"dwr2" to recompute the
/// whole pool's tiering at that redundancy from every disk (existing + new) together. Mutually
/// exclusive with <see cref="TargetProtection"/> and <see cref="TargetArrayDevice"/>.
/// </param>
/// <param name="TargetArrayDevice">
/// Optional; advanced/manual mode, bypasses the two-option computation entirely. Sends
/// <see cref="DiskIds"/> entirely toward completing this one existing tier (identified by its
/// array device, e.g. "/dev/md/diskweaver-pool-tier0") into a real mirror. Requires the target
/// tier to currently be missing a real member (unprotected-by-design or degraded from a disk
/// failure). Mutually exclusive with <see cref="TargetProtection"/> and <see cref="Redundancy"/>.
/// </param>
/// <param name="AssumeClean">
/// When true, any brand-new tier this expansion creates is built with <c>mdadm --create
/// --assume-clean</c>, skipping its initial full-array resync/parity-build -- see
/// <see cref="Executor.CommandPlanner.BuildIncremental"/>'s <c>assumeClean</c> parameter. Only
/// applies to newly-created tiers; growing an existing tier in place always resyncs (mdadm has no
/// equivalent skip for <c>--grow</c>). Defaults to false.
/// </param>
/// <param name="ChunkSizeKb">
/// The <c>mdadm --create --chunk</c> size (KiB) for any brand-new striped (RAID5/RAID6) tier this
/// expansion creates -- see <see cref="Executor.CommandPlanner.BuildIncremental"/>'s
/// <c>chunkSizeKb</c> parameter. Only applies to newly-created tiers; growing an existing tier in
/// place never changes its chunk size. Must be one of
/// <see cref="Executor.CommandPlanner.ValidChunkSizesKb"/> (<c>400</c> otherwise). Defaults to
/// <see cref="Executor.CommandPlanner.DefaultChunkSizeKb"/>.
/// </param>
public sealed record ExpansionRequest(
    string[] DiskIds,
    string? TargetProtection = null,
    string? Redundancy = null,
    string? TargetArrayDevice = null,
    bool AssumeClean = false,
    int ChunkSizeKb = Executor.CommandPlanner.DefaultChunkSizeKb);
