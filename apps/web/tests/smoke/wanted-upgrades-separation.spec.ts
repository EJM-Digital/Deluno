import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("wanted and upgrades library filters", () => {
  test("opens movie wanted and upgrades as distinct library filters", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies/wanted");
    await expect(page).toHaveURL(/\/movies\?filter=missing/);
    await expect(page.getByRole("button", { name: /^Missing / })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Upgrades / })).toBeVisible();

    await page.goto("/movies/upgrades");
    await expect(page).toHaveURL(/\/movies\?filter=upgrades/);
    await expect(page.getByRole("button", { name: /^Upgrades / })).toBeVisible();
  });

  test("opens TV wanted and upgrades as distinct library filters", async ({ page }) => {
    await authenticateAndNavigate(page, "/tv/wanted");
    await expect(page).toHaveURL(/\/tv\?filter=missing/);
    await expect(page.getByRole("button", { name: /^Missing / })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Upgrades / })).toBeVisible();

    await page.goto("/tv/upgrades");
    await expect(page).toHaveURL(/\/tv\?filter=upgrades/);
    await expect(page.getByRole("button", { name: /^Upgrades / })).toBeVisible();
  });
});
