namespace DiskWeaver.Executor;

/// <summary>The already-built state of a pool that <see cref="CommandPlanner.BuildIncremental"/> plans against.</summary>
/// <param name="Error">
/// Set when one or more of this pool's tiers couldn't be read (e.g. a member's mdadm array isn't
/// currently running, so LVM reports its PV device as "[unknown]") -- Tiers then holds only the
/// tiers that *could* be read, which may be an incomplete or empty list. Non-null Error is a signal
/// to callers (teardown, expand) to refuse acting on this pool until the underlying array is fixed,
/// rather than planning against partial/unknown state.
/// </param>
public sealed record ExistingPoolState(string PoolName, string VolumeName, IReadOnlyList<ExistingTier> Tiers, string? Error = null);
