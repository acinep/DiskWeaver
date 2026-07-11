import React, { useState } from "react";
import { Button, Modal, ModalVariant, ModalHeader, ModalBody, ModalFooter, Spinner, Tooltip } from "@patternfly/react-core";
import { TrashIcon } from "@patternfly/react-icons";
import { apiPostJson } from "../api.js";
import { JournalView } from "./JournalView.jsx";

// Teardown is driven by the pool's real on-disk state (GET /pools), via
// POST /pools/{poolName}/teardown -- deliberately not the plan/execute
// teardown path, which needs the original disk selection that produced a
// plan and is useless for a pool from a previous session. See
// docs/cockpit-plugin.md, "Tearing down a pool: two different actions".
export function TeardownButton({ poolName, onDone }) {
    const [open, setOpen] = useState(false);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);
    const [journal, setJournal] = useState(null);

    function teardown() {
        setLoading(true);
        setError(null);
        apiPostJson(`/pools/${encodeURIComponent(poolName)}/teardown`)
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
            <Tooltip content="Teardown">
                <Button variant="danger" icon={<TrashIcon />} aria-label="Teardown" onClick={() => setOpen(true)} />
            </Tooltip>

            {open && (
                <Modal variant={ModalVariant.medium} isOpen onClose={close}>
                    <ModalHeader title={journal ? `Teardown journal: ${poolName}` : "Confirm teardown"} />
                    <ModalBody>
                        {error && <p style={{ color: "var(--pf-t--global--color--status--danger--default)" }}>{error}</p>}
                        {loading && <Spinner size="md" />}
                        {!loading && !journal && (
                            <p>This will really run lvremove/vgremove/mdadm/wipefs to tear down "{poolName}". Continue?</p>
                        )}
                        {journal && <JournalView journal={journal} />}
                    </ModalBody>
                    <ModalFooter>
                        {!loading && !journal && (
                            <>
                                <Button variant="danger" icon={<TrashIcon />} onClick={teardown}>Teardown</Button>
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
