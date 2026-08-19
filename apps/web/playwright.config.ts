import os from "node:os";
import path from "node:path";
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig, devices } from "@playwright/test";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "../..");
const smokeDataRoot = path.join(os.tmpdir(), "deluno-playwright", `${process.pid}-${Date.now()}`);
const smokeApiPort = 5199;
const smokeWebPort = 5174;
const smokeApiUrl = `http://127.0.0.1:${smokeApiPort}`;
const smokeWebUrl = `http://127.0.0.1:${smokeWebPort}`;
const repoLocalDotnet = path.join(repoRoot, ".dotnet", "dotnet.exe");
const dotnetCommand = process.platform === "win32" && fs.existsSync(repoLocalDotnet)
  ? `"${repoLocalDotnet}"`
  : "dotnet";
// Start from the project rather than a previously compiled DLL. This keeps the
// browser suite honest when an API route has just been added: it must exercise
// the source revision under test, not an older artifact left in bin/Release.
const backendCommand = `${dotnetCommand} run --project ../../src/Deluno.Host/Deluno.Host.csproj --configuration Release --no-launch-profile`;

export default defineConfig({
  testDir: "./tests",
  // The smoke suite shares one disposable API and static production-bundle server.
  // Tests intentionally share one disposable database. A single worker keeps
  // setup drafts and seeded media from one scenario from leaking into another;
  // projects still verify both desktop and mobile in the same regression run.
  workers: 1,
  timeout: 30_000,
  expect: {
    timeout: 5_000
  },
  use: {
    baseURL: smokeWebUrl,
    trace: "retain-on-failure"
  },
  webServer: [
    {
      command: backendCommand,
      url: `${smokeApiUrl}/health`,
      reuseExistingServer: false,
      timeout: 90_000,
      env: {
        ...process.env,
        Storage__DataRoot: smokeDataRoot,
        // The suite logs in for every test from one address; the production
        // default of 10/min would throttle the run, not an attacker.
        Security__Login__PermitLimit: "100000",
        // Same reasoning for the global API limiter: 189 tests driving the
        // whole UI from one process would otherwise trip the 600/min default.
        Security__Api__PermitLimit: "1000000",
        Server__Port: String(smokeApiPort)
      }
    },
    {
      command: "npm run build && node scripts/serve-smoke-preview.mjs",
      url: smokeWebUrl,
      reuseExistingServer: false,
      timeout: 60_000,
      env: {
        ...process.env,
        DELUNO_WEB_PORT: String(smokeWebPort),
        DELUNO_API_ORIGIN: smokeApiUrl
      }
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
