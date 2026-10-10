import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30 live test, #1151. Weir refused a hand-off with HTTP 400. The download showed
// "Needs attention", but its drawer had no way to send it again; the fix sat in a second
// drawer further down the page, the failure was blamed on the download client, and
// it was printed three times. Now a person opens the download, reads the processor's
// reason once, and presses one "Send to Weir again". Only the REST lists are
// stubbed (the rig's clients and Weir are not in the smoke host).

const now = Date.now();
const releaseName = "Nosferatu.1922.1080p.BluRay.x264-HD4U";
const reason = "Weir refused it: the folder for this library is not set up.";
const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });

const download = {
  id: "hash-nosferatu", clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title: releaseName, releaseName,
  category: "movies", status: "processingFailed", progress: 100, speedMbps: 0, etaSeconds: 0, sizeBytes: 7e9, downloadedBytes: 7e9, peers: 0,
  indexerName: "Example Indexer", errorMessage: null, addedUtc: new Date(now - 3_600_000).toISOString(), sourcePath: "C:\Downloads\Completed\Movies\\" + releaseName,
  libraryId: null, healthFindings: null
};
const handoff = {
  id: "handoff-nosferatu", libraryId: "library-movies", mediaType: "movies", clientId: "c1", queueItemId: "hash-nosferatu", releaseName,
  sourcePath: download.sourcePath, processorName: "Weir", status: "failed", outputPath: null, importJobId: null, failureMessage: reason,
  createdUtc: new Date(now - 3_000_000).toISOString(), updatedUtc: new Date(now - 2_900_000).toISOString(), processor: null, outcome: null
};

async function serve(page: Page, posted: string[]) {
  await page.route(/\/api\/download-clients\/telemetry/, (route) => route.fulfill(json({
    summary: { activeCount: 0, queuedCount: 0, completedCount: 1, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    clients: [{
      clientId: "c1", clientName: "Deluge", protocol: "torrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { seedingCount: 0 }, history: [], historyTruncated: false, capturedUtc: new Date(now).toISOString(), queue: [download]
    }],
    capturedUtc: new Date(now).toISOString()
  })));
  await page.route("**/api/integrations/processors/handoffs?**", (route) => route.fulfill(json([handoff])));
  await page.route("**/api/integrations/processors/handoffs/*/retry", (route) => {
    posted.push(new URL(route.request().url()).pathname);
    return route.fulfill(json({ message: "Sent again." }));
  });
}

test("a failed hand-off is sent again from the download's own drawer, with the reason said once and not blamed on the client (#1151)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The drawer is the same on a phone; the desktop run proves the behaviour.");
  await authenticateAndNavigate(page, "/");
  const posted: string[] = [];
  await serve(page, posted);
  await page.goto("/queue");

  await page.getByRole("row").filter({ hasText: releaseName }).first().click();
  const drawer = page.getByRole("dialog");
  await expect(drawer).toBeVisible();

  // The processor's reason, said once, and not as the download client's failure.
  const words = (await drawer.innerText()).replace(/\s+/g, " ");
  expect(words.split(reason).length - 1, "the processor's reason is printed more than once").toBe(1);
  expect(words).not.toMatch(/Deluge[^.]{0,40}queue failed/i);

  // One send-again action, named for the processor, with a line saying what it does; no second near-identical button.
  await expect(drawer.getByRole("button", { name: "Send to Weir again" })).toHaveCount(1);
  await expect(drawer.getByRole("button", { name: /Ask the processor again/ })).toHaveCount(0);
  await expect(drawer.getByText(/Sends the finished download to Weir again/)).toBeVisible();
  await expect(drawer.getByRole("button", { name: "Import original" })).toBeVisible();
  await expect(drawer.getByRole("button", { name: "Search for another release" })).toBeVisible();

  await drawer.getByRole("button", { name: "Send to Weir again" }).click();
  await expect(page.getByText("Sent to Weir again.")).toBeVisible();
  expect(posted).toEqual([`/api/integrations/processors/handoffs/${handoff.id}/retry`]);
});

test("Activity shows a failed send to the processor as labelled fields, never raw JSON (#1151)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The drawer is the same on a phone; the desktop run proves the behaviour.");
  await authenticateAndNavigate(page, "/");
  await page.route("**/api/activity**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname !== "/api/activity" && url.pathname !== "/api/activity/") return route.fallback();
    return route.fulfill(json({
      items: [{
        id: "activity-send-failed", category: "downloads", kind: "processor.handoff.failed", title: "Could not send to the processor",
        message: `Could not send ${releaseName} to Weir.`, severity: "error", status: "failed", entityType: "movie", entityId: "movie-1",
        createdUtc: new Date(now).toISOString(), detailsJson: JSON.stringify({ DispatchId: "d-1", ReleaseName: releaseName, Processor: "Weir" })
      }],
      hasMore: false, nextPageToken: null, total: 1
    }));
  });
  await page.goto("/activity");
  const entry = page.getByText("Could not send to the processor").first();
  await expect(entry).toBeVisible();
  await entry.click();
  const drawer = page.getByRole("dialog");
  await expect(drawer).toBeVisible();
  const words = await drawer.innerText();
  expect(words).not.toMatch(/[{}]|"DispatchId"|"ReleaseName"/);
});

// #1155. Nosferatu was cleaned once, its hand-off had been refused earlier, and "Try
// again" from Transfers sent it a second time, leaving a second copy. A download that
// already has its processed file offers nothing to send again.
test("a download the processor already cleaned offers no second send (#1155)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The drawer is the same on a phone; the desktop run proves the behaviour.");
  await authenticateAndNavigate(page, "/");
  const posted: string[] = [];
  await serve(page, posted);
  // The same hand-off, now holding the file Weir made.
  await page.route("**/api/integrations/processors/handoffs?**", (route) => route.fulfill(json([
    { ...handoff, outputPath: "E:\Weir\Ready\Movies\\" + releaseName + ".mkv", failureMessage: null }
  ])));
  await page.goto("/queue");
  await page.getByRole("row").filter({ hasText: releaseName }).first().click();
  const drawer = page.getByRole("dialog");
  await expect(drawer).toBeVisible();
  await expect(drawer.getByRole("button", { name: /Send to Weir again/ })).toHaveCount(0);
  await expect(drawer.getByText(/already made the processed file/)).toBeVisible();
  expect(posted).toEqual([]);
});

// #1204. After "Send to Weir again" the Import box said "Sending it to Weir again" but the Health
// section still showed the old red failure beside it, and it came back on every refetch. While the
// resend is in flight no part of the drawer may show the old failure.
test("while a hand-off is being sent again no old failure shows beside it (#1204)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The drawer is the same on a phone; the desktop run proves the behaviour.");
  await authenticateAndNavigate(page, "/");
  const posted: string[] = [];
  await serve(page, posted);
  const oldFinding = "The old post-processing failure";
  const oldError = "The old client error message";
  await page.route(/\/api\/download-clients\/telemetry/, (route) => route.fulfill(json({
    summary: { activeCount: 0, queuedCount: 0, completedCount: 1, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    clients: [{
      clientId: "c1", clientName: "Deluge", protocol: "torrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { seedingCount: 0 }, history: [], historyTruncated: false, capturedUtc: new Date(now).toISOString(),
      queue: [{
        ...download,
        errorMessage: oldError,
        healthFindings: [{ kind: "post-processing-failed", summary: oldFinding, strikeCount: 1, candidateBlocked: false, ignoredUntilUtc: null, ruleId: null, ruleActionPreset: null, ruleStrikeLimit: null, nextStrikeUtc: null }]
      }]
    }],
    capturedUtc: new Date(now).toISOString()
  })));
  await page.route("**/api/integrations/processors/handoffs/*/retry", (route) => {
    posted.push(new URL(route.request().url()).pathname);
    return route.fulfill(json({ message: "Sent again." }));
  });
  // Once sent again, the processor is still working on it.
  await page.route("**/api/integrations/processors/handoffs?**", (route) => route.fulfill(json([
    posted.length ? { ...handoff, status: "waiting", failureMessage: null, updatedUtc: new Date().toISOString() } : handoff
  ])));
  await page.goto("/queue");
  await page.getByRole("row").filter({ hasText: releaseName }).first().click();
  const drawer = page.getByRole("dialog");
  await expect(drawer).toBeVisible();

  await drawer.getByRole("button", { name: "Send to Weir again" }).click();
  await expect(drawer.getByText(/Sending it to Weir again/)).toBeVisible();
  for (const old of [oldFinding, oldError, reason]) {
    await expect(drawer.getByText(old)).toHaveCount(0);
  }
  // Still gone after the page asks again.
  await page.waitForTimeout(1500);
  await expect(drawer.getByText(oldFinding)).toHaveCount(0);
  await expect(drawer.getByText(oldError)).toHaveCount(0);
});
