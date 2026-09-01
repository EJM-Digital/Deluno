import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

async function authHeaders(page: import("@playwright/test").Page) {
  const token = await page.evaluate(() => sessionStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

test.describe("library removal", () => {
  test("removes a selected movie from Deluno without presenting it as file or client cleanup", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");
    const headers = await authHeaders(page);
    const title = `Removal selection ${Date.now()}`;
    const create = await page.request.post("/api/movies/", {
      headers,
      data: { title, releaseYear: 2024, monitored: true }
    });
    expect(create.ok()).toBe(true);

    await page.reload();
    await page.getByPlaceholder("Search movies…").fill(title);
    await expect(page.getByText(title, { exact: true })).toBeVisible();
    await page.getByRole("button", { name: `Select ${title}`, exact: true }).click();
    await page.getByRole("button", { name: "Remove", exact: true }).click();

    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("Imported files and download clients are left alone.");
    await expect(dialog).toContainText("No files will be deleted by this bulk action.");
    await dialog.getByRole("button", { name: "Remove movie", exact: true }).click();

    await expect(page.getByText(title, { exact: true })).toHaveCount(0);
    const list = await page.request.get(`/api/movies/page?search=${encodeURIComponent(title)}`, { headers });
    const movies = await list.json() as { items: Array<{ title: string }> };
    expect(movies.items.some((movie) => movie.title === title)).toBe(false);
  });

  test("offers deliberate removal choices and removes a movie and TV show from their detail pages", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");
    const headers = await authHeaders(page);
    const movieTitle = `Removal detail movie ${Date.now()}`;
    const showTitle = `Removal detail show ${Date.now()}`;

    const movieCreate = await page.request.post("/api/movies/", {
      headers,
      data: { title: movieTitle, releaseYear: 2024, monitored: true }
    });
    const movie = await movieCreate.json() as { id: string };
    const seriesCreate = await page.request.post("/api/series/", {
      headers,
      data: { title: showTitle, startYear: 2024, monitored: true }
    });
    const series = await seriesCreate.json() as { id: string };

    await page.goto(`/movies/${movie.id}`);
    await page.getByRole("button", { name: "Remove from Deluno", exact: true }).click();
    const movieDialog = page.getByRole("dialog");
    await expect(movieDialog).toContainText("Prevent automatic re-add");
    await expect(movieDialog).toContainText("Delete imported files from disk");
    await expect(movieDialog).toContainText("Your download client is never changed here");
    await movieDialog.getByRole("button", { name: "Remove movie", exact: true }).click();
    await expect(page).toHaveURL(/\/movies$/);
    expect((await page.request.get(`/api/movies/${movie.id}`, { headers })).status()).toBe(404);

    await page.goto(`/tv/${series.id}`);
    await page.getByRole("button", { name: "Remove from Deluno", exact: true }).click();
    const seriesDialog = page.getByRole("dialog");
    await expect(seriesDialog).toContainText("Prevent automatic re-add");
    await expect(seriesDialog).toContainText("Delete imported files from disk");
    await seriesDialog.getByRole("button", { name: "Remove TV show", exact: true }).click();
    await expect(page).toHaveURL(/\/tv$/);
    expect((await page.request.get(`/api/series/${series.id}`, { headers })).status()).toBe(404);
  });

  test("reports mixed bulk removal results without leaving the valid title managed", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");
    const headers = await authHeaders(page);
    const title = `Removal mixed result ${Date.now()}`;
    const create = await page.request.post("/api/movies/", {
      headers,
      data: { title, releaseYear: 2024, monitored: true }
    });
    const movie = await create.json() as { id: string };

    const removal = await page.request.post("/api/movies/bulk", {
      headers,
      data: { movieIds: [movie.id, "missing-movie-id"], operation: "remove" }
    });
    expect(removal.ok()).toBe(true);
    const result = await removal.json() as { successCount: number; failureCount: number; results: Array<{ movieId: string; succeeded: boolean }> };
    expect(result.successCount).toBe(1);
    expect(result.failureCount).toBe(1);
    expect(result.results).toEqual(expect.arrayContaining([
      expect.objectContaining({ movieId: movie.id, succeeded: true }),
      expect.objectContaining({ movieId: "missing-movie-id", succeeded: false })
    ]));
    expect((await page.request.get(`/api/movies/${movie.id}`, { headers })).status()).toBe(404);
  });
});
