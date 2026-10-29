import { expect, test, type Page } from "@playwright/test";
import { api, DESKTOP, openScreen, PHONE, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * The search bar and its Filter menu (issues 1427 and 1428) against the real server. The indexer is a real Torznab address that
 * answers with what Jackett 0.24.2850 really answered, so the results are what a person sees. Nothing is stubbed in the app.
 */

const stamp = `sbar${Date.now()}`;
const indexerName = `Bar ${stamp}`;
let jackett: RecordedAppServer;
let indexerId = "";

test.describe("Search bar", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    jackett = await startRecordedAppServer([loadRecording("jackett", "0.24.2850", "caps"), loadRecording("jackett", "0.24.2850", "search-text"), loadRecording("jackett", "0.24.2850", "search-text")]);
    const page = await browser.newPage();
    await openScreen(page, "/");
    const made = await api<{ id: string }>(page, "POST", "/api/indexers", {
      name: indexerName, protocol: "torznab", baseUrl: `${jackett.baseUrl}/api/v2.0/indexers/yts/results/torznab/api`, apiKey: "0123456789abcdef0123456789abcdef",
      privacy: "public", priority: 10, categories: "2000,2040", tags: "", mediaScope: "movies", isEnabled: true
    });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    indexerId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/");
    if (indexerId) await api(page, "DELETE", `/api/indexers/${indexerId}`);
    await page.close();
    await jackett.close();
  });

  const sintel = "Sintel (2010) 720p BRRip x264 -YTS";
  const box = (page: Page) => page.getByPlaceholder("Search for a release…");

  test("there is no Search tab in the navigation, and the old address opens the search bar with its Filter button", async ({ page }) => {
    await openScreen(page, "/");
    await untilQuiet(page);
    await expect(page.getByRole("navigation").getByRole("link", { name: "Search", exact: true })).toHaveCount(0);

    await page.goto("/search");
    await untilQuiet(page);
    await expect(box(page)).toBeVisible();
    await expect(page.getByRole("button", { name: "Filter", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Search", exact: true })).toBeVisible();
    // The bar is bigger than an ordinary box (48px against the 36px of a form field).
    const height = (await box(page).boundingBox())?.height ?? 0;
    expect(height).toBeGreaterThanOrEqual(46);
  });

  test("the old address with words in it searches and shows the results", async ({ page }) => {
    await openScreen(page, "/");
    const requests = recordRequests(page);
    await page.goto("/search?q=Sintel");
    await expect(page.getByText("Results", { exact: true }).first()).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/search$/)[0].body).toMatchObject({ query: "Sintel", searchType: "movie" });
    await expect(box(page)).toHaveValue("Sintel");
    await expect(page.getByRole("row").filter({ hasText: sintel })).toHaveCount(1);
  });

  test("a search typed in the bar, narrowed from the Filter menu, sends the narrowing", async ({ page }) => {
    await openScreen(page, "/search");
    await untilQuiet(page);
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Filter", exact: true }).click();
    const menu = page.getByRole("group", { name: "Search filters" });
    await menu.getByRole("checkbox", { name: indexerName }).click();
    await expect(page.getByRole("button", { name: "Filter, 1 on" })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(menu).toHaveCount(0);

    await box(page).fill("Sintel");
    await box(page).press("Enter");
    await expect(page.getByText("Results", { exact: true }).first()).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/search$/)[0].body).toMatchObject({ query: "Sintel", indexerIds: [indexerId] });
    await expect(page.getByRole("row").filter({ hasText: sintel })).toHaveCount(1);
  });

  test("the top bar's search window offers to search every indexer for the words typed and opens those results", async ({ page }) => {
    await openScreen(page, "/");
    await untilQuiet(page);
    await page.getByRole("button", { name: /^Search & navigate \(/ }).click();
    await page.getByPlaceholder("Jump to area, run action…").fill("Sintel");
    await page.getByText("Search every indexer for “Sintel”").click();
    await expect(page).toHaveURL(/\/search\?q=Sintel$/);
    await expect(page.getByText("Results", { exact: true }).first()).toBeVisible();
  });

  for (const [label, size] of [["desktop", DESKTOP], ["tablet", { width: 768, height: 900 }], ["phone", PHONE]] as const) {
    test(`Indexers and Categories look the same and neither has a scrolling box of its own on a ${label}`, async ({ page }) => {
      await openScreen(page, "/search");
      await page.setViewportSize(size);
      await untilQuiet(page);
      await page.getByRole("button", { name: "Filter", exact: true }).click();
      const menu = page.getByRole("group", { name: "Search filters" });
      await expect(menu.getByRole("checkbox", { name: indexerName })).toBeVisible();
      await expect(menu.getByText(/^Categories/)).toBeVisible();
      await menu.getByRole("checkbox", { name: "Movies", exact: true }).first().waitFor();

      const read = await page.evaluate((name) => {
        const section = (checkboxName: string) => {
          const input = [...document.querySelectorAll<HTMLInputElement>("[role=group][aria-label='Search filters'] input[type=checkbox]")].find((el) => document.querySelector(`label[for='${el.id}']`)?.textContent?.trim() === checkboxName);
          const grid = input?.closest("div.grid") as HTMLElement | null;
          if (!grid) return null;
          const scrolls = (el: HTMLElement | null) => {
            for (let node = el; node && !node.matches("[role=group][aria-label='Search filters']"); node = node.parentElement) {
              const overflowY = getComputedStyle(node).overflowY;
              if ((overflowY === "auto" || overflowY === "scroll") && node.scrollHeight > node.clientHeight + 1) return true;
              if (overflowY === "auto" || overflowY === "scroll") return true;
            }
            return false;
          };
          const style = getComputedStyle(grid);
          return { scrolls: scrolls(grid), columns: style.gridTemplateColumns.split(" ").length, display: style.display, overflowY: style.overflowY };
        };
        return { indexers: section(name), categories: section("Movies") };
      }, indexerName);

      expect(read.indexers, "the Indexers section was found").not.toBeNull();
      expect(read.categories, "the Categories section was found").not.toBeNull();
      expect(read.indexers!.scrolls, "Indexers has no scrolling box").toBe(false);
      expect(read.categories!.scrolls, "Categories has no scrolling box").toBe(false);
      expect(read.categories!.columns, "both use the same number of columns").toBe(read.indexers!.columns);
      expect(read.categories!.display).toBe(read.indexers!.display);
      expect(read.categories!.overflowY).toBe(read.indexers!.overflowY);
    });
  }
});
