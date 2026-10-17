import path from "node:path";
import { pathToFileURL } from "node:url";
import { expect, test } from "@playwright/test";
import { makeFolders, openDeluno } from "./flow.ts";
import { until, setUpStack, type Stack } from "./stack.ts";

/**
 * Group `layout` (scripts/golden-path/suite/vm/scenarios/G12-Layout.ps1, #1191): every screen is opened at a phone's width (390 px, the
 * width the smoke walk uses, apps/web/tests/smoke/every-screen-fits-three-widths.spec.ts) while a library has a real folder-chain problem
 * (its download client's category folder does not agree with the library's downloads folder, which Deluno finds by reading the client).
 *   - no screen scrolls sideways, and no visible button, link or field sits outside the screen (outside a sideways scroller);
 *   - the folder-chain problem is raised by name: Health's row reads "The <library> library's category and save path", and a screen
 *     that lists problems shows that row with the library's name.
 * The screens are the smoke walk's own lists (the sidebar, every Settings page and area tab, the guided setup), read from the router so a
 * new screen is walked the day it is added, and a film's page. Real Chromium at a 390 px viewport; window.innerWidth is read on every screen.
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;

/** Runs in the page: what is wrong with the layout right now, in words ('' when nothing). The same two rules the smoke walk starts from. */
const MEASURE = () => {
  const d = document.documentElement, w = d.clientWidth, found: string[] = [];
  if (d.scrollWidth > w + 1) found.push(`the page scrolls sideways (${d.scrollWidth}px in ${w}px)`);
  const els = document.querySelectorAll("button,a[href],input,select,textarea,[role=button],[role=tab]");
  const outside: string[] = [];
  for (const e of Array.from(els) as HTMLElement[]) {
    const r = e.getBoundingClientRect();
    if (r.width <= 1 || r.height <= 1) continue;
    const cs = getComputedStyle(e);
    if (cs.visibility === "hidden" || cs.display === "none" || Number(cs.opacity) === 0) continue;
    if (e.closest("[aria-hidden=true],[inert],[hidden]")) continue;
    if (r.right <= w + 1 && r.left >= -1) continue;
    let p = e.parentElement, scroller = false;
    while (p && p !== document.body) {
      const o = getComputedStyle(p).overflowX;
      if ((o === "auto" || o === "scroll") && p.scrollWidth > p.clientWidth) { scroller = true; break; }
      p = p.parentElement;
    }
    if (!scroller) outside.push(`${e.getAttribute("aria-label") || e.innerText || e.tagName}`.trim().slice(0, 40));
  }
  if (outside.length) found.push(`${outside.length} control(s) outside the screen: ${outside.slice(0, 4).join(" | ")}`);
  return { problem: found.join("; "), innerWidth: window.innerWidth, text: document.body?.innerText ?? "" };
};

test.beforeAll(async () => {
  test.setTimeout(10 * 60_000);
  stack = await setUpStack();
});

test("layout.390-folder-chain: Every screen fits at 390 px wide while a library has a folder-chain problem, and the problem names its library", async ({ browser }) => {
  test.setTimeout(25 * 60_000);
  const token = Date.now().toString(36);
  const name = `Narrow walk ${token}`;
  let libId: string | undefined;
  try {
    // ---------------------------------------------------------- a real folder-chain problem
    const profile = (await stack.deluno.must<any[]>("GET", "/api/quality-profiles")).find((p) => p.mediaType === "movies");
    makeFolders(`/media/NarrowWalk${token}`, `/downloads/completed/NarrowWalk${token}`);
    const lib = await stack.deluno.must<any>("POST", "/api/libraries", {
      name, mediaType: "movies", purpose: "Main library", rootPath: `/media/NarrowWalk${token}`, downloadsPath: `/downloads/completed/NarrowWalk${token}`,
      qualityProfileId: profile.id, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false,
      removeEmptySourceFolders: false, searchIntervalMinutes: 720, retryDelayHours: 6, maxItemsPerRun: 10
    });
    libId = lib.id;
    // Routed to qBittorrent, whose category folder for movies is the Movies library's folder, not this library's.
    await stack.deluno.must("PUT", `/api/libraries/${libId}/routing`, {
      sources: [stack.indexerId, stack.usenetIndexerId].map((indexerId, i) => ({ indexerId, priority: i + 1, requiredTags: "", excludedTags: "" })),
      downloadClients: [{ downloadClientId: stack.clientIds.qbittorrent, priority: 1 }]
    });
    const chain = await stack.deluno.call("GET", `/api/libraries/${libId}/folder-chain`);
    const bad = ((chain.body?.clients ?? []) as any[]).flatMap((c) => c.links ?? []).filter((l: any) => l.status === "problem");
    expect(chain.status === 200 && chain.body?.allMatch === false && bad.length > 0,
      `The library has a real folder-chain problem (the client's folder does not agree with the library's downloads folder): ${bad[0]?.message ?? `HTTP ${chain.status}, allMatch ${chain.body?.allMatch}; nothing was marked as a problem`}`).toBe(true);

    // Health raises it by name: the check runs again at once ("Check now").
    await stack.deluno.call("POST", "/api/system/health/run");
    const raised = await until("Health to raise the folder-chain problem for the library", 240, async () => {
      const h = await stack.deluno.must<any>("GET", "/api/system/health");
      return (h.checks as any[]).find((c) => c.id === `library:${libId}:folder-chain` && c.status === "problem");
    }, 5000).catch(() => undefined);
    expect.soft(raised?.value?.name, `Health raises the folder-chain problem for the library, and its row names the library${raised ? `: ${raised.value.name}: ${raised.value.message}` : " (no problem raised in 4 minutes)"}`).toContain(name);
    if (raised) {
      expect.soft(String(raised.value.cause), "Its cause names the library").toContain(name);
      expect.soft(String(raised.value.fix), "Its fix names the library").toContain(name);
    }

    // ---------------------------------------------------------- the walk at 390 px
    // The smoke walk's own lists, read from the router and the shell (apps/web is an ES module package, so it is imported, not required).
    const { settingsRoutes, sidebarRoutes } = await import(pathToFileURL(path.resolve(process.cwd(), "apps/web/tests/helpers/route-lists.ts")).href) as { settingsRoutes(): string[]; sidebarRoutes(): string[] };
    const routes = [...new Set(["/", "/movies", ...sidebarRoutes(), ...settingsRoutes()])];
    // A film's page is walked too, so the stack gets a film to have a page.
    const movie = await stack.deluno.must<any>("POST", "/api/movies", { title: `Narrow walk film ${token}`, releaseYear: 2020, imdbId: null, monitored: true });
    routes.push(`/movies/${movie.id}`);
    const page = await openDeluno(browser, stack, routes[0], { width: 390, height: 844 });
    const walk: Array<{ route: string; shown: boolean; width: number; problem: string; text: string }> = [];
    try {
      for (const [i, route] of routes.entries()) {
        if (i > 0) await page.goto(route);
        // Shown when the screen has some text, then given a moment to lay out and load its rows.
        const shown = await page.waitForFunction(() => (document.body?.innerText ?? "").trim().length > 40, undefined, { timeout: 20_000 }).then(() => true, () => false);
        await page.waitForTimeout(1500);
        const read = await page.evaluate(MEASURE).catch((e) => ({ problem: `the screen could not be read: ${e.message}`, innerWidth: 0, text: "" }));
        walk.push({ route, shown, width: read.innerWidth, problem: read.problem, text: read.text });
      }
    } finally { await page.context().close(); }
    console.log(`walked ${walk.length} screens`);
    const notShown = walk.filter((w) => !w.shown);
    expect.soft(notShown.map((w) => w.route), `Every screen opened (${walk.length} screens) and showed something`).toEqual([]);
    expect.soft(walk.filter((w) => w.shown && w.width !== 390).map((w) => `${w.route} was ${w.width}`), "Every screen was measured at 390 px wide (window.innerWidth)").toEqual([]);
    expect.soft(walk.filter((w) => w.problem).map((w) => `${w.route}: ${w.problem}`), "No screen scrolls sideways or has a control outside the screen").toEqual([]);

    // ---------------------------------------------------------- the problem, on a screen, naming the library
    const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const pattern = new RegExp(`${escaped}[\\s\\S]{0,80}category and save path|category and save path[\\s\\S]{0,200}${escaped}`, "i");
    const naming = walk.filter((w) => pattern.test(w.text));
    expect.soft(naming.length, `At 390 px, a screen shows the folder-chain problem with the library's name${naming.length ? `: on ${naming.map((w) => w.route).join(", ")}` : ` (no screen's text names '${name}' beside the folder-chain problem)`}`).toBeGreaterThan(0);
    const dashOrSystem = walk.filter((w) => (w.route === "/" || w.route === "/system") && w.text.includes(name));
    expect.soft(dashOrSystem.length, `The Dashboard or System names the library in its problems${dashOrSystem.length ? `: on ${dashOrSystem.map((w) => w.route).join(", ")}` : ""}`).toBeGreaterThan(0);
  } finally {
    if (libId) await stack.deluno.call("DELETE", `/api/libraries/${libId}`);
  }
});
