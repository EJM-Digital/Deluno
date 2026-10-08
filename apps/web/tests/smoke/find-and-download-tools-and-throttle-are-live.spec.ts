import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1179: Find & Download follows the server on the parts the indexer-pacing test does not reach: the
// pacing message inside an open indexer, the companion tools being set up, and the installed apps
// being connected one by one. The server raises each event (through the Testing-only push endpoint);
// the one answer that differs afterwards is stood in for by a route so the test can see the screen
// take it. Nothing reloads.

const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });

async function push(page: Page, eventName: string, payload: unknown = {}) {
  const status = await page.evaluate(async ({ eventName, payload }) => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch("/api/testing/realtime/event", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify({ eventName, subject: "dashboard", payload })
    });
    return response.status;
  }, { eventName, payload });
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

test.describe("Find & Download tools and the outbound throttle are live (#1179)", () => {
  test("an open indexer's pacing message follows the outbound throttle", async ({ page, request, isMobile }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
    const headers = { Authorization: `Bearer ${token}` };
    const host = `live-throttle-${Date.now()}.example`;
    let throttle: unknown = { hosts: [] };
    await page.route(/\/api\/integrations\/outbound-throttle$/, (route) => route.fulfill(json(throttle)));
    const created = await request.post("/api/indexers", {
      headers,
      data: { name: host, protocol: "torznab", privacy: "private", baseUrl: `https://${host}`, apiKey: null, priority: 10, categories: "2000", tags: "", mediaScope: "movies", isEnabled: true }
    });
    expect(created.ok(), `POST /api/indexers failed: ${created.status()}`).toBe(true);
    const id = ((await created.json()) as { id: string }).id;
    try {
      const navigations = countReloads(page);
      await page.goto("/indexers");
      const row = page.getByRole("row").filter({ hasText: host });
      await expect(row).toBeVisible({ timeout: 15_000 });
      await watchingDashboard(page);

      // Open the indexer: its drawer says whether Deluno is holding requests back.
      await row.getByText(host, { exact: true }).first().click();
      const quiet = page.getByText("No request is waiting. Deluno will still follow this indexer's safe request interval.");
      await expect(quiet).toBeAttached();
      const afterOpen = navigations();

      throttle = { hosts: [{ host, waiting: 4, grantedCount: 12, refusedCount: 0, totalWaitedSeconds: 5, nextPermitInSeconds: 3 }] };
      await push(page, "OutboundThrottleChanged", { id: "outbound" });

      const waiting = page.getByText(`Deluno is waiting on ${host} before sending 4 requests.`);
      await (isMobile ? expect(waiting).toBeAttached({ timeout: 10_000 }) : expect(waiting).toBeVisible({ timeout: 10_000 }));
      expect(navigations(), "the page reloaded or navigated").toBe(afterOpen);
    } finally {
      await request.delete(`/api/indexers/${id}`, { headers });
    }
  });

  test("a companion tool's setup moves on the screen as the server says", async ({ page, isMobile }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const tools = (job: unknown, state: string) => ({
      availability: { available: true, reason: null },
      tools: [{ id: "qbittorrent", name: "qBittorrent", description: "Torrent client.", state, version: null, connectionName: null, job, url: null }]
    });
    let answer = tools(null, "not-installed");
    await page.route(/\/api\/companion-tools$/, (route) => route.fulfill(json(answer)));
    const navigations = countReloads(page);
    await page.goto("/indexers/companion-tools");
    const row = page.getByRole("row").filter({ hasText: "qBittorrent" });
    // A phone hides the "On this computer" column; the Status chip still says it.
    await (isMobile ? expect(row.getByText("Not installed").first()).toBeAttached() : expect(row.getByText("Not installed").first()).toBeVisible());
    const afterLoad = navigations();
    await watchingDashboard(page);

    answer = tools({ status: "running", message: "Downloading the official build" }, "not-installed");
    await push(page, "CompanionJobChanged", { id: "qbittorrent" });
    await expect(row.getByText("Setting up")).toBeVisible({ timeout: 10_000 });

    answer = tools({ status: "failed", message: "The download did not match its checksum" }, "not-installed");
    await push(page, "CompanionJobChanged", { id: "qbittorrent" });
    await expect(row.getByText("Needs you")).toBeVisible({ timeout: 10_000 });
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  });

  test("Connect all shows each installed app as connected the moment the server says so", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const app = (kind: string, displayName: string, connected: boolean) => ({
      kind, displayName, category: "download-client", installed: true, reachable: true, defaultHost: "127.0.0.1", defaultPort: 8080, connected
    });
    let apps = [app("qbittorrent", "qBittorrent", false), app("sabnzbd", "SABnzbd", false)];
    await page.route(/\/api\/system\/bundled-components$/, (route) => route.fulfill(json(apps)));
    const navigations = countReloads(page);
    await page.goto("/indexers/download-clients");
    const region = page.getByRole("region", { name: "Connect the apps you installed" });
    await expect(region.getByText("2 waiting")).toBeVisible();
    const afterLoad = navigations();
    await watchingDashboard(page);

    // The first app gets wired (as Connect all does it, one after another): the card follows with no ask.
    apps = [app("qbittorrent", "qBittorrent", true), app("sabnzbd", "SABnzbd", false)];
    await push(page, "BundledComponentChanged", { id: "qbittorrent" });
    await expect(region.getByText("1 waiting")).toBeVisible({ timeout: 10_000 });
    await expect(region.getByRole("button", { name: /^Connect qBittorrent/ })).toHaveCount(0);

    apps = [app("qbittorrent", "qBittorrent", true), app("sabnzbd", "SABnzbd", true)];
    await push(page, "BundledComponentChanged", { id: "sabnzbd" });
    await expect(region.getByRole("button", { name: /^Connect/ })).toHaveCount(0, { timeout: 10_000 });
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  });
});
