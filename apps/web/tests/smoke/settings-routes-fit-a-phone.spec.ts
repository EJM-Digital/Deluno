import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { trackDataRequests } from "../helpers/data-settled";

// #1059. At 390px the 1120px settings tab strip stretched the page, and the
// controls below it landed past the edge inside an overflow-x-hidden wrapper, so
// they could not be reached. document.scrollWidth does not see that (the wrapper
// hides it), so this measures the controls themselves.

const here = path.dirname(fileURLToPath(import.meta.url));
const src = path.resolve(here, "../../src");

/** Every settings destination the router and the tab strips know about. */
function settingsRoutes(): string[] {
  const router = readFileSync(path.join(src, "router.tsx"), "utf8");
  const shell = readFileSync(path.join(src, "components/app/settings-shell.tsx"), "utf8");
  const routes = new Set<string>(["/settings"]);
  // Nested children of the "settings" route, and flat "settings/..." routes.
  const block = router.slice(router.indexOf('path: "settings",'), router.indexOf('path: "system",'));
  for (const match of block.matchAll(/path: "([a-z][a-z0-9-]*)",\s*\n\s*lazy/g)) routes.add(`/settings/${match[1]}`);
  for (const match of router.matchAll(/path: "(settings\/[a-z0-9-]+)"/g)) routes.add(`/${match[1]}`);
  // Every tab of every area strip (settings, indexers, search-cycles, system).
  for (const match of shell.matchAll(/to: "(\/(?:settings|indexers|search-cycles|system)[a-z0-9/-]*)"/g)) routes.add(match[1]);
  return [...routes].sort();
}

test.describe("settings routes fit a phone", () => {
  test.skip(({ isMobile }) => !isMobile, "Phone-width behaviour only.");

  test("every settings route fits 390px and every button can be reached", async ({ page }) => {
    const routes = settingsRoutes();
    expect(routes.length).toBeGreaterThan(20);
    test.setTimeout(30_000 + routes.length * 8_000);
    await page.setViewportSize({ width: 390, height: 844 });
    await authenticateAndNavigate(page, "/settings");
    const data = trackDataRequests(page);

    const offenders: string[] = [];
    for (const route of routes) {
      await page.goto(route);
      await data.settled();

      const problems = await page.evaluate(() => {
        const width = document.documentElement.clientWidth;
        const found: string[] = [];
        const doc = document.documentElement;
        if (doc.scrollWidth > width + 1) found.push(`page scrolls sideways (${doc.scrollWidth}px)`);
        // The app shell hides sideways overflow, so also ask the wrappers.
        for (const el of Array.from(document.querySelectorAll<HTMLElement>("#root, #main-content, body > div, main"))) {
          if (el.scrollWidth > width + 1) found.push(`${el.tagName.toLowerCase()}#${el.id} is ${el.scrollWidth}px wide`);
        }
        const scrollsSideways = (el: HTMLElement) => {
          const overflowX = getComputedStyle(el).overflowX;
          return (overflowX === "auto" || overflowX === "scroll") && el.scrollWidth > el.clientWidth;
        };
        for (const button of Array.from(document.querySelectorAll<HTMLElement>("button, [role=button], a[href]"))) {
          const style = getComputedStyle(button);
          const rect = button.getBoundingClientRect();
          if (style.visibility === "hidden" || style.display === "none" || rect.width === 0 || rect.height === 0) continue;
          if (rect.left >= -1 && rect.right <= width + 1) continue;
          // Past the edge is fine only when its own scroller can bring it back.
          let owner: HTMLElement | null = button.parentElement;
          let scroller = false;
          while (owner && owner !== document.body) {
            if (scrollsSideways(owner)) { scroller = true; break; }
            owner = owner.parentElement;
          }
          if (!scroller) {
            const label = button.getAttribute("aria-label") ?? (button.textContent ?? "").trim().slice(0, 40);
            found.push(`"${label}" sits at x ${Math.round(rect.left)}-${Math.round(rect.right)}, out of reach`);
          }
        }
        return found;
      });

      if (problems.length > 0) offenders.push(`${route}: ${problems.slice(0, 4).join("; ")}`);
    }

    expect(offenders, offenders.join("\n")).toEqual([]);
  });
});
