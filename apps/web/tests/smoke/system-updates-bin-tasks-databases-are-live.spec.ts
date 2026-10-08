import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1179: the System screens that nothing else covered follow the server by themselves: update status
// and the update behaviour, the recycle bin, the scheduled tasks and the databases. Each test opens the
// screen, then the server raises the event (through the Testing-only push endpoint, or by a real change
// over the API) and the screen shows the new answer with no reload and no timer.

const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

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

const updateStatus = (over: Record<string, unknown> = {}) => ({
  currentVersion: "1.0.0-probe", channel: "stable", installKind: "manual", behaviorMode: "notify-only", isInstalled: false,
  canCheck: false, canDownload: false, canApply: false, updateAvailable: false, latestVersion: null, state: "not-supported",
  progressPercent: null, restartRequired: false, lastCheckedUtc: null, lastDownloadedUtc: null, message: "Nothing to update here.",
  lastError: null, notes: [], currentImageRef: null, currentImageDigest: null, changelog: null, appliedUpdates: [], ...over
});

test.describe("Updates, the recycle bin, scheduled tasks and the databases are live (#1179)", () => {
  test("the update status follows a change with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    let status = updateStatus();
    await page.route(/\/api\/updates\/status$/, (route) => route.fulfill(json(status)));
    const navigations = countReloads(page);
    await page.goto("/system/updates");
    await expect(page.getByText("Nothing to update here.")).toBeVisible();
    const afterLoad = navigations();
    await watchingDashboard(page);

    status = updateStatus({ updateAvailable: true, latestVersion: "9.9.9-probe", message: "A newer build is ready to download." });
    await push(page, "UpdateStatusChanged", { id: "updates" });

    await expect(page.getByText("A newer build is ready to download.")).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("9.9.9-probe").first()).toBeVisible();
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  });

  test("the update behaviour saved elsewhere shows in the open form with no reload", async ({ page, request }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const navigations = countReloads(page);
    await page.goto("/system/updates");
    const behaviour = page.getByRole("combobox", { name: "Update behaviour" });
    await expect(behaviour).toBeVisible();
    await expect(behaviour).toHaveValue("notify-only");
    const afterLoad = navigations();
    await watchingDashboard(page);

    // Saved over the API, as another tab would: the server announces the update settings changed.
    const headers = await authHeaders(page);
    try {
      const saved = await request.put("/api/updates/preferences", { headers, data: { mode: "download-background" } });
      expect(saved.ok(), `PUT /api/updates/preferences failed: ${saved.status()}`).toBe(true);
      await expect(behaviour).toHaveValue("download-background", { timeout: 10_000 });
      expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
    } finally {
      await request.put("/api/updates/preferences", { headers, data: { mode: "notify-only" } });
    }
  });

  test("the recycle bin follows a change to its settings and its contents with no reload", async ({ page, request }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const stamp = Date.now();
    const trashed = { id: "bin-probe", originalPath: `/media/movies/Probe ${stamp}/probe.mkv`, recyclePath: `/data/recycle/probe-${stamp}.mkv`, sizeBytes: 1024, reason: "removed", recycledUtc: new Date().toISOString(), expiresUtc: null, title: `Binned probe ${stamp}` };
    let items: unknown[] = [];
    await page.route(/\/api\/recycle-bin$/, (route) => route.request().method() === "GET" ? route.fulfill(json(items)) : route.continue());
    const navigations = countReloads(page);
    await page.goto("/system/recycle-bin");
    const retention = page.getByRole("spinbutton", { name: "Retention days" });
    await expect(retention).toBeVisible();
    const original = await retention.inputValue();
    const afterLoad = navigations();
    await watchingDashboard(page);

    const headers = await authHeaders(page);
    try {
      // A real change over the API (as another tab would): the open form takes the new value.
      const changed = original === "45" ? 46 : 45;
      const saved = await request.put("/api/recycle-bin/settings", { headers, data: { retentionDays: changed, maxSizeMb: 102400 } });
      expect(saved.ok(), `PUT /api/recycle-bin/settings failed: ${saved.status()}`).toBe(true);
      await expect(retention).toHaveValue(String(changed), { timeout: 10_000 });

      // A file going into the bin: the list reads again when the server says the bin changed.
      items = [trashed];
      await push(page, "RecycleBinChanged", { id: "recycle-bin" });
      await expect(page.getByText(`Binned probe ${stamp}`).or(page.getByText(`Probe ${stamp}`)).first()).toBeAttached({ timeout: 10_000 });
      expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
    } finally {
      await request.put("/api/recycle-bin/settings", { headers, data: { retentionDays: Number(original), maxSizeMb: 102400 } });
    }
  });

  test("a scheduled task that just ran shows its new result with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const task = (result: string) => ({
      key: "library.file.check", name: "Check library files", description: "Looks at the files Deluno holds.", intervalSeconds: 21600, isConfigurable: true,
      lastStartedUtc: result === "not-run" ? null : new Date().toISOString(), lastCompletedUtc: result === "not-run" ? null : new Date().toISOString(),
      lastResult: result, lastError: null, lastDurationMs: result === "not-run" ? null : 120, nextRunUtc: null
    });
    let current = task("not-run");
    await page.route(/\/api\/system\/tasks$/, (route) => route.fulfill(json([current])));
    const navigations = countReloads(page);
    await page.goto("/system");
    const row = page.getByRole("row").filter({ hasText: "Check library files" });
    await expect(row.getByText("not-run", { exact: true })).toBeVisible();
    const afterLoad = navigations();
    await watchingDashboard(page);

    current = task("completed");
    await push(page, "SystemTaskChanged", { id: "library.file.check" });

    await expect(row.getByText("completed", { exact: true })).toBeVisible({ timeout: 10_000 });
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  });

  test("the databases card follows a change to their size and checks with no reload", async ({ page, isMobile }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const databases = (total: number, checked: string | null) => ({
      databases: [{ key: "platform", name: "Settings and connections", sizeBytes: total }],
      totalBytes: total, integrityCheckedUtc: checked, integrityOk: checked ? true : null, integrityNote: null, compactedUtc: null
    });
    let current = databases(2 * 1024 * 1024, null);
    await page.route(/\/api\/system\/databases$/, (route) => route.fulfill(json(current)));
    const navigations = countReloads(page);
    await page.goto("/?tab=system");
    const shown = (text: RegExp) => isMobile ? expect(page.getByText(text).first()).toBeAttached({ timeout: 10_000 }) : expect(page.getByText(text).first()).toBeVisible({ timeout: 10_000 });
    await shown(/Databases · 2(\.0)? MB/);
    await shown(/integrity not checked yet/);
    const afterLoad = navigations();
    await watchingDashboard(page);

    current = databases(6 * 1024 * 1024, new Date().toISOString());
    await push(page, "SystemDatabasesChanged", { id: "databases" });

    await shown(/Databases · 6(\.0)? MB/);
    await shown(/integrity checked just now/);
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  });
});
