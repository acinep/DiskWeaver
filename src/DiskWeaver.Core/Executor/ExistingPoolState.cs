namespace DiskWeaver.Executor;

/// <summary>The already-built state of a pool that <see cref="CommandPlanner.BuildIncremental"/> plans against.</summary>
/// <param name="VolumeNames">
/// Every LV in the pool's VG, in safe-removal order: thin volumes (LVs with a non-empty pool_lv,
/// i.e. carved from a thin pool) before the thin pool/thick LV they depend on. A freshly-built
/// DiskWeaver pool has exactly one entry ("data", a thick LV). A pool a user has since converted to
/// a thin pool with multiple thin LVs on top (see docs on thin provisioning) reports all of them --
/// DiskWeaver doesn't create or manage those LVs itself, but teardown must remove every one of them
/// before the VG can go, and expand must not blindly lvextend the wrong one.
/// </param>
/// <param name="Error">
/// Set when one or more of this pool's tiers couldn't be read (e.g. a member's mdadm array isn't
/// currently running, so LVM reports its PV device as "[unknown]") -- Tiers then holds only the
/// tiers that *could* be read, which may be an incomplete or empty list. Non-null Error is a signal
/// to callers (teardown, expand) to refuse acting on this pool until the underlying array is fixed,
/// rather than planning against partial/unknown state.
/// </param>
public sealed record ExistingPoolState(
    string PoolName, IReadOnlyList<string> VolumeNames, IReadOnlyList<ExistingTier> Tiers, string? Error = null);
