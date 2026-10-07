import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1156: the card-wide Open button sat above the hover icons, so every click on
// Search or Refresh opened the title instead. A real click must reach the icon.
test("clicking a poster's Search icon runs the search and does not open the title", async ({ page }) => {
  await authenticateAndNavigate(page, "/movies");
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  const title = `Poster Click Check ${Date.now()}`;
  const create = await page.request.post("/api/movies/", { headers, data: { title, releaseYear: 2024, monitored: true } });
  expect(create.ok(), `POST /api/movies/ failed: ${create.status()}`).toBe(true);
  const movie = await create.json() as { id: string };

  try {
    await page.reload();
    const open = page.getByRole("button", { name: `Open ${title}` });
    await expect(open).toBeAttached({ timeout: 15_000 });

    const urlBefore = page.url();
    const searchRequest = page.waitForRequest((request) =>
      request.method() === "POST" && request.url().includes(`/api/movies/${movie.id}/search`));

    await open.hover({ force: true });
    await page.getByRole("button", { name: `Search for ${title} now` }).click();
    await searchRequest;

    expect(page.url()).toBe(urlBefore);

    // The rest of the poster still opens the title.
    await open.click({ position: { x: 20, y: 20 } });
    await expect(page).not.toHaveURL(urlBefore);
  } finally {
    const removed = await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movie.id], operation: "remove" } });
    expect(removed.ok(), "The fixture film was not removed; the shared smoke database is now polluted.").toBe(true);
  }
});
