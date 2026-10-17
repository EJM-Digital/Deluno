import { defineConfig } from "@playwright/test";

/**
 * The end-to-end suite (docs/exec-plans/active/PLAN-E2E-IN-CI.md). One file per golden-path group; CI runs one group per job:
 *   npx playwright test -c tests/e2e/playwright.config.ts films
 * The stack comes up first (tests/e2e/compose.yaml) and every spec reads each application's own API through stack.ts.
 */
export default defineConfig({
  testDir: ".",
  testMatch: "*.spec.ts",
  timeout: 15 * 60_000,
  workers: 1,
  fullyParallel: false,
  retries: 0,
  reporter: [["list"], ["json", { outputFile: "../../artifacts/e2e/results.json" }]],
  outputDir: "../../artifacts/e2e/output"
});
