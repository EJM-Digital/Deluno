import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("migration assistant", () => {
  test("previews changes without exposing imported secrets", async ({ page }) => {
    await page.route("**/api/migration/preview", async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({
          valid: true,
          sourceKind: "radarr",
          sourceName: "Home Radarr",
          errors: [],
          warnings: ["Quality rules need review before they become a Media Plan."],
          summary: { createCount: 1, skipCount: 0, unsupportedCount: 0, titleCount: 0, monitoredCount: 0, wantedCount: 0 },
          operations: [{
            id: "indexer-1",
            targetType: "indexer",
            action: "create",
            name: "Migrated source",
            reason: "A supported search source was found.",
            canApply: true,
            warnings: ["Quality rules need review before they become a Media Plan."],
            data: { baseUrl: "https://indexer.example/api", apiKey: "[redacted]" }
          }]
        })
      });
    });

    await authenticateAndNavigate(page, "/settings/migration");
    await page.getByLabel("Exported JSON").fill('{"indexers":[]}');
    await page.getByRole("button", { name: "Preview import" }).click();

    await expect(page.getByText("Preview ready. Review every create, skip and warning before applying.")).toBeVisible();
    await expect(page.getByRole("heading", { name: "Change report" })).toBeVisible();
    // Field values live in the row drawer now, not on the list — the change
    // report is a list -> drawer surface. Open the row to check the secret
    // came back redacted.
    await page.getByText("Migrated source", { exact: true }).click();
    await expect(page.getByText("[redacted]", { exact: true })).toBeVisible();
    await expect(page.getByText("Quality rules need review before they become a Media Plan.")).toBeVisible();
    await expect(page.getByText("secret", { exact: true })).not.toBeVisible();
  });

  test("validates saved imported connections without exposing credentials", async ({ page }) => {
    const report = {
      valid: true,
      sourceKind: "radarr",
      sourceName: "Home Radarr",
      errors: [],
      warnings: [],
      summary: { createCount: 1, skipCount: 0, unsupportedCount: 0, titleCount: 0, monitoredCount: 0, wantedCount: 0 },
      operations: [{ id: "indexer-1", category: "source", targetType: "indexer", action: "create", name: "Migrated source", reason: "A supported search source was found.", canApply: true, warnings: [], data: { baseUrl: "https://indexer.example/api", apiKey: "[redacted]" } }]
    };
    await page.route("**/api/migration/preview", async (route) => route.fulfill({ contentType: "application/json", body: JSON.stringify(report) }));
    await page.route("**/api/migration/apply", async (route) => route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ report, applied: [{ operationId: "indexer-1", targetType: "indexer", name: "Migrated source", createdId: "source-1", result: "created" }], auditReportId: "audit-1" })
    }));
    await page.route("**/api/indexers/source-1/test", async (route) => route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ healthStatus: "healthy", message: "Reached indexer.example and received a valid Torznab response.", latencyMs: 24 })
    }));

    await authenticateAndNavigate(page, "/settings/migration");
    await page.getByLabel("Exported JSON").fill('{"indexers":[]}');
    await page.getByRole("button", { name: "Preview import" }).click();
    await page.getByRole("button", { name: /Apply 1 selected/ }).click();

    await expect(page.getByRole("button", { name: "Test all" })).toBeVisible();
    await page.getByRole("button", { name: "Test all" }).click();
    // Health is a chip on the row now, with the provider message beside it.
    await expect(page.getByText("healthy", { exact: true })).toBeVisible();
    await expect(page.getByText("Reached indexer.example and received a valid Torznab response.")).toBeVisible();
    await expect(page.getByText("secret", { exact: true })).not.toBeVisible();
  });
});
