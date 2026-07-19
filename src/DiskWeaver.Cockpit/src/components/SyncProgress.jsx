import React from "react";
import { Progress, ProgressSize } from "@patternfly/react-core";
import { SYNC_OPERATION_LABELS, formatSyncSpeed, formatSyncEta } from "../format.js";

// A tier's mdadm recovery/resync/reshape/check progress (ExistingTier.syncOperation et al, sourced
// from /proc/mdstat) -- shared between PoolsTable's main row (so a multi-hour reshape doesn't look
// stuck without opening anything) and PoolDiagram's per-tier detail panel.
export function SyncProgress({ tier }) {
    if (tier.syncOperation == null) {
        return null;
    }

    return (
        <div>
            <Progress
                value={tier.syncPercentComplete ?? 0}
                title={SYNC_OPERATION_LABELS[tier.syncOperation] ?? tier.syncOperation}
                size={ProgressSize.sm}
                label={`${(tier.syncPercentComplete ?? 0).toFixed(1)}%`}
            />
            {(tier.syncSpeedKBps != null || tier.syncEtaMinutes != null) && (
                <div style={{ fontSize: "11px", color: "var(--pf-t--global--text--color--subtle)", marginTop: "2px" }}>
                    {formatSyncSpeed(tier.syncSpeedKBps)}
                    {tier.syncSpeedKBps != null && tier.syncEtaMinutes != null && " · "}
                    {formatSyncEta(tier.syncEtaMinutes) != null && `${formatSyncEta(tier.syncEtaMinutes)} remaining`}
                </div>
            )}
        </div>
    );
}
