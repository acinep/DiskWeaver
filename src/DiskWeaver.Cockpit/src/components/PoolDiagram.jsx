import React, { useState } from "react";
import { Label, Tooltip } from "@patternfly/react-core";
import { ExclamationTriangleIcon } from "@patternfly/react-icons";
import { formatBytes, RAID_LEVEL_LABELS, REDUNDANCY_LEVEL_LABELS, tierRedundancyLevel } from "../format.js";

// Fixed slot order, one categorical color per tier (identity, not redundancy level -- two tiers
// at the same DWR level would otherwise render identically and be indistinguishable). See
// overrides.css for the validated hue values and their dark-mode steps.
const TIER_FILLS = ["var(--dw-tier-1)", "var(--dw-tier-2)", "var(--dw-tier-3)", "var(--dw-tier-4)",
    "var(--dw-tier-5)", "var(--dw-tier-6)", "var(--dw-tier-7)", "var(--dw-tier-8)"];
const MAX_BAR_HEIGHT_PX = 220;
const FREE_HATCH = "repeating-linear-gradient(45deg, var(--dw-diagram-free) 0, var(--dw-diagram-free) 1px, transparent 1px, transparent 6px)";

// Renders each disk a tier touches as a column of stacked segments -- one per tier the disk
// belongs to, stacked bottom-up smallest-segment-first (tier0/the shared base tier first), so
// same-tier segments line up at the same height across disks the way typical hybrid-RAID
// diagrams show a shared RAID group spanning an equal slice of every member disk. Sorted by
// segmentSizeBytes rather than trusting `tiers`' own order -- GET /pools' array is creation
// order, which after an incremental grow can put a later, *smaller*-segment tier after an
// earlier, larger one for disks they share (see ExistingTier.cs's PartitionPaths doc comment),
// which would otherwise misalign every disk's bands above that point. Works for both an
// already-built pool's tiers (GET /pools) and a not-yet-built plan's tiers (POST /plan, POST
// /pools/{poolName}/expand) -- see tierRedundancyLevel's doc comment for how it normalizes the
// two shapes.
//
// Tier detail lives in a persistent panel to the right rather than a hover-only tooltip, so every
// row is comparable at once. The panel is ordered top-to-bottom to match the diagram's visual
// stacking (largest/topmost tier first, the shared base tier last), not `orderedTiers`' own
// bottom-to-top order, and each row's flex-grow is weighted by segment size so a row's share of
// the panel's height roughly tracks its band's share of a disk's height. Hovering a segment or
// its panel row highlights the other (matched by tier object identity -- `orderedTiers` is a sort
// of the same tier references `tiers` holds, not a clone, so `===` is safe).
export function PoolDiagram({ tiers, disks }) {
    const [hoveredTier, setHoveredTier] = useState(null);
    const sizeById = new Map(disks.map(disk => [disk.id, disk.sizeBytes]));
    const orderedTiers = [...tiers].sort((a, b) => a.segmentSizeBytes - b.segmentSizeBytes);
    const diskIds = [...new Set(orderedTiers.flatMap(tier => tier.diskIds))].sort();
    const fillByTier = new Map(orderedTiers.map((tier, i) => [tier, TIER_FILLS[i % TIER_FILLS.length]]));

    if (diskIds.length === 0) {
        return <p>No disks to visualize.</p>;
    }

    const maxBytes = Math.max(1, ...diskIds.map(id => sizeById.get(id) ?? 0));
    const pxPerByte = MAX_BAR_HEIGHT_PX / maxBytes;

    return (
        <div style={{ display: "flex", gap: "24px", alignItems: "flex-start" }}>
            <div style={{ display: "flex", alignItems: "flex-end", gap: "24px", overflowX: "auto", padding: "8px 4px", flex: "1 1 auto", minWidth: 0 }}>
                {diskIds.map(diskId => {
                    const diskBytes = sizeById.get(diskId);
                    let usedBytes = 0;
                    const segments = orderedTiers.filter(tier => tier.diskIds.includes(diskId));
                    for (const tier of segments) usedBytes += tier.segmentSizeBytes;
                    const freeBytes = diskBytes != null ? Math.max(0, diskBytes - usedBytes) : 0;
                    const barBytes = diskBytes ?? usedBytes;

                    return (
                        <div key={diskId} style={{ display: "flex", flexDirection: "column", alignItems: "center", flex: "0 0 auto" }}>
                            <div style={{ fontSize: "12px", color: "var(--pf-t--global--text--color--subtle)", marginBottom: "6px" }}>
                                {diskId}
                            </div>
                            <div
                                style={{
                                    boxSizing: "border-box",
                                    width: "56px",
                                    height: `${Math.max(4, barBytes * pxPerByte)}px`,
                                    display: "flex",
                                    flexDirection: "column-reverse",
                                    borderRadius: "6px 6px 3px 3px",
                                    overflow: "hidden",
                                    border: "1px solid var(--pf-t--global--border--color--default)",
                                }}
                            >
                                {segments.map((tier, i) => {
                                    const isHovered = hoveredTier === tier;
                                    return (
                                        <div
                                            key={i}
                                            onMouseEnter={() => setHoveredTier(tier)}
                                            onMouseLeave={() => setHoveredTier(null)}
                                            style={{
                                                boxSizing: "border-box",
                                                height: `${Math.max(2, tier.segmentSizeBytes * pxPerByte)}px`,
                                                background: fillByTier.get(tier),
                                                filter: isHovered ? "brightness(1.15)" : undefined,
                                                boxShadow: isHovered ? "inset 0 0 0 2px var(--pf-t--global--text--color--regular)" : undefined,
                                                borderBottom: i < segments.length - 1 || freeBytes > 0
                                                    ? "2px solid var(--pf-t--global--background--color--primary--default)"
                                                    : "none",
                                            }}
                                        />
                                    );
                                })}
                                {freeBytes > 0 && (
                                    <div style={{ boxSizing: "border-box", height: `${Math.max(2, freeBytes * pxPerByte)}px`, backgroundImage: FREE_HATCH }} />
                                )}
                            </div>
                            <div style={{ fontSize: "11px", color: "var(--pf-t--global--text--color--subtle)", marginTop: "6px" }}>
                                {diskBytes != null ? formatBytes(diskBytes) : "unknown size"}
                            </div>
                        </div>
                    );
                })}
            </div>

            <div style={{ flex: "0 0 260px", display: "flex", flexDirection: "column", height: `${MAX_BAR_HEIGHT_PX}px` }}>
                <div style={{ flex: "1 1 auto", display: "flex", flexDirection: "column", gap: "6px", minHeight: 0 }}>
                    {[...orderedTiers].reverse().map((tier, i) => (
                        <TierRow
                            key={i}
                            tier={tier}
                            fill={fillByTier.get(tier)}
                            isHovered={hoveredTier === tier}
                            onMouseEnter={() => setHoveredTier(tier)}
                            onMouseLeave={() => setHoveredTier(null)}
                        />
                    ))}
                </div>
                <div style={{ display: "flex", alignItems: "center", gap: "7px", marginTop: "8px", paddingTop: "10px", borderTop: "1px solid var(--pf-t--global--border--color--default)" }}>
                    <div style={{ width: "12px", height: "12px", borderRadius: "3px", border: "1px solid var(--pf-t--global--border--color--default)", backgroundImage: FREE_HATCH }} />
                    <span style={{ fontSize: "12.5px", color: "var(--pf-t--global--text--color--subtle)" }}>Free / unpartitioned</span>
                </div>
            </div>
        </div>
    );
}

function TierRow({ tier, fill, isHovered, onMouseEnter, onMouseLeave }) {
    const level = tierRedundancyLevel(tier);
    const isUnprotected = level === 0;
    return (
        <div
            onMouseEnter={onMouseEnter}
            onMouseLeave={onMouseLeave}
            style={{
                display: "flex",
                gap: "8px",
                padding: "6px 8px",
                borderRadius: "6px",
                background: isHovered ? "var(--pf-t--global--background--color--secondary--default)" : "transparent",
                border: "1px solid var(--pf-t--global--border--color--default)",
                fontSize: "12.5px",
                lineHeight: 1.5,
                // Weighted by segment size, same basis as the diagram bands, so a row's share of
                // the panel roughly tracks its band's share of a disk -- not pixel-exact (a
                // minHeight guards tiny segments from collapsing below readable text), but the
                // ordering and rough proportions read the same as the diagram.
                flex: `${tier.segmentSizeBytes} 1 0px`,
                minHeight: "44px",
            }}
        >
            <div style={{ width: "12px", height: "12px", borderRadius: "3px", marginTop: "2px", flex: "0 0 auto", background: fill }} />
            <div>
                <div>
                    <strong>{RAID_LEVEL_LABELS[tier.raidLevel] ?? tier.raidLevel}</strong>
                    {" "}&middot; {REDUNDANCY_LEVEL_LABELS[level] ?? "Mixed"}
                    {isUnprotected && (
                        <Tooltip content="Single disk, no redundancy -- add a matching disk and Expand to build a mirror">
                            <Label color="orange" icon={<ExclamationTriangleIcon />} isCompact style={{ marginLeft: "8px" }}>
                                unprotected
                            </Label>
                        </Tooltip>
                    )}
                </div>
                <div style={{ color: "var(--pf-t--global--text--color--subtle)" }}>
                    {tier.diskIds.length} disks &middot; {formatBytes(tier.segmentSizeBytes)} segment &rarr; {formatBytes(tier.usableBytes)} usable
                </div>
            </div>
        </div>
    );
}
