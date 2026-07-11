import React, { useState } from "react";
import { Button, Modal, ModalVariant, ModalHeader, ModalBody, ModalFooter, Spinner } from "@patternfly/react-core";
import { ShieldAltIcon } from "@patternfly/react-icons";
import { apiPostJson } from "../api.js";
import { formatBytes } from "../format.js";
import { DiskPickerStep } from "./wizard/DiskPickerStep.jsx";
import { JournalView } from "./JournalView.jsx";

// "Advanced" per-tier protection: explicitly pick a disk (or disks) to complete exactly this one
// tier into a real mirror, via POST /pools/{poolName}/expand's targetArrayDevice mode -- the
// manual counterpart to CreateExpandWizard's default two-option (protection/space) flow, which
// does this automatically across every eligible tier instead of one named one. To split one
// larger disk across several tiers, use this button once per tier, one at a time.
export function ProtectTierButton({ poolName, arrayDevice, disks, pools, onDone }) {
    const [open, setOpen] = useState(false);
    const [selectedDiskIds, setSelectedDiskIds] = useState(new Set());
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);
    const [planId, setPlanId] = useState(null);
    const [plan, setPlan] = useState(null);
    const [executing, setExecuting] = useState(false);
    const [journal, setJournal] = useState(null);

    function preview() {
        if (selectedDiskIds.size === 0) {
            setError("Select at least one disk first.");
            return;
        }
        setLoading(true);
        setError(null);
        apiPostJson(`/pools/${encodeURIComponent(poolName)}/expand`,
            { diskIds: [...selectedDiskIds], targetArrayDevice: arrayDevice })
            .then(response => {
                const option = response.options[0];
                setPlanId(option.planId);
                setPlan(option.desiredPlan);
            })
            .catch(err => setError(err.message || String(err)))
            .finally(() => setLoading(false));
    }

    function execute() {
        setExecuting(true);
        setError(null);
        apiPostJson(`/pools/${encodeURIComponent(poolName)}/expand/${planId}/execute`)
            .then(setJournal)
            .catch(err => setError(err.message || String(err)))
            .finally(() => setExecuting(false));
    }

    function close() {
        setOpen(false);
        setSelectedDiskIds(new Set());
        setPlanId(null);
        setPlan(null);
        setJournal(null);
        setError(null);
        onDone();
    }

    const busy = loading || executing;

    return (
        <>
            <Button variant="secondary" icon={<ShieldAltIcon />} onClick={() => setOpen(true)}>
                Add protection
            </Button>

            {open && (
                <Modal
                    variant={ModalVariant.medium}
                    isOpen
                    onClose={executing ? undefined : close}
                >
                    <ModalHeader title={`Protect ${arrayDevice}`} />
                    <ModalBody style={{ display: "flex", flexDirection: "column", gap: "var(--pf-t--global--spacer--md)" }}>
                        {error && <p style={{ color: "var(--pf-t--global--color--status--danger--default)" }}>{error}</p>}

                        {!plan && !journal && (
                            <DiskPickerStep
                                disks={disks}
                                pools={pools}
                                selectedDiskIds={selectedDiskIds}
                                onChange={setSelectedDiskIds}
                            />
                        )}

                        {plan && !journal && !executing && (
                            <p>
                                This will grow {arrayDevice} into a {formatBytes(plan.tiers[0]?.usableBytes ?? 0)}{" "}
                                mirror using {[...selectedDiskIds].join(", ")}. Continue?
                            </p>
                        )}

                        {journal && (
                            <>
                                <h3>Execution journal</h3>
                                <JournalView journal={journal} />
                            </>
                        )}

                        {busy && <Spinner size="md" />}
                    </ModalBody>
                    <ModalFooter>
                        {!plan && !journal && !loading && (
                            <>
                                <Button variant="primary" onClick={preview}>Review</Button>
                                <Button variant="link" onClick={close}>Cancel</Button>
                            </>
                        )}
                        {plan && !journal && !busy && (
                            <>
                                <Button variant="danger" onClick={execute}>Execute</Button>
                                <Button variant="link" onClick={() => setPlan(null)}>Back</Button>
                            </>
                        )}
                        {journal && <Button variant="primary" onClick={close}>Close</Button>}
                    </ModalFooter>
                </Modal>
            )}
        </>
    );
}
