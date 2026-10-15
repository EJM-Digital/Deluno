import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, type Request } from "@playwright/test";
import { authenticateAndNavigate } from "./auth-helper";

/**
 * Helpers for the real specs under tests/real (docs/testing/STRATEGY.md, measure 4).
 *
 * A real spec talks to the real Deluno host the config starts. Nothing here intercepts or fakes a request
 * (scripts/check-real-specs.mjs reads this file too): the page only listens to the requests it really makes.
 */

export const DESKTOP = { width: 1440, height: 900 } as const;
export const PHONE = { width: 390, height: 844 } as const;

/** Signs in and opens the screen. */
export async function openScreen(page: Page, path: string) {
  await page.setViewportSize(DESKTOP);
  await authenticateAndNavigate(page, path);
}

export async function authHeaders(page: Page): Promise<Record<string, string>> {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/** Calls the real API as the signed-in person (seeding, and reading back what a screen action did). */
export async function api<T = unknown>(page: Page, method: "GET" | "POST" | "PUT" | "DELETE", url: string, data?: unknown): Promise<{ status: number; body: T }> {
  const headers = await authHeaders(page);
  const response = await page.request.fetch(url, { method, headers, data });
  const text = await response.text();
  let body: unknown = text;
  try { body = text ? JSON.parse(text) : null; } catch { /* a reply that is not JSON stays text */ }
  return { status: response.status(), body: body as T };
}

/** What the page really sent: the requests it makes from now on, to the API, in order. */
export function recordRequests(page: Page) {
  const seen: Array<{ method: string; path: string; search: string; body: unknown }> = [];
  const onRequest = (request: Request) => {
    const url = new URL(request.url());
    if (!url.pathname.startsWith("/api/")) return;
    let body: unknown = null;
    try { body = request.postDataJSON(); } catch { body = request.postData(); }
    seen.push({ method: request.method(), path: url.pathname, search: url.search, body });
  };
  page.on("request", onRequest);
  return {
    seen,
    find: (method: string, pathPattern: RegExp) => seen.filter((r) => r.method === method && pathPattern.test(r.path)),
    stop: () => page.off("request", onRequest)
  };
}

/** The page has no screen-level error and says something in words. */
export async function expectNoErrorScreen(page: Page) {
  await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
  await expect(page.getByText("This area could not load")).toHaveCount(0);
}

export interface A11yFinding { id: string; help: string; nodes: string[] }

/** axe on the page as it stands. Serious and critical rules only, as the owner's accessibility bar. */
export async function axeFindings(page: Page): Promise<A11yFinding[]> {
  // A row that fades in as a live push arrives is half transparent for 0.6 s; axe would read its colours mid-fade.
  // Wait for every animation that has an end (not the endless spinners) so it measures the colours the person ends up with.
  await page.evaluate(() => Promise.all(document.getAnimations()
    .filter((a) => a.effect?.getComputedTiming().iterations !== Infinity)
    .map((a) => a.finished.catch(() => undefined))));
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  return results.violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => ({ id: v.id, help: v.help, nodes: v.nodes.slice(0, 4).map((n) => n.target.join(" ")) }));
}

export const describeFindings = (found: A11yFinding[]) => found.map((f) => `${f.id}: ${f.help} -> ${f.nodes.join(" | ")}`).join("\n");

export interface FocusStop { label: string; tag: string; visibleFocus: boolean; inOrder: boolean }

/**
 * The accessible name of a control, as a screen reader works it out: aria-label, then aria-labelledby, then the
 * label(s) the control belongs to (for a checkbox, switch or field), then its own text, title or placeholder.
 * Written as source text so the same function runs inside the page.
 */
const NAME_OF_SOURCE = String.raw`(el) => {
  const text = (node) => (node ? (node.innerText || node.textContent || "") : "").trim().replace(/\s+/g, " ").slice(0, 60);
  const labelled = (el.getAttribute("aria-labelledby") || "").split(/\s+/).map((id) => text(document.getElementById(id))).join(" ").trim();
  const labels = el.labels ? Array.from(el.labels).map(text).join(" ").trim() : "";
  return el.getAttribute("aria-label") || labelled || labels || text(el) || el.getAttribute("placeholder") || el.getAttribute("title") || el.getAttribute("name") || "";
}`;

/**
 * Walks the page with the Tab key alone and says what each stop was: its accessible name, whether the focus is visible
 * (an outline or a ring) and whether it comes after the previous stop in the page. Stops when focus leaves the page's
 * own controls or comes round to the first one again.
 */
export async function tabWalk(page: Page, limit = 200): Promise<{ stops: FocusStop[]; reachable: string[]; missed: string[] }> {
  await page.evaluate(`window.__nameOf = ${NAME_OF_SOURCE};`);
  await page.evaluate(() => {
    (document.activeElement as HTMLElement | null)?.blur?.();
    // A blur alone leaves the browser's place for the next Tab at the control that had focus, so a screen that
    // focuses its first field on its own would never list that field as a stop. Park the starting point at the very
    // top of the document, then remove the marker.
    const marker = document.createElement("span");
    marker.tabIndex = -1;
    document.body.prepend(marker);
    marker.focus();
    marker.remove();
    window.scrollTo(0, 0);
    (window as unknown as { __tabWalk: { previous: Element | null } }).__tabWalk = { previous: null };
  });
  const stops: FocusStop[] = [];
  let first: string | null = null;
  for (let i = 0; i < limit; i++) {
    await page.keyboard.press("Tab");
    const stop = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null;
      if (!el || el === document.body) return null;
      const state = (window as unknown as { __tabWalk: { previous: Element | null } }).__tabWalk;
      const name = (window as unknown as { __nameOf: (e: Element) => string }).__nameOf(el);
      const style = getComputedStyle(el);
      const outline = style.outlineStyle !== "none" && parseFloat(style.outlineWidth) > 0;
      const ring = style.boxShadow !== "none" && style.boxShadow !== "";
      // A control may show its focus on a wrapper (a custom field), so a ring on the closest wrapper counts too.
      const wrap = el.closest("label, [data-focus-ring], [class*=field], [class*=input]") as HTMLElement | null;
      const wrapStyle = wrap ? getComputedStyle(wrap) : null;
      const wrapRing = !!wrapStyle && ((wrapStyle.boxShadow !== "none" && wrapStyle.boxShadow !== "") || (wrapStyle.outlineStyle !== "none" && parseFloat(wrapStyle.outlineWidth) > 0) || wrapStyle.borderColor !== style.borderColor);
      const id = `${el.tagName.toLowerCase()}|${name}|${Math.round(el.getBoundingClientRect().top)}|${Math.round(el.getBoundingClientRect().left)}`;
      const inOrder = !state.previous || !!(state.previous.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_FOLLOWING) || el.contains(state.previous) || state.previous.contains(el);
      state.previous = el;
      return { id, label: name, tag: el.tagName.toLowerCase(), visibleFocus: outline || ring || wrapRing, inOrder };
    });
    if (!stop) break;
    if (first === null) first = stop.id;
    else if (stop.id === first) break;
    stops.push({ label: stop.label, tag: stop.tag, visibleFocus: stop.visibleFocus, inOrder: stop.inOrder });
  }
  const reachable = stops.map((s) => s.label);
  // Every visible, enabled control in the main area must have been a stop.
  const wanted = await page.evaluate(() => {
    const main = document.querySelector("main") ?? document.body;
    return Array.from(main.querySelectorAll<HTMLElement>("a[href], button, input, select, textarea, [role=button], [role=tab], [role=switch], [role=menuitem]"))
      .filter((el) => {
        const rect = el.getBoundingClientRect();
        const style = getComputedStyle(el);
        return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && !(el as HTMLButtonElement).disabled && el.tabIndex >= 0 && !el.closest("[aria-hidden=true], [inert]");
      })
      .map((el) => (window as unknown as { __nameOf: (e: Element) => string }).__nameOf(el));
  });
  const missed = wanted.filter((label) => label && !reachable.includes(label));
  return { stops, reachable, missed };
}

/** The standard keyboard-only expectations over a walk. */
export function expectKeyboardWalkSound(walk: Awaited<ReturnType<typeof tabWalk>>) {
  expect(walk.stops.length, "Tab reaches at least one control").toBeGreaterThan(0);
  const unnamed = walk.stops.filter((s) => !s.label);
  expect(unnamed, `every focus stop has a name: ${JSON.stringify(unnamed)}`).toEqual([]);
  const noFocus = walk.stops.filter((s) => !s.visibleFocus).map((s) => `${s.tag} "${s.label}"`);
  expect(noFocus, "focus is visible on every stop").toEqual([]);
  const outOfOrder = walk.stops.filter((s) => !s.inOrder).map((s) => `${s.tag} "${s.label}"`);
  expect(outOfOrder, "focus moves forward through the page").toEqual([]);
  expect(walk.missed, "every control in the main area can be reached by Tab").toEqual([]);
}
