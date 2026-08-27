import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// The chip reads "Upgradable" — the same word the mark on the poster uses, so
// the filter and the titles it selects say the same thing. The filter *key* is
// still `upgrades`, which is what these URLs assert.
test.describe("wanted and upgrades library filters", () => {
  test("opens movie wanted and upgrades as distinct library filters", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies/wanted");
    await expect(page).toHaveURL(/\/movies\?filter=missing/);
    await expect(page.getByRole("button", { name: /^Missing / })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Upgradable / })).toBeVisible();

    await page.goto("/movies/upgrades");
    await expect(page).toHaveURL(/\/movies\?filter=upgrades/);
    await expect(page.getByRole("button", { name: /^Upgradable / })).toBeVisible();
  });

  test("opens TV wanted and upgrades as distinct library filters", async ({ page }) => {
    await authenticateAndNavigate(page, "/tv/wanted");
    await expect(page).toHaveURL(/\/tv\?filter=missing/);
    await expect(page.getByRole("button", { name: /^Missing / })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Upgradable / })).toBeVisible();

    await page.goto("/tv/upgrades");
    await expect(page).toHaveURL(/\/tv\?filter=upgrades/);
    await expect(page.getByRole("button", { name: /^Upgradable / })).toBeVisible();
  });
});
