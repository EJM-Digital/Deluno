import os from "node:os";
import path from "node:path";
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig, devices } from "@playwright/test";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "../..");
const smokeDataRoot = path.join(os.tmpdir(), "deluno-playwright", `${process.pid}-${Date.now()}`);
const repoLocalDotnet = path.join(repoRoot, ".dotnet", "dotnet.exe");
const dotnetCommand = process.platform === "win32" && fs.existsSync(repoLocalDotnet)
  ? `"${repoLocalDotnet}"`
  : "dotnet";
const backendCommand = `${dotnetCommand} run --project ../../src/Deluno.Host/Deluno.Host.csproj --urls http://127.0.0.1:5099`;

export default defineConfig({
  testDir: "./tests",
  timeout: 30_000,
  expect: {
    timeout: 5_000
  },
  use: {
    baseURL: "http://127.0.0.1:5173",
    trace: "retain-on-failure"
  },
  webServer: [
    {
      command: backendCommand,
      url: "http://127.0.0.1:5099/health",
      reuseExistingServer: !process.env.CI,
      timeout: 90_000,
      env: {
        ...process.env,
        Storage__DataRoot: smokeDataRoot
      }
    },
    {
      command: "npm run dev -- --host 127.0.0.1",
      url: "http://127.0.0.1:5173",
      reuseExistingServer: !process.env.CI,
      timeout: 60_000
    }
  ],
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] }
    },
    {
      name: "mobile",
      use: {
        ...devices["Pixel 7"],
        browserName: "chromium"
      }
    }
  ]
});
