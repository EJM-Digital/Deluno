import { expect, test } from "@playwright/test";
import { screenRoutes } from "../helpers/inventory-routes";
import { PHONE, DESKTOP, axeFindings, describeFindings, expectKeyboardWalkSound, expectNoErrorScreen, openScreen, tabWalk } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Measure 4 of docs/testing/STRATEGY.md, for every screen route of the app (read from the router through the coverage
 * inventory): each opens on the real server and says something in words, has no serious or critical accessibility
 * violation (axe) at desktop width and at 390 px, and can be walked by keyboard alone: every control in the main area
 * is reached by Tab, focus is visible on every stop and moves forward through the page.
 *
 * Nothing is stubbed. Every screen must pass: there is no list of known defects to hide behind.
 */

// EVERY_SCREEN_SHARD="2/4" runs every fourth route starting at the second: a way to walk the screens on several hosts at once.
const shard = /^(\d+)\/(\d+)$/.exec(process.env.EVERY_SCREEN_SHARD ?? "");
const routes = screenRoutes().filter((_, index) => !shard || index % Number(shard[2]) === Number(shard[1]) - 1);

for (const path of routes) {
  test.describe(path, () => {
    test(`opens on the real server and says something in words on ${path}`, async ({ page }) => {
      await openScreen(page, path);
      await untilQuiet(page);
      await expectNoErrorScreen(page);
      const words = (await page.locator("main").innerText()).trim();
      expect(words.length, "the screen shows words, not a blank page").toBeGreaterThan(10);
      expect(words).not.toMatch(/\b(undefined|NaN|\[object Object\])\b/);
    });

    test(`has no serious accessibility violations at 1440 and at 390 on ${path}`, async ({ page }) => {
      await openScreen(page, path);
      await untilQuiet(page);
      const desktop = await axeFindings(page);
      expect(desktop, `at ${DESKTOP.width}px\n${describeFindings(desktop)}`).toEqual([]);

      await page.setViewportSize(PHONE);
      await untilQuiet(page);
      const phone = await axeFindings(page);
      expect(phone, `at ${PHONE.width}px\n${describeFindings(phone)}`).toEqual([]);
    });

    test(`can be walked by keyboard alone on ${path}`, async ({ page }) => {
      await openScreen(page, path);
      await untilQuiet(page);
      expectKeyboardWalkSound(await tabWalk(page));
    });
  });
}
