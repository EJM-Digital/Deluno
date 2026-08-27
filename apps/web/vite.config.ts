import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

const developmentPort = Number(process.env.DELUNO_WEB_PORT ?? 5173);
const apiProxyTarget = process.env.DELUNO_API_ORIGIN ?? "http://127.0.0.1:5099";
const apiProxy = {
  "/api": apiProxyTarget,
  "/hubs": {
    target: apiProxyTarget,
    ws: true,
    changeOrigin: true
  }
};

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
    globals: false,
    setupFiles: ["src/test/setup.ts"]
  },
  build: {
    rollupOptions: {
      output: {
        /*
          Content-hashed, so a deploy cannot leave a browser running yesterday's
          bundle.

          These were fixed names — `assets/deluno.js` — which meant a new build
          landed on disk under a name the browser already had cached, and kept
          serving the old one until someone thought to hard-reload. A fix that
          had shipped correctly looked exactly like a fix that had not worked,
          which cost a wrong diagnosis. `index.html` is never cached (it is the
          app shell the host rewrites to), so it always names the current hashes.
        */
        entryFileNames: "assets/deluno.[hash].js",
        chunkFileNames: "assets/[name].[hash].js",
        assetFileNames: "assets/[name].[hash][extname]",
        manualChunks(id) {
          if (!id.includes("node_modules")) {
            return;
          }

          if (id.includes("react-router")) {
            return "router";
          }

          if (id.includes("framer-motion")) {
            return "motion";
          }

          if (id.includes("@radix-ui")) {
            return "radix";
          }

          if (id.includes("lucide-react")) {
            return "icons";
          }

          if (id.includes("cmdk")) {
            return "cmdk";
          }

          if (id.includes("@dnd-kit")) {
            return "dnd";
          }

          if (id.includes("@microsoft/signalr")) {
            return "signalr";
          }

          if (id.includes("@tanstack/react-query")) {
            return "react-query";
          }

          if (id.includes("react") || id.includes("scheduler")) {
            return "react-vendor";
          }
        }
      }
    }
  },
  server: {
    host: "0.0.0.0",
    port: developmentPort,
    strictPort: true,
    proxy: apiProxy
  },
  preview: {
    host: "0.0.0.0",
    port: developmentPort,
    strictPort: true,
    proxy: apiProxy
  }
});
