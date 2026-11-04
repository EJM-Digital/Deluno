import { mkdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1473. Searching Add for Kickboxer shows the whole first page of TMDb's matches (20), each as a roomy card with its synopsis; the
// matches scroll inside the dialog so the search and the settings stay in view; More matches adds the next page. The search answers
// are the gateway's recorded replies (tests/fixtures/recorded/tmdb), served for /api/metadata/search.

const recorded = (state: string) => JSON.parse(readFileSync(path.resolve(import.meta.dirname, `../../../../tests/fixtures/recorded/tmdb/2026-11-04/${state}.json`), "utf8")).exchanges[0].reply.body.results;
const shots = process.env.DELUNO_SHOT_DIR;

test("Add shows a whole page of matches with synopses, scrolls them inside the dialog, and More matches adds the next page (#1473)", async ({ page, isMobile }) => {
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/movies");
  const asked: string[] = [];
  await page.route("**/api/metadata/search?**", async (route) => {
    const url = route.request().url();
    asked.push(url);
    await route.fulfill({ json: recorded(url.includes("page=2") ? "search-movie-kickboxer-page2" : "search-movie-kickboxer-page1") });
  });

  await page.goto("/movies?add=true");
  const dialog = page.getByRole("dialog", { name: "Add movie" });
  await expect(dialog).toBeVisible();
  await dialog.getByRole("textbox", { name: "What do you want to add?" }).fill("Kickboxer");
  await dialog.getByRole("textbox", { name: "What do you want to add?" }).press("Enter");

  const cards = dialog.locator("[data-testid='add-matches'] button[title]");
  await expect(cards).toHaveCount(20);
  await expect(dialog.getByText("Kickboxer 4: The Aggressor")).toBeVisible();
  // One screen: the dialog fits the window and the matches, not the page, scroll.
  const box = await dialog.boundingBox();
  const viewport = page.viewportSize()!;
  expect(box!.height).toBeLessThanOrEqual(viewport.height);
  // On a laptop or larger the matches scroll in their own area; on a phone the whole dialog scrolls and Add stays pinned at its foot.
  const scroller = isMobile ? dialog : dialog.locator("[data-testid='add-matches']");
  expect(await scroller.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  // The settings stay reachable without scrolling past every match.
  await expect(dialog.getByRole("button", { name: /Add movie manually|Add selected movie/ })).toBeInViewport();
  // A synopsis is on the card.
  await expect(cards.first().locator("span.line-clamp-4")).toBeVisible();
  if (shots) { mkdirSync(shots, { recursive: true }); await page.screenshot({ path: path.join(shots, `add-dialog-${viewport.width}x${viewport.height}${isMobile ? "-phone" : ""}.png`) }); }
  // Fluid at every width: the same dialog on a laptop, a full-HD and a large screen still fits one screen with the matches scrolling.
  if (!isMobile) {
    for (const [width, height] of [[1366, 768], [1920, 1080], [2560, 1300]]) {
      await page.setViewportSize({ width, height });
      await expect(dialog).toBeVisible();
      expect((await dialog.boundingBox())!.height).toBeLessThanOrEqual(height);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await expect(dialog.getByRole("button", { name: /Add movie manually|Add selected movie/ })).toBeInViewport();
      if (shots) await page.screenshot({ path: path.join(shots, `add-dialog-${width}x${height}.png`) });
    }
    await page.setViewportSize(viewport);
  }

  await dialog.getByRole("button", { name: "More matches" }).scrollIntoViewIfNeeded();
  await dialog.getByRole("button", { name: "More matches" }).click();
  await expect(cards).toHaveCount(40);
  expect(asked.some((url) => url.includes("page=2"))).toBe(true);
  await dialog.getByTitle("Select Kickboxer: Retaliation").scrollIntoViewIfNeeded();
  await dialog.getByTitle("Select Kickboxer: Retaliation").click();
  await expect(dialog.getByText("Selected", { exact: true })).toBeVisible();
  if (shots) await page.screenshot({ path: path.join(shots, `add-dialog-selected-${viewport.width}${isMobile ? "-phone" : ""}.png`) });
});
