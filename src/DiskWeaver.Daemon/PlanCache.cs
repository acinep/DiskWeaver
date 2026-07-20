using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DiskWeaver.Planner;

namespace DiskWeaver.Daemon;

/// <summary>
/// Caches computed <see cref="PoolPlan"/>s keyed by a content hash of their inputs (the exact
/// selected disks + redundancy level), so <c>GET /plan/{id}/script</c> can reference the plan
/// that was actually computed, and re-planning identical inputs is idempotent (same id back).
/// Also retains the exact <see cref="Disk"/>s the plan was computed against (see
/// <see cref="TryGetSelectedDisks"/>), so <c>POST /plan/{id}/execute</c> can re-check live
/// inventory immediately before running anything, rather than trusting a plan that could be
/// arbitrarily stale by the time it's executed. See docs/daemon-api.md.
/// </summary>
public sealed class PlanCache
{
    private readonly ConcurrentDictionary<string, CachedPlan> _plans = new();

    public string Store(
        IReadOnlyList<Disk> selectedDisks, RedundancyLevel redundancy, string poolName, PoolPlan plan,
        bool thinProvisioned = false)
    {
        var id = ComputeId(selectedDisks, redundancy, poolName, thinProvisioned);
        _plans[id] = new CachedPlan(plan, selectedDisks, poolName, thinProvisioned);
        return id;
    }

    public bool TryGet(string id, out PoolPlan? plan)
    {
        var found = _plans.TryGetValue(id, out var cached);
        plan = cached?.Plan;
        return found;
    }

    /// <summary>
    /// The exact disks (id + size + blank-ness) this plan was computed against -- so a caller about
    /// to execute the plan can re-fetch live inventory and confirm nothing changed since <c>POST
    /// /plan</c> before running a single destructive command. See Program.cs's <c>/plan/{id}/execute</c>.
    /// </summary>
    public bool TryGetSelectedDisks(string id, out IReadOnlyList<Disk>? selectedDisks)
    {
        var found = _plans.TryGetValue(id, out var cached);
        selectedDisks = cached?.SelectedDisks;
        return found;
    }

    /// <summary>
    /// The pool name this plan was computed for -- so <c>/plan/{id}/script</c> and
    /// <c>/plan/{id}/execute</c> pass the same name to <see cref="CommandPlanner.Build"/>/
    /// <see cref="CommandPlanner.BuildTeardown"/> that <c>POST /plan</c> validated and used,
    /// instead of silently falling back to the default and building/tearing down the wrong VG.
    /// </summary>
    public bool TryGetPoolName(string id, out string? poolName)
    {
        var found = _plans.TryGetValue(id, out var cached);
        poolName = cached?.PoolName;
        return found;
    }

    /// <summary>
    /// Whether <c>POST /plan</c> requested a thin-provisioned pool (see
    /// <see cref="Executor.CommandPlanner.Build"/>'s <c>thinProvisioned</c> parameter) -- so
    /// <c>/plan/{id}/script</c> and <c>/plan/{id}/execute</c> build/tear down the same layout
    /// <c>POST /plan</c> actually described, instead of silently falling back to the thick-LV
    /// default.
    /// </summary>
    public bool TryGetThinProvisioned(string id, out bool thinProvisioned)
    {
        var found = _plans.TryGetValue(id, out var cached);
        thinProvisioned = cached?.ThinProvisioned ?? false;
        return found;
    }

    private sealed record CachedPlan(PoolPlan Plan, IReadOnlyList<Disk> SelectedDisks, string PoolName, bool ThinProvisioned);

    private static string ComputeId(IReadOnlyList<Disk> disks, RedundancyLevel redundancy, string poolName, bool thinProvisioned)
    {
        var canonical = string.Join(
            ";",
            disks.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => $"{d.Id}:{d.SizeBytes}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{canonical};R={redundancy};P={poolName};T={thinProvisioned}"));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
