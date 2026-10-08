import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { trackDataRequests } from "../helpers/data-settled";
import { settingsRoutes, sidebarRoutes } from "../helpers/route-lists";

// #1168. Every sidebar screen, every Settings page, a film page and a show page, at a wide desktop, a laptop and a
// phone. Two things fail it, and neither is weakened to make a screen pass:
//   1. the page scrolls sideways (documentElement.scrollWidth wider than its clientWidth);
//   2. a visible button, link or field is clipped: it sits outside the viewport sideways, or an ancestor that hides
//      overflow cuts it off. A control past the edge inside its own sideways scroller (a tab strip you swipe) can
//      be brought back, so only that one case is allowed, the same line settings-routes-fit-a-phone.spec.ts draws.
// Three more checks came from the first walk on real data (the 8 Oct rig walk): a button whose own words are wider than
// the button, a row, cell or panel that runs past the screen's edge inside a parent that hides it, and Dashboard cards
// that overlap. The screens that failed are visited with long names (the data below): the Dashboard, the blocklist,
// the calendar, a show's episodes, the setup guide and the download clients.
// The last walk (8 Oct, issue 1168) added what a phone and a laptop still lost on real data, and each is checked on every
// screen above: a row whose name has no room (Transfers' Needs attention rows under their buttons), a value cut short
// that a phone cannot read (a tooltip is not enough with no hover: the row must open its drawer, or the value must wrap),
// and a column heading or label cut with nothing to say the rest.

const SIZES = [
  { width: 1920, height: 1080 },
  { width: 1366, height: 768 },
  { width: 390, height: 844 }
] as const;

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

// What the test adds must not outlive it: later specs in the same shared database assume an empty library and no
// indexers or clients.
const seeded: { movieId: string; seriesId: string }[] = [];
const seededIndexerIds: string[] = [];
const seededClientIds: string[] = [];
const seededLibraryIds: string[] = [];

test.afterEach(async ({ page }) => {
  if (seeded.length === 0 && seededIndexerIds.length === 0 && seededClientIds.length === 0 && seededLibraryIds.length === 0) return; // skipped on the phone project: nothing to remove
  const headers = await authHeaders(page);
  for (const { movieId, seriesId } of seeded.splice(0)) {
    await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movieId], operation: "remove" } });
    await page.request.post("/api/series/bulk", { headers, data: { seriesIds: [seriesId], operation: "remove" } });
  }
  for (const id of seededIndexerIds.splice(0)) await page.request.delete(`/api/indexers/${id}`, { headers });
  for (const id of seededClientIds.splice(0)) await page.request.delete(`/api/download-clients/${id}`, { headers });
  for (const id of seededLibraryIds.splice(0)) await page.request.delete(`/api/libraries/${id}`, { headers });
});

const LONG_RELEASE = "The.Extremely.Long.Named.Documentary.Series.S01E01.Part.One.The.Beginning.2160p.UHD.BluRay.REMUX.DV.HDR10.TrueHD.Atmos.7.1.HEVC-LONGGROUPNAME";
const LONG_SHOW = "The Extraordinarily Long Titled Documentary About Everything";
const json = (body: unknown) => ({ contentType: "application/json", body: JSON.stringify(body) });

/**
 * The data the screens that failed the walk need to fail again: long release names, a show with seasons of long
 * episode titles, a calendar day with long show names, indexers with long names and a busy scoreboard, a download
 * client with a warning. Real rows where an API takes them (indexers, clients, titles) and the answers the screens
 * read for the rest, so the layout under test is the real one.
 */
async function seedLongData(page: Page, seriesId: string) {
  const headers = await authHeaders(page);
  const indexers: { id: string; name: string }[] = [];
  for (let index = 1; index <= 5; index += 1) {
    const created = await (await page.request.post("/api/indexers", {
      headers,
      data: { name: `A Very Long Indexer Name Number ${index}`, protocol: "torznab", baseUrl: `https://tracker${index}.example.test/api`, apiKey: "key", mediaScope: "both", isEnabled: true }
    })).json() as { id: string; name: string };
    seededIndexerIds.push(created.id);
    indexers.push(created);
  }
  const client = await (await page.request.post("/api/download-clients", {
    headers,
    data: { protocol: "sabnzbd", host: "a-long-host-name-for-a-usenet-box.example.test", port: 8085, isEnabled: true }
  })).json() as { id: string; name: string };
  seededClientIds.push(client.id);

  // Settings > Libraries (F10): a library with a long "advanced source" under a long folder, as the rig has.
  for (const [kind, folder] of [["movies", "Movies"], ["tv", "TV"]] as const) {
    // The folders must exist on the machine the smoke host runs on, so they are made under its temp folder, deep enough to run long.
    const downloads = path.join(os.tmpdir(), "deluno-fit-check", "Completed", LONG_SHOW, folder);
    fs.mkdirSync(downloads, { recursive: true });
    const response = await page.request.post("/api/libraries", {
      headers,
      data: {
        name: `Fit check ${folder} ${Date.now()}`, mediaType: kind, purpose: "Main library", rootPath: os.tmpdir(), downloadsPath: downloads,
        autoSearchEnabled: false, missingSearchEnabled: true, upgradeSearchEnabled: true, searchIntervalMinutes: 12, retryDelayHours: 6, maxItemsPerRun: 10
      }
    });
    if (response.ok()) seededLibraryIds.push(((await response.json()) as { id: string }).id);
  }

  const now = new Date().toISOString();
  // The guided setup saves its draft as it goes; a held-open save is not what this test measures.
  await page.route("**/api/setup/draft", (route) => route.request().method() === "PUT" ? route.fulfill(json({})) : route.continue());
  await page.route("**/api/indexers/scoreboard**", (route) => route.fulfill(json({
    indexers: indexers.map((indexer, index) => ({ id: indexer.id, name: indexer.name, totalQueries: 2368 - index * 300, failedQueries: index, successfulGrabs: 24 - index * 5, lastQueryUtc: now, candidatesReturned: 16842 - index * 1000, averageResponseMilliseconds: 64825 - index * 9000 }))
  })));
  await page.route(/\/api\/activity\?.*pageSize=80/, (route) => route.fulfill(json({
    items: Array.from({ length: 12 }, (_, index) => ({
      id: `fit-activity-${index}`, category: "import", label: "Imported", message: `${LONG_RELEASE}.${index} was imported from the download`, severity: "success",
      detailsJson: null, relatedJobId: null, relatedEntityType: null, relatedEntityId: null, createdUtc: new Date(Date.now() - index * 60_000).toISOString()
    })),
    nextPageToken: null, hasMore: false
  })));
  await page.route("**/api/download-clients/telemetry", (route) => route.fulfill(json({
    summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    capturedUtc: now,
    clients: [{
      clientId: client.id, clientName: client.name, protocol: "sabnzbd", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: {}, summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
      queue: [], history: [], capturedUtc: now, historyTruncated: false, clientWarnings: ["Disk is nearly full, downloads may stop."]
    }]
  })));
  // Transfers (#1162): hand-offs whose Source / Output are long, so a cell that outgrows its row shows.
  await page.route("**/api/integrations/processors/handoffs**", (route) => route.fulfill(json(Array.from({ length: 7 }, (_, index) => ({
    id: `fit-handoff-${index}`, libraryId: "fit-library", mediaType: "tv", clientId: client.id, queueItemId: `fit-queue-${index}`, releaseName: LONG_RELEASE.replace("S01E01", `S01E0${index + 2}`),
    sourcePath: `C:\\Downloads\\Completed\\TV\\${LONG_RELEASE}.${index}\\${LONG_RELEASE}.${index}\\${LONG_RELEASE}.${index}.mkv`, processorName: "A Very Long Processor Connector Name", status: index >= 5 ? "failed" : "completed",
    outputPath: `D:\\Processed Output\\Television\\${LONG_SHOW}\\Season 01\\${LONG_SHOW} - S01E0${index} - ${LONG_RELEASE}.mkv`, importJobId: null, failureMessage: index >= 5 ? `${LONG_RELEASE} was refused by the processor because the file inside it could not be read` : null, createdUtc: now, updatedUtc: now
  })))));
  // Needs attention (#1162): releases held for a yes, whose names, qualities and reasons are long, beside four buttons.
  await page.route("**/api/attention/held-releases", (route) => route.fulfill(json(Array.from({ length: 3 }, (_, index) => ({
    historyId: `fit-held-${index}`, mediaType: index === 1 ? "series" : "movie", id: `fit-held-title-${index}`, episodeId: null, title: index === 1 ? LONG_SHOW : `${LONG_SHOW} The Movie`, year: 2024, episodeLabel: index === 1 ? "S01E02" : null,
    libraryId: "fit-library", releaseName: `${LONG_RELEASE}.${index}`, indexerName: indexers[0].name, indexerId: indexers[0].id, downloadUrl: null, quality: "2160p UHD BluRay REMUX", sizeBytes: 54_000_000_000,
    seeders: 3, reason: "This release is much larger than the size you chose for this quality and would need an OK before it is sent.", heldUtc: now
  })))));
  await page.route("**/api/blocked-releases", (route) => route.request().method() === "GET"
    ? route.fulfill(json(Array.from({ length: 4 }, (_, index) => ({
        id: `fit-block-${index}`, releaseKey: `key-${index}`, releaseName: `${LONG_RELEASE}.${index}`, indexerName: indexers[0].name, mediaType: "tv", entityId: null, title: LONG_SHOW,
        reasonCode: "noVideoStream", reason: "There was no video in it.", torrentHashOrItemId: null, downloadClientId: null, downloadClientName: null, blockedUtc: now,
        state: index === 0 ? "proposed" : "refused", origin: index === 1 ? "manual" : "automatic"
      }))))
    : route.continue());
  await page.route("**/api/series/calendar**", (route) => route.fulfill(json([1, 2, 3].map((episode) => ({
    episodeId: `fit-cal-${episode}`, seriesId, seriesTitle: `${LONG_SHOW} ${episode}`, posterUrl: null, seasonNumber: 38, episodeNumber: episode, title: "An Episode With A Long Name",
    airDateUtc: now, monitored: true, wantedStatus: "missing"
  })))));
  await page.route(`**/api/series/${seriesId}/inventory`, (route) => route.fulfill(json({
    seriesId, title: LONG_SHOW, startYear: 2024, seasonCount: 2, episodeCount: 12, importedEpisodeCount: 0,
    episodes: Array.from({ length: 12 }, (_, index) => ({
      episodeId: `fit-ep-${index}`, seasonNumber: index < 6 ? 1 : 2, episodeNumber: (index % 6) + 1, title: `An Episode Whose Title Goes On And On Number ${index + 1}`,
      airDateUtc: `2024-03-0${(index % 6) + 1}T00:00:00Z`, monitored: true, hasFile: index % 3 === 0, wantedStatus: index % 3 === 0 ? "none" : "missing", wantedReason: "",
      qualityCutoffMet: false, lastSearchUtc: null, nextEligibleSearchUtc: null, updatedUtc: now
    }))
  })));
}

async function seedTitles(page: Page) {
  const headers = await authHeaders(page);
  const stamp = Date.now();
  const movie = await (await page.request.post("/api/movies/", {
    headers,
    data: { title: `Fit check movie ${stamp}`, releaseYear: 1962, monitored: true }
  })).json() as { id: string };
  const series = await (await page.request.post("/api/series/", {
    headers,
    data: { title: `Fit check show ${stamp}`, startYear: 2024, monitored: true }
  })).json() as { id: string };
  seeded.push({ movieId: movie.id, seriesId: series.id });
  return { routes: [`/movies/${movie.id}`, `/tv/${series.id}`], seriesId: series.id };
}

// Pages that list records (the audit trail, trusted addresses): the same reason on two records is data, not a repeated help line.
const RECORD_PAGES = new Set(["/system/audit", "/system/security"]);

/** Runs in the page: what is wrong with the layout right now, in words. */
function measure({ checkRepeats, phone }: { checkRepeats: boolean; phone: boolean }) {
  const doc = document.documentElement;
  const width = doc.clientWidth;
  const found: string[] = [];
  if (doc.scrollWidth > width + 1) found.push(`page scrolls sideways (${doc.scrollWidth}px in ${width}px)`);

  // A sentence of 25 or more characters said twice outside list rows (a row is real data): one description pasted
  // into several places. Same rule as src/test/repeated-text.ts, which the unit tests use.
  const said = new Map<string, number>();
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_ELEMENT);
  for (let node = walker.currentNode as Element | null; node; node = walker.nextNode() as Element | null) {
    if (node.closest("[role=row], [aria-hidden=true], [inert], [hidden], script, style, option")) continue;
    const box = node.getBoundingClientRect();
    if (box.width <= 1 || box.height <= 1) continue;
    const own = Array.from(node.childNodes).filter((child) => child.nodeType === Node.TEXT_NODE).map((child) => child.textContent ?? "").join(" ");
    for (const sentence of own.split(/(?<=[.!?])\s+/)) {
      const text = sentence.replace(/\s+/g, " ").trim();
      if (text.length >= 25) said.set(text, (said.get(text) ?? 0) + 1);
    }
  }
  if (checkRepeats) for (const [text, count] of said) if (count > 1) found.push(`"${text.slice(0, 60)}" is said ${count} times`);

  const sidewaysScroller = (el: HTMLElement) => {
    const overflowX = getComputedStyle(el).overflowX;
    return (overflowX === "auto" || overflowX === "scroll") && el.scrollWidth > el.clientWidth;
  };
  const hidesOverflow = (el: HTMLElement) => {
    const overflowX = getComputedStyle(el).overflowX;
    return overflowX === "hidden" || overflowX === "clip";
  };

  const isHiddenOrTiny = (el: HTMLElement) => {
    const style = getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0 || rect.width <= 1 || rect.height <= 1 || Boolean(el.closest("[aria-hidden=true], [inert], [hidden]"));
  };
  const inSidewaysScroller = (el: HTMLElement) => {
    for (let owner = el.parentElement; owner && owner !== document.body; owner = owner.parentElement) {
      if (sidewaysScroller(owner)) {
        const box = owner.getBoundingClientRect();
        if (box.left >= -1 && box.right <= width + 1) return true;
      }
    }
    return false;
  };

  // A row, cell or panel past the screen's edge: the page does not scroll (its parent hides the overflow), so the
  // person sees a cut-off card, which is what the setup guide's step cards and the show page's episode panel were.
  for (const box of Array.from(document.querySelectorAll<HTMLElement>("section, form, aside, article, [role=row], [role=cell], [role=table]"))) {
    if (isHiddenOrTiny(box) || inSidewaysScroller(box)) continue;
    const rect = box.getBoundingClientRect();
    if (rect.right > width + 1 || rect.left < -1) {
      const label = (box.getAttribute("aria-label") ?? box.textContent ?? "").trim().replace(/\s+/g, " ").slice(0, 40);
      found.push(`${box.tagName.toLowerCase()}${box.getAttribute("role") ? "[" + box.getAttribute("role") + "]" : ""} "${label}" runs from x ${Math.round(rect.left)} to ${Math.round(rect.right)}, outside 0-${width}`);
    }
  }

  // Dashboard cards sit in one grid and never overlap one another.
  const cards = Array.from(document.querySelectorAll<HTMLElement>("[data-live-grid] > *")).filter((card) => !isHiddenOrTiny(card));
  for (let first = 0; first < cards.length; first += 1) {
    for (let second = first + 1; second < cards.length; second += 1) {
      const a = cards[first].getBoundingClientRect();
      const b = cards[second].getBoundingClientRect();
      const overlapX = Math.min(a.right, b.right) - Math.max(a.left, b.left);
      const overlapY = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      if (overlapX > 2 && overlapY > 2) found.push(`Dashboard cards overlap by ${Math.round(overlapX)}x${Math.round(overlapY)}px (${(cards[first].textContent ?? "").trim().slice(0, 20)} / ${(cards[second].textContent ?? "").trim().slice(0, 20)})`);
    }
  }

  // Text that runs out of its row: a cell taller than the row it sits in prints over the next row (#1162).
  for (const row of Array.from(document.querySelectorAll<HTMLElement>("[role=row]"))) {
    if (isHiddenOrTiny(row)) continue;
    const rowBox = row.getBoundingClientRect();
    for (const el of Array.from(row.querySelectorAll<HTMLElement>("span, p, div"))) {
      if (!Array.from(el.childNodes).some((child) => child.nodeType === Node.TEXT_NODE && (child.textContent ?? "").trim())) continue;
      const box = el.getBoundingClientRect();
      // A clamped or clipped value is cut on purpose (its full text is the tooltip or the drawer): only visible overflow prints over a neighbour.
      let clipped = false;
      for (let owner: HTMLElement | null = el; owner && owner !== row; owner = owner.parentElement) if (getComputedStyle(owner).overflowY !== "visible") clipped = true;
      if (clipped) continue; // what an ancestor clips is not on screen
      const textBottom = Math.max(box.bottom, box.top + el.scrollHeight);
      if (box.height > 1 && textBottom > rowBox.bottom + 1) {
        found.push(`text "${(el.textContent ?? "").trim().replace(/\s+/g, " ").slice(0, 30)}" (${el.tagName.toLowerCase()}.${String(el.className).split(/\s+/).slice(0, 4).join(".")}) runs ${Math.round(textBottom - rowBox.bottom)}px below its row`);
        break;
      }
    }
  }

  // A row's name has room: the first cell a row shows is not squeezed to nothing by the buttons beside it (Transfers' Needs
  // attention on a phone showed three rows with no name at all, only their switch and buttons).
  for (const row of Array.from(document.querySelectorAll<HTMLElement>("[role=row]"))) {
    if (isHiddenOrTiny(row)) continue;
    const first = Array.from(row.querySelectorAll<HTMLElement>(":scope > [role=cell]")).find((cell) => getComputedStyle(cell).display !== "none");
    if (!first || !(first.textContent ?? "").trim()) continue;
    const box = first.getBoundingClientRect();
    if (box.width < 56) found.push(`row "${(first.textContent ?? "").trim().replace(/\s+/g, " ").slice(0, 30)}" has only ${Math.round(box.width)}px for its name`);
  }

  // A value the screen cuts short (an ellipsis or a line clamp, or a box that hides what runs past it) carries its full
  // text as a tooltip, or sits in a row that opens a drawer holding it (#1168). A phone has no hover, so there a tooltip
  // is not a way to read it: only a row that opens its drawer is, or the value is not cut at all.
  const cutHeadings: string[] = [];
  for (const el of Array.from(document.querySelectorAll<HTMLElement>("body *"))) {
    if (el.children.length > 0 && !Array.from(el.childNodes).some((child) => child.nodeType === Node.TEXT_NODE && (child.textContent ?? "").trim())) continue;
    const text = (el.textContent ?? "").trim();
    if (!text || isHiddenOrTiny(el)) continue;
    const style = getComputedStyle(el);
    const hides = style.overflowX === "hidden" || style.overflowX === "clip";
    const cutSideways = hides && (style.textOverflow === "ellipsis" || style.whiteSpace === "nowrap") && el.scrollWidth > el.clientWidth + 1;
    const cutTall = style.getPropertyValue("-webkit-line-clamp") !== "none" && style.overflowY !== "visible" && el.scrollHeight > el.clientHeight + 3;
    if (!cutSideways && !cutTall) continue;
    if (el.closest("[role=row][tabindex], [data-fit-ignore]")) continue;
    const label = `${text.replace(/\s+/g, " ").slice(0, 40)}" (${el.tagName.toLowerCase()}.${String(el.className).split(/\s+/).slice(0, 3).join(".")}`;
    // A heading or a column heading is cut short with nothing to read the rest by, tooltip or no: say it whole or wrap it.
    if (el.closest("h1, h2, h3, h4, [role=columnheader]")) { cutHeadings.push(label); continue; }
    if (phone) found.push(`cut text "${label}" cannot be read on a phone: it neither wraps nor sits in a row that opens`);
    else if (!el.closest("[title], [aria-label]")) found.push(`cut text "${label}" has no tooltip and no drawer`);
  }
  for (const label of cutHeadings) found.push(`heading "${label}" is cut`);

  const controls = document.querySelectorAll<HTMLElement>("button, a[href], input, select, textarea, [role=button]");
  for (const control of Array.from(controls)) {
    if (control instanceof HTMLInputElement && control.type === "hidden") continue;
    const style = getComputedStyle(control);
    const rect = control.getBoundingClientRect();
    if (style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0) continue;
    if (rect.width <= 1 || rect.height <= 1) continue; // screen-reader-only text and the like
    if (control.closest("[aria-hidden=true], [inert], [hidden]")) continue;

    const label = (control.getAttribute("aria-label") ?? control.getAttribute("placeholder") ?? control.textContent ?? "").trim().replace(/\s+/g, " ").slice(0, 40);
    const name = `${control.tagName.toLowerCase()} "${label}"`;

    // A button whose own words are wider than the button (a flex row squeezing it): the label is cut or spills over.
    if ((control.tagName === "BUTTON" || control.getAttribute("role") === "button") && style.textOverflow !== "ellipsis" && !control.querySelector("[class*=absolute]") && control.scrollWidth > control.clientWidth + 1) {
      found.push(`${name} is ${control.clientWidth}px wide for ${control.scrollWidth}px of label`);
    }

    let reachable = false;
    let cutBy: string | null = null;
    let cutBox = "";
    for (let owner = control.parentElement; owner && owner !== document.body; owner = owner.parentElement) {
      if (sidewaysScroller(owner)) {
        // Brought back by its own scroller: only that one case is allowed past the edge.
        const box = owner.getBoundingClientRect();
        if (box.left >= -1 && box.right <= width + 1) { reachable = true; break; }
      }
      if (hidesOverflow(owner)) {
        const box = owner.getBoundingClientRect();
        // A one-pixel screen-reader-only wrapper is not a visible clip: nothing of it is on screen.
        if (box.width <= 1 || box.height <= 1) { reachable = true; break; }
        if (rect.left < box.left - 1 || rect.right > box.right + 1) {
          cutBox = `${Math.round(box.left)}-${Math.round(box.right)}`;
          cutBy = `${owner.tagName.toLowerCase()}${owner.className ? "." + String(owner.className).split(/\s+/).slice(0, 2).join(".") : ""}`;
          break;
        }
      }
    }
    if (reachable) continue;
    if (cutBy) found.push(`${name} is cut off by ${cutBy} (x ${Math.round(rect.left)}-${Math.round(rect.right)} in ${cutBox})`);
    else if (rect.left < -1 || rect.right > width + 1) found.push(`${name} sits at x ${Math.round(rect.left)}-${Math.round(rect.right)}, outside 0-${width}`);
  }
  return found;
}

test.describe("every screen fits three widths", () => {
  // The viewport is set explicitly per run; the phone project's device profile adds nothing and doubles the work.
  test.skip(({ isMobile }) => isMobile, "Sizes are set in the test itself.");

  for (const size of SIZES) {
    test(`no screen scrolls sideways or clips a control at ${size.width}x${size.height}`, async ({ page }) => {
      const routes = [...sidebarRoutes(), ...settingsRoutes()];
      expect(routes.length).toBeGreaterThan(30);
      test.setTimeout(60_000 + routes.length * 8_000);
      await page.setViewportSize(size);
      await authenticateAndNavigate(page, "/movies");
      const { routes: titles, seriesId } = await seedTitles(page);
      await seedLongData(page, seriesId);
      const data = trackDataRequests(page);

      const offenders: string[] = [];
      const only = process.env.FIT_ONLY;
      // The Dashboard's System tab (its cards cut captions the Live tab does not) is a page of its own to the walk.
      for (const route of [...new Set([...routes, "/?tab=system", ...titles])].filter((item) => !only || only.split(",").some((part) => (part === "home" ? item === "/" : item.includes(part))))) {
        await page.goto(route);
        await data.settled();
        // A title page lays itself out again when its metadata arrives (#1179): measure when two looks agree.
        let previous = "";
        await expect.poll(async () => {
          const now = JSON.stringify(await page.evaluate(measure, { checkRepeats: !RECORD_PAGES.has(route), phone: size.width < 768 }));
          const same = now === previous;
          previous = now;
          return same;
        }, { timeout: 8_000 }).toBe(true);
        const problems = JSON.parse(previous) as string[];
        const shown = route.startsWith("/movies/") ? "a film page" : route.startsWith("/tv/") ? "a show page" : route;
        if (problems.length > 0) offenders.push(`${shown}: ${problems.slice(0, 5).join("; ")}`);
      }

      expect(offenders, offenders.join("\n")).toEqual([]);
    });
  }

  // D2 (rc.30.7 walk): the guided setup's Search source card was 422 px wide in a 380 px page. A fresh install sits on the
  // first step, so the walk above never saw it: this one opens every step, at every width.
  for (const size of SIZES) {
    test(`every guided setup step fits at ${size.width}x${size.height}`, async ({ page }) => {
      test.setTimeout(120_000);
      await page.setViewportSize(size);
      // Connected clients with long names, as on a real install: they once made the whole step wider than the page.
      await page.route(/\/api\/download-clients$/, (route) => route.request().method() !== "GET" ? route.continue() : route.fulfill(json(
        ["qBittorrent", "Transmission", "Deluge", "SABnzbd", "NZBGet"].map((name, index) => ({
          id: `c${index}`, name: `${name} on a rather long computer name`, protocol: name.toLowerCase(), host: "a-rather-long-computer-name.home.example.org",
          port: 8000 + index, isEnabled: true, healthStatus: "healthy", categoryTemplate: "x"
        })))));
      await authenticateAndNavigate(page, "/setup-guide");
      const steps = [
        "Your account", "Time and region", "How you sign in", "Addresses", "Other devices", "Updates", "Your library", "Folders",
        "Library Profiles", "Find & Download", "Connect installed apps", "Automation", "Start"
      ];
      const offenders: string[] = [];
      for (const step of steps) {
        const label = step.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
        await page.getByRole("button", { name: new RegExp(`^(?:\\d+ )?${label}\\b`) }).first().click();
        await expect(page.getByRole("heading", { name: step, exact: true })).toBeVisible();
        if (step === "Find & Download") {
          // The rig's draft held a long address; a test of it answers with that address in one unbroken word.
          await page.getByPlaceholder("https://indexer.example/api").fill("https://a-very-long-indexer-host-name.example.org/torznab/api/v2.0/indexers/all/results/torznab/api?apikey=0123456789abcdef0123456789abcdef");
          await page.getByRole("button", { name: "Test search source" }).click();
          await page.waitForTimeout(1500);
        }
        await page.waitForTimeout(300);
        const problems = await page.evaluate(measure, { checkRepeats: false, phone: size.width < 768 });
        // The page's container hides what runs past its edge, so look at everything inside it.
        problems.push(...await page.evaluate(() => {
          const width = document.documentElement.clientWidth;
          const past: string[] = [];
          for (const element of Array.from(document.querySelectorAll("#main-content *"))) {
            const box = element.getBoundingClientRect();
            if (box.width <= 1 || box.right <= width + 1) continue;
            let inScroller = false;
            for (let owner = element.parentElement; owner; owner = owner.parentElement) {
              if (/(auto|scroll)/.test(getComputedStyle(owner).overflowX)) { inScroller = true; break; }
            }
            if (!inScroller) past.push(`${element.tagName.toLowerCase()} runs to x ${Math.round(box.right)} in a ${width}px page`);
          }
          return past.slice(0, 3);
        }));
        if (problems.length > 0) offenders.push(`${step}: ${problems.slice(0, 5).join("; ")}`);
      }
      expect(offenders, offenders.join("\n")).toEqual([]);
    });
  }

  // #1162 line 1: at 1920 a Source / Output value that runs to three lines grew its cell but not its row, and the next
  // row's text printed over it. Long Source and Output values (seeded above) must leave every row clear of the next.
  test("Transfers rows with long Source and Output values grow with them at 1920", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/movies");
    const { seriesId } = await seedTitles(page);
    await seedLongData(page, seriesId);
    await page.goto("/queue");
    await expect(page.getByText("Processing connector").first()).toBeVisible();
    const rows = page.locator("[role=row]").filter({ hasText: "A Very Long Processor Connector Name" });
    await expect(rows.first()).toBeVisible();
    expect(await rows.count()).toBeGreaterThanOrEqual(3);
    const boxes = await rows.evaluateAll((elements) => elements.map((row) => {
      const rect = row.getBoundingClientRect();
      // scrollHeight: a clamped box reports its clamped height while its text runs on below it.
      const inner = Array.from(row.querySelectorAll("span")).map((span) => Math.max(span.getBoundingClientRect().bottom, span.getBoundingClientRect().top + span.scrollHeight));
      return { top: rect.top, bottom: rect.bottom, lowestText: Math.max(...inner) };
    }));
    for (let index = 0; index < boxes.length; index += 1) {
      expect(boxes[index].lowestText, `row ${index + 1}'s text ends below its row`).toBeLessThanOrEqual(boxes[index].bottom + 1);
      if (index > 0) expect(boxes[index].top, `row ${index + 1} starts inside row ${index}`).toBeGreaterThanOrEqual(boxes[index - 1].bottom - 1);
    }
  });

  // D1 (8 Oct walk): the phone's More menu opened half off the screen, because the sheet moved itself by minus half its
  // width twice (a Tailwind translate and the animation's own).
  test("the phone's More menu opens inside the screen", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await authenticateAndNavigate(page, "/movies");
    await page.getByRole("button", { name: "More destinations" }).click();
    const menu = page.getByRole("dialog").first();
    await expect(menu).toBeVisible();
    // The opening animation takes about a third of a second: measure once it has settled.
    await page.waitForTimeout(700);
    const box = (await menu.boundingBox())!;
    expect(box.x, `the menu starts at x ${box.x}`).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width, `the menu ends at x ${box.x + box.width}`).toBeLessThanOrEqual(390 + 1);
    // A destination inside it is whole too, not half off the left edge.
    const destination = menu.getByText("Preferences").first();
    await destination.scrollIntoViewIfNeeded();
    const label = (await destination.boundingBox())!;
    expect(label.x).toBeGreaterThanOrEqual(0);
    expect(label.x + label.width).toBeLessThanOrEqual(390);
  });
});

// The 8 Oct walk, items F8 and F9: a film's History tab prints a "Quality / language" heading, and a show page's long
// synopsis is clamped to the room its header has. Neither showed on an empty library, so a film with a video file and a
// 355-character synopsis on both pages are served here, and what is cut must be readable: a heading is not cut, and a
// clamped synopsis offers "More" in view.
test.describe("a title page's History heading and synopsis are readable", () => {
  test.skip(({ isMobile }) => isMobile, "Sizes are set in the test itself.");

  const SYNOPSIS = "A documentary crew follows a small town through one long winter, and the longer they stay the less sure they are of what they came to film. ".repeat(3).trim().slice(0, 355);

  for (const size of [SIZES[1], SIZES[2]]) {
    test(`the History heading and the synopsis fit at ${size.width}x${size.height}`, async ({ page }) => {
      test.setTimeout(90_000);
      await page.setViewportSize(size);
      await authenticateAndNavigate(page, "/movies");
      const { routes: titles } = await seedTitles(page);
      const [moviePath, showPath] = titles;
      const movieId = moviePath.split("/").pop()!;
      await page.route(new RegExp(`/api/(movies|series)/(${movieId}|${showPath.split("/").pop()})$`), async (route) => {
        if (route.request().method() !== "GET") return route.continue();
        const response = await route.fetch();
        const body = await response.json() as Record<string, unknown>;
        return route.fulfill({ response, json: { ...body, overview: SYNOPSIS } });
      });
      await page.route(`**/api/movies/${movieId}/files`, (route) => route.request().method() === "GET"
        ? route.fulfill(json([{
            path: `D:\Movies\Fit check\${LONG_RELEASE}.mkv`, sizeBytes: 54_000_000_000, kind: "video", isPrimary: true, quality: "2160p UHD BluRay REMUX",
            videoCodec: "hevc", audioCodec: "truehd", audioChannels: "7.1", language: null, hearingImpaired: null
          }]))
        : route.continue());

      const offenders: string[] = [];
      await page.goto(moviePath);
      await page.getByRole("tab", { name: "History" }).or(page.getByRole("radio", { name: "History" })).or(page.getByRole("button", { name: "History", exact: true })).first().click();
      await expect(page.getByText("Quality / language").first()).toBeVisible();
      const heading = await page.getByRole("columnheader", { name: "Quality / language" }).first().evaluate((el) => {
        const cuts = Array.from(el.querySelectorAll<HTMLElement>("*")).concat(el as HTMLElement).filter((node) => node.scrollWidth > node.clientWidth + 1 || node.scrollHeight > node.clientHeight + 3);
        return cuts.length === 0;
      });
      if (!heading) offenders.push("the film History tab cuts its \"Quality / language\" heading");

      for (const path of [moviePath, showPath]) {
        await page.goto(path);
        const synopsis = page.getByTestId("title-synopsis");
        await expect(synopsis).toBeVisible();
        await expect(synopsis).toContainText("documentary crew");
        const state = await synopsis.evaluate((box) => {
          const paragraph = box.querySelector("p")!;
          const cut = paragraph.scrollHeight > paragraph.clientHeight + 1;
          const more = box.querySelector<HTMLElement>("button[aria-label='Read the full synopsis']");
          const boxRect = box.getBoundingClientRect();
          const moreRect = more?.getBoundingClientRect();
          const moreInside = Boolean(moreRect && moreRect.bottom <= boxRect.bottom + 1 && moreRect.top >= boxRect.top - 1 && moreRect.height > 4);
          return { cut, hasMore: Boolean(more), moreInside };
        });
        if (state.cut && !state.moreInside) offenders.push(`${path.startsWith("/movies") ? "the film" : "the show"} page clamps its synopsis and shows no "More" to read the rest`);
      }
      expect(offenders, offenders.join("\n")).toEqual([]);
    });
  }
});

// #1162 line 1 on a phone (F2 and F4): every row of Transfers' Needs attention shows its name and opens its drawer, and a
// drawer says its whole description instead of cutting it to one line.
test.describe("Needs attention rows open on a phone", () => {
  test.skip(({ isMobile }) => isMobile, "Sizes are set in the test itself.");

  test("every row names itself and opens a drawer whose description is not cut", async ({ page }) => {
    test.setTimeout(90_000);
    await page.setViewportSize(SIZES[2]);
    await authenticateAndNavigate(page, "/movies");
    const { seriesId } = await seedTitles(page);
    await seedLongData(page, seriesId);
    await page.goto("/queue");
    const table = page.getByRole("region", { name: "Needs attention" });
    await expect(table).toBeVisible();
    const rows = table.getByRole("row").filter({ has: page.getByRole("cell") });
    const count = await rows.count();
    expect(count).toBeGreaterThanOrEqual(5);
    const offenders: string[] = [];
    for (let index = 0; index < count; index += 1) {
      const row = rows.nth(index);
      const name = (await row.getByRole("cell").first().innerText()).trim().split("\n")[0];
      if (!name) offenders.push(`row ${index + 1} shows no name`);
      await row.getByRole("cell").first().click();
      const drawer = page.getByRole("dialog");
      if (!(await drawer.isVisible({ timeout: 2_000 }).catch(() => false))) { offenders.push(`"${name.slice(0, 40)}" opens no drawer`); continue; }
      await page.waitForTimeout(500); // the drawer slides in
      const problems = await page.evaluate(measure, { checkRepeats: false, phone: true });
      for (const problem of problems) offenders.push(`"${name.slice(0, 30)}" drawer: ${problem}`);
      await page.keyboard.press("Escape");
      await expect(drawer).toBeHidden();
    }
    expect(offenders, offenders.join("\n")).toEqual([]);
  });
});
