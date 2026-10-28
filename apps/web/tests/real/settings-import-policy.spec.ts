import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Import Policy against the real server (measure 4, issue 1329). The four numbers the tests change are put back
 * after each test; nothing reaches another app.
 */

interface Settings { leftoverMinAgeHours: number; libraryFreeSpaceWarnPercent: number; libraryFreeSpacePausePercent: number; minimumFreeSpaceMb: number }
const settings = async (page: Page) => (await api<Settings>(page, "GET", "/api/settings")).body;
const patch = (page: Page, body: Partial<Settings>) => api(page, "PATCH" as "POST", "/api/settings", body);
const saveButton = (page: Page) => page.getByRole("button", { name: "Save import settings" });

test.describe("Import policy", () => {
  test.setTimeout(90_000);
  let before: Settings;

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/import-policy");
    before = await settings(page);
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    const restored = await patch(page, { leftoverMinAgeHours: before.leftoverMinAgeHours, libraryFreeSpaceWarnPercent: before.libraryFreeSpaceWarnPercent, libraryFreeSpacePausePercent: before.libraryFreeSpacePausePercent, minimumFreeSpaceMb: before.minimumFreeSpaceMb });
    expect(restored.status).toBeLessThan(300);
  });

  test("Count as a leftover after takes whole hours from 1 to 8760 and the number saved is the number the server keeps", async ({ page }) => {
    const hours = page.getByLabel("Count as a leftover after");
    await expect(hours).toHaveAttribute("min", "1");
    await expect(hours).toHaveAttribute("max", "8760");
    await expect(hours).toHaveValue(String(before.leftoverMinAgeHours));
    const next = before.leftoverMinAgeHours === 5 ? 6 : 5;
    await hours.fill(String(next));
    await saveButton(page).click();
    await expect.poll(async () => (await settings(page)).leftoverMinAgeHours).toBe(next);
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByLabel("Count as a leftover after")).toHaveValue(String(next));
  });

  test("a warn or pause percentage outside 1 to 50, or a minimum free space below 0, is refused in words and nothing is sent", async ({ page }) => {
    const requests = recordRequests(page);
    const warn = page.getByLabel("Warn when a library has less than");
    const pause = page.getByLabel("Pause imports when a library has less than");
    const minimum = page.getByLabel("Minimum free space");
    const message = async (expected: string) => {
      await saveButton(page).click();
      await expect(page.getByText(expected, { exact: true }).first()).toBeVisible();
    };
    await warn.fill("60");
    await message("Choose a warn percentage between 1 and 50.");
    await warn.fill(String(before.libraryFreeSpaceWarnPercent));
    await pause.fill("0");
    await message("Choose a pause percentage between 1 and 50.");
    await pause.fill(String(before.libraryFreeSpacePausePercent));
    await minimum.fill("-5");
    await message("Choose a minimum free space of 0 MB or more.");
    requests.stop();
    expect(requests.seen.filter((r) => r.method !== "GET" && /^\/api\/settings/.test(r.path)), "a refused save sends nothing").toEqual([]);
    expect(await settings(page)).toMatchObject({ libraryFreeSpaceWarnPercent: before.libraryFreeSpaceWarnPercent, libraryFreeSpacePausePercent: before.libraryFreeSpacePausePercent, minimumFreeSpaceMb: before.minimumFreeSpaceMb });
  });

  test("leaving with an unsaved edit asks first, and a setting changed elsewhere shows up on its own while nothing here is edited", async ({ page }) => {
    const minimum = page.getByLabel("Minimum free space");
    const elsewhere = before.minimumFreeSpaceMb + 111;
    await patch(page, { minimumFreeSpaceMb: elsewhere });
    await expect(minimum).toHaveValue(String(elsewhere));

    await minimum.fill(String(elsewhere + 1));
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    const prompt = page.getByRole("dialog", { name: "Unsaved changes" });
    await expect(prompt).toBeVisible();
    await prompt.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page).toHaveURL(/\/settings\/import-policy/);
    await expect(minimum).toHaveValue(String(elsewhere + 1));
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    await prompt.getByRole("button", { name: "Discard and continue", exact: true }).click();
    await expect(page).not.toHaveURL(/import-policy/);
    expect((await settings(page)).minimumFreeSpaceMb).toBe(elsewhere);
  });
});
