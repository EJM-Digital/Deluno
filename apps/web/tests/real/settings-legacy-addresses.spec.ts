import { expect, test } from "@playwright/test";
import { openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The old Settings addresses and the unknown ones, against the real server (measure 4, issue 1329). Nothing is seeded:
 * each address is opened and the address the screen settles on is read.
 */

test.describe("Old and unknown Settings addresses", () => {
  test.setTimeout(90_000);

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings");
    await untilQuiet(page);
  });

  for (const [from, to] of [["/settings/import-lists", "/settings/lists"], ["/settings/indexers", "/indexers/indexers"], ["/settings/download-clients", "/indexers/download-clients"], ["/settings/connect", "/indexers"]]) {
    test(`${from} lands on ${to}`, async ({ page }) => {
      await page.goto(from);
      await untilQuiet(page);
      await expect(page).toHaveURL(new RegExp(`${to}$`));
      await expect(page.getByText("This area could not load")).toHaveCount(0);
    });
  }

  for (const unknown of ["/settings/does-not-exist-1329", "/settings/api"]) {
    test(`${unknown} falls back to /settings and shows the Settings screen, not a stale page`, async ({ page }) => {
      await page.goto(unknown);
      await untilQuiet(page);
      await expect(page).toHaveURL(/\/settings$/);
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      await expect(page.getByRole("heading").first()).toBeVisible();
    });
  }
});
