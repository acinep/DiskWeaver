import React from "react";
import { Table, Thead, Tbody, Tr, Th, Td } from "@patternfly/react-table";
import { STEP_STATUS_LABELS, JOURNAL_STATUS_LABELS } from "../format.js";

export function JournalView({ journal }) {
    return (
        <div>
            <p><strong>Overall status:</strong> {JOURNAL_STATUS_LABELS[journal.status] ?? journal.status}</p>
            <div className="table-scroll">
                <Table variant="compact">
                    <Thead>
                        <Tr>
                            <Th>Step</Th>
                            <Th>Status</Th>
                            <Th>Exit code</Th>
                            <Th>Error</Th>
                        </Tr>
                    </Thead>
                    <Tbody>
                        {journal.steps.map((step, i) => (
                            <Tr key={i}>
                                <Td dataLabel="Step">{step.description}</Td>
                                <Td dataLabel="Status">{STEP_STATUS_LABELS[step.status] ?? step.status}</Td>
                                <Td dataLabel="Exit code">{step.exitCode ?? ""}</Td>
                                <Td dataLabel="Error">{step.error && <pre>{step.error}</pre>}</Td>
                            </Tr>
                        ))}
                    </Tbody>
                </Table>
            </div>
        </div>
    );
}
