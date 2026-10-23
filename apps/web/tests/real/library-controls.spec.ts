import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The Movies screen's search, sort, status and filter controls against the real server, nothing stubbed (measure 4,
 * issue 1270). Two titles of this spec's own (named with a stamp) are made through the real API, and every check narrows
 * to them with the search box, so what other specs left in the library cannot change what is asserted.
 */

const stamp = `lc${Date.now()}`;
const alpha = `Alpha ${stamp}`;
const beta = `Beta ${stamp}`;
const ids: string[] = [];

test.describe("Movies controls", () => {
  test.describe.configure({ mode: "serial" });

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    for (const [title, monitored] of [[alpha, true], [beta, false]] as const) {
      const made = await api<{ id: string }>(page, "POST", "/api/movies/", { title, releaseYear: 2020, monitored });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      ids.push(made.body.id);
    }
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    for (const id of ids) await api(page, "DELETE", `/api/movies/${id}`);
    const views = await api<Array<{ id: string; name: string }>>(page, "GET", "/api/library-views?variant=movies");
    for (const view of views.body ?? []) if (view.name.startsWith(stamp)) await api(page, "DELETE", `/api/library-views/${view.id}`);
    await page.close();
  });

  // A menu opened inside the drawer is drawn above it but hit-tests under it, so the option is clicked with a click event.
  async function choose(page: Page, name: RegExp) {
    await page.getByRole("listbox").last().getByRole("option", { name }).first().dispatchEvent("click");
  }
  const box = (page: Page) => page.getByPlaceholder("Search movies…");
  const rowTitles = (page: Page) => page.locator("main [aria-label^='Open ']").evaluateAll((els) => els.map((el) => el.getAttribute("aria-label") ?? ""));
  async function narrow(page: Page) {
    await box(page).fill(stamp);
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${alpha}`, `Open ${beta}`]);
  }

  test("typing in search narrows the shelf through a real request and Clear empties the box again", async ({ page }) => {
    await openScreen(page, "/movies");
    await untilQuiet(page);
    const requests = recordRequests(page);
    await narrow(page);
    // The request leaves a short wait after the typing stops, and on an install holding only these titles the rows already read right before it does.
    await expect.poll(() => requests.find("GET", /^\/api\/movies\/page/).some((r) => r.search.includes(`search=${stamp}`))).toBe(true);
    requests.stop();

    await page.getByRole("button", { name: "Clear", exact: true }).click();
    await expect(box(page)).toHaveValue("");
  });

  test("the direction button turns the order round and Sort offers the orders the server can perform", async ({ page }) => {
    await openScreen(page, "/movies");
    await untilQuiet(page);
    await narrow(page);

    await page.getByRole("button", { name: /^Ascending/ }).click();
    await expect(page.getByRole("button", { name: /^Descending/ })).toBeVisible();
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${beta}`, `Open ${alpha}`]);
    await page.getByRole("button", { name: /^Descending/ }).click();
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${alpha}`, `Open ${beta}`]);

    await page.getByRole("combobox", { name: "Sort", exact: true }).click();
    const orders = page.getByRole("listbox", { name: "Sort" });
    await expect(orders).toBeVisible();
    await orders.getByRole("option", { name: /Year/ }).first().click();
    await expect(page.getByRole("combobox", { name: "Sort", exact: true })).toHaveText(/Year/);
    await expectNoErrorScreen(page);
  });

  test("the Unmonitored status chip keeps only the title nobody is watching for", async ({ page }) => {
    await openScreen(page, "/movies");
    await untilQuiet(page);
    await narrow(page);

    await page.getByRole("button", { name: /^Unmonitored/ }).click();
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${beta}`]);
    await page.getByRole("button", { name: /^All/ }).first().click();
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${alpha}`, `Open ${beta}`]);
  });

  test("the Monitoring choice in the Filter drawer narrows the same way, and Clear filter lifts a field filter", async ({ page }) => {
    await openScreen(page, "/movies");
    await untilQuiet(page);
    await narrow(page);

    await page.getByRole("button", { name: /^Filter/ }).click();
    const drawer = page.getByRole("dialog", { name: "Filter" });
    await expect(drawer).toBeVisible();
    await drawer.getByRole("combobox", { name: "Monitoring" }).click();
    await choose(page, /^Monitored/);
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${alpha}`]);

    await drawer.getByRole("combobox", { name: "Monitoring" }).click();
    await choose(page, /^Any/);
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${alpha}`, `Open ${beta}`]);

    // A field filter: pick one by searching the 54 fields, give it a value, then take it away again.
    await drawer.getByRole("button", { name: "Add a field", exact: true }).click();
    await drawer.getByPlaceholder("Search fields — codec, certification, last searched…").fill("codec");
    await drawer.getByRole("button", { name: /^Audio codec/ }).click();
    const how = drawer.getByRole("combobox", { name: /How Audio codec is compared/ });
    await expect(how).toBeVisible();
    await drawer.getByRole("textbox", { name: "Audio codec" }).fill("truehd");
    await expect.poll(() => rowTitles(page)).toEqual([]);
    await drawer.getByRole("button", { name: "Clear 1 filter", exact: true }).click();
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${alpha}`, `Open ${beta}`]);

    // Stop adding a field closes the picker without choosing.
    await drawer.getByRole("button", { name: "Add a field", exact: true }).click();
    await drawer.getByRole("button", { name: "Stop adding a field" }).click();
    await expect(drawer.getByRole("button", { name: "Add a field", exact: true })).toBeVisible();
    await drawer.getByRole("button", { name: "Close", exact: true }).first().click();
    await expect(drawer).toHaveCount(0);
  });

  test("a filter is saved under a name, comes back in one click, and Remove takes it away from the server", async ({ page }) => {
    await openScreen(page, "/movies");
    await untilQuiet(page);
    await narrow(page);
    const name = `${stamp} unwatched`;

    await page.getByRole("button", { name: /^Filter/ }).click();
    const drawer = page.getByRole("dialog", { name: "Filter" });
    await drawer.getByRole("combobox", { name: "Monitoring" }).click();
    await choose(page, /^Unmonitored/);
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${beta}`]);

    const requests = recordRequests(page);
    await drawer.getByRole("textbox", { name: "Name this filter" }).fill(name);
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect(drawer.getByText(name, { exact: true })).toBeVisible();
    requests.stop();
    const posted = requests.find("POST", /^\/api\/library-views$/);
    expect(posted).toHaveLength(1);
    expect(posted[0].body).toMatchObject({ variant: "movies", name, monitoring: "unmonitored" });
    const stored = await api<Array<{ id: string; name: string }>>(page, "GET", "/api/library-views?variant=movies");
    expect(stored.body.some((v) => v.name === name)).toBe(true);

    // Back to Any, then one click on the saved filter brings the narrowing back.
    await drawer.getByRole("combobox", { name: "Monitoring" }).click();
    await choose(page, /^Any/);
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${alpha}`, `Open ${beta}`]);
    await drawer.getByRole("button", { name: new RegExp(name) }).first().click();
    await expect.poll(() => rowTitles(page)).toEqual([`Open ${beta}`]);

    await drawer.getByRole("button", { name: "Remove", exact: true }).first().click();
    await expect(drawer.getByText(name, { exact: true })).toHaveCount(0);
    await expect.poll(async () => (await api<Array<{ name: string }>>(page, "GET", "/api/library-views?variant=movies")).body.some((v) => v.name === name)).toBe(false);
  });
});

test.describe("Movies view and library controls", () => {
  test("the View drawer switches the layout and the poster size, and Reset puts the list columns back in their order", async ({ page }) => {
    await openScreen(page, "/movies");
    await untilQuiet(page);
    await page.getByRole("button", { name: /^View/ }).click();
    const drawer = page.getByRole("dialog", { name: "View" });
    await expect(drawer).toBeVisible();

    await drawer.getByRole("button", { name: /^Large/ }).or(drawer.getByRole("button", { name: /Large|lg/i })).first().click();
    await drawer.getByRole("button", { name: /Compact list/ }).click();
    await expect(drawer.getByLabel("Current list column order")).toBeVisible();
    const original = (await drawer.getByLabel("Current list column order").innerText()).trim();
    expect(original.length).toBeGreaterThan(3);
    await drawer.getByRole("button", { name: "Reset", exact: true }).click();
    await expect(drawer.getByLabel("Current list column order")).toHaveText(original);

    await drawer.getByRole("button", { name: /Overview/ }).click();
    await drawer.getByRole("button", { name: /Poster grid/ }).click();
    await expect(drawer.getByText("Poster size")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);
    await expectNoErrorScreen(page);
  });

  test("choosing a library from the Library menu asks the server for that library only", async ({ page }) => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-libpick-"));
    const libraryName = `Pick me ${stamp}`;
    await openScreen(page, "/movies");
    const made = await api<{ id: string; name: string }>(page, "POST", "/api/libraries", { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    try {
      await page.reload();
      await untilQuiet(page);
      const requests = recordRequests(page);
      await page.getByRole("combobox", { name: "Library", exact: true }).click();
      await page.getByRole("listbox", { name: "Library" }).getByRole("option", { name: new RegExp(libraryName) }).dispatchEvent("click");
      await expect(page.getByRole("combobox", { name: "Library", exact: true })).toHaveText(new RegExp(libraryName));
      await untilQuiet(page);
      requests.stop();
      expect(requests.find("GET", /^\/api\/movies\/page/).some((r) => r.search.includes(`libraryId=${made.body.id}`))).toBe(true);
      await expect(page.getByText(alpha)).toHaveCount(0);
    } finally {
      await api(page, "DELETE", `/api/libraries/${made.body.id}`);
      fs.rmSync(folder, { recursive: true, force: true });
    }
  });
});
