import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";
import "@fortawesome/fontawesome-free/css/all.min.css";

// iOS Safari ignores touch-action/viewport for pinch-zoom; its proprietary
// gesture events are the only reliable kill switch. Harmless elsewhere.
for (const t of ["gesturestart", "gesturechange"] as const) {
  document.addEventListener(t, (e) => e.preventDefault(), { passive: false });
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);