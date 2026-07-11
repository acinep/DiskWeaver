using DiskWeaver.Planner;

namespace DiskWeaver.Daemon;

/// <summary>
/// One candidate plan returned by <c>POST /pools/{poolName}/expand</c>. <see cref="DesiredPlan"/>
/// is the full end-state (as if any grow-candidate tiers had also been manually reshaped);
/// <see cref="AchievedCapacityBytes"/> is what actually results from calling
/// <c>POST /pools/{poolName}/expand/{id}/execute</c> on <see cref="PlanId"/> right now -- see
/// <see cref="Executor.CommandPlanner.AchievedCapacityBytes"/>. A UI should show the second number
/// as "what you'll get," not the first.
/// </summary>
/// <param name="Intent">
/// <c>"protection"</c> or <c>"space"</c> for one of the two default candidates (see
/// <see cref="Executor.ExpansionOptionsPlanner"/>), or <c>"manual"</c> for the advanced
/// <c>targetArrayDevice</c>/<c>redundancy</c> request modes, which always return exactly one.
/// </param>
/// <param name="AchievedRedundancy">
/// The redundancy level this option's tiers end up at, when it's unambiguous -- null for the
/// <c>space</c> intent (which deliberately doesn't change redundancy) and for <c>manual</c> mode.
/// </param>
public sealed record ExpansionOption(
    string Intent, string PlanId, PoolPlan DesiredPlan, long AchievedCapacityBytes, string? AchievedRedundancy);

/// <summary>
/// Response for <c>POST /pools/{poolName}/expand</c>: 0, 1, or 2 candidate plans (see
/// docs/algorithm.md's expand tenets) -- a disk too small to help any tier yields none (the "hot
/// spare" case), the advanced request modes always return exactly one.
/// </summary>
/// <param name="HypotheticalRebuildCapacityBytes">
/// Purely informational, never executed: what tearing down and rebuilding fresh from every disk in
/// the pool plus the ones being offered right now would achieve at the pool's own redundancy level
/// -- see <see cref="Executor.CommandPlanner.HypotheticalFullRebuildCapacityBytes"/>. Null when it
/// can't be meaningfully computed (e.g. a pool whose tiers disagree on redundancy). A "btw" note,
/// never a blocking error or a third option.
/// </param>
public sealed record ExpansionOptionsResponse(
    IReadOnlyList<ExpansionOption> Options, long? HypotheticalRebuildCapacityBytes);
