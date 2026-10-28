import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Interface against the real server (measure 4, issue 1329): the Relative dates switch and the Timezone field.
 * Each test puts back what it changed.
 */

interface Settings { showRelativeDates: boolean; timezone: string | null }
const settings = async (page: Page) => (await api<Settings>(page, "GET", "/api/settings")).body;
const save = (page: Page) => page.getByRole("button", { name: "Save interface settings" });

test.describe("Interface settings", () => {
  test.setTimeout(90_000);
  let before: Settings;

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/ui");
    before = await settings(page);
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    await api(page, "PATCH" as "POST", "/api/settings", { showRelativeDates: before.showRelativeDates, timezone: before.timezone ?? "" });
  });

  test("the Relative dates switch is saved with the page and read back from the server", async ({ page }) => {
    const relative = page.getByRole("switch", { name: /Relative dates/ }).or(page.getByLabel("Relative dates")).first();
    await expect(relative).toBeChecked({ checked: before.showRelativeDates });
    await relative.click();
    await save(page).click();
    await expect.poll(async () => (await settings(page)).showRelativeDates).toBe(!before.showRelativeDates);
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByRole("switch", { name: /Relative dates/ }).or(page.getByLabel("Relative dates")).first()).toBeChecked({ checked: !before.showRelativeDates });
  });

  test("Timezone takes a typed zone, Use this browser fills this browser's zone, and the stored zone is what was shown", async ({ page }) => {
    const zone = page.getByRole("textbox", { name: "Timezone" });
    await zone.fill("Pacific/Auckland");
    await save(page).click();
    await expect.poll(async () => (await settings(page)).timezone).toBe("Pacific/Auckland");
    await zone.fill("");
    await page.getByRole("button", { name: "Use this browser" }).click();
    const browserZone = await page.evaluate(() => Intl.DateTimeFormat().resolvedOptions().timeZone);
    await expect(zone).toHaveValue(browserZone);
    await save(page).click();
    await expect.poll(async () => (await settings(page)).timezone).toBe(browserZone);
  });
});
