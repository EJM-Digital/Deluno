import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1509: the sidebar's In progress entry shows its whole label, with the plain count on the label's row and the amber
// "need you" pill on its own line, at the narrowest and the widest sidebar, and one mark when collapsed.

const now = Date.now();
const entry = (id: string, status: string, progress: number) => ({
  id, clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title: `Example Film ${id}`, releaseName: `Example.Film.${id}.2024.1080p`, category: "movies",
  status, progress, speedMbps: 1, etaSeconds: 600, sizeBytes: 4.2e9, downloadedBytes: 1e9, peers: 3, indexerName: "Example Indexer",
  errorMessage: null, addedUtc: new Date(now - 3_600_000).toISOString(), sourcePath: null, libraryId: null, healthFindings: null
});
const held = (n: number) => ({
  historyId: `hold-${n}`, mediaType: "movie", id: `m${n}`, episodeId: null, title: `Held Film ${n}`, year: 2026, episodeLabel: null, libraryId: "l1",
  releaseName: `Held.Film.${n}.2026.1080p`, indexerName: "Example Indexer", indexerId: "i1", downloadUrl: "http://example.invalid/x", quality: "1080p",
  sizeBytes: 1e9, seeders: 4, reason: "Waiting for your yes", heldUtc: new Date(now - 60_000).toISOString()
});

async function serve(page: Page) {
  const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  const queue = ["a", "b", "c"].map((id) => entry(id, "downloading", 40));
  await page.route(/\/api\/download-clients\/telemetry/, (route) => route.fulfill(json({
    summary: { activeCount: 3, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 3, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    clients: [{
      clientId: "c1", clientName: "Deluge", protocol: "torrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { seedingCount: 0 }, history: [], historyTruncated: false, capturedUtc: new Date(now).toISOString(), queue
    }],
    capturedUtc: new Date(now).toISOString()
  })));
  await page.route(/\/api\/attention\/held-releases/, (route) => route.fulfill(json(Array.from({ length: 19 }, (_, n) => held(n + 1)))));
}

/** The label's box is not clipped, and neither badge overlaps it or the other. */
async function measure(page: Page) {
  return page.evaluate(() => {
    const link = Array.from(document.querySelectorAll("aside a")).find((a) => /^In progress/.test(a.textContent ?? "")) as HTMLElement;
    const label = link.querySelector("[data-nav-label]") as HTMLElement;
    const count = link.querySelector('[data-badge="count"]') as HTMLElement;
    const needs = link.querySelector('[data-badge="needs"]') as HTMLElement;
    const box = (e: HTMLElement) => e.getBoundingClientRect();
    const hit = (a: DOMRect, b: DOMRect) => a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5;
    const l = box(label), c = box(count), n = box(needs), row = box(link);
    return {
      clipped: label.scrollWidth !== label.clientWidth,
      labelText: label.textContent,
      countOverLabel: hit(l, c), needsOverLabel: hit(l, n), countOverNeeds: hit(c, n),
      insideRow: [l, c, n].every((r) => r.left >= row.left - 0.5 && r.right <= row.right + 0.5),
      countText: count.textContent, needsText: needs.textContent
    };
  });
}

test.describe("the sidebar's In progress entry stays readable (#1509)", () => {
  for (const [name, width] of [["narrowest", 1100], ["widest", 2560]] as const) {
    test(`at the ${name} expanded sidebar the label is whole and neither count overlaps it`, async ({ page, isMobile }) => {
      test.skip(isMobile, "the sidebar is a desktop surface");
      test.setTimeout(90_000);
      await page.setViewportSize({ width, height: 900 });
      await authenticateAndNavigate(page, "/");
      await serve(page);
      await page.goto("/queue");
      await expect(page.locator('aside [data-badge="needs"]')).toHaveText("19 need you", { timeout: 20_000 });
      await expect(page.locator('aside [data-badge="count"]')).toHaveText("3");
      const m = await measure(page);
      expect(m.labelText).toBe("In progress");
      expect(m.clipped).toBe(false);
      expect(m.countOverLabel).toBe(false);
      expect(m.needsOverLabel).toBe(false);
      expect(m.countOverNeeds).toBe(false);
      expect(m.insideRow).toBe(true);
    });
  }

  test("collapsed, the row has one mark", async ({ page, isMobile }) => {
    test.skip(isMobile, "the sidebar is a desktop surface");
    test.setTimeout(90_000);
    await page.setViewportSize({ width: 1400, height: 900 });
    await authenticateAndNavigate(page, "/");
    await serve(page);
    await page.goto("/queue");
    await expect(page.locator('aside [data-badge="needs"]')).toBeVisible({ timeout: 20_000 });
    await page.getByRole("button", { name: "Collapse navigation" }).click();
    const link = page.locator("aside").getByRole("link", { name: /^In progress/ });
    await expect(link).toHaveCount(1);
    await expect(link.locator("[data-badge]")).toHaveCount(1);
    await expect(link.locator("[data-badge]")).toHaveAttribute("data-badge", "needs");
  });
});
