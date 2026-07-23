namespace DiskWeaver.Executor;

/// <summary>
/// How a RAID5 tier protects itself against the "write hole" (a stripe torn by power loss leaving
/// data and parity silently inconsistent) -- a straight trade-off between write performance and
/// how well that hole is closed. Only meaningful for RAID5: Mirror has no parity to protect, and
/// RAID6 has no kernel support for either of these (its equivalent write-hole protection needs a
/// dedicated journal device, a real, separate gap DiskWeaver's planner doesn't cover today), so it
/// always keeps the plain internal bitmap regardless of this setting -- see
/// <see cref="CommandPlanner"/>'s mdadm --create args for where that's decided.
/// </summary>
public enum Raid5ConsistencyPolicy
{
    /// <summary>No bitmap, no ppl -- the fastest steady-state writes (zero extra per-write I/O),
    /// but an unclean shutdown forces a full-array resync (every stripe re-checked, potentially
    /// hours on a large array) and the write hole stays open the entire time it's running: a stripe
    /// torn mid-write by power loss can leave data and parity silently inconsistent until that
    /// resync reaches it. Reasonable only when the array has solid power protection (so unclean
    /// shutdowns are rare) and a long resync after one is genuinely acceptable.</summary>
    Resync,

    /// <summary>An internal write-intent bitmap: after an unclean shutdown, only the regions the
    /// bitmap marked dirty need resyncing (seconds to minutes instead of hours), at the cost of a
    /// small steady-state write overhead -- far less than Ppl's. Recommended default. It does not
    /// itself close the write hole (a stripe torn mid-write can still leave parity inconsistent
    /// until resynced), but that exposure already exists every day the array runs; this only
    /// changes how expensive recovering from an unclean shutdown is, not whether one is
    /// dangerous.</summary>
    Bitmap,

    /// <summary>Partial Parity Log: logs each stripe's pre-write parity so the write hole is
    /// genuinely closed (no silent data/parity inconsistency from an unclean shutdown, and no
    /// resync needed afterward). The cost is real and can be severe -- every write now also costs a
    /// journal write to the same disks, which has been measured to badly cut sustained random-write
    /// throughput on RAID5. Reserve this for arrays where a torn stripe is unacceptable regardless
    /// of throughput cost; most workloads are better served by Bitmap.</summary>
    Ppl,
}
