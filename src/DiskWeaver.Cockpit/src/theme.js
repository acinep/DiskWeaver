// base1/cockpit.js does not apply any theme class to embedded plugin frames
// (confirmed by decompiling it -- no "pf-v6-theme-dark" reference anywhere).
// The shell instead writes the user's choice to the "shell:style" localStorage
// key (shared with us, since plugin frames are same-origin) and broadcasts a
// "cockpit-style" CustomEvent on change; each PatternFly-based plugin is
// expected to replicate this itself -- verified by extracting the identical
// pattern from the bundled `storaged` plugin (the closest official analog to
// this one) in a real Cockpit install.
function isDark(styleOverride) {
    const style = styleOverride || localStorage.getItem("shell:style") || "auto";
    if (style === "dark") return true;
    if (style === "light") return false;
    return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
}

function applyTheme(styleOverride) {
    document.documentElement.classList.toggle("pf-v6-theme-dark", isDark(styleOverride));
}

export function initTheme() {
    applyTheme();
    window.addEventListener("storage", e => {
        if (e.key === "shell:style") applyTheme();
    });
    window.addEventListener("cockpit-style", e => {
        if (e instanceof CustomEvent) applyTheme(e.detail.style);
    });
    window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => applyTheme());
}
