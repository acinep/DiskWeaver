import React from "react";
import { Form, FormGroup, TextInput, FormSelect, FormSelectOption, Checkbox } from "@patternfly/react-core";

// Redundancy and pool name are only chosen when creating a new pool -- expanding
// an existing one always targets that pool's own (inferred) redundancy level and
// its existing name, per daemon-api.md. This step is skipped entirely by
// CreateExpandWizard when expanding.
export function ConfigureStep({
    poolName, onPoolNameChange, redundancy, onRedundancyChange, diskCount, thinProvisioned, onThinProvisionedChange,
    assumeClean, onAssumeCleanChange, chunkSizeKb, onChunkSizeKbChange,
    raid5ConsistencyPolicy, onRaid5ConsistencyPolicyChange,
}) {
    // DWR-2 tolerates 2 disk failures, which needs a disk on top of the 2 that
    // hold the parity itself -- selecting it with 2 or fewer disks would just
    // fail once the wizard tries to plan/build.
    const dwr2Disabled = diskCount <= 2;
    return (
        <Form>
            <FormGroup label="Pool name" fieldId="pool-name">
                <TextInput
                    id="pool-name"
                    value={poolName}
                    onChange={(_event, value) => onPoolNameChange(value)}
                />
            </FormGroup>
            <FormGroup label="Redundancy" fieldId="redundancy">
                <FormSelect
                    id="redundancy"
                    value={redundancy}
                    onChange={(_event, value) => onRedundancyChange(value)}
                >
                    <FormSelectOption value="none" label="No protection (each disk independent -- add a mirror disk later)" />
                    <FormSelectOption value="dwr1" label="DWR-1 (survives 1 disk failure)" />
                    <FormSelectOption
                        value="dwr2"
                        label={dwr2Disabled ? "DWR-2 (needs more than 2 disks)" : "DWR-2 (survives 2 disk failures)"}
                        isDisabled={dwr2Disabled}
                    />
                </FormSelect>
            </FormGroup>
            <FormGroup fieldId="thin-provisioned">
                <Checkbox
                    id="thin-provisioned"
                    label="Thin-provision this pool"
                    description={
                        "Creates a thin pool (with 10% headroom reserved) plus one \"data\" volume "
                        + "using its full capacity, instead of one plain volume using 100% of the pool. "
                        + "Lets you carve out further thin volumes yourself later (e.g. for iSCSI LUNs) "
                        + "-- see docs/execution.md's \"Multiple logical volumes (thin pools)\"."
                    }
                    isChecked={thinProvisioned}
                    onChange={(_event, checked) => onThinProvisionedChange(checked)}
                />
            </FormGroup>
            <FormGroup fieldId="assume-clean">
                <Checkbox
                    id="assume-clean"
                    label="Skip initial resync (--assume-clean)"
                    description={
                        "Creates each tier's mdadm array with --assume-clean, skipping the initial "
                        + "full-array resync/parity-build. Safe here because every disk is verified "
                        + "blank before creation, so there's no real data whose parity could be wrong "
                        + "to begin with -- but it does mean any latent bad sectors go undetected "
                        + "until they're actually read/written, instead of being surfaced by the resync."
                    }
                    isChecked={assumeClean}
                    onChange={(_event, checked) => onAssumeCleanChange(checked)}
                />
            </FormGroup>
            <FormGroup label="Chunk size" fieldId="chunk-size">
                <FormSelect
                    id="chunk-size"
                    value={chunkSizeKb}
                    onChange={(_event, value) => onChunkSizeKbChange(Number(value))}
                >
                    <FormSelectOption value={64} label="64 KiB (default -- favors small/random I/O)" />
                    <FormSelectOption value={128} label="128 KiB" />
                    <FormSelectOption value={256} label="256 KiB" />
                    <FormSelectOption value={512} label="512 KiB (mdadm's own default -- favors large sequential I/O)" />
                </FormSelect>
            </FormGroup>
            <FormGroup label="RAID5 write-hole protection" fieldId="raid5-consistency-policy">
                <FormSelect
                    id="raid5-consistency-policy"
                    value={raid5ConsistencyPolicy}
                    onChange={(_event, value) => onRaid5ConsistencyPolicyChange(value)}
                >
                    <FormSelectOption value="resync" label="Resync -- fastest writes, full array resync (hours) after any unclean shutdown" />
                    <FormSelectOption value="bitmap" label="Bitmap (default) -- small write overhead, fast (seconds/minutes) recovery after an unclean shutdown" />
                    <FormSelectOption value="ppl" label="PPL -- closes the write hole entirely, but a major, measured hit to sustained write throughput" />
                </FormSelect>
                <p style={{ marginTop: "0.5rem", color: "var(--pf-v5-global--Color--200, #6a6e73)" }}>
                    Only applies to RAID5 tiers (ignored for Mirror/RAID6, which always use a plain
                    bitmap). This is a straight performance-vs-safety trade-off against the RAID5 write
                    hole -- a stripe torn by power loss mid-write, silently leaving data and parity
                    inconsistent. Resync is fastest day-to-day but leaves the hole open and a slow
                    recovery; PPL closes the hole completely but its per-write journal overhead has been
                    measured to badly cut RAID5 write throughput -- Bitmap is the recommended middle
                    ground for most workloads.
                </p>
            </FormGroup>
        </Form>
    );
}
