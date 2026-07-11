import React, { useEffect, useState } from "react";
import { Bullseye, Spinner } from "@patternfly/react-core";
import { apiGetJson, apiRequest } from "../api.standalone.js";
import { App } from "../components/App.jsx";
import { LoginForm } from "./LoginForm.jsx";

// "checking" exists as its own state (not just a loggedIn boolean) so a returning user with a
// still-valid session cookie never sees a flash of the login form while GET /auth/session is in
// flight -- only shown once we positively know there's no session.
export function StandaloneApp() {
    const [authState, setAuthState] = useState("checking");

    useEffect(() => {
        apiGetJson("/auth/session")
            .then(() => setAuthState("loggedIn"))
            .catch(() => setAuthState("loggedOut"));
    }, []);

    useEffect(() => {
        // api.standalone.js dispatches this on any 401 -- e.g. the session cookie expiring
        // mid-use -- so the user lands back on the login form instead of a component left stuck
        // on a stale "unauthorized" error banner that a re-login would never clear on its own.
        function handleUnauthenticated() {
            setAuthState("loggedOut");
        }
        window.addEventListener("diskweaver-unauthenticated", handleUnauthenticated);
        return () => window.removeEventListener("diskweaver-unauthenticated", handleUnauthenticated);
    }, []);

    function handleLogout() {
        apiRequest("POST", "/auth/logout").finally(() => setAuthState("loggedOut"));
    }

    if (authState === "checking") {
        return (
            <Bullseye style={{ height: "100vh" }}>
                <Spinner size="xl" aria-label="Checking session" />
            </Bullseye>
        );
    }

    if (authState === "loggedOut") {
        return <LoginForm onLoggedIn={() => setAuthState("loggedIn")} />;
    }

    return <App onLogout={handleLogout} />;
}
