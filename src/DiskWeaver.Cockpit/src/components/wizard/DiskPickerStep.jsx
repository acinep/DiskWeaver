import React from "react";
import { Table, Thead, Tbody, Tr, Th, Td } from "@patternfly/react-table";
import { formatBytes } from "../../format.js";
import { poolNameByDiskId, diskStatus } from "../DiskInventory.jsx";

export function DiskPickerStep({ disks, pools, selectedDiskIds, onChange }) {
    const busy = poolNameByDiskId(pools);

    function toggle(diskId) {
        const next = new Set(selectedDiskIds);
        if (next.has(diskId)) {
            next.delete(diskId);
        } else {
            next.add(diskId);
        }
        onChange(next);
    }

    if (disks.length === 0) {
        return <p>No disks found.</p>;
    }

    return (
        <div className="table-scroll">
            <Table variant="compact">
                <Thead>
                    <Tr>
                        <Th screenReaderText="Select" />
                        <Th>Disk</Th>
                        <Th>Size</Th>
                        <Th></Th>
                    </Tr>
                </Thead>
                <Tbody>
                    {disks.map((disk, rowIndex) => {
                        const poolName = busy.get(disk.id);
                        // Not-blank disks not already accounted for by a pool would just fail
                        // server-side once the wizard tries to plan/build -- refuse the selection here
                        // instead, same as `plan`'s own DiskSelector.EnsureBlank gate.
                        const isSelectable = !poolName && disk.isBlank;
                        return (
                            <Tr key={disk.id}>
                                <Td
                                    select={{
                                        variant: "checkbox",
                                        rowIndex,
                                        isSelected: selectedDiskIds.has(disk.id),
                                        isDisabled: !isSelectable,
                                        onSelect: () => toggle(disk.id),
                                    }}
                                />
                                <Td dataLabel="Disk">{disk.id}</Td>
                                <Td dataLabel="Size">{formatBytes(disk.sizeBytes)}</Td>
                                <Td dataLabel="Status">{diskStatus(disk, busy)}</Td>
                            </Tr>
                        );
                    })}
                </Tbody>
            </Table>
        </div>
    );
}
