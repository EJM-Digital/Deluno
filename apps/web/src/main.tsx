import React from "react";
import ReactDOM from "react-dom/client";
import { ThemeProvider } from "next-themes";
import { RouterProvider } from "react-router-dom";
import { TooltipProvider } from "./components/ui/tooltip";
import { AuthProvider } from "./lib/use-auth";
import { ColorModeProvider } from "./lib/use-color-mode";
import { router } from "./router";
import { installStaleBuildRecovery } from "./lib/stale-build";
import { installHiddenPageMotionGuard } from "./lib/page-motion";
import "./index.css";

// A screen that fails because the tab predates an update is the page being
// opened, so the reload lands there (#622).
installStaleBuildRecovery(window, () => {
  const pending = router.state.navigation.location;
  return pending ? pending.pathname + pending.search + pending.hash : null;
});

// Nothing replays a backlog of movement when a hidden tab is shown again (#873).
installHiddenPageMotionGuard();

// Register PWA service worker (production only)
if ("serviceWorker" in navigator && import.meta.env.PROD) {
  window.addEventListener("load", () => {
    navigator.serviceWorker
      .register("/sw.js", { scope: "/" })
      .catch(() => { /* Silently ignore — SW is a progressive enhancement */ });
  });
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <TooltipProvider delayDuration={400}>
      <ThemeProvider
        attribute="class"
        defaultTheme="dark"
        enableSystem
        disableTransitionOnChange={false}
        storageKey="deluno-theme"
      >
        <ColorModeProvider>
          <AuthProvider>
            {/* Toaster now lives inside <AppLayout> so it inherits density and theme */}
            <RouterProvider router={router} />
          </AuthProvider>
        </ColorModeProvider>
      </ThemeProvider>
    </TooltipProvider>
  </React.StrictMode>
);
