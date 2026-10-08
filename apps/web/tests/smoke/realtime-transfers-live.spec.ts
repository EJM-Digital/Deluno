import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1179: Transfers changes by itself. The page joins the hub, and a download's
// progress and a sent download's stage reach the screen with no reload and no
// Refresh button. Only the two REST lists (the downloads and what was sent) are stubbed; the pushes
// are raised by the server through the Testing-only endpoints
// (Testing:EnableRealtimePushEndpoint).

const now = Date.now();
const queueItem = (id: string, releaseName: string, progress: number, speedMbps: number) => ({
  id, clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title: releaseName, releaseName, category: "movies",
  status: "downloading", progress, speedMbps, etaSeconds: 540, sizeBytes: 4.2e9, downloadedBytes: 1e9, peers: 12, indexerName: "Example Indexer",
  errorMessage: null, addedUtc: new Date(now - 20 * 60_000).toISOString(), sourcePath: null, libraryId: null, healthFindings: null
});
const sent = (id: string, releaseName: string, queueId: string) => ({
  id, libraryId: "l1", mediaType: "movies", entityType: "movie", entityId: `m-${id}`, releaseName, indexerName: "Example Indexer",
  downloadClientId: "c1", downloadClientName: "Deluge", status: "sent", notesJson: null, createdUtc: new Date(now - 3_600_000).toISOString(),
  torrentHashOrItemId: queueId, importCompletedUtc: null
});

async function serveTransfers(page: Page) {
  const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  await page.route(/\/api\/download-clients\/telemetry/, (route) => route.fulfill(json({
    summary: { activeCount: 2, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 8.5, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    clients: [{
      clientId: "c1", clientName: "Deluge", protocol: "torrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { seedingCount: 0 }, history: [], historyTruncated: false, capturedUtc: new Date(now).toISOString(),
      queue: [queueItem("q1", "Example.Film.One.2024.1080p", 61, 6.4), queueItem("q2", "Example.Film.Two.2023.1080p", 88, 2.1)]
    }],
    capturedUtc: new Date(now).toISOString()
  })));
  await page.route(/\/api\/download-dispatches/, (route) => route.fulfill(json({
    items: [sent("d1", "Example.Film.One.2024.1080p", "q1"), sent("d2", "Example.Film.Two.2023.1080p", "q2")], hasMore: false, nextPageToken: null
  })));
}

/** Calls a Testing-only endpoint as the signed-in user. */
async function server(page: Page, path: string, body?: unknown) {
  return page.evaluate(async ({ path, body }) => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch(path, {
      method: body === undefined ? "GET" : "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    return { status: response.status, json: response.status === 200 ? await response.json() : null };
  }, { path, body });
}

test.describe("Transfers is live (#1179)", () => {
  test("a download's progress and a sent download's stage change with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    await serveTransfers(page);
    let reloads = 0;
    page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) reloads += 1; });
    await page.goto("/queue");
    await expect(page.getByText(/Example.Film.One/).first()).toBeVisible();
    const navigationsAfterLoad = reloads;

    // There is nothing to press: the page keeps itself up to date.
    await expect(page.getByRole("button", { name: /^Refresh$/ })).toHaveCount(0);

    // The page joined the queue and live subjects.
    await expect.poll(async () => ((await server(page, "/api/testing/realtime/watchers")).json as { live: number; queue: number }).live, { timeout: 15_000 }).toBeGreaterThan(0);

    // Progress: Example Film One was at 61%.
    await expect(page.getByText("61%").first()).toBeVisible();
    expect((await server(page, "/api/testing/realtime/download-progress", { id: "q1", progress: 77, speedMbps: 9.5, status: "downloading" })).status).toBe(204);
    await expect(page.getByText("77%").first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("9.5 MB/s").first()).toBeVisible();

    // Stage: Example Film Two's sent download fails to grab, so Needs action stops saying all clear.
    await expect(page.getByText("all clear")).toBeVisible();
    expect((await server(page, "/api/testing/realtime/live-event", {
      eventName: "JourneyStageChanged",
      payload: { dispatchId: "d2", releaseName: "Example.Film.Two.2023.1080p", mediaType: "movies", entityType: "movie", entityId: "m-d2", libraryId: "l1", stage: "grab_failed", detail: "The client refused it", at: new Date().toISOString() }
    })).status).toBe(204);
    await expect(page.getByText("all clear")).toHaveCount(0, { timeout: 10_000 });
    await expect(page.getByText("The client refused it").first()).toBeVisible();

    expect(reloads, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });
});
