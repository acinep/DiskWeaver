import React, { useState } from "react";
import { Button, Tooltip, Label } from "@patternfly/react-core";
import { ExclamationTriangleIcon, ProjectDiagramIcon } from "@patternfly/react-icons";
import { Table, Thead, Tbody, Tr, Th, Td } from "@patternfly/react-table";
import { formatBytes, RAID_LEVEL_LABELS } from "../../format.js";
import { apiRequest } from "../../api.js";

// onVisualize swaps in a PoolDiagram as an alternate body within the wizard's own single Modal
// (see CreateExpandWizard's showDiagram) rather than this step opening a second nested <Modal> --
// PatternFly's focus trap only expects one active Modal at a time (see confirmingExecute's own
// comment in CreateExpandWizard for the same reasoning).
export function ReviewPlanStep({
    plan, achievedCapacityBytes, hypotheticalRebuildCapacityBytes, expansionPoolName, poolName, planId,
    thinProvisioned, assumeClean, chunkSizeKb, raid5ConsistencyPolicy, onVisualize,
}) {
    const [scriptText, setScriptText] = useState(null);
    const [scriptError, setScriptError] = useState(null);

    // For a fresh pool, "achieved" and "desired" are the same thing. For an
    // expansion, they can differ: growing an existing tier's member count
    // assumes a manual grow already happened -- see docs/cockpit-plugin.md
    // bug #5 for why this distinction matters and must stay visible.
    const achieved = achievedCapacityBytes ?? plan.poolCapacityBytes;
    const growNote = achievedCapacityBytes !== undefined && achievedCapacityBytes !== plan.poolCapacityBytes
        ? ` (reaching the full ${formatBytes(plan.poolCapacityBytes)} shown below requires manually growing an existing tier first -- see the script for exactly which one)`
        : "";

    // Independent tiers each separately pay a mirror's redundancy overhead, where a from-scratch
    // rebuild across every disk (existing + new) would amortize it across a much wider shared
    // array -- often a meaningfully higher number even for the exact same disks. Only worth
    // surfacing when it actually beats this plan by an amount the user can see: a gap of a few MB
    // out of several GB is real (raw bytes do differ) but rounds to the identical displayed value
    // at formatBytes's 2-decimal precision, which reads as "rebuilding gets you the same number" --
    // confusingly self-defeating. Comparing the formatted strings themselves is the simplest way to
    // guarantee the note is only ever shown alongside two genuinely different numbers.
    const hypotheticalRebuildText = hypotheticalRebuildCapacityBytes != null
        ? formatBytes(hypotheticalRebuildCapacityBytes)
        : null;
    const rebuildIsBetter = hypotheticalRebuildCapacityBytes !== undefined
        && hypotheticalRebuildCapacityBytes !== null
        && hypotheticalRebuildCapacityBytes > achieved
        && hypotheticalRebuildText !== formatBytes(achieved);

    function viewScript(kind) {
        setScriptError(null);
        const path = expansionPoolName
            ? `/pools/${encodeURIComponent(expansionPoolName)}/expand/${planId}/script`
            : `/plan/${planId}/script?kind=${encodeURIComponent(kind)}`;
        apiRequest("GET", path)
            .then(setScriptText)
            .catch(err => setScriptError(err.message || String(err)));
    }

    return (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--pf-t--global--spacer--md)" }}>
            <h3>{expansionPoolName ? `Plan: expand "${expansionPoolName}"` : `Plan: new pool "${poolName}"`}</h3>
            <div>
                <p><strong>Usable capacity once Execute completes:</strong> {formatBytes(achieved)}{growNote}</p>
                <p>
                    <strong>Full end-state usable capacity:</strong> {formatBytes(plan.poolCapacityBytes)}
                    {plan.reservedBytes > 0 && ` (${formatBytes(plan.reservedBytes)} unallocated until a matching disk is added)`}
                </p>
                {thinProvisioned && (
                    <p>Thin-provisioned: a thin pool with 10% headroom, plus one "data" volume using its full capacity.</p>
                )}
                {assumeClean && (
                    <p>Skipping initial resync: each tier's array is created with --assume-clean.</p>
                )}
                {chunkSizeKb !== undefined && plan.tiers.some(t => t.raidLevel !== 0) && (
                    <p>Striped (RAID5/RAID6) tiers use a {chunkSizeKb} KiB chunk size.</p>
                )}
                {raid5ConsistencyPolicy !== undefined && plan.tiers.some(t => t.raidLevel === 1) && (
                    <p>
                        RAID5 tier(s) use{" "}
                        {raid5ConsistencyPolicy === "resync" && "plain resync (fastest writes, but a full array resync and an open write hole after any unclean shutdown)"}
                        {raid5ConsistencyPolicy === "bitmap" && "an internal bitmap (fast recovery after an unclean shutdown, small write overhead)"}
                        {raid5ConsistencyPolicy === "ppl" && "PPL (closes the write hole entirely, at a significant cost to sustained write throughput)"}
                        {" "}for write-hole protection.
                    </p>
                )}
                {rebuildIsBetter && (
                    <p>
                        Tearing down and rebuilding this pool from scratch with all its disks (existing
                        + new) could reach <strong>{hypotheticalRebuildText}</strong> usable
                        instead -- growing keeps your existing data intact but doesn't share redundancy
                        overhead across independent tiers the way a full rebuild would.
                    </p>
                )}
            </div>
            <div className="table-scroll">
                <Table variant="compact">
                    <Thead>
                        <Tr>
                            <Th>RAID level</Th>
                            <Th>Segment size</Th>
                            <Th>Disks</Th>
                            <Th>Usable</Th>
                        </Tr>
                    </Thead>
                    <Tbody>
                        {plan.tiers.map((tier, i) => (
                            <Tr key={i}>
                                <Td dataLabel="RAID level">
                                    {RAID_LEVEL_LABELS[tier.raidLevel] ?? tier.raidLevel}
                                    {tier.degradedSlots > 0 && (
                                        <Tooltip content="Single disk, no redundancy -- add a matching disk and Expand later to build a mirror">
                                            <Label color="orange" icon={<ExclamationTriangleIcon />} isCompact style={{ marginLeft: "8px" }}>
                                                unprotected
                                            </Label>
                                        </Tooltip>
                                    )}
                                </Td>
                                <Td dataLabel="Segment size">{formatBytes(tier.segmentSizeBytes)}</Td>
                                <Td dataLabel="Disks">{tier.diskIds.join(", ")}</Td>
                                <Td dataLabel="Usable">{formatBytes(tier.usableBytes)}</Td>
                            </Tr>
                        ))}
                    </Tbody>
                </Table>
            </div>

            <div>
                <Button variant="link" isInline icon={<ProjectDiagramIcon />} onClick={onVisualize}>
                    Visualize
                </Button>{" "}
                {expansionPoolName ? (
                    <Button variant="link" isInline onClick={() => viewScript("build")}>View script</Button>
                ) : (
                    <Button variant="link" isInline onClick={() => viewScript("build")}>View build script</Button>
                )}
            </div>

            {scriptError && <p style={{ color: "var(--pf-t--global--color--status--danger--default)" }}>{scriptError}</p>}
            {scriptText && <pre>{scriptText}</pre>}
        </div>
    );
}
