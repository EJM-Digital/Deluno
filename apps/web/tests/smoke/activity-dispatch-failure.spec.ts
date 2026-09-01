import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test("Activity explains a failed download handoff with retry and trace attribution", async ({ page }) => {
  await authenticateAndNavigate(page, "/");

  await page.route("**/api/download-dispatches**", async (route) => {
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        items: [{
          id: "dispatch-trace-42",
          libraryId: "movie-library",
          mediaType: "movie",
          entityType: "movie",
          entityId: "movie-42",
          releaseName: "Example.Movie.2026.2160p.WEB-DL",
          indexerName: "Example Indexer",
          downloadClientId: "client-1",
          downloadClientName: "qBittorrent",
          status: "failed",
          notesJson: null,
          createdUtc: "2026-09-01T02:00:00Z",
          grabStatus: "failed",
          grabAttemptedUtc: "2026-09-01T02:00:01Z",
          torrentHashOrItemId: "torrent-hash-42",
          attemptCount: 3,
          nextRetryEligibleUtc: "2026-09-01T02:15:00Z",
          failure: {
            serviceType: "downloadClient",
            serviceId: "client-1",
            serviceName: "qBittorrent",
            operation: "download.dispatch",
            kind: "Unavailable",
            retryState: "RetryScheduled",
            message: "The download client refused the connection.",
            code: "connection_refused",
            httpStatus: null,
            upstreamDetail: "No connection could be made because the target machine refused it.",
            externalId: "torrent-hash-42",
            retryAfterUtc: "2026-09-01T02:15:00Z",
            attempts: 3,
            isTransient: true,
            legacyCategory: "connectivity",
            summary: "qBittorrent could not be reached while Deluno sent the release.",
            nextAction: "Check qBittorrent and its network address. Deluno will retry automatically."
          }
        }],
        nextPageToken: null
      })
    });
  });

  await page.goto("/activity");
  await page.getByText("Example.Movie.2026.2160p.WEB-DL", { exact: true }).click();

  await expect(page.getByRole("heading", { name: "Why it failed" })).toBeVisible();
  await expect(page.getByText("qBittorrent could not be reached while Deluno sent the release.", { exact: true })).toBeVisible();
  await expect(page.getByText("Retry scheduled", { exact: true })).toBeVisible();
  await expect(page.getByText("Check qBittorrent and its network address. Deluno will retry automatically.", { exact: true })).toBeVisible();
  await expect(page.getByText(/Next eligible attempt:/)).toBeVisible();
  await expect(page.getByText("dispatch-trace-42", { exact: true })).toBeVisible();
  await expect(page.getByText("torrent-hash-42", { exact: true })).toBeVisible();
});
