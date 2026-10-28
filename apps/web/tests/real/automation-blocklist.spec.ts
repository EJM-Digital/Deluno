import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation > Blocklist against the real server, nothing stubbed (measure 4, issue 1270): a release blocked by hand through the real
 * route is listed; a click or Enter on its row opens the drawer with the facts; the row's own buttons do not also open it; Clean up now
 * and Clean up all say what the server answered when no download client holds anything; a row the server no longer has fails its
 * cleanup in words. Every release made here is lifted again.
 */

interface Blocked { id: string; releaseName: string; indexerName: string; title: string | null }

const stamp = `bl${Date.now()}`;
const release = `Arrival.2016.2160p.${stamp}`;
const second = `Zulu.Film.2020.${stamp}`;
const blocked = async (page: Page) => (await api<Blocked[]>(page, "GET", "/api/blocked-releases")).body;
const toast = (page: Page) => page.locator("[data-sonner-toast]");
const row = (page: Page, name: string) => page.getByRole("row").filter({ hasText: name }).first();
const open = async (page: Page) => { await openScreen(page, "/search-cycles/blocklist"); await untilQuiet(page); };

test.describe("Blocklist", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/blocklist");
    for (const item of await blocked(page)) await api(page, "DELETE", `/api/blocked-releases/${item.id}`);
    await page.close();
  });

  test("a release blocked by hand is listed, and clicking its row opens a drawer with the release, title, indexer, why, who and when", async ({ page }) => {
    await open(page);
    for (const [name, title, indexer] of [[release, "Arrival", "Nebula"], [second, "Zulu Film", "Orion"]]) {
      const made = await api(page, "POST", "/api/blocked-releases", { releaseName: name, title, indexerName: indexer, mediaType: "movies" });
      expect(made.status, JSON.stringify(made.body)).toBe(200);
    }
    await page.reload();
    await untilQuiet(page);

    await row(page, release).click();
    const drawer = page.getByRole("dialog", { name: release });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText("Refused", { exact: true }).first()).toBeVisible();
    for (const [label, value] of [["Release", release], ["Title", "Arrival"], ["Found on", "Nebula"], ["Who", "You blocked it"]]) {
      await expect(drawer.getByText(label, { exact: true })).toBeVisible();
      await expect(drawer.getByText(value, { exact: true }).first()).toBeVisible();
    }
    await expect(drawer.getByText("Why", { exact: true })).toBeVisible();
    await expect(drawer.getByText("Refused", { exact: true }).nth(1)).toBeVisible();
    await expect(drawer.getByText(new Date().getFullYear().toString(), { exact: false }).first()).toBeVisible();
  });

  test("pressing Enter on a focused row opens its drawer too", async ({ page }) => {
    await open(page);
    await row(page, second).focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("dialog", { name: second })).toBeVisible();
    await expect(page.getByRole("dialog", { name: second }).getByText("Orion", { exact: true }).first()).toBeVisible();
  });

  test("the buttons in a row act on the row only: Clean up now toasts and no drawer opens", async ({ page }) => {
    await open(page);
    await row(page, release).getByRole("button", { name: "Clean up now", exact: true }).click();
    await expect(toast(page).first()).toBeVisible();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });

  test("Clean up now on a release no download client holds says there was nothing left to clear", async ({ page }) => {
    await open(page);
    const cleanup = page.waitForResponse((response) => /\/api\/blocked-releases\/[^/]+\/cleanup$/.test(response.url()));
    await row(page, release).getByRole("button", { name: "Clean up now", exact: true }).click();
    expect((await (await cleanup).json()).outcome).toBe("nothingToClear");
    await expect(toast(page).filter({ hasText: "There was nothing left to clear." })).toBeVisible();
  });

  test("Clean up all, confirmed, with no client holding anything, warns that there was nothing left and keeps the blocks", async ({ page }) => {
    await open(page);
    await page.getByRole("button", { name: "Clean up all", exact: true }).click();
    const confirm = page.getByRole("dialog");
    await expect(confirm.getByText("This clears 2 refused releases", { exact: false })).toBeVisible();
    const done = page.waitForResponse((response) => response.url().endsWith("/api/blocked-releases/cleanup-all"));
    await confirm.getByRole("button", { name: "Clean up all", exact: true }).click();
    expect(await (await done).json()).toMatchObject({ cleared: 0, waitingOnSharing: 0 });
    await expect(toast(page).filter({ hasText: "There was nothing left to clear." })).toBeVisible();
    expect((await blocked(page)).map((item) => item.releaseName).sort()).toEqual([release, second].sort());
  });

  test("a row the server no longer has fails its cleanup in words", async ({ page }) => {
    await open(page);
    const gone = (await blocked(page)).find((item) => item.releaseName === second)!;
    // Lifted from another place while this list is open: the list still shows the row.
    expect((await api(page, "DELETE", `/api/blocked-releases/${gone.id}`)).status).toBeLessThan(300);
    await row(page, second).getByRole("button", { name: "Clean up now", exact: true }).click();
    await expect(toast(page).filter({ hasText: "That could not be cleared up." })).toBeVisible();
  });
});
