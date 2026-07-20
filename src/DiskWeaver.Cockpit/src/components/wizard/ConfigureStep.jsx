import React from "react";
import { Form, FormGroup, TextInput, FormSelect, FormSelectOption, Checkbox } from "@patternfly/react-core";

// Redundancy and pool name are only chosen when creating a new pool -- expanding
// an existing one always targets that pool's own (inferred) redundancy level and
// its existing name, per daemon-api.md. This step is skipped entirely by
// CreateExpandWizard when expanding.
export function ConfigureStep({
    poolName, onPoolNameChange, redundancy, onRedundancyChange, diskCount, thinProvisioned, onThinProvisionedChange,
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
        </Form>
    );
}
