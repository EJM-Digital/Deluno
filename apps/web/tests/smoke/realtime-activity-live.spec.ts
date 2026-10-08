import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1179: Activity, the bell and the outage banner change by themselves. Each test raises
// the event from the server through the Testing-only push endpoints
// (Testing:EnableRealtimePushEndpoint) and checks the screen follows with no reload and no
// timer: the page is never navigated again, and a pushed Activity row is not read back from
// the server (it came in the push). Pairs with the unit tests in lib/activity-live-patch.test.ts,
// components/shell/notifications-bell-live.test.tsx and components/app/outage-banner.test.tsx.

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

/** Counts navigations of the main frame, so a test can prove nothing reloaded. */
function countNavigations(page: Page) {
  const state = { count: 0 };
  page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) state.count += 1; });
  return state;
}

/** Counts the times the page asks for the Activity list, so a test can prove a pushed row was not read back. */
function countActivityReads(page: Page) {
  const state = { count: 0 };
  page.on("request", (request) => { if (/\/api\/activity\?/.test(request.url())) state.count += 1; });
  return state;
}

test.describe("Activity, the bell and the outage banner are live (#1179)", () => {
  test("a new event appears on Activity as it happens, with no reload and no second read", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/activity");
    const navigations = countNavigations(page);
    await expect(page.getByRole("button", { name: "Refresh" })).toHaveCount(0);
    await expect.poll(async () => (await watchers(page)).activity, { timeout: 15_000, message: "Activity never joined its subject" }).toBeGreaterThan(0);
    await expect.poll(async () => (await watchers(page)).live, { timeout: 15_000, message: "Activity never joined live" }).toBeGreaterThan(0);
    const reads = countActivityReads(page);
    const navigationsAfterLoad = navigations.count;

    const message = `Smoke: found a better copy of Activity Probe ${Date.now()}`;
    expect((await server(page, "/api/testing/realtime/activity", {
      message, category: "download.import.completed", severity: "success", title: "Activity Probe", relatedEntityType: "movie", relatedEntityId: "probe-1"
    })).status).toBe(204);

    await expect(page.getByText(message).first()).toBeVisible({ timeout: 10_000 });
    expect(reads.count, "the pushed row was read back from the server").toBe(0);
    expect(navigations.count, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });

  test("a view filtered by title takes only the events that belong in it", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/activity?title=Probe%20Film");
    await expect.poll(async () => (await watchers(page)).activity, { timeout: 15_000 }).toBeGreaterThan(0);

    const stamp = Date.now();
    const other = `Smoke: unrelated event ${stamp}`;
    const mine = `Smoke: event for the probe film ${stamp}`;
    await server(page, "/api/testing/realtime/activity", { message: other, category: "download.import.completed", title: "Another Title" });
    await server(page, "/api/testing/realtime/activity", { message: mine, category: "download.import.completed", title: "Probe Film" });

    await expect(page.getByText(mine).first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(other)).toHaveCount(0);
  });

  test("the bell's Recent list takes a pushed event whether the panel is open or closed", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/system");
    const navigations = countNavigations(page);
    await expect.poll(async () => (await watchers(page)).activity, { timeout: 15_000 }).toBeGreaterThan(0);
    const bell = page.getByRole("button", { name: /^Notifications/ });
    const navigationsAfterLoad = navigations.count;

    // Opened once so the list is read, then closed.
    await bell.click();
    await expect(page.getByRole("dialog", { name: /Notifications|Needs you|Recent/i }).or(page.getByRole("dialog")).first()).toBeVisible();
    await bell.click();
    const reads = countActivityReads(page);

    const whileClosed = `Smoke: imported while the bell was closed ${Date.now()}`;
    await server(page, "/api/testing/realtime/activity", { message: whileClosed, category: "filesystem.import.completed", severity: "success" });
    await expect.poll(async () => {
      if (await page.getByText(whileClosed).count() === 0) {
        // The text only exists once the panel is open.
        await bell.click();
        const found = await page.getByText(whileClosed).count();
        await bell.click();
        return found;
      }
      return 1;
    }, { timeout: 10_000 }).toBeGreaterThan(0);

    await bell.click();
    const whileOpen = `Smoke: imported while the bell was open ${Date.now()}`;
    await server(page, "/api/testing/realtime/activity", { message: whileOpen, category: "filesystem.import.completed", severity: "success" });
    await expect(page.getByText(whileOpen)).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(whileClosed)).toBeVisible();

    expect(reads.count, "the bell read the Activity list again").toBe(0);
    expect(navigations.count, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });

  test("the bell's count changes when the server says something that needs a person changed", async ({ page }) => {
    test.setTimeout(90_000);
    // A failed job not about a title is one row (cleared on System); one about a title is a second row (cleared on Activity).
    const failedJob = (n: number) => ({
      id: `probe-failed-${n}`, jobType: "movies.metadata.refresh", label: "Refresh film details", source: "scheduler", sourceLabel: "The schedule", status: "failed",
      payloadJson: "{}", attempts: 3, maxAttempts: 3, createdUtc: "2026-10-08T00:00:00Z", scheduledUtc: "2026-10-08T00:00:00Z", startedUtc: "2026-10-08T00:00:00Z",
      completedUtc: "2026-10-08T00:01:00Z", leasedUntilUtc: null, workerId: null, lastError: "The provider did not answer.", relatedEntityType: n === 0 ? null : "movie",
      relatedEntityId: n === 0 ? null : `probe-movie-${n}`, idempotencyKey: null, dedupeKey: null, lastAttemptUtc: null, nextAttemptUtc: null
    });
    let failing = 1;
    await page.route("**/api/jobs?**", async (route) => {
      const failedOnly = route.request().url().includes("failed=true");
      const items = failedOnly ? Array.from({ length: failing }, (_, n) => failedJob(n)) : [];
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ items, hasMore: false, nextPageToken: null }) });
    });

    await authenticateAndNavigate(page, "/system");
    const navigations = countNavigations(page);
    const bell = page.getByRole("button", { name: /^Notifications/ });
    await expect(bell).toHaveAccessibleName(/1 thing needs a look/, { timeout: 20_000 });
    await expect.poll(async () => (await watchers(page)).dashboard, { timeout: 15_000 }).toBeGreaterThan(0);
    const navigationsAfterLoad = navigations.count;

    // A job about a title fails too; the server announces it and the bell follows.
    failing = 2;
    expect((await server(page, "/api/testing/realtime/event", { name: "AttentionChanged", subject: "dashboard", payload: { reason: "recovery" } })).status).toBe(204);

    await expect(bell).toHaveAccessibleName(/2 things need a look/, { timeout: 15_000 });
    expect(navigations.count, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });

  test("the outage banner follows the server's push in both directions", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/system");
    const navigations = countNavigations(page);
    await expect.poll(async () => (await watchers(page)).dashboard, { timeout: 15_000 }).toBeGreaterThan(0);
    const navigationsAfterLoad = navigations.count;
    const banner = page.getByRole("status").filter({ hasText: /Internet down/ });

    const now = new Date().toISOString();
    expect((await server(page, "/api/testing/realtime/event", {
      name: "ConnectivityChanged", subject: "dashboard", payload: { isOutage: true, sinceUtc: now, checkedUtc: now, message: "Internet down" }
    })).status).toBe(204);
    await expect(banner).toBeVisible({ timeout: 10_000 });
    await expect(banner).toContainText("cleanup and searching paused");

    expect((await server(page, "/api/testing/realtime/event", {
      name: "ConnectivityChanged", subject: "dashboard", payload: { isOutage: false, sinceUtc: null, checkedUtc: now, message: "Connected." }
    })).status).toBe(204);
    await expect(banner).toHaveCount(0, { timeout: 10_000 });
    expect(navigations.count, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });
});
