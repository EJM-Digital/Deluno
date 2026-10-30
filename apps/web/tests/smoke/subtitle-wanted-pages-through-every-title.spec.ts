import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1439: the Wanted subtitles screen used to stop at 100 titles per library and never said there were more.
// It now says how many are waiting in all and carries on page by page, on a desktop and at phone width.
const TOTAL = 230;
const row = (index: number) => ({
  kind: "Movie", mediaId: `m-${index}`, libraryId: "lib", libraryName: "Movies", title: `Film ${String(index).padStart(3, "0")}`,
  year: 2020, seasonNumber: null, episodeNumber: null, episodeTitle: null, languagesMissing: ["en"], lastAttempt: null
});

async function pagesThroughEveryTitle(page: Page, size: { width: number; height: number }) {
  await page.setViewportSize(size);
  await authenticateAndNavigate(page, "/");
  await page.route("**/api/subtitles/wanted?**", async (route) => {
    const url = new URL(route.request().url());
    const pageSize = Number(url.searchParams.get("pageSize") ?? "100");
    const offset = Number(url.searchParams.get("pageToken") ?? "0");
    const items = Array.from({ length: Math.min(pageSize, TOTAL - offset) }, (_, i) => row(offset + i));
    const next = offset + items.length < TOTAL ? String(offset + items.length) : null;
    await route.fulfill({ contentType: "application/json", body: JSON.stringify({ items, nextPageToken: next, hasMore: next !== null, totalCount: TOTAL }) });
  });

  await page.goto("/settings/subtitles-wanted");
  await expect(page.getByText(`${TOTAL} short of a language`)).toBeVisible();
  await expect(page.getByText(`Showing 100 of ${TOTAL}`)).toBeVisible();

  await page.getByRole("button", { name: "Load more" }).click();
  await expect(page.getByText(`Showing 200 of ${TOTAL}`)).toBeVisible();
  await page.getByRole("button", { name: "Load more" }).click();
  await expect(page.getByRole("button", { name: "Load more" })).toHaveCount(0);

  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
}

test("the wanted subtitles list pages through every title at desktop width", async ({ page }) => {
  await pagesThroughEveryTitle(page, { width: 1280, height: 800 });
});

test("the wanted subtitles list pages through every title at phone width", async ({ page }) => {
  await pagesThroughEveryTitle(page, { width: 390, height: 800 });
});
