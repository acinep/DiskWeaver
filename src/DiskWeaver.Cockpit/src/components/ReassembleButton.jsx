import React, { useState } from "react";
import { Button, Modal, ModalVariant, ModalHeader, ModalBody, ModalFooter, Spinner } from "@patternfly/react-core";
import { SyncAltIcon } from "@patternfly/react-icons";
import { apiPostJson } from "../api.js";
import { JournalView } from "./JournalView.jsx";

// Brings up any mdadm array/LVM volume group that exists on disk but isn't currently
// assembled/active -- via POST /arrays/reassemble, CommandPlanner.BuildReassemble's
// mdadm --assemble --scan + vgchange -ay. This is what makes a pool built under a previous OS
// install (e.g. Proxmox) show up again after installing DiskWeaver fresh on the same disks under
// a new OS: the diskweaver-managed tag survives in LVM's own on-disk metadata, but the kernel
// still needs telling to reassemble/activate it. Always available, not tied to any pool -- that's
// exactly the case where GET /pools has nothing to show yet.
export function ReassembleButton({ onDone }) {
    const [open, setOpen] = useState(false);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);
    const [journal, setJournal] = useState(null);

    function reassemble() {
        setLoading(true);
        setError(null);
        apiPostJson("/arrays/reassemble")
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
            <Button variant="secondary" icon={<SyncAltIcon />} onClick={() => setOpen(true)}>
                Scan for existing pools
            </Button>

            {open && (
                <Modal variant={ModalVariant.medium} isOpen onClose={close}>
                    <ModalHeader title={journal ? "Reassemble journal" : "Scan for existing pools"} />
                    <ModalBody>
                        {error && <p style={{ color: "var(--pf-t--global--color--status--danger--default)" }}>{error}</p>}
                        {loading && <Spinner size="md" />}
                        {!loading && !journal && (
                            <p>
                                This will run mdadm --assemble --scan and vgchange -ay to bring up any
                                array/volume group that already exists on this host's disks (e.g. a pool
                                built under a previous OS install) but isn't currently active, then persist
                                it to mdadm.conf and rebuild the initramfs so it survives a reboot. Safe to
                                run any time -- it never creates, destroys, or modifies data, and is a no-op
                                if there's nothing inactive to bring up. Continue?
                            </p>
                        )}
                        {journal && <JournalView journal={journal} />}
                    </ModalBody>
                    <ModalFooter>
                        {!loading && !journal && (
                            <>
                                <Button variant="secondary" icon={<SyncAltIcon />} onClick={reassemble}>Scan</Button>
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
