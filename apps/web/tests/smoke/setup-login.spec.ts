import { expect, test } from "@playwright/test";

test.describe("first-run and auth screens", () => {
  test("setup entry is usable whether the install is fresh or already bootstrapped", async ({ page }) => {
    await page.goto("/setup");

    const setupHeading = page.getByRole("heading", { name: "Set up Deluno" });
    const loginHeading = page.getByRole("heading", { name: "Sign in to Deluno" });

    await expect(setupHeading.or(loginHeading)).toBeVisible();

    if (await setupHeading.isVisible()) {
      await expect(page.getByLabel("Display name")).toBeVisible();
      await expect(page.getByLabel("Username")).toBeVisible();
      await expect(page.getByLabel("Password", { exact: true })).toBeVisible();
      await expect(page.getByRole("button", { name: "Create account" })).toBeDisabled();

      await page.getByLabel("Display name").fill("Test User");
      await page.getByLabel("Username").fill("test-user");
      await page.getByLabel("Password", { exact: true }).fill("password-123");
      await page.getByLabel("Confirm password").fill("different");
      await expect(page.getByRole("button", { name: "Create account" })).toBeEnabled();
    } else {
      await expect(page.getByLabel("Username")).toBeVisible();
      await expect(page.getByRole("button", { name: "Sign in" })).toBeDisabled();
    }
  });

  test("login screen exposes the expected sign-in controls", async ({ page }) => {
    await page.goto("/login");

    await expect(page.getByRole("heading", { name: "Sign in to Deluno" })).toBeVisible();
    await expect(page.getByLabel("Username")).toBeVisible();
    await expect(page.getByLabel("Password", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  });
});
