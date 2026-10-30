import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1448: In progress shows what is happening now. Finished downloads that a client still lists (imported and seeding,
// files gone) sit in their own folded group with what Deluno will do, never counted as in flight. The sidebar number is
// the page's Needs action tile, and its tooltip says so. The Dashboard agrees that nothing is in progress.

const now = Date.now();
const entry = (id: string, releaseName: string, status: string, progress: number) => ({
  id, clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title: releaseName, releaseName, category: "movies",
  status, progress, speedMbps: 0, etaSeconds: 0, sizeBytes: 4.2e9, downloadedBytes: 4.2e9, peers: 0, indexerName: "Example Indexer",
  errorMessage: null, addedUtc: new Date(now - 3_600_000).toISOString(), sourcePath: null, libraryId: null, healthFindings: null
});
const held = (n: number) => ({
  historyId: `hold-${n}`, mediaType: "movie", id: `m${n}`, episodeId: null, title: `Held Film ${n}`, year: 2026, episodeLabel: null, libraryId: "l1",
  releaseName: `Held.Film.${n}.2026.1080p`, indexerName: "Example Indexer", indexerId: "i1", downloadUrl: "http://example.invalid/x", quality: "1080p",
  sizeBytes: 1e9, seeders: 4, reason: "Waiting for your yes", heldUtc: new Date(now - 60_000).toISOString()
});

async function serve(page: Page, queue: unknown[], heldCount: number) {
  const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  await page.route(/\/api\/download-clients\/telemetry/, (route) => route.fulfill(json({
    summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    clients: [{
      clientId: "c1", clientName: "Deluge", protocol: "torrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { seedingCount: 0 }, history: [], historyTruncated: false, capturedUtc: new Date(now).toISOString(), queue
    }],
    capturedUtc: new Date(now).toISOString()
  })));
  await page.route(/\/api\/attention\/held-releases/, (route) => route.fulfill(json(Array.from({ length: heldCount }, (_, n) => held(n + 1)))));
}

test.describe("In progress shows what is happening now (#1448)", () => {
  test("finished downloads still in a client are folded away, not in flight, and the badge equals Needs action", async ({ page, isMobile }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    await serve(page, [
      entry("a", "Seeding.Film.One.2024.1080p", "imported", 100),
      entry("b", "Seeding.Film.Two.2023.1080p", "imported", 100),
      entry("c", "Oppenheimer.2023.2160p", "filesGone", 100)
    ], 2);
    await page.goto("/queue");

    // Nothing is moving: no "in flight", and the empty state says everything has finished.
    await expect(page.getByText("Nothing in motion")).toBeVisible();
    await expect(page.getByText(/in flight/)).toHaveCount(0);

    // The finished ones are in a folded group with their count.
    await expect(page.getByText("Finished, still in the client (3)")).toBeVisible();
    await expect(page.getByText("Seeding.Film.One.2024.1080p")).toHaveCount(0);
    await page.getByRole("button", { name: "Show finished downloads" }).click();
    await expect(page.getByText("Seeding.Film.One.2024.1080p").first()).toBeVisible();
    await expect(page.getByText(/The files are gone\. Search for another copy/)).toBeVisible();
    await expect(page.getByText(/^(Seeding until|Deluno leaves it|Deluno removes it)/).first()).toBeVisible();

    // The two releases waiting for a yes are Needs action, and the sidebar says the same number with a tooltip.
    if (!isMobile) {
      const badge = page.getByTitle(/The same number as Needs action on this page/);
      await expect(badge).toHaveText("2");
      await expect(page.getByText("Needs action").first()).toBeVisible();
    }

    // The Dashboard agrees that nothing is in progress.
    await page.goto("/");
    await expect(page.getByText("nothing in progress right now").first()).toBeVisible();
  });
});
