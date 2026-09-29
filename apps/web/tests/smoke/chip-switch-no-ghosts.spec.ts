import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * #934 — the owner: switching the status chips on Movies/TV "is not showing all
 * the titles, or slow, or ghosts titles from other filters". The cause was a
 * 250ms debounce meant for typing in the search box applying to a chip click
 * too, so the previous filter's rows sat on screen for the whole round trip.
 *
 * This proves the fix with a real race rather than trusting timing: the
 * network response for the next filter is deliberately slowed down, and the
 * previous filter's titles must already be gone well before that response
 * can possibly have landed.
 */
test.describe("library status chips (#934)", () => {
  test("clears the previous filter's titles before the next filter's response can land", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");
    const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
    const headers = token ? { Authorization: `Bearer ${token}` } : {};

    const stamp = Date.now();
    const titleA = `Chip Ghost A ${stamp}`;
    const titleB = `Chip Ghost B ${stamp}`;
    const createdIds: string[] = [];
    for (const title of [titleA, titleB]) {
      const create = await page.request.post("/api/movies/", {
        headers,
        data: { title, releaseYear: 2024, monitored: true }
      });
      expect(create.ok(), `POST /api/movies/ failed for "${title}": ${create.status()}`).toBe(true);
      const movie = await create.json() as { id: string };
      createdIds.push(movie.id);
    }

    try {
      // Wait for the page's own list response, then for the paint. A bare 5 s
      // expect after a reload timed out when the gate ran other suites beside
      // this one (#1057): the first fetch after a reload is the slowest one.
      const listed = page.waitForResponse(
        (response) => response.url().includes("/api/movies/page") && response.request().method() === "GET",
        { timeout: 20_000 }
      );
      await page.reload();
      await listed;
      await expect(page.getByText(titleA, { exact: true })).toBeVisible({ timeout: 15_000 });
      await expect(page.getByText(titleB, { exact: true })).toBeVisible({ timeout: 15_000 });

      // Slow the next page fetch down. If the grid only clears once a
      // response lands, both titles are still on screen well past 200ms; if
      // it clears in the same paint as the click (the fix), they are gone
      // almost immediately.
      await page.route("**/api/movies/page**", async (route) => {
        await new Promise((resolve) => setTimeout(resolve, 600));
        await route.continue();
      });

      await page.getByRole("button", { name: /^Upcoming /, exact: false }).first().click();

      await expect(page.getByText(titleA, { exact: true })).toHaveCount(0, { timeout: 200 });
      await expect(page.getByText(titleB, { exact: true })).toHaveCount(0, { timeout: 200 });

      // Let the held-back response finish before removing the route: on a
      // busy machine the delayed handler could otherwise try to continue a
      // request unroute had already let through ("Route is already handled").
      await page.unrouteAll({ behavior: "wait" });
      // And the real content still arrives once the slow response lands.
      await expect(page.getByText("Nothing matches")).toBeVisible({ timeout: 5_000 });
    } finally {
      await page.unroute("**/api/movies/page**").catch(() => undefined);
      const removed = await page.request.post("/api/movies/bulk", {
        headers,
        data: { movieIds: createdIds, operation: "remove" }
      });
      expect(removed.ok(), "Fixture movies were not removed; the shared smoke database is now polluted.").toBe(true);
    }
  });
});
