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
/// <param name="ThinProvisioned">
/// When true, the pool's volume group gets a thin pool (with headroom reserved, see
/// <see cref="Executor.CommandPlanner.ThinPoolHeadroomPercent"/>) plus one thin volume named
/// <c>data</c> sized to the thin pool's full capacity, instead of the default single thick LV --
/// see execution.md's "Multiple logical volumes (thin pools)" for what this does and doesn't cover.
/// </param>
/// <param name="AssumeClean">
/// When true, each tier's <c>mdadm --create</c> is run with <c>--assume-clean</c>, skipping the
/// initial full-array resync/parity-build -- safe here because every disk this builds on was just
/// verified blank, so there's no real data whose parity could be silently wrong to begin with.
/// Defaults to false (the safer, resync-on-create default) since this is opt-in.
/// </param>
/// <param name="ChunkSizeKb">
/// The <c>mdadm --create --chunk</c> size (KiB) for striped (RAID5/RAID6) tiers -- ignored for
/// Mirror tiers, which don't stripe. Must be one of <see cref="Executor.CommandPlanner.ValidChunkSizesKb"/>
/// (<c>400</c> otherwise). Defaults to <see cref="Executor.CommandPlanner.DefaultChunkSizeKb"/>.
/// </param>
public sealed record PlanRequest(
    string[] DiskIds,
    string Redundancy,
    string? PoolName = null,
    bool ThinProvisioned = false,
    bool AssumeClean = false,
    int ChunkSizeKb = Executor.CommandPlanner.DefaultChunkSizeKb);
