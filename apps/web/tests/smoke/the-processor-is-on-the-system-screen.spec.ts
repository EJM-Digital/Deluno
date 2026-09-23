import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #577. A processor holds every file mid-import, and its health showed on one
// chip under Settings and nowhere else. The System screen listed indexers and
// download clients; the dashboard fetched processor connections only to decide
// whether setup was finished. So somebody watching either screen would have
// seen nothing at all while their processor was down.
const weir = {
  id: "processor-1",
  name: "Weir",
  provider: "generic-webhook",
  submissionUrl: "http://127.0.0.1:9347/api/v1/intake/webhook/deluno",
  authHeaderName: "X-Webhook-Secret",
  isEnabled: true,
  healthStatus: "unreachable",
  lastHealthMessage: "Weir did not answer.",
  lastHealthLatencyMs: null,
  lastHealthTestUtc: "2026-09-23T04:00:00Z",
  createdUtc: "2026-09-15T00:00:00Z",
  updatedUtc: "2026-09-23T04:00:00Z"
};

test("a processor that is not answering is visible on the System screen", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  await page.route("**/api/integrations/processors/connections", async (route) => {
    await route.fulfill({ contentType: "application/json", body: JSON.stringify([weir]) });
  });

  await page.goto("/system");

  const row = page.getByRole("row").filter({ hasText: "Weir" }).first();
  await expect(row).toBeVisible();
  await expect(row.getByText("Processor")).toBeVisible();
  await expect(row.getByText("unreachable")).toBeVisible();
});

test("a processor that is switched off is not counted as unhealthy", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  await page.route("**/api/integrations/processors/connections", async (route) => {
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify([{ ...weir, isEnabled: false, healthStatus: "unreachable" }])
    });
  });

  await page.goto("/system");

  const row = page.getByRole("row").filter({ hasText: "Weir" }).first();
  await expect(row.getByText("off")).toBeVisible();
});
