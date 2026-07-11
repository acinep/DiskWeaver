import React from "react";
import { Alert } from "@patternfly/react-core";

export function ErrorBanner({ message }) {
    if (!message) return null;
    return <Alert variant="danger" title={message} style={{ marginBottom: "1rem" }} />;
}
