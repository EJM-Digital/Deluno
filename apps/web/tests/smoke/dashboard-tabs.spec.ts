import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { serveLiveBusy } from "../helpers/live-busy";
import { serveLiveRig } from "../helpers/live-rig";

// #1087, #1098: the Dashboard is two tabs, Live and System. Each fits one screen
// at 1920x1080 and 2560x1440: the page never scrolls, whatever the tab holds,
// and a tab can be linked with ?tab=. Only System carries a number: Live shows
// what is happening, and every problem is in the bell (the one place for
// problems on every screen).

const TABS = [
  { name: "Live", query: "", panel: /^the journey/i },
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
        // A station whose cards run past its height scrolls inside itself: its cards are all there, one scroll away.
        const scroller = row.closest<HTMLElement>("[data-station]");
        if (scroller && getComputedStyle(scroller).overflowY === "auto") continue;
        // Activity is a stream that fades out at the bottom (as in the mockup) instead of ending on a whole row: the rest is one click away.
        if (row.closest("[data-testid='live-stream'], [data-testid='live-turned-down-latest']")) continue;
        if (rect.top < box.top - 0.5 || rect.bottom > box.bottom + 0.5) {
          found.push(`${name}: a row (${(row.textContent ?? "").slice(0, 30)}) is partly outside its panel (${Math.round(rect.top)}-${Math.round(rect.bottom)} against ${Math.round(box.top)}-${Math.round(box.bottom)})`);
        }
      }
      for (const element of [section as HTMLElement, ...Array.from(section.querySelectorAll<HTMLElement>("*"))]) {
        const style = getComputedStyle(element);
        // Text clamped to a few lines, with all of it in its tooltip, is a design choice and not a cut-off row.
        if (style.webkitLineClamp && style.webkitLineClamp !== "none") continue;
        if (element.getAttribute("data-testid") === "live-stream" || element.getAttribute("data-testid") === "live-turned-down-latest") continue;
        if ((style.overflowY === "hidden" || style.overflowY === "clip") && element.clientHeight > 2 && element.scrollHeight > element.clientHeight + 1) {
          found.push(`${name}: something is hidden inside it (${element.tagName.toLowerCase()} is ${element.scrollHeight}px tall in ${element.clientHeight}px)`);
        }
      }
    }
    return found;
  });
  expect(problems, `${label}: a panel cuts something off`).toEqual([]);
}

/**
 * Every full card on the Live journey is one template (#1098): all the same height with the same poster slot, and the
 * journey carries no buttons (problems are in the bell). Compact tiles are the same family and one size among themselves.
 */
async function expectCardsUniform(page: Page, label: string) {
  const report = await page.evaluate(() => {
    const visible = (card: Element) => card.getBoundingClientRect().height > 0;
    const cards = Array.from(document.querySelectorAll<HTMLElement>("[data-journey-card]:not([data-compact])")).filter(visible);
    const tiles = Array.from(document.querySelectorAll<HTMLElement>("[data-journey-card][data-compact]")).filter(visible);
    const sizeOf = (element: Element | null) => (element ? `${Math.round(element.getBoundingClientRect().width)}x${Math.round(element.getBoundingClientRect().height)}` : "none");
    return {
      count: cards.length,
      tileCount: tiles.length,
      heights: [...new Set(cards.map((card) => Math.round(card.getBoundingClientRect().height)))],
      posterSizes: [...new Set(cards.map((card) => sizeOf(card.querySelector("[data-card-poster]"))))],
      tileHeights: [...new Set(tiles.map((tile) => Math.round(tile.getBoundingClientRect().height)))],
      buttonsOnTheJourney: document.querySelectorAll("[data-journey-card] button").length
    };
  });
  console.log(`${label}: ${report.count} cards (heights ${report.heights.join("/")}), ${report.tileCount} compact tiles`);
  expect(report.heights, `${label}: cards are not all one height`).toHaveLength(report.count > 0 ? 1 : 0);
  expect(report.posterSizes, `${label}: poster slots are not all one size`).toHaveLength(report.count > 0 ? 1 : 0);
  expect(report.tileHeights, `${label}: compact tiles are not all one size`).toHaveLength(report.tileCount > 0 ? 1 : 0);
  expect(report.buttonsOnTheJourney, `${label}: the journey carries buttons`).toBe(0);
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
    await expect(page.getByRole("region", { name: /^the journey/i })).toBeVisible();
    await expect(page.getByRole("region", { name: "Working on now" })).toBeVisible();

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
    // Live carries no problems of its own: nothing on it asks to be settled.
    await expect(page.getByRole("region", { name: "Needs you" })).toHaveCount(0);
    await page.goto("/?tab=system");
    // System scrolls inside its lists (one screen, nothing cut): the rest of the tasks are one scroll away.
    const list = page.getByRole("region", { name: "Scheduled tasks" }).locator(".sy-tk-list");
    await expect(list).toBeVisible();
    expect(await list.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
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
      await expect(page.getByRole("region", { name: /^the journey/i })).toBeVisible();
      // The speed says its unit, and the indexers, clients and connections are one card: a failing indexer is a red tile that goes to System.
      await expect(page.getByRole("region", { name: /^speed/i }).getByText(/^MB\/s down/)).toBeVisible();
      const sources = page.getByRole("region", { name: "Sources" });
      await expect(sources.getByRole("link", { name: /^Nyaa\.si: not answering/ })).toHaveAttribute("href", "/?tab=system");
      // The tiles say what happened today, and where a limit is set how much of it is used; a switched-off one is grey.
      await expect(sources.getByRole("link", { name: /^NZBgeek: 46 of 100 today/ })).toBeVisible();
      await expect(sources.getByRole("link", { name: /^OldIndexer: switched off/ })).toHaveAttribute("data-tone", "idle");
      await expect(sources.getByText("14 seeding", { exact: false }).first()).toBeVisible();
      await expect(sources.getByRole("link", { name: "Weir: Processor · working on 1" })).toBeVisible();
      // The card holds every tile inside it and leaves Activity room for rows.
      const fit = await page.evaluate(() => {
        const box = (name: string) => [...document.querySelectorAll("section")].find((el) => el.querySelector("h2")?.textContent === name)?.getBoundingClientRect();
        const map = box("Sources");
        const activity = box("Activity");
        const tiles = [...document.querySelectorAll("a.ls-ix, a.ls-cl")].map((el) => el.getBoundingClientRect());
        return { mapBottom: map?.bottom ?? 0, activityTop: activity?.top ?? 0, activityHeight: activity?.height ?? 0, tilesInside: !!map && tiles.length > 0 && tiles.every((box) => box.left >= map.left - 1 && box.right <= map.right + 1 && box.bottom <= map.bottom + 1), tileHeights: [...new Set(tiles.map((box) => Math.round(box.height)))] };
      });
      expect(fit.tilesInside, "every Sources tile is inside its card").toBe(true);
      expect(fit.tileHeights.length, "every tile is the same height").toBe(1);
      expect(fit.activityTop, "Activity is below Sources").toBeGreaterThanOrEqual(fit.mapBottom - 1);
      await expect(page.getByRole("region", { name: /^turned down today/i }).getByText("Not in a language you want").first()).toBeVisible();
      expect(perTitle).toEqual([]);
      // Needs you is not on Live; the next run is in the band.
      await expect(page.getByRole("region", { name: "Needs you" })).toHaveCount(0);
      await expect(page.getByRole("region", { name: "Next" }).getByText("running")).toBeVisible();
      // A set-up install has no guided-setup banners above the board. The checks below run with them (a fresh
      // install), then the photograph is taken without them, as it looks once set up.
      await expectFitsOneScreen(page, `live busy with setup banners ${size.width}x${size.height}`);
      await expectNoPanelCutsAnything(page, `live busy with setup banners ${size.width}x${size.height}`);
      await page.addStyleTag({ content: '[role=tabpanel] div.shrink-0.flex-col:has(> section[aria-label="Guided setup"]) { display: none !important; }' });
      await expectFitsOneScreen(page, `live busy ${size.width}x${size.height}`);
      await page.screenshot({ path: `test-results/e2e-rig/live-busy-early-${size.width}.png` });
      // The one activity stream shows what Deluno did for the titles, names each colour, and leaves the housekeeping out.
      const activity = page.getByRole("region", { name: /^activity/i });
      await expect(activity.getByText(/Grabbed Dune Part Two/).first()).toBeVisible();
      await expect(activity.getByText(/Task (started|finished|queued)/)).toHaveCount(0);
      await expect(activity.getByText(/[0-9]+ routine left out/)).toBeVisible();
      await expect(page.getByRole("region", { name: /^(today|latest)/i })).toHaveCount(0);
      // Only titles that are moving are on the journey, each with its library title; a station with too many to fit ends with an and-N-more link.
      await expect(page.getByRole("link", { name: /^Oppenheimer \(2023\): 88%/ })).toBeVisible();
      await expect(page.getByRole("link", { name: /^Sintel \(2010\): Cleaner is working on it/ })).toBeVisible();
      await expect(page.getByRole("link", { name: /^Shogun: Asked for in Seerr · looking/ })).toBeVisible();
      await expect(page.getByRole("link", { name: /^Coherence \(2013\): in the library/ })).toBeVisible();
      // A stopped download is not a problem card: it stays on the journey, after the downloads that are moving, so
      // on a busy board it is one of the "and N more" (named in that link's list) or a card of its own.
      const kid = page.getByRole("link", { name: /^The Kid \(1921\): Stuck at 34% · not moving/ });
      if ((await kid.count()) === 0) await expect(page.locator("a.lj-more[title*='The Kid (1921)']")).toHaveCount(1);
      else await expect(kid).toBeVisible();
      await expect(page.getByRole("link", { name: /^www Torrenting/ })).toHaveCount(0);
      await expect(page.getByRole("group", { name: "Needs you" })).toHaveCount(0);
      // A station with more titles than its rows hold ends with "and N more" to Transfers, as the mockup does.
      await expect(page.getByRole("region", { name: /^the journey/i }).getByRole("link", { name: /^and [0-9]+ more/ }).first()).toHaveAttribute("href", "/queue");
      await expect(page.getByRole("region", { name: /^the journey/i }).getByText(/on their way to your library · all in by about/)).toBeVisible();
      // Space and standing facts are on System, not here.
      await expect(page.getByText("Drive space")).toHaveCount(0);
      await expect(page.getByRole("img", { name: /meets its quality goal/ })).toHaveCount(0);
      await expect(page.getByText("Speed now")).toHaveCount(0);
      await page.waitForTimeout(1600);
      await page.screenshot({ path: `test-results/e2e-rig/live-busy-${size.width}.png` });
      await expectNoPanelCutsAnything(page, `live busy ${size.width}x${size.height}`);
      await expectCardsUniform(page, `live busy ${size.width}x${size.height}`);
    }
  });

  test("a Live board shaped like the real rig fits both sizes and says each thing once", async ({ page }) => {
    test.setTimeout(120_000);
    await authenticateAndNavigate(page, "/");
    await serveLiveRig(page);
    for (const size of SIZES) {
      await page.setViewportSize(size);
      await page.goto("/");
      await expect(page.getByRole("region", { name: /^the journey/i })).toBeVisible();
      await page.addStyleTag({ content: '[role=tabpanel] div.shrink-0.flex-col:has(> section[aria-label="Guided setup"]) { display: none !important; }' });
      await expectFitsOneScreen(page, `live rig-like ${size.width}x${size.height}`);
      await page.waitForTimeout(1600);
      await page.screenshot({ path: `test-results/e2e-rig/live-riglike-${size.width}.png` });
      await expectNoPanelCutsAnything(page, `live rig-like ${size.width}x${size.height}`);
      await expectCardsUniform(page, `live rig-like ${size.width}x${size.height}`);
    }
  });

  test("neither tab carries a number, and the bell counts every problem in two sections", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await serveBusyInstall(page);
    await page.goto("/");
    await expect(page.getByRole("region", { name: /^the journey/i })).toBeVisible();
    // The lists the count reads arrive after the summary; the bell holds the problems of both tabs.
    await expect.poll(() => bell(page), { timeout: 15_000 }).toBeGreaterThan(0);
    // No number beside the Live or System tab, however many things need you (#1098).
    expect(await badge(page, "System")).toBe(0);
    expect(await badge(page, "Live")).toBe(0);
    await expect(page.getByRole("region", { name: "Needs you" })).toHaveCount(0);

    // The left menu no longer carries a Needs a look card; the bell is the one place for problems.
    await expect(page.locator("aside").getByText("Needs a look")).toHaveCount(0);
    await page.getByRole("button", { name: /^Notifications/ }).click();
    const panel = page.getByRole("dialog");
    await expect(panel.getByRole("region", { name: "Needs you" })).toBeVisible();
    // Two sections, and every row has at most two buttons, each saying what it does on hover.
    await expect(panel.getByRole("region", { name: "Deluno" })).toBeVisible();
    for (const row of await panel.getByRole("listitem").all()) {
      const controls = row.locator("[data-row-actions] button, [data-row-actions] a");
      expect(await controls.count()).toBeLessThanOrEqual(2);
      for (const control of await controls.all()) expect(await control.getAttribute("title")).toBeTruthy();
    }
    await expect(panel.getByRole("link", { name: "20 leftover folders" })).toBeVisible();
    await page.keyboard.press("Escape");
  });

  test("a fresh install shows no number on either tab and a bell that is quiet or counts what needs you", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await expect(page.getByRole("region", { name: /^the journey/i })).toBeVisible();
    await page.waitForTimeout(1500);
    expect(await badge(page, "System")).toBe(0);
    expect(await badge(page, "Live")).toBe(0);
    await expect(page.getByRole("tab", { name: /^System/ }).getByLabel(/need a look|needs a look/)).toHaveCount(0);
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
