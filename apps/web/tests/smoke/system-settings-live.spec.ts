import { expect as baseExpect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// Cause of the flakes (#1191): the default 5 s wait for a heading or a control is shorter than a cold render takes on a busy machine.
const expect = baseExpect.configure({ timeout: 15_000 });

// #1179: System, Find & Download and the search schedule change by themselves. Each test opens the
// screen, then the server raises the event (through the Testing-only push endpoint, or by a real
// change made over the API) and the screen shows the new answer with no reload and no timer.

const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });

/** Raises an event on the "dashboard" (default) or "live" subject, as the signed-in user. */
async function push(page: Page, eventName: string, payload: unknown = {}, subject: "dashboard" | "live" = "dashboard") {
  const status = await page.evaluate(async ({ eventName, payload, subject }) => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch("/api/testing/realtime/event", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify({ eventName, subject, payload })
    });
    return response.status;
  }, { eventName, payload, subject });
  expect(status).toBe(204);
}

async function watchingDashboard(page: Page) {
  await expect.poll(async () => page.evaluate(async () => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch("/api/testing/realtime/watchers", { headers: { Authorization: `Bearer ${token}` } });
    return ((await response.json()) as { dashboard: number }).dashboard;
  }), { timeout: 15_000, message: "the page never joined the dashboard subject" }).toBeGreaterThan(0);
}

function countReloads(page: Page) {
  let navigations = 0;
  page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) navigations += 1; });
  return () => navigations;
}

test.describe("System, Find & Download and the search schedule are live (#1179)", () => {
  test("a new backup appears on System without a reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    let backups: unknown[] = [];
    await page.route(/\/api\/backups(\?.*)?$/, (route) => route.request().method() === "GET" ? route.fulfill(json(backups)) : route.continue());
    const navigations = countReloads(page);
    await page.goto("/system/backups");
    await expect(page.getByRole("heading", { name: "No backups yet" })).toBeVisible();
    const afterLoad = navigations();
    await watchingDashboard(page);

    backups = [{ id: "backup-1", fileName: "deluno-probe.zip", fullPath: "/data/backups/deluno-probe.zip", sizeBytes: 2048, createdUtc: new Date().toISOString(), reason: "manual" }];
    await push(page, "BackupChanged", { id: "backups" });

    await expect(page.getByRole("heading", { name: "1 backup" })).toBeVisible({ timeout: 10_000 });
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  });

  test("a provider that stops answering turns red on System when a health change is pushed", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const client = (healthStatus: string) => [{
      id: "live-health-client",
      name: "Live health qBittorrent",
      protocol: "qbittorrent",
      host: "192.168.1.142",
      port: 8080,
      username: null,
      endpointUrl: "http://192.168.1.142:8080",
      moviesCategory: "movies",
      tvCategory: "tv",
      categoryTemplate: null,
      priority: 1,
      isEnabled: true,
      healthStatus,
      lastHealthMessage: null,
      lastHealthLatencyMs: 20,
      lastHealthTestUtc: new Date().toISOString(),
      createdUtc: "2026-09-01T04:00:00Z",
      updatedUtc: "2026-09-01T05:00:00Z"
    }];
    let clients = client("healthy");
    await page.route(/\/api\/download-clients(\?.*)?$/, (route) => route.request().method() === "GET" ? route.fulfill(json(clients)) : route.continue());
    const navigations = countReloads(page);
    await page.goto("/system");
    const row = page.getByRole("row").filter({ hasText: "Live health qBittorrent" });
    await expect(row.getByText("healthy", { exact: true })).toBeVisible();
    const afterLoad = navigations();
    await watchingDashboard(page);

    clients = client("unreachable");
    await push(page, "HealthChanged", { source: "download-client", status: "unreachable", message: "No answer" });

    await expect(row.getByText("unreachable", { exact: true })).toBeVisible({ timeout: 10_000 });
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  });

  test("a new indexer shows on Find & Download, and the pacing figure follows the server", async ({ page, request, isMobile }) => {
    // A phone lists each indexer by name; its state and pacing are columns it hides, so there the check is that the
    // value changed on the page, not that it is on screen.
    const shown = (locator: ReturnType<Page["getByText"]>, timeout = 15_000) => isMobile
      ? expect(locator).toBeAttached({ timeout })
      : expect(locator).toBeVisible({ timeout });
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    let throttle: unknown = { hosts: [] };
    await page.route(/\/api\/integrations\/outbound-throttle$/, (route) => route.fulfill(json(throttle)));
    const navigations = countReloads(page);
    await page.goto("/indexers");
    await expect(page.getByRole("heading", { name: /Indexers|Search sources/i }).first()).toBeVisible();
    const afterLoad = navigations();
    await watchingDashboard(page);

    // A real change made over the API (as another tab or an import would): the open page picks it up.
    const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
    // Deluno names an indexer after its address when it is not given one.
    const host = `live-probe-${Date.now()}.example`;
    const name = host;
    const created = await request.post("/api/indexers", {
      headers: { Authorization: `Bearer ${token}` },
      data: { name, protocol: "torznab", privacy: "private", baseUrl: `https://${host}`, apiKey: null, priority: 10, categories: "2000", tags: "", mediaScope: "movies", isEnabled: true }
    });
    expect(created.ok(), `POST /api/indexers failed: ${created.status()}`).toBe(true);
    const id = ((await created.json()) as { id: string }).id;
    try {
      const row = page.getByRole("row").filter({ hasText: name });
      await expect(row).toBeVisible({ timeout: 10_000 });
      await shown(row.getByText("Ready", { exact: true }));

      throttle = { hosts: [{ host, waiting: 3, grantedCount: 10, refusedCount: 0, totalWaitedSeconds: 4, nextPermitInSeconds: 2 }] };
      await push(page, "OutboundThrottleChanged", { id: "outbound" });
      await shown(row.getByText("3 requests waiting"), 15_000);
      expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
    } finally {
      await request.delete(`/api/indexers/${id}`, { headers: { Authorization: `Bearer ${token}` } });
    }
  });

  test("a finished search cycle shows on the search schedule without a reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const cycle = (id: string, libraryName: string) => ({
      id, libraryId: "probe-library", libraryName, mediaType: "movies", triggerKind: "scheduled", status: "completed",
      plannedCount: 5, queuedCount: 2, skippedCount: 3, notesJson: null, startedUtc: new Date(Date.now() - 60_000).toISOString(), completedUtc: new Date().toISOString()
    });
    let cycles = [cycle("cycle-1", "Probe Movies One")];
    await page.route(/\/api\/search-cycles(\?.*)?$/, (route) => route.fulfill(json({ items: cycles, totalCount: cycles.length, page: 1, pageSize: 50 })));
    const navigations = countReloads(page);
    await page.goto("/search-cycles");
    await expect(page.getByText("Probe Movies One")).toBeVisible();
    const afterLoad = navigations();
    await watchingDashboard(page);

    cycles = [cycle("cycle-2", "Probe Movies Two"), ...cycles];
    await push(page, "SearchRunCompleted", { libraryId: "probe-library", libraryName: "Probe Movies Two", mediaType: "movies", plannedCount: 5, queuedCount: 2, skippedCount: 3, completedUtc: new Date().toISOString() });

    await expect(page.getByText("Probe Movies Two")).toBeVisible({ timeout: 10_000 });
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  });
  test("a tag made elsewhere appears on the Tags page without a reload", async ({ page, request }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const navigations = countReloads(page);
    await page.goto("/settings/tags");
    await expect(page.getByRole("heading", { name: /^\d+ tags?$/ })).toBeVisible();
    const before = await page.getByRole("heading", { name: /^\d+ tags?$/ }).innerText();
    const afterLoad = navigations();
    await watchingDashboard(page);

    // Saved over the API, as another tab would: the server announces the Tags area changed.
    const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
    const name = `live-tag-${Date.now()}`;
    const created = await request.post("/api/tags", {
      headers: { Authorization: `Bearer ${token}` },
      data: { name, color: "slate", description: "" }
    });
    expect(created.ok(), `POST /api/tags failed: ${created.status()}`).toBe(true);
    const id = ((await created.json()) as { id: string }).id;
    try {
      await expect(page.getByText(name).first()).toBeVisible({ timeout: 10_000 });
      await expect(page.getByRole("heading", { name: before, exact: true })).toHaveCount(0);
      expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
    } finally {
      await request.delete(`/api/tags/${id}`, { headers: { Authorization: `Bearer ${token}` } });
    }
  });
});
