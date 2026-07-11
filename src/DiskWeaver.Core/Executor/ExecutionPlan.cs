namespace DiskWeaver.Executor;

/// <summary>The ordered set of commands needed to realize a <see cref="Planner.PoolPlan"/>.</summary>
public sealed record ExecutionPlan(IReadOnlyList<ExecutionStep> Steps);
