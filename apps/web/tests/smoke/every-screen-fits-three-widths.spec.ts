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

test.afterEach(async ({ page }) => {
  if (seeded.length === 0 && seededIndexerIds.length === 0 && seededClientIds.length === 0) return; // skipped on the phone project: nothing to remove
  const headers = await authHeaders(page);
  for (const { movieId, seriesId } of seeded.splice(0)) {
    await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movieId], operation: "remove" } });
    await page.request.post("/api/series/bulk", { headers, data: { seriesIds: [seriesId], operation: "remove" } });
  }
  for (const id of seededIndexerIds.splice(0)) await page.request.delete(`/api/indexers/${id}`, { headers });
  for (const id of seededClientIds.splice(0)) await page.request.delete(`/api/download-clients/${id}`, { headers });
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
function measure(checkRepeats: boolean) {
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
      for (const route of [...new Set([...routes, ...titles])].filter((item) => !only || only.split(",").some((part) => (part === "home" ? item === "/" : item.includes(part))))) {
        await page.goto(route);
        await data.settled();
        // A title page lays itself out again when its metadata arrives (#1179): measure when two looks agree.
        let previous = "";
        await expect.poll(async () => {
          const now = JSON.stringify(await page.evaluate(measure, !RECORD_PAGES.has(route)));
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
        const problems = await page.evaluate(measure, false);
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
