import React from "react";
import { createRoot } from "react-dom/client";
import "@patternfly/react-core/dist/styles/base.css";
import "./overrides.css";
import { App } from "./components/App.jsx";
import { initTheme } from "./theme.js";

initTheme();
createRoot(document.getElementById("app")).render(<App />);
