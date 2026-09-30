import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1082: the Dashboard is four tabs (Overview, Library, Downloads, System).
// Every tab fits one screen at 1920x1080 and 2560x1440: the page never
// scrolls, whatever the tab holds, and a tab can be linked with ?tab=.

const TABS = [
  { name: "Overview", query: "", panel: /^needs you$/i },
  { name: "Library", query: "?tab=library", panel: /^wanted$/i },
  { name: "Downloads", query: "?tab=downloads", panel: null },
  { name: "System", query: "?tab=system", panel: null }
] as const;

const SIZES = [
  { width: 1920, height: 1080 },
  { width: 2560, height: 1440 }
] as const;

async function fit(page: Page) {
  return page.evaluate(() => ({
    scrollHeight: document.documentElement.scrollHeight,
    innerHeight: window.innerHeight,
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: window.innerWidth
  }));
}

async function expectFitsOneScreen(page: Page, label: string) {
  // Let the tab's own lists arrive: a late list must not push the page down.
  await page.waitForTimeout(900);
  const measured = await fit(page);
  console.log(`${label}: scrollHeight ${measured.scrollHeight} / innerHeight ${measured.innerHeight}`);
  expect(measured.scrollHeight, `${label} scrolls the page`).toBeLessThanOrEqual(measured.innerHeight + 1);
  // The tab really fills the space under the header (a collapsed panel would pass the check above trivially).
  const box = await page.getByRole("tabpanel").boundingBox();
  expect(box?.height ?? 0, `${label} panel is collapsed`).toBeGreaterThan(400);
  expect((box?.y ?? 0) + (box?.height ?? 0), `${label} panel runs past the window`).toBeLessThanOrEqual(measured.innerHeight + 1);
  expect(measured.scrollWidth, `${label} scrolls sideways`).toBeLessThanOrEqual(measured.innerWidth + 1);
}

/**
 * No panel cuts a row in half or hides content without saying so: every row
 * inside a panel sits wholly inside the panel's visible box, and nothing a panel
 * clips has anything left over (#1082).
 */
async function expectNoPanelCutsAnything(page: Page, label: string) {
  await page.waitForTimeout(600);
  const problems = await page.evaluate(() => {
    const found: string[] = [];
    const panel = document.querySelector('[role="tabpanel"]');
    if (!panel) return ["no tab panel"];
    for (const section of Array.from(panel.querySelectorAll("section[aria-labelledby]"))) {
      const name = section.querySelector("h2")?.textContent ?? "a panel";
      const box = section.getBoundingClientRect();
      for (const row of Array.from(section.querySelectorAll<HTMLElement>("li, [role='row'], [data-fit-row]"))) {
        const rect = row.getBoundingClientRect();
        if (rect.height === 0 || getComputedStyle(row).visibility === "hidden") continue;
        if (rect.top < box.top - 0.5 || rect.bottom > box.bottom + 0.5) {
          found.push(`${name}: a row (${(row.textContent ?? "").slice(0, 30)}) is partly outside its panel (${Math.round(rect.top)}-${Math.round(rect.bottom)} against ${Math.round(box.top)}-${Math.round(box.bottom)})`);
        }
      }
      for (const element of [section as HTMLElement, ...Array.from(section.querySelectorAll<HTMLElement>("*"))]) {
        const style = getComputedStyle(element);
        if ((style.overflowY === "hidden" || style.overflowY === "clip") && element.clientHeight > 2 && element.scrollHeight > element.clientHeight + 1) {
          found.push(`${name}: something is hidden inside it (${element.tagName.toLowerCase()} is ${element.scrollHeight}px tall in ${element.clientHeight}px)`);
        }
      }
    }
    return found;
  });
  expect(problems, `${label}: a panel cuts something off`).toEqual([]);
}

/** A busy install: far more rows than any panel can show, all served from stored lists. */
async function serveBusyInstall(page: Page) {
  const now = new Date().toISOString();
  const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  const clients = [1, 2, 3, 4, 5].map((n) => ({ id: `c${n}`, name: `Download client number ${n} on a machine`, protocol: "torrent", reachable: n !== 2, paused: n === 3, version: "1.0", warnings: n === 4 ? 2 : 0, freeSpaceBytes: 2e12, healthStatus: n === 2 ? "unreachable" : "healthy", active: 3, queued: 1 }));
  const library = { total: 20, monitored: 20, unmonitored: 0, missing: 2, upgradable: 2, qualityMet: 16, upcoming: 0, downloading: 0, airing: 0, fileBytes: 1e11 };
  const mix = (labels: string[]) => labels.map((label, index) => ({ label, count: 9 - index }));
  const summary = {
    asOfUtc: now, buildMilliseconds: 5, unavailableSections: [],
    library: {
      asOfUtc: now, movies: library, shows: library, totalBytes: 2e11, libraries: [],
      mix: {
        movieQuality: mix(["Bluray 1080p", "WEB 1080p", "WEB 720p", "DVD", "Unknown"]),
        movieCodec: mix(["H.264", "H.265", "AV1"]),
        episodeQuality: mix(["WEB 1080p", "WEB 720p", "HDTV", "Unknown"]),
        showCodec: mix(["H.264", "H.265"])
      },
      collections: { followed: 3, monitored: 2, members: 20, inLibrary: 12, syncFailing: 0, lastSyncedUtc: now }
    },
    downloads: { asOfUtc: now, hasReading: true, readingUtc: now, active: 0, queued: 0, stalled: 30, processing: 0, importReady: 0, waitingForProcessor: 0, downloadMbps: 0, uploadMbps: 0, clients, sharingHolds: 0, sharingExtraBytes: 0, leftovers: { scanned: true, count: 20, bytes: 1e10, scannedUtc: now }, failedDownloads: 0 },
    pipeline: { asOfUtc: now, today: { grabbed: 0, detected: 0, imported: 0, failed: 0 }, totals: { grabbed: 0, detected: 0, imported: 0, failedGrabs: 0, failedImports: 0, activeDispatches: 0 }, averageGrabToDetectionSeconds: null, averageDetectionToImportSeconds: null },
    imports: { asOfUtc: now, recoveryOpen: 30, recoveryByKind: {}, failedImportJobs: 0, failedProcessorHandoffs: 0 },
    automation: {
      asOfUtc: now, autoStartJobs: true,
      queuePause: { enabled: false, threshold: 0, moviesPaused: false, tvPaused: false, moviesWaiting: 0, tvWaiting: 0, unreadable: false, unreadableReason: null },
      libraries: [1, 2, 3, 4, 5, 6].map((n) => ({ libraryId: `l${n}`, name: `Library ${n}`, mediaType: "movies", status: "active", nextSearchUtc: now, nextMissingSearchUtc: null, nextUpgradeSearchUtc: null, lastCompletedUtc: now, lastError: null })),
      lastSearchCycle: null,
      tasks: Array.from({ length: 24 }, (_, n) => ({ key: `t${n}`, name: `Scheduled task number ${n + 1}`, lastResult: "completed", lastCompletedUtc: now, nextRunUtc: now }))
    },
    indexers: { asOfUtc: now, total: 14, healthy: 11, failing: 3, rateLimited: 0, disabled: 0, queriesToday: 40, failedQueriesToday: 3, grabsToday: 6, topFailing: [] },
    processors: { asOfUtc: now, total: 1, healthy: 1, failing: 0, handoffsToday: 6, waiting: 0 },
    retention: { asOfUtc: now, leavingSoon: 0, nextLeaving: [], readyToRemove: 0, removedLast7Days: 0, removedBytesLast7Days: 0 },
    subtitles: { asOfUtc: now, wanted: 0, held: 0 },
    requests: { asOfUtc: now, seerrServers: 0, seerrHealthy: 0, seerrFailing: 0, acquisitionsLast7Days: {}, importLists: 0, importListsByLastSync: {}, listTitlesAddedLast7Days: 0 },
    blocklist: { asOfUtc: now, count: 48 },
    health: {
      asOfUtc: now, problems: 30, unknown: 0, suggestions: 0, lastRunUtc: now,
      topProblems: [1, 2, 3, 4, 5].map((n) => ({ id: `health-${n}`, title: `Health problem number ${n} with a long title`, category: "system", message: "A sentence that says what is wrong and is long enough to need the room it is given.", sinceUtc: null, link: "/system", snoozed: false }))
    },
    storage: { asOfUtc: now, readingUtc: now, volumes: [1, 2, 3, 4, 5, 6, 7].map((n) => ({ id: `v${n}`, label: `Drive ${n}`, roles: ["library"], paths: [`/media/${n}`], available: true, totalBytes: 8e12, freeBytes: 4e12 })) },
    system: { asOfUtc: now, version: "1.0.0-rc.23", updateAvailable: false, latestVersion: null, uptimeSeconds: 3600, machine: { capturedUtc: now, cpuPercent: 4, memoryBytes: 9e8, memoryPercent: 3 }, jobs: { running: 1, queued: 0, failed: 0 }, lanes: [], lastBackupUtc: null, nextBackupUtc: null, backupsEnabled: false, internetOutage: false, outageSinceUtc: null, notificationsUnread: 0 },
    activity: {
      asOfUtc: now, windowHours: 24, searchesRun: 1, downloadsStarted: 0, importsCompleted: 0, cleanupRemovals: 0, retentionRemovals: 0,
      recent: Array.from({ length: 12 }, (_, n) => ({ id: `a${n}`, category: "task.finished", label: "Task finished", message: `Something Deluno did, number ${n + 1}.`, relatedEntityType: null, relatedEntityId: null, createdUtc: now }))
    }
  };
  const queue = Array.from({ length: 30 }, (_, n) => ({ id: `q${n}`, clientId: "c1", clientName: "Deluge on MEDIA-SERVER", protocol: "torrent", mediaType: "movie", title: `Stalled download number ${n + 1} (2010)`, releaseName: "Release.Name.1080p", category: "movies", status: "stalled", progress: 100, speedMbps: 0, etaSeconds: 0, sizeBytes: 1e9, downloadedBytes: 1e9, peers: 0, indexerName: "x", errorMessage: "Missing or invalid torrent data", addedUtc: now, sourcePath: null, libraryId: null, healthFindings: null }));
  const cases = Array.from({ length: 30 }, (_, n) => ({ id: `r${n}`, title: `Recovery case number ${n + 1}`, failureKind: "importFailed", summary: "Import failed: release refused", recommendedAction: "retry", detailsJson: null, detectedUtc: now }));
  const wanted = Array.from({ length: 25 }, (_, n) => ({ movieId: `m${n}`, title: `Wanted movie number ${n + 1}`, releaseYear: 2000 + n, imdbId: null, libraryId: "l1", wantedStatus: "missing", wantedReason: "Deluno is still looking for this movie.", hasFile: false, currentQuality: null, targetQuality: "WEB 1080p", qualityCutoffMet: false, missingSinceUtc: null, lastSearchUtc: now, nextEligibleSearchUtc: now, lastSearchResult: null, updatedUtc: now }));
  await page.route("**/api/dashboard/summary", (route) => route.fulfill(json(summary)));
  await page.route("**/api/download-clients/telemetry", (route) => route.fulfill(json({ summary: { activeCount: 0, queuedCount: 0, stalledCount: 30, importReadyCount: 0, waitingForProcessorCount: 0 }, clients: [{ clientId: "c1", clientName: "Deluge on MEDIA-SERVER", queue }], capturedUtc: now })));
  await page.route("**/api/movies/import-recovery*", (route) => route.fulfill(json({ openCount: 30, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 30, recentCases: cases })));
  await page.route("**/api/movies/wanted", (route) => route.fulfill(json({ totalWanted: 25, missingCount: 25, upgradeCount: 0, coveredCount: 0, upcomingCount: 0, recentItems: wanted })));
  await page.route("**/api/system/health/summary", (route) => route.fulfill(json({ problemCount: 30, unsnoozedProblemCount: 30, unknownCount: 0, suggestionCount: 0, lastRunUtc: now })));
}

test.describe("Dashboard tabs", () => {
  test.skip(({ isMobile }) => isMobile, "The one-screen rule is for a desktop window; a phone scrolls.");

  test("opens on the Overview and deep links open each tab", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await expect(page.getByRole("tab", { name: /^Overview/ })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("region", { name: /^needs you$/i })).toBeVisible();

    for (const tab of TABS.slice(1)) {
      await page.goto(`/${tab.query}`);
      await expect(page.getByRole("tab", { name: new RegExp(`^${tab.name}`) })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByRole("tabpanel")).toBeVisible();
    }
  });

  test("switching tabs changes the address without a reload and keeps the page", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await expect(page.getByRole("tab", { name: /^Overview/ })).toBeVisible();
    await page.evaluate(() => { (window as unknown as { __kept: boolean }).__kept = true; });

    await page.getByRole("tab", { name: /^Library/ }).click();
    await expect(page).toHaveURL(/\/\?tab=library$/);
    await page.getByRole("tab", { name: /^System/ }).click();
    await expect(page).toHaveURL(/\/\?tab=system$/);
    await page.getByRole("tab", { name: /^Overview/ }).click();
    await expect(page).toHaveURL(/\/$/);

    expect(await page.evaluate(() => (window as unknown as { __kept?: boolean }).__kept)).toBe(true);
  });

  test("a fresh install fits every tab on one screen at both sizes", async ({ page }) => {
    await authenticateAndNavigate(page, "/");
    for (const size of SIZES) {
      await page.setViewportSize(size);
      for (const tab of TABS) {
        await page.goto(`/${tab.query}`);
        await expect(page.getByRole("tab", { name: new RegExp(`^${tab.name}`) })).toHaveAttribute("aria-selected", "true");
        await expectFitsOneScreen(page, `fresh ${tab.name} ${size.width}x${size.height}`);
      }
    }
  });

  test("with far more rows than fit, no panel cuts a row or hides anything at both sizes", async ({ page }) => {
    test.setTimeout(120_000);
    await authenticateAndNavigate(page, "/");
    await serveBusyInstall(page);
    for (const size of SIZES) {
      await page.setViewportSize(size);
      for (const tab of TABS) {
        await page.goto(`/${tab.query}`);
        await expect(page.getByRole("tab", { name: new RegExp(`^${tab.name}`) })).toHaveAttribute("aria-selected", "true");
        await expectFitsOneScreen(page, `busy ${tab.name} ${size.width}x${size.height}`);
        await expectNoPanelCutsAnything(page, `busy ${tab.name} ${size.width}x${size.height}`);
      }
    }
    // The rest are named, with a way to them, rather than dropped.
    await page.goto("/?tab=downloads");
    await expect(page.getByRole("link", { name: /^and [0-9]+ more on Transfers/ }).first()).toBeVisible();
    await page.goto("/?tab=system");
    await expect(page.getByRole("link", { name: /^and [0-9]+ more/ }).first()).toBeVisible();
  });

  test.describe("with a library to look at", () => {
    let token = "";

    test.beforeAll(async ({ request }) => {
      const login = await request.post("/api/auth/login", {
        data: { username: process.env.DELUNO_E2E_USERNAME ?? "e2e-test-user", password: process.env.DELUNO_E2E_PASSWORD ?? "e2e-test-password-123" }
      });
      if (login.ok()) token = ((await login.json()) as { accessToken: string }).accessToken;
    });

    test("every tab still fits one screen at both sizes", async ({ page, request }) => {
      test.setTimeout(120_000);
      await authenticateAndNavigate(page, "/");
      token = (await page.evaluate(() => localStorage.getItem("deluno-auth-token"))) ?? token;
      const headers = { Authorization: `Bearer ${token}` };
      const seeded = await request.post("/api/testing/movies/seed-large-library", { headers, data: { count: 300 }, timeout: 60_000 });
      expect(seeded.ok(), `Seeding failed: HTTP ${seeded.status()}`).toBe(true);
      try {
        for (const size of SIZES) {
          await page.setViewportSize(size);
          for (const tab of TABS) {
            await page.goto(`/${tab.query}`);
            await expect(page.getByRole("tab", { name: new RegExp(`^${tab.name}`) })).toHaveAttribute("aria-selected", "true");
            if (tab.panel) await expect(page.getByRole("region", { name: tab.panel })).toBeVisible();
            await expectFitsOneScreen(page, `seeded ${tab.name} ${size.width}x${size.height}`);
          }
        }
      } finally {
        await request.post("/api/testing/movies/unseed-large-library", { headers, timeout: 60_000 });
      }
    });
  });
});
