import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

async function authHeaders(page: import("@playwright/test").Page) {
  const token = await page.evaluate(() => sessionStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

test("explains a no-indexer interactive search and links to indexers", async ({ page }) => {
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);
  const title = `Search reason ${Date.now()}`;
  const create = await page.request.post("/api/movies/", {
    headers,
    data: { title, releaseYear: 2024, monitored: true }
  });
  expect(create.ok()).toBe(true);
  const movie = await create.json() as { id: string };

  await page.route(`**/api/movies/${movie.id}/search**`, async (route) => {
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        outcome: "blocked",
        reason: "no_indexers",
        summary: "No enabled movie indexers are linked to this library policy.",
        candidates: []
      })
    });
  });

  try {
    await page.goto(`/movies/${movie.id}`);
    await page.getByRole("button", { name: "Choose a release", exact: true }).click();

    await expect(page.getByText("No indexers are linked to this library", { exact: true })).toBeVisible();
    const action = page.getByRole("button", { name: "Open Indexers", exact: true });
    await expect(action).toBeVisible();
    await action.click();
    await expect(page).toHaveURL(/\/indexers\/indexers$/);
  } finally {
    const cleanup = await page.request.post("/api/movies/bulk", {
      headers,
      data: { movieIds: [movie.id], operation: "remove" },
      timeout: 5_000
    });
    expect(cleanup.ok(), `POST /api/movies/bulk cleanup failed: ${cleanup.status()}`).toBe(true);
  }
});
