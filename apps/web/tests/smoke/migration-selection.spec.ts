import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("migration selection", () => {
  test("lets a user select individual safe operations after a non-mutating preview", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/migration");

    await page.getByRole("button", { name: "Load example" }).click();
    await page.getByRole("button", { name: "Preview import" }).click();

    await expect(page.getByRole("heading", { name: "Change report" })).toBeVisible();
    const indexerSelection = page.getByRole("switch", { name: "Apply Existing Indexer" });
    await expect(indexerSelection).toBeChecked();

    await indexerSelection.click();
    await expect(indexerSelection).not.toBeChecked();
    await expect(page.getByRole("button", { name: /Apply selected changes \(/ })).toBeVisible();
  });
});
