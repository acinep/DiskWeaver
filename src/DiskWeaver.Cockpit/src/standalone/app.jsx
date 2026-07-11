import React from "react";
import { createRoot } from "react-dom/client";
import "@patternfly/react-core/dist/styles/base.css";
import "../overrides.css";
import { initTheme } from "../theme.js";
import { StandaloneApp } from "./StandaloneApp.jsx";

// initTheme() reads a "shell:style" localStorage key/listens for a "cockpit-style" event that
// only exist inside an actual Cockpit shell frame -- both silently never fire here, so this
// degrades to the plain prefers-color-scheme media query fallback already built into theme.js.
initTheme();
createRoot(document.getElementById("app")).render(<StandaloneApp />);
