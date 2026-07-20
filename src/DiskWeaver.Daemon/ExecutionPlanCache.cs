using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DiskWeaver.Executor;

namespace DiskWeaver.Daemon;

/// <summary>
/// Caches <see cref="ExecutionPlan"/>s computed for pool expansion (<c>BuildIncremental</c>
/// output, which is already a final execution plan, not a <see cref="Planner.PoolPlan"/> that
/// still needs <see cref="CommandPlanner.Build"/>) -- same content-addressed-id idea as
/// <see cref="PlanCache"/>, kept separate since it caches a different shape of object.
///
/// Also retains a fingerprint of the pool state the plan was computed against (see
/// <see cref="ComputeFingerprint"/>) and the disk ids that were newly added, so <c>POST
/// /pools/{poolName}/expand/{id}/execute</c> can re-fetch live pool/disk state and refuse to run
/// a plan whose assumptions no longer hold -- e.g. the pool was torn down, grown, or reshaped by
/// another operation between preview and execute.
///
/// The id itself includes that fingerprint (not just poolName + addedDiskIds): without it, two
/// callers previewing the same pool/disk-ids tuple at different times -- one before some other
/// change to the pool, one after -- would collide on the same id, and the second `Store` would
/// silently overwrite the first caller's still-outstanding plan out from under it, pointing their
/// already-shown execute URL at a plan they never actually reviewed. Folding the fingerprint into
/// the id instead means a changed pool naturally produces a different id, so a stale plan is
/// orphaned rather than overwritten -- the same content-addressed guarantee <see cref="PlanCache"/>
/// already gives `POST /plan`.
/// </summary>
public sealed class ExecutionPlanCache
{
    private readonly ConcurrentDictionary<string, CachedExecution> _plans = new();

    /// <param name="intent">
    /// Discriminates between multiple candidate plans computed for the exact same
    /// pool/addedDiskIds/fingerprint tuple in one request -- see
    /// <see cref="Executor.ExpansionOptionsPlanner"/>, which can return a "protection" and a
    /// "space" candidate for the same expand request. Without this, both would hash to the same
    /// id, and the second <see cref="Store"/> call would silently overwrite the first candidate's
    /// still-outstanding plan -- confirmed live: both options in one preview response came back
    /// with the identical planId, so executing the id shown for "protection" would actually run
    /// whichever candidate was stored last ("space"). Omit for the single-candidate (manual) modes.
    /// </param>
    public string Store(string poolName, IReadOnlyList<string> addedDiskIds, ExecutionPlan plan, string poolStateFingerprint, string intent = "")
    {
        var id = ComputeId(poolName, addedDiskIds, poolStateFingerprint, intent);
        _plans[id] = new CachedExecution(plan, addedDiskIds, poolStateFingerprint);
        return id;
    }

    public bool TryGet(string id, out ExecutionPlan? plan)
    {
        var found = _plans.TryGetValue(id, out var cached);
        plan = cached?.Plan;
        return found;
    }

    public bool TryGetValidation(string id, out IReadOnlyList<string>? addedDiskIds, out string? poolStateFingerprint)
    {
        var found = _plans.TryGetValue(id, out var cached);
        addedDiskIds = cached?.AddedDiskIds;
        poolStateFingerprint = cached?.PoolStateFingerprint;
        return found;
    }

    /// <summary>
    /// A content hash of everything about <paramref name="pool"/> that <c>BuildIncremental</c>
    /// planned against (its tiers' array devices, RAID levels, segment sizes, and member disks) --
    /// two calls return the same value if and only if the pool hasn't changed in any way that
    /// would make a previously-computed <see cref="ExecutionPlan"/> stale.
    /// </summary>
    public static string ComputeFingerprint(ExistingPoolState pool)
    {
        var tiers = string.Join(
            ";",
            pool.Tiers
                .OrderBy(t => t.ArrayDevice, StringComparer.Ordinal)
                .Select(t => $"{t.ArrayDevice}:{t.RaidLevel}:{t.SegmentSizeBytes}:"
                    + string.Join(",", t.DiskIds.OrderBy(id => id, StringComparer.Ordinal))));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{pool.PoolName};{string.Join(",", pool.VolumeNames)};{tiers}"));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private sealed record CachedExecution(ExecutionPlan Plan, IReadOnlyList<string> AddedDiskIds, string PoolStateFingerprint);

    private static string ComputeId(string poolName, IReadOnlyList<string> addedDiskIds, string poolStateFingerprint, string intent)
    {
        var canonical = $"{poolName};{string.Join(";", addedDiskIds.OrderBy(id => id, StringComparer.Ordinal))};{poolStateFingerprint};{intent}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
