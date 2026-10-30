import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { untilQuiet } from "../helpers/settle";

// #1329: two promises about the Dashboard's Live tab that the other Dashboard specs do not measure.
//  - On a phone the panels stack in one order: what is happening now, the pipeline, the library shelf, Activity, then the
//    Sources. A panel that jumps ahead of the others hides what the person opened the Dashboard to see.
//  - In a window shorter than the least the grid is drawn in, the tab scrolls inside itself; its panels never lie on top of
//    each other.

/** The panels of the open tab: their heading and where each one sits on the page. */
async function panels(page: Page) {
  return page.evaluate(() => {
    const tabpanel = document.querySelector('[role="tabpanel"]');
    if (!tabpanel) return [];
    return Array.from(tabpanel.querySelectorAll("section[aria-labelledby], section[aria-label]")).map((section) => {
      const box = section.getBoundingClientRect();
      const heading = section.querySelector("h2")?.textContent ?? section.getAttribute("aria-label") ?? "";
      return { heading: heading.trim(), top: box.top + window.scrollY, left: box.left, right: box.right, bottom: box.bottom + window.scrollY, width: box.width, height: box.height };
    }).filter((panel) => panel.width > 0 && panel.height > 0);
  });
}

test.describe("the Dashboard's Live tab in unusual windows", () => {
  test("on a phone the panels stack in the order a person reads them", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await authenticateAndNavigate(page, "/");
    await untilQuiet(page);

    const found = await panels(page);
    const order = [/^working on now$/i, /^pipeline$/i, /^your library$/i, /^activity$/i, /^sources$/i];
    const tops = order.map((pattern) => ({ pattern: String(pattern), panel: found.find((panel) => pattern.test(panel.heading)) }));
    const seen = found.map((panel) => `${panel.heading}@${Math.round(panel.top)}`).join(" | ");

    // Every panel of the Live tab is drawn on a fresh install, so each one must be found.
    for (const entry of tops) expect(entry.panel, `no ${entry.pattern} panel among: ${seen}`).toBeDefined();
    for (let index = 1; index < tops.length; index += 1) {
      expect(tops[index].panel!.top, `${tops[index].pattern} does not sit below ${tops[index - 1].pattern} on a phone (${seen})`)
        .toBeGreaterThan(tops[index - 1].panel!.top);
    }

    // And they stack in one column: each of the five starts at the left edge and none is wider than the phone.
    for (const entry of tops) {
      expect(entry.panel!.left, `${entry.pattern} is not at the left edge`).toBeLessThan(40);
      expect(entry.panel!.right, `${entry.pattern} runs past the phone`).toBeLessThanOrEqual(391);
    }
  });

  test("in a window shorter than the grid needs, the tab scrolls and no panel lies on another", async ({ page }) => {
    await page.setViewportSize({ width: 1366, height: 600 });
    await authenticateAndNavigate(page, "/");
    await untilQuiet(page);

    const found = await panels(page);
    expect(found.length, "the Live tab drew no panels").toBeGreaterThan(2);

    // No two panels overlap by more than a hairline, however short the window is.
    for (let i = 0; i < found.length; i += 1) {
      for (let j = i + 1; j < found.length; j += 1) {
        const a = found[i];
        const b = found[j];
        const overlapX = Math.min(a.right, b.right) - Math.max(a.left, b.left);
        const overlapY = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
        // A panel that contains another (a card inside the grid) is not "on top of" it; only equal-level siblings are compared.
        const nested = (a.top <= b.top && a.bottom >= b.bottom && a.left <= b.left && a.right >= b.right)
          || (b.top <= a.top && b.bottom >= a.bottom && b.left <= a.left && b.right >= a.right);
        if (nested) continue;
        expect(overlapX > 1 && overlapY > 1, `"${a.heading}" and "${b.heading}" overlap at 1366x600`).toBe(false);
      }
    }

    // A person scrolls with the wheel (the page eases, so the end is waited for): after enough of it the last panel is on screen.
    await page.mouse.move(683, 300);
    for (let turn = 0; turn < 12; turn += 1) await page.mouse.wheel(0, 400);
    const lowestEnd = async () => page.evaluate(() => {
      const sections = Array.from(document.querySelectorAll('[role="tabpanel"] section[aria-labelledby], [role="tabpanel"] section[aria-label]'));
      return { scrollY: window.scrollY, height: window.innerHeight, lowest: Math.max(...sections.map((section) => section.getBoundingClientRect().bottom)) };
    });
    await expect.poll(async () => {
      const now = await lowestEnd();
      return now.scrollY > 0 && now.lowest <= now.height + 2;
    }, { message: "scrolling to the end never brought the lowest panel fully on screen at 1366x600", timeout: 5_000 }).toBe(true);
  });
});
