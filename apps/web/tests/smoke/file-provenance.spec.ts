import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

async function authHeaders(page: import("@playwright/test").Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/**
 * A title must be able to say where its copy came from. Deluno archives a
 * dispatch the instant its import succeeds, so the page cannot depend on that
 * row still being listed - it reads what was kept with the file, and asks for
 * archived dispatches when it shows the title's own history.
 */
test("a title says which release it took, what that matched, and where it came from", async ({ page }) => {
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);
  const title = `Provenance ${Date.now()}`;
  const create = await page.request.post("/api/movies/", {
    headers,
    data: { title, releaseYear: 2010, monitored: true }
  });
  expect(create.ok()).toBe(true);
  const movie = await create.json() as { id: string };

  await page.route(`**/api/movies/${movie.id}/copy**`, async (route) => {
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        movieId: movie.id,
        filePath: "Z:\\Movies\\Sintel (2010)\\Sintel (2010) [WEB 1080p] [NF].mkv",
        fileSizeBytes: 1048576,
        quality: "WEB 1080p",
        releaseName: "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO",
        releaseGroup: "DELUNO",
        indexerName: "Example Indexer",
        downloadClientName: "qBittorrent",
        grabbedUtc: "2026-09-16T02:00:00Z",
        importedUtc: "2026-09-16T02:30:00Z",
        source: "filesystem.import",
        formats: [{ id: "rule-nf", name: "NF", trashId: "trash-nf" }],
        planId: "plan-1",
        planVersion: "v1/def-abc",
        planHash: "abc"
      })
    });
  });

  // The archived grab the page must still ask for. Anything that drops
  // includeArchived leaves this card empty and the regression is invisible.
  await page.route("**/api/download-dispatches**", async (route) => {
    const url = new URL(route.request().url());
    if (url.searchParams.get("includeArchived") !== "true") {
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ items: [], nextPageToken: null }) });
      return;
    }

    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        items: [{
          id: "dispatch-archived-1",
          libraryId: "movie-library",
          mediaType: "movies",
          entityType: "movie",
          entityId: movie.id,
          releaseName: "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO",
          indexerName: "Example Indexer",
          downloadClientId: "client-1",
          downloadClientName: "qBittorrent",
          status: "archived",
          notesJson: null,
          createdUtc: "2026-09-16T02:00:00Z",
          grabStatus: "succeeded",
          grabAttemptedUtc: "2026-09-16T02:00:01Z",
          torrentHashOrItemId: "torrent-hash-1",
          attemptCount: 1
        }],
        nextPageToken: null
      })
    });
  });

  try {
    await page.goto(`/movies/${movie.id}`);

    await expect(page.getByText("Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO").first()).toBeVisible();
    await expect(page.getByText("Came from", { exact: true })).toBeVisible();
    await expect(page.getByText("Example Indexer · qBittorrent", { exact: true })).toBeVisible();

    await page.getByRole("radio", { name: "History", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Sent to downloads" })).toBeVisible();
    await expect(page.getByText("Example Indexer").first()).toBeVisible();
  } finally {
    await page.request.delete(`/api/movies/${movie.id}`, { headers }).catch(() => undefined);
  }
});
