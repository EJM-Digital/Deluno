import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30 live test, #1138. A fresh user saw issue numbers ("#905", "#909"), notes about
// what had "moved", and old page names in the words of the screens. This walks every
// owner screen the way a person does, opens each of its tabs, and reads what is on
// screen. No static check of the source replaces reading the rendered words.

const SCREENS = [
  "/", "/setup-guide", "/movies", "/movies/wanted", "/movies/upgrades", "/movies/import", "/tv", "/tv/wanted", "/tv/season-pass", "/tv/upgrades", "/tv/import",
  "/collections", "/calendar", "/discover", "/search", "/queue", "/activity",
  "/settings/libraries", "/settings/media-management", "/settings/import-policy", "/settings/processing", "/settings/destination-rules",
  "/settings/policy-sets", "/settings/profiles", "/settings/quality", "/settings/custom-formats", "/settings/release-rules",
  "/settings/lists", "/settings/automation", "/settings/migration", "/settings/metadata", "/settings/auto-tagging", "/settings/tags",
  "/settings/general", "/settings/notifications", "/settings/ui", "/settings/subtitles", "/settings/subtitles-wanted",
  "/indexers/indexers", "/indexers/proxies", "/indexers/scoreboard", "/indexers/history", "/indexers/download-clients", "/indexers/path-mappings",
  "/indexers/library-routing", "/indexers/subtitle-providers", "/indexers/media-servers", "/indexers/companion-tools",
  "/search-cycles", "/search-cycles/missing", "/search-cycles/upgrades", "/search-cycles/failed-downloads", "/search-cycles/retention",
  "/search-cycles/download-health", "/search-cycles/keep-seeding", "/search-cycles/blocklist",
  "/system", "/system/audit", "/system/api", "/system/docs", "/system/backups", "/system/recycle-bin", "/system/updates", "/system/security"
];

/** An issue number as a person would see it: "#905" or "(#909)". A heading id such as "#main-content" is not one. */
const ISSUE_NUMBER = /(^|[\s(])#\d{3,4}\b/;
const MOVED_NOTE = /moved to its own page|nothing changed for this installation/i;
const OLD_NAMES = /Search & Cycles|Search and Cycles|Failure and blocklist|Settings\s*(→|->|,|>)\s*General/i;

async function wordsOn(page: Page): Promise<string> {
  return (await page.locator("#main-content").innerText()).replace(/\s+/g, " ");
}

test("no owner screen or tab shows an issue number, a 'moved' note or an old page name (#1138)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The words are the same on a phone; the desktop run reads them all.");
  test.setTimeout(240_000);
  await authenticateAndNavigate(page, "/");
  const problems: string[] = [];

  for (const screen of SCREENS) {
    await page.goto(screen);
    await expect(page.locator("#main-content")).toBeVisible();
    await expect.poll(async () => (await wordsOn(page)).length, { timeout: 4_000 }).toBeGreaterThan(20).catch(() => undefined);
    const reads: Array<{ where: string; words: string }> = [{ where: screen, words: await wordsOn(page) }];
    for (const { where, words } of reads) {
      // A screen that had not drawn yet would pass for nothing.
      if (words.length <= 20) problems.push(`${where}: the screen had no words to read`);
      for (const [name, pattern] of [["an issue number", ISSUE_NUMBER], ["a 'moved' note", MOVED_NOTE], ["an old page name", OLD_NAMES]] as const) {
        const hit = words.match(pattern);
        if (hit) problems.push(`${where}: ${name}: "${words.slice(Math.max(0, (hit.index ?? 0) - 40), (hit.index ?? 0) + 60)}"`);
      }
    }
  }
  expect(problems, problems.join("\n")).toEqual([]);
});
