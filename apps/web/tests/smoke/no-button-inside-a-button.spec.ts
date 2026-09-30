import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// A button inside a button is invalid HTML, and a screen reader reads the pair
// as one control. Each library poster was a button round the whole artwork with
// Search and Refresh buttons inside it (found on the live dev copy, 1 Oct 2026).
test("a library poster has no button inside another button", async ({ page }) => {
  await authenticateAndNavigate(page, "/movies");
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  const title = `Nested Button Check ${Date.now()}`;
  const create = await page.request.post("/api/movies/", { headers, data: { title, releaseYear: 2024, monitored: true } });
  expect(create.ok(), `POST /api/movies/ failed: ${create.status()}`).toBe(true);
  const movie = await create.json() as { id: string };

  try {
    await page.reload();
    await expect(page.getByText(title, { exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(page.locator("button button, a button, button a")).toHaveCount(0);
    await expect(page.getByRole("button", { name: `Open ${title}` })).toBeAttached();
    await expect(page.getByRole("button", { name: `Search for ${title} now` })).toBeAttached();
  } finally {
    const removed = await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movie.id], operation: "remove" } });
    expect(removed.ok(), "The fixture film was not removed; the shared smoke database is now polluted.").toBe(true);
  }
});
