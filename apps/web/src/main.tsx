import React from "react";
import ReactDOM from "react-dom/client";
import { ThemeProvider } from "next-themes";
import { RouterProvider } from "react-router-dom";
import { TooltipProvider } from "./components/ui/tooltip";
import { AuthProvider } from "./lib/use-auth";
import { router } from "./router";
import "./index.css";

// Lock Deluno to one product accent. Status colour now carries semantic meaning.
document.documentElement.setAttribute("data-accent", "cobalt");
try {
  window.localStorage.removeItem("deluno-accent");
} catch {
  /* ignore storage failures */
}

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
        <AuthProvider>
          {/* Toaster now lives inside <AppLayout> so it inherits density and theme */}
          <RouterProvider router={router} />
        </AuthProvider>
      </ThemeProvider>
    </TooltipProvider>
  </React.StrictMode>
);
