import React, { useState, useRef } from "react";
import { Modal, ModalVariant, ModalHeader, ModalBody, ModalFooter, Button, Spinner } from "@patternfly/react-core";
import { apiPostJson, apiGetJson } from "../api.js";
import { DiskPickerStep } from "./wizard/DiskPickerStep.jsx";
import { ConfigureStep } from "./wizard/ConfigureStep.jsx";
import { ExpansionOptionsStep } from "./wizard/ExpansionOptionsStep.jsx";
import { ReviewPlanStep } from "./wizard/ReviewPlanStep.jsx";
import { JournalView } from "./JournalView.jsx";
import { PoolDiagram } from "./PoolDiagram.jsx";

const STEP_DISKS = "disks";
const STEP_CONFIGURE = "configure";
const STEP_OPTIONS = "options";
const STEP_REVIEW = "review";

export function CreateExpandWizard({ pools, disks, expansionPoolName, onClose }) {
    const isExpand = !!expansionPoolName;
    const [step, setStep] = useState(STEP_DISKS);
    const [selectedDiskIds, setSelectedDiskIds] = useState(new Set());
    const [poolName, setPoolName] = useState("diskweaver-pool");
    const [redundancy, setRedundancy] = useState("dwr1");
    const [thinProvisioned, setThinProvisioned] = useState(false);
    const [assumeClean, setAssumeClean] = useState(false);
    // Expand-only: the daemon's up-to-two candidate plans (protection/space) for the picked
    // disk(s) -- see ExpansionOptionsStep and daemon-api.md's POST /pools/{poolName}/expand.
    const [options, setOptions] = useState([]);
    const [selectedOptionId, setSelectedOptionId] = useState(null);
    const [planId, setPlanId] = useState(null);
    const [plan, setPlan] = useState(null);
    const [achievedCapacityBytes, setAchievedCapacityBytes] = useState(undefined);
    const [hypotheticalRebuildCapacityBytes, setHypotheticalRebuildCapacityBytes] = useState(undefined);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);
    // Execute confirmation is rendered as an alternate body within this same
    // Modal (never a second nested <Modal>) -- a Modal-in-Modal made the inner
    // one unclickable, since PatternFly's focus trap only expects one active.
    const [confirmingExecute, setConfirmingExecute] = useState(false);
    // Rendered as an alternate STEP_REVIEW body within this same Modal, same reasoning as
    // confirmingExecute just above -- a second nested <Modal> here (PoolDiagramModal, which
    // PoolsTable uses fine at the top level) left the inner one unclickable, since PatternFly's
    // focus trap only expects one active Modal at a time.
    const [showDiagram, setShowDiagram] = useState(false);
    const [executing, setExecuting] = useState(false);
    const [journal, setJournal] = useState(null);
    // Filled in by polling GET /execute/{id}/status while executePlan's own POST is still
    // in flight -- see executePlan for why that POST alone can't show real progress.
    const [progressJournal, setProgressJournal] = useState(null);
    const pollHandleRef = useRef(null);

    function createPlan() {
        setLoading(true);
        setError(null);
        const diskIds = [...selectedDiskIds];
        const request = isExpand
            // No redundancy/autoProtect/growIndependently flags -- the daemon computes up to two
            // candidate plans (protection, space) itself; see ExpansionOptionsStep.
            ? apiPostJson(`/pools/${encodeURIComponent(expansionPoolName)}/expand`, { diskIds })
                .then(response => {
                    setOptions(response.options);
                    setSelectedOptionId(response.options[0]?.planId ?? null);
                    setHypotheticalRebuildCapacityBytes(response.hypotheticalRebuildCapacityBytes);
                })
            : apiPostJson("/plan", { diskIds, redundancy, poolName: poolName.trim() || undefined, thinProvisioned, assumeClean })
                .then(response => {
                    setPlanId(response.id);
                    setPlan(response.plan);
                    setAchievedCapacityBytes(undefined);
                    setHypotheticalRebuildCapacityBytes(undefined);
                });

        request
            .then(() => setStep(isExpand ? STEP_OPTIONS : STEP_REVIEW))
            .catch(err => setError(err.message || String(err)))
            .finally(() => setLoading(false));
    }

    // Commits the option picked on STEP_OPTIONS as the plan STEP_REVIEW/Execute act on.
    function selectOption() {
        const option = options.find(o => o.planId === selectedOptionId);
        setPlanId(option.planId);
        setPlan(option.desiredPlan);
        setAchievedCapacityBytes(option.achievedCapacityBytes);
        setStep(STEP_REVIEW);
    }

    function executePlan() {
        setConfirmingExecute(false);
        setExecuting(true);
        setError(null);
        setProgressJournal(null);
        const path = isExpand
            ? `/pools/${encodeURIComponent(expansionPoolName)}/expand/${planId}/execute`
            : `/plan/${planId}/execute?kind=build`;

        // The POST below runs every step server-side and doesn't respond until the whole plan
        // finishes (or a step fails) -- for a real RAID build/expand that's well over a minute
        // with nothing on screen but a spinner. The daemon saves its journal file after each
        // individual step completes, though, so polling GET /execute/{id}/status concurrently
        // (same execution id the daemon derives: "{planId}-build" / "{planId}-expand") surfaces
        // real step-by-step progress instead of an all-or-nothing wait.
        const executionId = isExpand ? `${planId}-expand` : `${planId}-build`;
        pollHandleRef.current = setInterval(() => {
            apiGetJson(`/execute/${encodeURIComponent(executionId)}/status`)
                .then(setProgressJournal)
                .catch(() => {}); // no journal file yet on the very first tick(s) -- ignore, next tick finds it
        }, 1000);

        apiPostJson(path)
            .then(setJournal)
            .catch(err => setError(err.message || String(err)))
            .finally(() => {
                clearInterval(pollHandleRef.current);
                pollHandleRef.current = null;
                setProgressJournal(null);
                setExecuting(false);
            });
    }

    function onNext() {
        if (step === STEP_DISKS) {
            if (selectedDiskIds.size === 0) {
                setError("Select at least one disk first.");
                return;
            }
            setError(null);
            if (isExpand) {
                createPlan();
            } else {
                // DWR-2 needs more than 2 disks -- if the user picked it while more disks
                // were selected, then went back and dropped down to 2 or fewer, silently
                // downgrade rather than letting a build request go out for an unmet redundancy.
                if (selectedDiskIds.size <= 2 && redundancy === "dwr2") {
                    setRedundancy("dwr1");
                }
                setStep(STEP_CONFIGURE);
            }
        } else if (step === STEP_CONFIGURE) {
            createPlan();
        } else if (step === STEP_OPTIONS) {
            selectOption();
        }
    }

    function onBack() {
        if (step === STEP_CONFIGURE) setStep(STEP_DISKS);
        else if (step === STEP_OPTIONS) setStep(STEP_DISKS);
        else if (step === STEP_REVIEW && !isExpand) setStep(STEP_CONFIGURE);
        else if (step === STEP_REVIEW && isExpand) setStep(STEP_OPTIONS);
    }

    const busy = loading || executing;

    return (
        <Modal
            variant={ModalVariant.large}
            isOpen
            // Suppresses both the header's "X" and Escape-to-close while a build/expand is
            // actually running commands -- closing the modal mid-execution wouldn't stop it
            // server-side (the POST keeps running), it would just hide the only feedback the
            // user has that something is still in progress.
            onClose={executing ? undefined : onClose}
        >
            <ModalHeader title={isExpand ? `Expand "${expansionPoolName}"` : "Create a new pool"} />
            <ModalBody style={{ display: "flex", flexDirection: "column", gap: "var(--pf-t--global--spacer--md)" }}>
                {error && <p style={{ color: "var(--pf-t--global--color--status--danger--default)" }}>{error}</p>}

                {confirmingExecute && (
                    <p>
                        This will actually run parted/mdadm/lvm2 commands to{" "}
                        {isExpand ? `expand "${expansionPoolName}"` : "build this pool"}. Continue?
                    </p>
                )}
                {!confirmingExecute && step === STEP_DISKS && (
                    <DiskPickerStep
                        disks={disks}
                        pools={pools}
                        selectedDiskIds={selectedDiskIds}
                        onChange={setSelectedDiskIds}
                    />
                )}
                {!confirmingExecute && step === STEP_CONFIGURE && (
                    <ConfigureStep
                        poolName={poolName}
                        onPoolNameChange={setPoolName}
                        redundancy={redundancy}
                        onRedundancyChange={setRedundancy}
                        diskCount={selectedDiskIds.size}
                        thinProvisioned={thinProvisioned}
                        onThinProvisionedChange={setThinProvisioned}
                        assumeClean={assumeClean}
                        onAssumeCleanChange={setAssumeClean}
                    />
                )}
                {!confirmingExecute && step === STEP_OPTIONS && (
                    <ExpansionOptionsStep
                        options={options}
                        selectedOptionId={selectedOptionId}
                        onSelect={setSelectedOptionId}
                    />
                )}
                {!confirmingExecute && !executing && step === STEP_REVIEW && plan && !journal && !showDiagram && (
                    <ReviewPlanStep
                        plan={plan}
                        achievedCapacityBytes={achievedCapacityBytes}
                        hypotheticalRebuildCapacityBytes={hypotheticalRebuildCapacityBytes}
                        expansionPoolName={expansionPoolName}
                        poolName={poolName}
                        planId={planId}
                        thinProvisioned={!isExpand && thinProvisioned}
                        assumeClean={!isExpand && assumeClean}
                        onVisualize={() => setShowDiagram(true)}
                    />
                )}
                {!confirmingExecute && !executing && step === STEP_REVIEW && plan && !journal && showDiagram && (
                    <>
                        <h3>{expansionPoolName ? `Visualize plan: expand "${expansionPoolName}"` : `Visualize plan: "${poolName}"`}</h3>
                        <PoolDiagram tiers={plan.tiers} disks={disks} />
                    </>
                )}
                {(journal || progressJournal) && (
                    <>
                        <h3>{journal ? "Execution journal" : "Running..."}</h3>
                        <JournalView journal={journal ?? progressJournal} />
                    </>
                )}
                {loading && <Spinner size="md" />}
                {executing && !progressJournal && <Spinner size="md" />}
            </ModalBody>
            <ModalFooter style={{ justifyContent: "space-between" }}>
                {!confirmingExecute && !busy && !journal && showDiagram ? (
                    <Button variant="secondary" onClick={() => setShowDiagram(false)}>Back</Button>
                ) : !confirmingExecute && !busy && !journal && step !== STEP_DISKS ? (
                    <Button variant="secondary" onClick={onBack}>Back</Button>
                ) : <span />}
                <div>
                    {confirmingExecute && !busy && (
                        <>
                            <Button variant="danger" onClick={executePlan}>Execute</Button>
                            <Button variant="link" onClick={() => setConfirmingExecute(false)}>Cancel</Button>
                        </>
                    )}
                    {!confirmingExecute && !busy && !journal && !showDiagram && step !== STEP_REVIEW && (
                        <Button
                            variant="primary"
                            onClick={onNext}
                            isDisabled={step === STEP_OPTIONS && !selectedOptionId}
                        >
                            Next
                        </Button>
                    )}
                    {!confirmingExecute && !busy && !journal && !showDiagram && step === STEP_REVIEW && (
                        <Button variant="primary" onClick={() => setConfirmingExecute(true)}>
                            {isExpand ? "Expand" : "Create"}
                        </Button>
                    )}
                    {!confirmingExecute && journal && (
                        <Button variant="primary" onClick={onClose}>Close</Button>
                    )}
                </div>
            </ModalFooter>
        </Modal>
    );
}
