import { defineConfig } from "vite";
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
  build: {
    rollupOptions: {
      output: {
        entryFileNames: "assets/deluno.js",
        chunkFileNames: "assets/[name].js",
        assetFileNames: "assets/[name][extname]",
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
