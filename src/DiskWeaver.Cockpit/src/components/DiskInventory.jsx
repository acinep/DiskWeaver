import React from "react";
import { Button, Spinner, Card, CardTitle, CardBody, CardFooter } from "@patternfly/react-core";
import { Table, Thead, Tbody, Tr, Th, Td } from "@patternfly/react-table";
import { formatBytes } from "../format.js";
import { WipeButton } from "./WipeButton.jsx";

// Read-only for now (Phase 2) -- selection checkboxes for the create/expand
// wizard land in Phase 3 as wizard/DiskPickerStep.jsx, reusing poolNameByDiskId.
export function poolNameByDiskId(pools) {
    const map = new Map();
    for (const pool of pools) {
        for (const tier of pool.tiers) {
            for (const diskId of tier.diskIds) {
                map.set(diskId, pool.poolName);
            }
        }
    }
    return map;
}

// A disk already in a pool is expected to be non-blank (it's a live mdadm/LVM member by design) --
// "not blank" is only worth flagging for a disk that ISN'T already accounted for by a pool, since
// that's the case `plan`/the create-pool wizard would otherwise refuse with no earlier warning.
export function diskStatus(disk, busyByDiskId) {
    if (busyByDiskId.has(disk.id)) {
        return `already in ${busyByDiskId.get(disk.id)}`;
    }
    if (disk.isLikelySystemDisk) {
        return "likely system/boot disk -- do not use";
    }
    return disk.isBlank ? "" : "not blank -- wipe before use";
}

// Wipe is only ever offered for a disk that's both not already claimed by a pool (that'd be a live
// mdadm/LVM member, not a mistake) and not flagged isLikelySystemDisk -- isLikelySystemDisk is only
// a best-effort mount-point heuristic (see Disk.IsLikelySystemDisk's doc comment), so this is a
// last line of defense, not a substitute for the user's own judgement about which disks are safe.
export function canWipe(disk, busyByDiskId) {
    return !busyByDiskId.has(disk.id) && !disk.isBlank && !disk.isLikelySystemDisk;
}

// Disks selectable right now for a brand-new tier (new pool, or a disk added to an existing one):
// not already claimed by a pool, and blank. Mirrors the daemon's own DiskSelector.EnsureBlank gate
// client-side, so Create pool/Expand can be disabled with an explanatory hint before a request that
// would just be refused server-side is ever made.
export function countAvailableDisks(disks, pools) {
    const busy = poolNameByDiskId(pools);
    return disks.filter(disk => disk.isBlank && !busy.has(disk.id)).length;
}

export function DiskInventory({ disks, pools, loading, onRefresh }) {
    const busy = poolNameByDiskId(pools);

    return (
        <Card>
            <CardTitle>Disk inventory</CardTitle>
            <CardBody>
                {loading && <Spinner size="md" />}
                {!loading && disks.length === 0 && <p>No disks found.</p>}
                {!loading && disks.length > 0 && (
                    <div className="table-scroll">
                        <Table variant="compact">
                            <Thead>
                                <Tr>
                                    <Th>Disk</Th>
                                    <Th>Size</Th>
                                    <Th>Status</Th>
                                    <Th></Th>
                                </Tr>
                            </Thead>
                            <Tbody>
                                {disks.map(disk => (
                                    <Tr key={disk.id}>
                                        <Td dataLabel="Disk">{disk.id}</Td>
                                        <Td dataLabel="Size">{formatBytes(disk.sizeBytes)}</Td>
                                        <Td dataLabel="Status">{diskStatus(disk, busy)}</Td>
                                        <Td dataLabel="Actions">
                                            {canWipe(disk, busy) && <WipeButton diskId={disk.id} onDone={onRefresh} />}
                                        </Td>
                                    </Tr>
                                ))}
                            </Tbody>
                        </Table>
                    </div>
                )}
            </CardBody>
            <CardFooter>
                <Button variant="secondary" onClick={onRefresh} isDisabled={loading}>Refresh disks</Button>
            </CardFooter>
        </Card>
    );
}
