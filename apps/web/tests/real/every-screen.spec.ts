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
 * Nothing is stubbed. A screen that fails is listed below with the evidence and its test is marked fixme with the
 * reason; the defect is not fixed here.
 */

/** Screens with an accessibility defect found by this spec: route -> what axe reported (rule: help -> first nodes). */
const A11Y_DEFECTS: Record<string, string> = {
  "/": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href=\"/\"] > .whitespace-nowrap.flex-1.min-w-0 | kbd | section[aria-label=\"Speed\"] > .l",
  "/activity": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> the active sidebar link, kbd and .border-destructive/30 and .min-h-(--list-row-height) in the list",
  "/calendar": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[aria-current=\"page\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd | .bg-surface-2\\/30.min-h-\\[112px\\].p-1\\.5:nth-child(1) > .justif",
  "/collections": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[aria-current=\"page\"] > .whitespace-nowrap.flex-1.min-w-0 | kbd",
  "/discover": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[aria-current=\"page\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/companion-tools": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/download-clients": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/history": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/indexers": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/library-routing": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/media-servers": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/path-mappings": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/proxies": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/indexers/scoreboard": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd | .text-primary.tracking-widest.",
  "/indexers/subtitle-providers": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href=\"/indexers/indexers\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/movies": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"movies\"] > .whitespace-nowrap.flex-1.min-w-0 | .bg-background\\/70",
  "/queue": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"queue\"] > .whitespace-nowrap.flex-1.min-w-0 | kbd",
  "/search": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[aria-current=\"page\"] > .whitespace-nowrap.flex-1.min-w-0 | kbd",
  "/search-cycles": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"search-cycles\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/search-cycles/blocklist": "At 1440px axe reports aria-required-children: Certain ARIA roles must contain particular children -> div[role=\"rowgroup\"] ; color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-",
  "/search-cycles/download-health": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[href$=\"search-cycles\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/search-cycles/failed-downloads": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"search-cycles\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/search-cycles/keep-seeding": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"search-cycles\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd ; link-in-text-block:",
  "/search-cycles/missing": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"search-cycles\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/search-cycles/retention": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[href$=\"search-cycles\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/search-cycles/upgrades": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"search-cycles\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).gap-2\\.5[href$=\"libraries\"] > .whitespace-nowrap.flex-1.min-w-0 | kbd",
  "/settings/auto-tagging": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/automation": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[href$=\"search-cycles\"][aria-current=\"page\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/custom-formats": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[href$=\"policy-sets\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/destination-rules": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/general": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/settings/import-policy": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd ; link-in-text-block: Links must be dist",
  "/settings/libraries": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/lists": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/settings/media-management": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).gap-2\\.5[href$=\"libraries\"] > .whitespace-nowrap.flex-1.min-w-0 | kbd | div[aria-label=\"movie-folder naming",
  "/settings/metadata": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).gap-2\\.5[href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/migration": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/settings/notifications": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/settings/policy-sets": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href$=\"policy-sets\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/processing": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/profiles": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[href$=\"policy-sets\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/quality": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href$=\"policy-sets\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/release-rules": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> a[href$=\"policy-sets\"] > .whitespace-nowrap.flex-1.min-w-0 | kbd ; select-name: Select element must have an accessible name -> sec",
  "/settings/subtitles": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/subtitles-wanted": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/tags": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\).group[href$=\"libraries\"] > .flex-1.whitespace-nowrap.min-w-0 | kbd",
  "/settings/ui": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/setup-guide": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd | .bg-success\\/10 | .rounded-md",
  "/system": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd | .border-destructive\\/30 | .min-h-\\(--list-row-height\\).last\\:border-b-0.max-md\\:flex:nth-child(2) > .text-\\(length\\:--list-r",
  "/system/api": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/system/audit": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd | .pb-5.gap-\\(--grid-gap\\):nth-child(1) > .pt-1.flex-1.min-w-0 > .items-start.gap-y-0\\.5.justify-between > .gap-2.shrink-0 > .",
  "/system/backups": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd | .border-warning\\/35",
  "/system/docs": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/system/recycle-bin": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/system/security": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/system/updates": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> kbd",
  "/tv": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href=\"/tv\"] > .whitespace-nowrap.flex-1.min-w-0 | .bg-background\\/70",
  "/tv/season-pass": "At 1440px axe reports color-contrast: Elements must meet minimum color contrast ratio thresholds -> .text-\\(--nav-accent\\)[aria-current=\"page\"][href=\"/tv\"] > .whitespace-nowrap.flex-1.min-w-0 | kbd",
};

/** Screens whose keyboard walk found a defect: route -> what the walk reported. */
const KEYBOARD_DEFECTS: Record<string, string> = {
  "/search": "8 focus stop(s) have no accessible name (tag: input); a screen reader announces them as just a button or field",
  "/search-cycles/download-health": "8 focus stop(s) have no accessible name (tag: button/input); a screen reader announces them as just a button or field",
  "/search-cycles/failed-downloads": "8 focus stop(s) have no accessible name (tag: button); a screen reader announces them as just a button or field",
  "/search-cycles/upgrades": "1 focus stop(s) have no accessible name (tag: button); a screen reader announces them as just a button or field",
  "/settings/custom-formats": "1 focus stop(s) have no accessible name (tag: button); a screen reader announces them as just a button or field",
  "/settings/import-policy": "8 focus stop(s) have no accessible name (tag: button/input); a screen reader announces them as just a button or field",
  "/settings/lists": "3 focus stop(s) have no accessible name (tag: input/button); a screen reader announces them as just a button or field",
  "/settings/media-management": "2 focus stop(s) have no accessible name (tag: button); a screen reader announces them as just a button or field",
  "/settings/quality": "8 focus stop(s) have no accessible name (tag: input); a screen reader announces them as just a button or field",
  "/settings/subtitles": "1 focus stop(s) have no accessible name (tag: button); a screen reader announces them as just a button or field",
  "/settings/ui": "1 focus stop(s) have no accessible name (tag: button); a screen reader announces them as just a button or field",
  "/system/backups": "7 focus stop(s) have no accessible name (tag: button/input); a screen reader announces them as just a button or field",
  "/system/recycle-bin": "2 focus stop(s) have no accessible name (tag: input); a screen reader announces them as just a button or field",
  "/system/security": "7 focus stop(s) have no accessible name (tag: button/input); a screen reader announces them as just a button or field",
  "/system/updates": "1 focus stop(s) have no accessible name (tag: button); a screen reader announces them as just a button or field",
};

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
      test.fixme(path in A11Y_DEFECTS, A11Y_DEFECTS[path] ?? "no accessibility defect recorded for this screen");
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
      test.fixme(path in KEYBOARD_DEFECTS, KEYBOARD_DEFECTS[path] ?? "no keyboard defect recorded for this screen");
      await openScreen(page, path);
      await untilQuiet(page);
      expectKeyboardWalkSound(await tabWalk(page));
    });
  });
}
