using DiskWeaver.Planner;

namespace DiskWeaver.Daemon;

/// <summary>Response body for <c>POST /plan</c>: the computed plan plus its content-addressed id.</summary>
public sealed record PlanResponse(string Id, PoolPlan Plan);
