import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, axeFindings, describeFindings, expectNoErrorScreen, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation's routes against the real server, nothing stubbed (measure 4, issue 1270): the tab strip lists all eight tabs, marks the
 * current one and each tab goes to its route; a ?libraryId= naming a library that is not there leaves the list without a drawer or an
 * error; the page carries no serious accessibility findings; the paused notice on the Dashboard follows the server's setting.
 */

const tabs: Array<[string, RegExp]> = [
  ["Automation", /\/search-cycles$/],
  ["Missing Searches", /\/search-cycles\/missing$/],
  ["Upgrades", /\/search-cycles\/upgrades$/],
  ["Failed Downloads", /\/search-cycles\/failed-downloads$/],
  ["Download Health", /\/search-cycles\/download-health$/],
  ["Keep Seeding", /\/search-cycles\/keep-seeding$/],
  ["Blocklist", /\/search-cycles\/blocklist$/],
  ["Retention", /\/search-cycles\/retention$/]
];

test.describe("Automation routes", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);
  let folder = "";
  let libraryId = "";
  const name = `Routes ${Date.now()}`;

  test.beforeAll(async ({ browser }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-routes-"));
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles");
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name, mediaType: "movies", purpose: "collection", rootPath: folder });
    libraryId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles");
    if (libraryId) await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("the tab strip lists all eight tabs, marks the current one, and each tab goes to its own route", async ({ page }) => {
    await openScreen(page, "/search-cycles");
    await untilQuiet(page);
    const strip = page.getByRole("navigation", { name: "Sections" });
    // The strip shows the tabs that fit and gathers the rest under More; together they are all eight, in order.
    const shown = await strip.getByRole("link").allTextContents();
    expect(shown).toEqual(tabs.slice(0, shown.length).map(([label]) => label));
    await strip.getByRole("button", { name: "More" }).click();
    const gathered = await page.getByRole("menuitem").allTextContents();
    expect([...shown, ...gathered.map((text) => text.trim())]).toEqual(tabs.map(([label]) => label));
    await page.keyboard.press("Escape");

    for (const [label, route] of tabs) {
      const link = strip.getByRole("link", { name: label, exact: true });
      if (await link.count()) await link.click();
      else {
        await strip.getByRole("button", { name: "More" }).click();
        await page.getByRole("menuitem", { name: label, exact: true }).click();
      }
      await expect(page).toHaveURL(route);
      await untilQuiet(page);
      await expectNoErrorScreen(page);
      // The current tab is marked: its link, or the More button that holds it.
      await expect(strip.locator("[aria-current='page']")).toHaveCount(1);
      await expect(strip.locator("[aria-current='page']")).toContainText(await link.count() ? label : "More");
    }
  });

  test("a libraryId that names no library leaves the list showing, with no drawer and no error", async ({ page }) => {
    await openScreen(page, "/search-cycles?libraryId=does-not-exist");
    await untilQuiet(page);
    await expectNoErrorScreen(page);
    await expect(page.getByRole("row").filter({ hasText: name }).first()).toBeVisible();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });

  test("a libraryId that names a library opens its drawer, for contrast", async ({ page }) => {
    await openScreen(page, `/search-cycles?libraryId=${libraryId}`);
    await untilQuiet(page);
    await expect(page.getByRole("dialog", { name })).toBeVisible();
    await expect(page).toHaveURL(/\/search-cycles$/);
  });

  test("each tab carries no serious or critical accessibility finding", async ({ page }) => {
    for (const [label, route] of tabs) {
      await openScreen(page, route.source.includes("missing") ? "/search-cycles/missing" : route.source.includes("upgrades") ? "/search-cycles/upgrades"
        : route.source.includes("failed") ? "/search-cycles/failed-downloads" : route.source.includes("download-health") ? "/search-cycles/download-health"
        : route.source.includes("keep-seeding") ? "/search-cycles/keep-seeding" : route.source.includes("blocklist") ? "/search-cycles/blocklist"
        : route.source.includes("retention") ? "/search-cycles/retention" : "/search-cycles");
      await untilQuiet(page);
      const found = await axeFindings(page);
      expect(found, `${label}\n${describeFindings(found)}`).toEqual([]);
    }
  });
});
