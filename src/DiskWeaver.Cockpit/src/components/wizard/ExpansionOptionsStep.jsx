import React from "react";
import { Radio } from "@patternfly/react-core";
import { formatBytes } from "../../format.js";

const INTENT_LABELS = {
    protection: "Add protection",
    space: "Add space",
    manual: "Manual",
};

const REDUNDANCY_LABELS = {
    none: "no protection",
    dwr1: "DWR-1 (survives 1 disk failure)",
    dwr2: "DWR-2 (survives 2 disk failures)",
};

// Shown after picking disks to expand an existing pool with: the daemon computes up to two
// candidate plans -- one that adds protection, one that adds space (see docs/algorithm.md's
// expand tenets) -- and this just presents whichever of those actually came back for the caller
// to pick between. Neither is ever forced into existing; a disk too small to help anything (the
// "hot spare" case) yields an empty list, handled here rather than treated as an error.
export function ExpansionOptionsStep({ options, selectedOptionId, onSelect }) {
    if (options.length === 0) {
        return (
            <p>
                These disk(s) don't add protection or space to this pool right now -- there's
                nothing useful to do with this selection. Go back and pick different disk(s).
            </p>
        );
    }

    return (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--pf-t--global--spacer--md)" }}>
            {options.map(option => (
                <Radio
                    key={option.planId}
                    id={`expansion-option-${option.planId}`}
                    name="expansion-option"
                    isChecked={selectedOptionId === option.planId}
                    onChange={() => onSelect(option.planId)}
                    label={INTENT_LABELS[option.intent] ?? option.intent}
                    body={
                        <span>
                            {formatBytes(option.achievedCapacityBytes)} usable
                            {option.achievedRedundancy && ` at ${REDUNDANCY_LABELS[option.achievedRedundancy] ?? option.achievedRedundancy}`}
                        </span>
                    }
                />
            ))}
        </div>
    );
}
