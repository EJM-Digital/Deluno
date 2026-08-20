import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("System settings save footers", () => {
  test("keeps backup schedule edits in the shared footer until saved or discarded", async ({ page }) => {
    await authenticateAndNavigate(page, "/system/backups");

    const retention = page.locator('input[type="number"]');
    const originalRetention = await retention.inputValue();
    const changedRetention = originalRetention === "100" ? "99" : String(Number(originalRetention || "7") + 1);
    await retention.fill(changedRetention);

    await expect(page.getByText("Unsaved changes", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Save backup schedule" })).toBeVisible();
    await page.locator('a[href="/system/updates"]').first().click();
    const discardDialog = page.getByRole("dialog", { name: "Discard unsaved changes?" });
    await expect(discardDialog).toBeVisible();
    await discardDialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page).toHaveURL(/\/system\/backups$/);
    await page.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(retention).toHaveValue(originalRetention);

    await page.locator('a[href="/system/updates"]').first().click();
    await expect(page).toHaveURL(/\/system\/updates$/);
  });

  test("keeps update preference edits in the shared footer until saved or discarded", async ({ page }) => {
    await authenticateAndNavigate(page, "/system/updates");

    const updateMode = page.locator("select");
    const originalMode = await updateMode.inputValue();
    const changedMode = originalMode === "notify-only" ? "download-background" : "notify-only";
    await updateMode.selectOption(changedMode);

    await expect(page.getByText("Unsaved changes", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Save update preferences" })).toBeVisible();
    await page.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(updateMode).toHaveValue(originalMode);
  });
});
