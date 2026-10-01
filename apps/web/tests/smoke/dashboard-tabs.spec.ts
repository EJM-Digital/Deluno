import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { serveLiveBusy } from "../helpers/live-busy";

// #1087: the Dashboard is two tabs, Live and System. Each fits one screen
// at 1920x1080 and 2560x1440: the page never scrolls, whatever the tab holds,
// and a tab can be linked with ?tab=. The tab numbers count only real problems,
// and the bell (the one place for problems on every screen) shows the two
// added together.

const TABS = [
  { name: "Live", query: "", panel: /^needs you/i },
  { name: "System", query: "?tab=system", panel: /^health/i }
] as const;

/** The number on a tab, or 0 when it has no badge. */
async function badge(page: Page, tab: string): Promise<number> {
  const label = await page.getByRole("tab", { name: new RegExp(`^${tab}`) }).getByLabel(/need a look|needs a look/).getAttribute("aria-label", { timeout: 1500 }).catch(() => null);
  return label ? Number(label.match(/^[0-9]+/)?.[0] ?? 0) : 0;
}

/** The bell's number, or 0 when it has no badge. */
async function bell(page: Page): Promise<number> {
  const label = await page.getByRole("button", { name: /^Notifications/ }).getAttribute("aria-label");
  return Number(label?.match(/— ([0-9]+)/)?.[1] ?? 0);
}

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
      // The "and N more" line counts as a row: it must not be cut in half either.
      const moreLines = Array.from(section.querySelectorAll<HTMLElement>("a")).filter((link) => /^and [0-9,]+ more/.test((link.textContent ?? "").trim()));
      for (const row of [...Array.from(section.querySelectorAll<HTMLElement>("li, [role='row'], [data-fit-row]")), ...moreLines]) {
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
  await page.route("**/api/dashboard/summary", (route) => route.fulfill(json(summary)));
  await page.route("**/api/download-clients/telemetry", (route) => route.fulfill(json({ summary: { activeCount: 0, queuedCount: 0, stalledCount: 30, importReadyCount: 0, waitingForProcessorCount: 0 }, clients: [{ clientId: "c1", clientName: "Deluge on MEDIA-SERVER", queue }], capturedUtc: now })));
  await page.route("**/api/movies/import-recovery*", (route) => route.fulfill(json({ openCount: 30, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 30, recentCases: cases })));
  await page.route("**/api/download-clients/orphans", (route) => route.fulfill(json(Array.from({ length: 20 }, (_, n) => ({ name: `Leftover folder ${n + 1}`, path: `/downloads/leftover-${n + 1}`, sizeBytes: 5e8 })))));
  await page.route("**/api/system/health/summary", (route) => route.fulfill(json({ problemCount: 30, unsnoozedProblemCount: 30, unknownCount: 0, suggestionCount: 0, lastRunUtc: now })));
}

test.describe("Dashboard tabs", () => {
  test.skip(({ isMobile }) => isMobile, "The one-screen rule is for a desktop window; a phone scrolls.");

  test("has two tabs, opens on Live, and deep links open each tab", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await expect(page.getByRole("tab")).toHaveCount(2);
    await expect(page.getByRole("tab", { name: /^Live/ })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("region", { name: /^needs you/i })).toBeVisible();

    for (const tab of TABS.slice(1)) {
      await page.goto(`/${tab.query}`);
      await expect(page.getByRole("tab", { name: new RegExp(`^${tab.name}`) })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByRole("tabpanel")).toBeVisible();
    }
  });

  test("the older tab links open the tab their content moved to", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    for (const [old, expected] of [["now", "Live"], ["overview", "Live"], ["library", "Live"], ["downloads", "Live"], ["setup-health", "System"], ["system", "System"]] as const) {
      await page.goto(`/?tab=${old}`);
      await expect(page.getByRole("tab", { name: new RegExp(`^${expected}`) }), `?tab=${old}`).toHaveAttribute("aria-selected", "true");
    }
  });

  test("switching tabs changes the address without a reload and keeps the page", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await expect(page.getByRole("tab", { name: /^Live/ })).toBeVisible();
    await page.evaluate(() => { (window as unknown as { __kept: boolean }).__kept = true; });

    await page.getByRole("tab", { name: /^System/ }).click();
    await expect(page).toHaveURL(/\/\?tab=system$/);
    await page.getByRole("tab", { name: /^Live/ }).click();
    await expect(page).toHaveURL(/\/$/);

    expect(await page.evaluate(() => (window as unknown as { __kept?: boolean }).__kept)).toBe(true);
  });

  test("a fresh install fits both tabs on one screen at both sizes", async ({ page }) => {
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
    await page.goto("/");
    await expect(page.getByRole("link", { name: /^and [0-9]+ more on Transfers/ }).first()).toBeVisible();
    await page.goto("/?tab=system");
    await expect(page.getByRole("link", { name: /^and [0-9]+ more/ }).first()).toBeVisible();
  });

  test("a busy Live board keeps to one screen with whole cards, and is photographed at both sizes", async ({ page }) => {
    test.setTimeout(120_000);
    await authenticateAndNavigate(page, "/");
    await serveLiveBusy(page);
    // Posters come in one request per kind; no title is asked for on its own, so a deleted title can never 404.
    const perTitle: string[] = [];
    page.on("request", (request) => {
      if (/\/api\/(movies|series)\/(?:m|s)?[0-9]+$/.test(new URL(request.url()).pathname)) perTitle.push(request.url());
    });
    for (const size of SIZES) {
      await page.setViewportSize(size);
      await page.goto("/");
      await expect(page.getByRole("region", { name: /^every title/i })).toBeVisible();
      // Today is the day's real count (the fixture holds more than the 200 a page of events could), the speed
      // peaks are labelled apart, and a failing indexer is left to Needs you instead of being listed twice.
      await expect(page.getByRole("region", { name: /^today/i }).getByText(/^[0-9,]{3,} events$/).first()).toBeVisible();
      await expect(page.getByRole("region", { name: /^speed/i }).getByText(/^Download peak/)).toBeVisible();
      await expect(page.getByRole("region", { name: /^speed/i }).getByText(/^Upload peak/)).toBeVisible();
      await expect(page.getByRole("region", { name: /^indexers/i }).getByText("NZBgeek")).toBeVisible();
      await expect(page.getByRole("region", { name: /^indexers/i }).getByText("Nyaa.si")).toHaveCount(0);
      // The indexers say what happened today, not a latency from days ago.
      await expect(page.getByRole("region", { name: /^indexers/i }).getByText(/96 queries/)).toBeVisible();
      // Latest and Today show what Deluno did for the titles, name each colour, and leave the housekeeping out.
      await expect(page.getByRole("region", { name: /^latest/i }).getByText(/Grabbed Dune Part Two/)).toBeVisible();
      await expect(page.getByRole("region", { name: /^latest/i }).getByText(/Task (started|finished|queued)/)).toHaveCount(0);
      await expect(page.getByRole("region", { name: /^latest/i }).getByText(/routine background events?, left out/)).toBeVisible();
      await expect(page.getByRole("region", { name: /^today/i }).getByText("searches", { exact: true })).toBeVisible();
      await expect(page.getByRole("region", { name: /^today/i }).getByText("routine", { exact: true })).toBeVisible();
      expect(perTitle).toEqual([]);
      await expect(page.getByRole("region", { name: /^needs you/i })).toBeVisible();
      await expect(page.getByRole("link", { name: /^Search Movies: in/ })).toBeVisible();
      // A set-up install has no guided-setup banners above the board. The checks below run with them (a fresh
      // install), then the photograph is taken without them, as it looks once set up.
      await expectFitsOneScreen(page, `live busy with setup banners ${size.width}x${size.height}`);
      await expectNoPanelCutsAnything(page, `live busy with setup banners ${size.width}x${size.height}`);
      await page.addStyleTag({ content: '[role=tabpanel] div.shrink-0.flex-col:has(> section[aria-label="Guided setup"]) { display: none !important; }' });
      await expectFitsOneScreen(page, `live busy ${size.width}x${size.height}`);
      // Every lane has its own cards, and the countdown reads by the second.
      await expect(page.getByRole("group", { name: "Downloading" }).getByText("Oppenheimer (2023)")).toBeVisible();
      await expect(page.getByRole("group", { name: "With the processor" }).getByText("Sintel (2010)")).toBeVisible();
      await expect(page.getByRole("group", { name: "In the library" }).getByText("Coherence (2013)")).toBeVisible();
      // Cards carry the library's title, never the raw release name; stopped downloads are Stuck, not Downloading; a
      // deleted title keeps its release's own name; the speed is read once; what the board shows is one line in Needs you.
      const stuck = page.getByRole("group", { name: "Stuck" });
      await expect(stuck.getByText("The Wild Robot (2024)")).toBeVisible();
      await expect(stuck.getByText("Say Yes to the Dress S04E02")).toBeVisible();
      await expect(stuck.getByRole("button", { name: /^Search again for/ }).first()).toBeVisible();
      await expect(page.getByRole("link", { name: /^www Torrenting/ })).toHaveCount(0);
      await expect(page.getByRole("group", { name: "Downloading" }).getByText("Stuck")).toHaveCount(0);
      await expect(page.getByRole("group", { name: "With the processor" }).getByRole("button", { name: /^Retry Metropolis/ })).toBeVisible();
      await expect(page.getByRole("region", { name: /^needs you/i }).getByText("7 stuck downloads")).toBeVisible();
      await expect(page.getByRole("region", { name: /^needs you/i }).getByRole("button", { name: /^Retry Metropolis/ })).toHaveCount(0);
      await expect(page.getByText("Speed now")).toHaveCount(0);
      await page.waitForTimeout(1600);
      await page.screenshot({ path: `C:/Projects/Deluno/artifacts/e2e-rig/live-busy-${size.width}.png` });
      await expectNoPanelCutsAnything(page, `live busy ${size.width}x${size.height}`);
    }
  });

  test("the tab numbers count only real problems, and the bell shows the two added together", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await serveBusyInstall(page);
    await page.goto("/");
    await expect(page.getByRole("region", { name: /^needs you/i })).toBeVisible();
    // The lists the count reads arrive after the summary; wait until the badges settle.
    await expect.poll(async () => (await badge(page, "Live")) > 0 && (await badge(page, "System")) > 0, { timeout: 15_000 }).toBe(true);
    const now = await badge(page, "Live");
    const setup = await badge(page, "System");
    await expect.poll(() => bell(page), { timeout: 15_000 }).toBe(now + setup);
    console.log(`busy fixture: Live ${now}, System ${setup}, bell ${now + setup}`);

    // The 20 leftover folders are reachable and not counted: one quiet line under Needs you.
    const quiet = page.getByRole("region", { name: /^needs you/i }).getByText(/Also worth a look/);
    await expect(quiet).toBeVisible();
    await expect(page.getByRole("link", { name: "20 leftover folders" })).toHaveAttribute("href", "/queue");

    // The left menu no longer carries a Needs a look card; the bell is the one place for problems.
    await expect(page.locator("aside").getByText("Needs a look")).toHaveCount(0);
    await page.getByRole("button", { name: /^Notifications/ }).click();
    const panel = page.getByRole("dialog");
    await expect(panel.getByRole("region", { name: "Needs you" })).toBeVisible();
    await expect(panel.getByRole("link", { name: "20 leftover folders" })).toBeVisible();
    await page.keyboard.press("Escape");

    // The same number on any other screen.
    await page.goto("/activity");
    await expect.poll(() => bell(page), { timeout: 15_000 }).toBe(now + setup);
  });

  test("a fresh install's badges add up to the bell, and none shows at zero", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await expect(page.getByRole("region", { name: /^needs you/i })).toBeVisible();
    await page.waitForTimeout(1500);
    const now = await badge(page, "Live");
    const setup = await badge(page, "System");
    expect(await bell(page)).toBe(now + setup);
    for (const [tab, count] of [["Live", now], ["System", setup]] as const) {
      if (count === 0) await expect(page.getByRole("tab", { name: new RegExp(`^${tab}`) }).getByLabel(/need a look|needs a look/)).toHaveCount(0);
    }
  });

  test.describe("with a library to look at", () => {
    let token = "";

    test.beforeAll(async ({ request }) => {
      const login = await request.post("/api/auth/login", {
        data: { username: process.env.DELUNO_E2E_USERNAME ?? "e2e-test-user", password: process.env.DELUNO_E2E_PASSWORD ?? "e2e-test-password-123" }
      });
      if (login.ok()) token = ((await login.json()) as { accessToken: string }).accessToken;
    });

    test("both tabs still fit one screen at both sizes", async ({ page, request }) => {
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
