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

const SIZES = [
  { width: 1920, height: 1080 },
  { width: 1366, height: 768 },
  { width: 390, height: 844 }
] as const;

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

// The titles must not outlive the test: later specs in the same shared database assume an empty library.
const seeded: { movieId: string; seriesId: string }[] = [];

test.afterEach(async ({ page }) => {
  if (seeded.length === 0) return; // skipped on the phone project: nothing to remove
  const headers = await authHeaders(page);
  for (const { movieId, seriesId } of seeded.splice(0)) {
    await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movieId], operation: "remove" } });
    await page.request.post("/api/series/bulk", { headers, data: { seriesIds: [seriesId], operation: "remove" } });
  }
});

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
  return [`/movies/${movie.id}`, `/tv/${series.id}`];
}

/** Runs in the page: what is wrong with the layout right now, in words. */
function measure() {
  const doc = document.documentElement;
  const width = doc.clientWidth;
  const found: string[] = [];
  if (doc.scrollWidth > width + 1) found.push(`page scrolls sideways (${doc.scrollWidth}px in ${width}px)`);

  const sidewaysScroller = (el: HTMLElement) => {
    const overflowX = getComputedStyle(el).overflowX;
    return (overflowX === "auto" || overflowX === "scroll") && el.scrollWidth > el.clientWidth;
  };
  const hidesOverflow = (el: HTMLElement) => {
    const overflowX = getComputedStyle(el).overflowX;
    return overflowX === "hidden" || overflowX === "clip";
  };

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

    let reachable = false;
    let cutBy: string | null = null;
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
          cutBy = `${owner.tagName.toLowerCase()}${owner.className ? "." + String(owner.className).split(/\s+/).slice(0, 2).join(".") : ""}`;
          break;
        }
      }
    }
    if (reachable) continue;
    if (cutBy) found.push(`${name} is cut off by ${cutBy} (x ${Math.round(rect.left)}-${Math.round(rect.right)})`);
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
      const titles = await seedTitles(page);
      const data = trackDataRequests(page);

      const offenders: string[] = [];
      for (const route of [...new Set([...routes, ...titles])]) {
        await page.goto(route);
        await data.settled();
        // A title page lays itself out again when its metadata arrives (#1179): measure when two looks agree.
        let previous = "";
        await expect.poll(async () => {
          const now = JSON.stringify(await page.evaluate(measure));
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
});
