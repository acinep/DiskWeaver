import React, { useState } from "react";
import { Button, Spinner, Card, CardTitle, CardBody, CardFooter, Flex, FlexItem, Tooltip, Label } from "@patternfly/react-core";
import { PlusIcon, ExclamationTriangleIcon, InfoCircleIcon, ProjectDiagramIcon } from "@patternfly/react-icons";
import { Table, Thead, Tbody, Tr, Th, Td } from "@patternfly/react-table";
import { formatBytes, RAID_LEVEL_LABELS, poolType } from "../format.js";
import { TeardownButton } from "./TeardownButton.jsx";
import { ProtectTierButton } from "./ProtectTierButton.jsx";
import { PoolDiagramModal } from "./PoolDiagramModal.jsx";

function tiersSummary(pool, disks, pools, onProtectDone) {
    return pool.tiers.map((tier, i) => {
        const configuredMemberCount = tier.configuredMemberCountOrDefault ?? tier.diskIds.length;
        // Built-as-unprotected and really-degraded are the same on-disk shape (both configured for
        // more members than are actually present) -- only the daemon's PV tag lookup can tell them
        // apart, so trust tier.isUnprotectedByDesign from the API rather than inferring it here.
        const isUnprotectedByDesign = tier.isUnprotectedByDesign === true;
        const isDegraded = tier.diskIds.length < configuredMemberCount && !isUnprotectedByDesign;
        const countLabel = isDegraded ? `${tier.diskIds.length} of ${configuredMemberCount}` : `${tier.diskIds.length}`;
        return (
            <div key={i}>
                {tier.arrayDevice}: {RAID_LEVEL_LABELS[tier.raidLevel] ?? tier.raidLevel}{" "}
                ({countLabel}x{formatBytes(tier.segmentSizeBytes)}) &rarr; {formatBytes(tier.usableBytes)} usable
                {isDegraded && (
                    <Tooltip content="A disk failed out of this mirror -- replace it and Expand to resync">
                        <Label color="orange" icon={<ExclamationTriangleIcon />} isCompact style={{ marginLeft: "8px" }}>
                            degraded
                        </Label>
                    </Tooltip>
                )}
                {isUnprotectedByDesign && (
                    <Tooltip content="Single disk, no redundancy -- add a matching disk and Expand to build a mirror">
                        <Label color="blue" icon={<InfoCircleIcon />} isCompact style={{ marginLeft: "8px" }}>
                            unprotected
                        </Label>
                    </Tooltip>
                )}
                {(isDegraded || isUnprotectedByDesign) && (
                    <div style={{ marginTop: "4px" }}>
                        <ProtectTierButton
                            poolName={pool.poolName}
                            arrayDevice={tier.arrayDevice}
                            disks={disks}
                            pools={pools}
                            onDone={onProtectDone}
                        />
                    </div>
                )}
            </div>
        );
    });
}

function poolDisks(pool) {
    return [...new Set(pool.tiers.flatMap(tier => tier.diskIds))].join(", ");
}

function totalUsableBytes(pool) {
    return pool.tiers.reduce((sum, tier) => sum + tier.usableBytes, 0);
}

// Wraps a Button that may be disabled with an explanatory Tooltip. isAriaDisabled (rather than
// isDisabled) keeps the button focusable/hoverable while still blocking the click, which is what
// lets the Tooltip actually appear -- a natively `disabled` element fires no pointer events at all.
// iconOnly drops the visible label text (for dense per-row actions) in favor of aria-label plus a
// Tooltip carrying the same text -- disabledReason, when present, takes over that same Tooltip
// rather than stacking two.
function ActionButton({ variant, icon, onClick, disabledReason, label, iconOnly, children }) {
    const button = (
        <Button
            variant={variant}
            icon={icon}
            onClick={onClick}
            isAriaDisabled={!!disabledReason}
            aria-label={iconOnly ? label : undefined}
        >
            {iconOnly ? null : children}
        </Button>
    );
    const tooltipContent = disabledReason ?? (iconOnly ? label : null);
    return tooltipContent ? <Tooltip content={tooltipContent}>{button}</Tooltip> : button;
}

export function PoolsTable({ pools, disks, loading, onRefresh, onDataChanged, onExpand, onCreate, expandDisabledReason, createDisabledReason }) {
    // Teardown/protect change which disks are blank/in-use, so they need both pools AND disk
    // inventory refreshed -- unlike the manual "Refresh" button (onRefresh), which is deliberately
    // pools-only (see docs/cockpit-plugin.md bug #6).
    const onTeardownDone = onDataChanged;
    const onProtectDone = onDataChanged;
    const [diagramPool, setDiagramPool] = useState(null);
    return (
        <Card>
            <CardTitle>Existing pools</CardTitle>
            <CardBody>
                {loading && <Spinner size="md" />}
                {!loading && pools.length === 0 && <p>No pools found on this host.</p>}
                {!loading && pools.length > 0 && (
                    <div className="table-scroll">
                        <Table variant="compact">
                            <Thead>
                                <Tr>
                                    <Th>Pool</Th>
                                    <Th>Type</Th>
                                    <Th>Volume</Th>
                                    <Th>Size</Th>
                                    <Th>Tiers</Th>
                                    <Th>Disks</Th>
                                    <Th></Th>
                                </Tr>
                            </Thead>
                            <Tbody>
                                {pools.map(pool => (
                                    <Tr key={pool.poolName}>
                                        <Td dataLabel="Pool">{pool.poolName}</Td>
                                        <Td dataLabel="Type">{poolType(pool)}</Td>
                                        <Td dataLabel="Volume">{pool.volumeName}</Td>
                                        <Td dataLabel="Size">{formatBytes(totalUsableBytes(pool))}</Td>
                                        <Td dataLabel="Tiers">{tiersSummary(pool, disks, pools, onProtectDone)}</Td>
                                        <Td dataLabel="Disks">{poolDisks(pool)}</Td>
                                        <Td dataLabel="Actions">
                                            <ActionButton
                                                variant="secondary"
                                                icon={<ProjectDiagramIcon />}
                                                onClick={() => setDiagramPool(pool)}
                                                label="Visualize"
                                                iconOnly
                                            />{" "}
                                            <ActionButton
                                                variant="secondary"
                                                icon={<PlusIcon />}
                                                onClick={() => onExpand(pool.poolName)}
                                                disabledReason={expandDisabledReason}
                                                label="Expand"
                                                iconOnly
                                            />{" "}
                                            <TeardownButton poolName={pool.poolName} onDone={onTeardownDone} />
                                        </Td>
                                    </Tr>
                                ))}
                            </Tbody>
                        </Table>
                    </div>
                )}
            </CardBody>
            <CardFooter>
                <Flex justifyContent={{ default: "justifyContentSpaceBetween" }}>
                    <FlexItem>
                        <Button variant="secondary" onClick={onRefresh} isDisabled={loading}>Refresh</Button>
                    </FlexItem>
                    <FlexItem>
                        <ActionButton variant="primary" onClick={onCreate} disabledReason={createDisabledReason}>
                            Create pool
                        </ActionButton>
                    </FlexItem>
                </Flex>
            </CardFooter>

            <PoolDiagramModal
                title={diagramPool ? `Visualize: ${diagramPool.poolName}` : ""}
                tiers={diagramPool?.tiers ?? []}
                disks={disks}
                isOpen={!!diagramPool}
                onClose={() => setDiagramPool(null)}
            />
        </Card>
    );
}
