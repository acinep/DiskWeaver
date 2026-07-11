// Standalone SPA transport: same-origin fetch() against the daemon's second, TCP listener
// (DISKWEAVER_HTTP_PORT), gated by the cookie session from POST /auth/login -- see api.js for
// the Cockpit-facing transport (cockpit.http() over the Unix socket) this deliberately mirrors.
// Exports the same three functions with the same call/error shape so every component under
// src/components/ works unmodified against either -- esbuild.config.mjs's onResolve plugin picks
// this file instead of api.js only when bundling src/standalone/app.jsx.

// A 401 here means the session cookie is missing or expired -- StandaloneApp listens for this to
// drop back to the login screen instead of leaving components stuck retrying/showing a raw error
// banner for a request that will never succeed until the user logs in again.
function reportUnauthenticated() {
    window.dispatchEvent(new CustomEvent("diskweaver-unauthenticated"));
}

export function apiRequest(method, path, jsonBody) {
    return fetch(path, {
        method,
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: jsonBody === undefined ? undefined : JSON.stringify(jsonBody),
    }).then(async response => {
        const text = await response.text();
        if (response.status === 401) {
            reportUnauthenticated();
        }
        if (!response.ok) {
            // Matches api.js/cockpit.http()'s shape (err.message carries the daemon's plain-text
            // body) so components' existing `err.message || err` handling needs no changes.
            throw new Error(text || `${method} ${path} failed with HTTP ${response.status}`);
        }
        return text;
    });
}

export function apiGetJson(path) {
    return apiRequest("GET", path).then(text => JSON.parse(text));
}

export function apiPostJson(path, jsonBody) {
    return apiRequest("POST", path, jsonBody).then(text => JSON.parse(text));
}
