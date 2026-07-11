import React from "react";
import { Modal, ModalVariant, ModalHeader, ModalBody, ModalFooter, Button } from "@patternfly/react-core";
import { PoolDiagram } from "./PoolDiagram.jsx";

// Shared by PoolsTable (an already-built pool's real tiers, from GET /pools) and
// ReviewPlanStep (a not-yet-built plan's tiers, from POST /plan or POST
// /pools/{poolName}/expand) -- PoolDiagram itself is agnostic to which, so this is just the
// Modal chrome around it.
export function PoolDiagramModal({ title, tiers, disks, isOpen, onClose }) {
    if (!isOpen) return null;
    return (
        <Modal variant={ModalVariant.large} isOpen onClose={onClose}>
            <ModalHeader title={title} />
            <ModalBody>
                <PoolDiagram tiers={tiers} disks={disks} />
            </ModalBody>
            <ModalFooter>
                <Button variant="primary" onClick={onClose}>Close</Button>
            </ModalFooter>
        </Modal>
    );
}
