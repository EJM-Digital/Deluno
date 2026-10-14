import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { untilQuiet } from "../helpers/settle";

// #1081. Every sidebar screen change feels instant: the screen's real content is
// on screen within 300 ms of the click once the app has been opened (100 ms on a
// second visit), and the page the person was on never goes blank on the way.

type Screen = { name: string; heading: string; path: string };

const screens: Screen[] = [
  { name: "Dashboard", heading: "Dashboard", path: "/" },
  { name: "Movies", heading: "Movies", path: "/movies" },
  { name: "TV Shows", heading: "TV Shows", path: "/tv" },
  { name: "Transfers", heading: "Transfers", path: "/queue" },
  { name: "Activity", heading: "Activity", path: "/activity" },
  { name: "Find & Download", heading: "Find & Download", path: "/indexers/indexers" },
  { name: "System", heading: "System", path: "/system" },
  { name: "Schedule", heading: "Schedule", path: "/calendar" }
];

const COLD_BUDGET_MS = 300;
const WARM_BUDGET_MS = 100;
/**
 * The disposable API here answers from an empty database in a few
 * milliseconds, which no real install does. Every API answer is held for this
 * long so the numbers reflect an install that has a library and a busy host:
 * what makes a screen feel instant is not waiting for the answer at all.
 */
const API_LATENCY_MS = Number(process.env.DELUNO_NAV_LATENCY_MS ?? 150);
const SETTLE_MS = 900;

type Measurement = { ms: number; blankFrames: number; spinnerFrames: number; timedOut: boolean };

/**
 * Clicks a sidebar link inside the page (so the clock is the page's own, not
 * Playwright's round trip) and watches every animation frame until the new
 * screen's real content is there: the topbar names the screen, the main area
 * has text in it and nothing in it is still marked busy.
 */
async function clickAndMeasure(page: Page, target: Screen): Promise<Measurement> {
  return page.evaluate(({ name, heading }) => new Promise<Measurement>((resolve) => {
    const link = Array.from(document.querySelectorAll<HTMLAnchorElement>("aside a, nav a"))
      .find((anchor) => anchor.offsetParent !== null && (anchor.textContent ?? "").trim().replace(/\d+$/, "").trim() === name);
    if (!link) {
      resolve({ ms: -1, blankFrames: 0, spinnerFrames: 0, timedOut: true });
      return;
    }

    let blankFrames = 0;
    let spinnerFrames = 0;
    const start = performance.now();
    link.click();

    const step = () => {
      const now = performance.now();
      const main = document.getElementById("main-content");
      const text = (main?.innerText ?? "").trim();
      const busy = main?.querySelector('[aria-busy="true"]') != null;
      const title = document.querySelector("h1")?.textContent?.trim();

      // Blank: the person is looking at an empty main area (no text and no
      // placeholder shapes). A skeleton is not blank; a bare spinner is.
      const hasShapes = main?.querySelector("[data-route-skeleton], [data-skeleton]") != null;
      if (!text && !hasShapes) blankFrames++;
      if (busy && !text) spinnerFrames++;

      // The error screen is text too; it is never the screen that was asked for.
      const failed = /This area could not load|Unexpected Application Error|That page is missing/.test(text);
      if (failed) {
        resolve({ ms: now - start, blankFrames, spinnerFrames, timedOut: true });
        return;
      }

      if (title === heading && text.length > 20 && !busy) {
        resolve({ ms: now - start, blankFrames, spinnerFrames, timedOut: false });
        return;
      }
      if (now - start > 5000) {
        resolve({ ms: now - start, blankFrames, spinnerFrames, timedOut: true });
        return;
      }
      requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }), { name: target.name, heading: target.heading });
}

async function settle(page: Page, screen: Screen) {
  await expect(page.getByText("This area could not load")).toHaveCount(0);
  await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
  await expect(page.locator("h1").first()).toHaveText(screen.heading, { timeout: 15_000 });
  await expect(page.locator("#main-content [aria-busy='true']")).toHaveCount(0, { timeout: 15_000 });
  // Background refreshes of the screen just opened finish before the next click,
  // so each measurement starts from a quiet page. (networkidle never arrives:
  // the live connection and the pollers keep the network busy by design.)
  await untilQuiet(page, { frames: Math.round(SETTLE_MS / 30) });
}

async function goTo(page: Page, screen: Screen) {
  const result = await clickAndMeasure(page, screen);
  expect(result.timedOut, `${screen.name} never showed its content`).toBe(false);
  await settle(page, screen);
  return result;
}

test.describe("sidebar navigation speed (#1081)", () => {
  test.setTimeout(240_000);

  test("every sidebar screen shows its content at once, from two other screens, and never blanks", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The sidebar is a desktop control.");
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.route("**/api/**", async (route) => {
      if (route.request().url().includes("/api/auth/")) {
        await route.continue();
        return;
      }
      await new Promise((resolve) => setTimeout(resolve, API_LATENCY_MS));
      await route.continue().catch(() => undefined);
    });
    await authenticateAndNavigate(page, "/");
    await settle(page, screens[0]);

    // Warm-up: open every screen once, as a person does in their first minute.
    for (const screen of screens) await goTo(page, screen);
    await goTo(page, screens[0]);

    const report: string[] = [];
    const failures: string[] = [];

    for (const [index, target] of screens.entries()) {
      // Two other screens to come from, so no result rests on one neighbour.
      const origins = [screens[(index + 3) % screens.length], screens[(index + 5) % screens.length]];
      for (const [visit, origin] of origins.entries()) {
        await goTo(page, origin);
        const result = await clickAndMeasure(page, target);
        await settle(page, target);
        const budget = visit === 0 ? COLD_BUDGET_MS : WARM_BUDGET_MS;
        report.push(`${origin.name} -> ${target.name} (${visit === 0 ? "first" : "second"} visit): ${result.ms.toFixed(0)} ms, blank frames ${result.blankFrames}`);
        if (result.timedOut) failures.push(`${target.name}: never showed its content`);
        else if (result.ms > budget) failures.push(`${origin.name} -> ${target.name}: ${result.ms.toFixed(0)} ms is over ${budget} ms`);
        if (result.blankFrames > 0) failures.push(`${origin.name} -> ${target.name}: main area was blank for ${result.blankFrames} frame(s)`);
      }
    }

    console.log(`NAVIGATION SPEED\n${report.join("\n")}`);
    testInfo.annotations.push({ type: "navigation-speed", description: report.join("; ") });
    expect(failures, failures.join("\n")).toEqual([]);
  });
});
