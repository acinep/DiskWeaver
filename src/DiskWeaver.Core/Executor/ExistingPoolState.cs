namespace DiskWeaver.Executor;

/// <summary>The already-built state of a pool that <see cref="CommandPlanner.BuildIncremental"/> plans against.</summary>
public sealed record ExistingPoolState(string PoolName, string VolumeName, IReadOnlyList<ExistingTier> Tiers);
