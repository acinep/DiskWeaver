// Enum fields (raidLevel, step/journal status) come back from System.Text.Json's default enum
// converter as integers, not strings. These maps match the C# enum declarations' *declaration
// order* -- if a new enum value is ever inserted rather than appended on the C# side, these maps
// would silently go stale, since nothing enforces the mapping stays in sync.
export const RAID_LEVEL_LABELS = { 0: "Mirror", 1: "RAID5", 2: "RAID6" };
export const STEP_STATUS_LABELS = { 0: "pending", 1: "succeeded", 2: "failed" };
export const JOURNAL_STATUS_LABELS = { 0: "running", 1: "succeeded", 2: "failed" };
export const REDUNDANCY_LEVEL_LABELS = { 0: "None", 1: "DWR1", 2: "DWR2" };

export function formatBytes(bytes) {
    const tb = 1_000_000_000_000;
    const gb = 1_000_000_000;
    return bytes >= tb ? `${(bytes / tb).toFixed(2)} TB` : `${(bytes / gb).toFixed(2)} GB`;
}

// Mirrors DiskWeaver.Executor.TierRedundancy.InferTier/Infer (C#) so the UI can show a redundancy
// level without a dedicated API field -- a tier's raidLevel plus (for mirrors only, where both
// DWR1 and DWR2 use the same RaidLevel.Mirror) its configured member count is enough to tell them
// apart, same as the server-side inference. Works for both an already-built tier (ExistingTier's
// isUnprotectedByDesign/configuredMemberCountOrDefault, from GET /pools) and a not-yet-built plan
// tier (Tier's degradedSlots is the pre-build equivalent of isUnprotectedByDesign -- a
// RedundancyLevel.None tier is the only one ever built degraded).
export function tierRedundancyLevel(tier) {
    if (tier.raidLevel === 1) return 1; // RAID5 -> DWR1
    if (tier.raidLevel === 2) return 2; // RAID6 -> DWR2
    if (tier.isUnprotectedByDesign === true || (tier.degradedSlots ?? 0) > 0) return 0;
    return (tier.configuredMemberCountOrDefault ?? tier.diskIds.length) - 1; // Mirror -> DWR1 or DWR2
}

export function poolType(pool) {
    const levels = [...new Set(pool.tiers.map(tierRedundancyLevel))];
    if (levels.length !== 1) return "Mixed";
    return REDUNDANCY_LEVEL_LABELS[levels[0]] ?? "—";
}
