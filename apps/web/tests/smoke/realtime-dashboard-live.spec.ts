import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { serveLiveBusy } from "../helpers/live-busy";

// #1178: the Dashboard's Live tab changes by itself. The page joins the hub's
// subjects (queue, activity, live) whatever order its screens register them in,
// and a download's progress and a new activity row reach the screen with no
// reload. Pairs with the unit tests in lib/use-signalr-subscriptions.test.tsx.
// The data comes from the smoke host's real hub: only the REST lists are
// stubbed (serveLiveBusy), and the pushes are raised by the server through the
// Testing-only endpoints (Testing:EnableRealtimePushEndpoint).

interface Watchers { connections: number; dashboard: number; queue: number; activity: number; live: number }

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

async function watchers(page: Page): Promise<Watchers> {
  const result = await server(page, "/api/testing/realtime/watchers");
  expect(result.status).toBe(200);
  return result.json as Watchers;
}

test.describe("the Dashboard Live tab is live (#1178)", () => {
  test("joins the hub's subjects and changes on screen when the server pushes, with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    await serveLiveBusy(page);
    let reloads = 0;
    page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) reloads += 1; });
    await page.goto("/");
    await expect(page.getByRole("region", { name: /^pipeline/i })).toBeVisible();
    const navigationsAfterLoad = reloads;

    // The page really joined: the server counts a watcher on each subject, Live included.
    await expect.poll(async () => (await watchers(page)).live, { timeout: 15_000, message: "the page never joined the live subject" }).toBeGreaterThan(0);
    const joined = await watchers(page);
    expect(joined.queue).toBeGreaterThan(0);
    expect(joined.activity).toBeGreaterThan(0);

    // A new activity row slides in.
    const message = `Smoke: found a better copy of Realtime Probe ${Date.now()}`;
    expect((await server(page, "/api/testing/realtime/activity", { message, category: "search" })).status).toBe(204);
    await expect(page.getByText(message)).toBeVisible({ timeout: 10_000 });

    // A download's progress follows the client: Dune Part Two was at 61%.
    const before = await page.locator("body").innerText();
    expect(before).toContain("Dune");
    expect((await server(page, "/api/testing/realtime/download-progress", { id: "q1", progress: 77, speedMbps: 9.5, status: "downloading" })).status).toBe(204);
    await expect(page.getByText("77%").first()).toBeVisible({ timeout: 10_000 });

    expect(reloads, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });

  test("says so in plain words when the live connection is down, and is live again when it is back", async ({ page }) => {
    test.setTimeout(180_000);
    // The topbar badge only shows from the small breakpoint up.
    await page.setViewportSize({ width: 1600, height: 900 });
    // The smoke host serves the hub over server-sent events; keep a handle on the stream so the test can cut it.
    await page.addInitScript(() => {
      const Original = window.EventSource;
      const streams: EventSource[] = [];
      (window as unknown as { __streams: EventSource[] }).__streams = streams;
      window.EventSource = class extends Original {
        constructor(url: string | URL, init?: EventSourceInit) {
          super(url, init);
          streams.push(this);
        }
      };
    });
    await authenticateAndNavigate(page, "/");
    await serveLiveBusy(page);
    await page.goto("/");
    await expect(page.getByRole("region", { name: /^pipeline/i })).toBeVisible();
    await expect.poll(async () => (await watchers(page)).live, { timeout: 15_000 }).toBeGreaterThan(0);
    await expect(page.getByRole("status", { name: /Not live/ })).toHaveCount(0);

    // The hub goes away: the stream breaks and every new attempt is refused.
    const refuse = (route: { abort(): Promise<void> }) => route.abort();
    await page.route(/\/hubs\/deluno/, refuse);
    await page.evaluate(() => (window as unknown as { __streams: EventSource[] }).__streams.forEach((stream) => stream.dispatchEvent(new Event("error"))));
    await expect(page.getByRole("status", { name: /Not live/ })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole("status", { name: /numbers on screen may be out of date/ })).toBeVisible();

    // Back online: the page reconnects by itself, joins again, and the notice goes.
    await page.unroute(/\/hubs\/deluno/, refuse);
    await expect(page.getByRole("status", { name: /Not live/ })).toHaveCount(0, { timeout: 90_000 });
    const message = `Smoke: back online Realtime Probe ${Date.now()}`;
    expect((await server(page, "/api/testing/realtime/activity", { message, category: "search" })).status).toBe(204);
    await expect(page.getByText(message)).toBeVisible({ timeout: 10_000 });
  });
});
