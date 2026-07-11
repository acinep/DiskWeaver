import React, { useState } from "react";
import { Button, Modal, ModalVariant, ModalHeader, ModalBody, ModalFooter, Spinner } from "@patternfly/react-core";
import { apiPostJson } from "../api.js";
import { JournalView } from "./JournalView.jsx";

// Clears a stale/foreign filesystem/RAID/LVM signature off one disk via POST /disks/wipe, so it
// passes DiskSelector.EnsureBlank and can be selected for a new pool afterward. Deliberately never
// offered for a disk DiskInventory.jsx flags isLikelySystemDisk -- see the caller.
export function WipeButton({ diskId, onDone }) {
    const [open, setOpen] = useState(false);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);
    const [journal, setJournal] = useState(null);

    function wipe() {
        setLoading(true);
        setError(null);
        apiPostJson("/disks/wipe", { diskIds: [diskId] })
            .then(setJournal)
            .catch(err => setError(err.message || String(err)))
            .finally(() => setLoading(false));
    }

    function close() {
        setOpen(false);
        setJournal(null);
        setError(null);
        onDone();
    }

    return (
        <>
            <Button variant="warning" onClick={() => setOpen(true)}>Wipe</Button>

            {open && (
                <Modal variant={ModalVariant.medium} isOpen onClose={close}>
                    <ModalHeader title={journal ? `Wipe journal: ${diskId}` : "Confirm wipe"} />
                    <ModalBody>
                        {error && <p style={{ color: "var(--pf-t--global--color--status--danger--default)" }}>{error}</p>}
                        {loading && <Spinner size="md" />}
                        {!loading && !journal && (
                            <p>
                                This will really run mdadm --zero-superblock/wipefs on "{diskId}", clearing its
                                partition table, filesystem, and any RAID/LVM signature. Continue?
                            </p>
                        )}
                        {journal && <JournalView journal={journal} />}
                    </ModalBody>
                    <ModalFooter>
                        {!loading && !journal && (
                            <>
                                <Button variant="warning" onClick={wipe}>Wipe</Button>
                                <Button variant="link" onClick={() => setOpen(false)}>Cancel</Button>
                            </>
                        )}
                        {journal && <Button variant="primary" onClick={close}>Close</Button>}
                    </ModalFooter>
                </Modal>
            )}
        </>
    );
}
