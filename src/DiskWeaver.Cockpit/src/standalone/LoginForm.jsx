import React, { useState } from "react";
import { LoginPage, LoginForm as PfLoginForm } from "@patternfly/react-core";
import { apiRequest } from "../api.standalone.js";

// Only exists in the standalone build -- Cockpit never renders this, since a Cockpit session is
// already authenticated (PAM, via cockpit-bridge) before this plugin ever loads. POST /auth/login
// itself doesn't return a JSON body (just 200/401/403), so this calls apiRequest directly rather
// than apiPostJson -- there's nothing to JSON.parse.
export function LoginForm({ onLoggedIn }) {
    const [username, setUsername] = useState("");
    const [password, setPassword] = useState("");
    const [error, setError] = useState(null);
    const [submitting, setSubmitting] = useState(false);

    function handleSubmit(event) {
        event.preventDefault();
        setSubmitting(true);
        setError(null);
        apiRequest("POST", "/auth/login", { username, password })
            .then(() => onLoggedIn(username))
            .catch(err => setError(err.message || String(err)))
            .finally(() => setSubmitting(false));
    }

    return (
        <LoginPage
            brandImgSrc=""
            brandImgAlt=""
            loginTitle="DiskWeaver login"
            loginSubtitle="Root or a member of the diskweaver group"
        >
            <PfLoginForm
                usernameLabel="Username"
                usernameValue={username}
                onChangeUsername={(_event, value) => setUsername(value)}
                passwordLabel="Password"
                passwordValue={password}
                onChangePassword={(_event, value) => setPassword(value)}
                onLoginButtonClick={handleSubmit}
                isLoginButtonDisabled={submitting || !username || !password}
                loginButtonLabel={submitting ? "Signing in..." : "Log in"}
                helperText={error}
                showHelperText={Boolean(error)}
                isValidUsername={!error}
                isValidPassword={!error}
            />
        </LoginPage>
    );
}
