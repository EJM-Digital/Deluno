import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation > Download health, the lists, against the real server with nothing stubbed (measure 4, issue 1270): the empty rules card, the
 * rule rows (kind with On or Off, scope, completion range, strikes with the check interval, action), the columns that sort them, and the
 * ignore list with its Remove. Rules and entries are made through the real routes and removed again.
 */

interface Rule { id: string }
interface Entry { id: string }

const rule = (over: Record<string, unknown>) => ({
  kind: "no-progress", enabled: true, strikeLimit: 3, timerMinutes: 30, appliesTo: "both", minCompletionPercent: 0, maxCompletionPercent: 100,
  resetStrikesOnProgress: true, actionPreset: "just-tell-me", threshold: 0, privacyScope: "both", ...over
});
const rules = async (page: Page) => (await api<Rule[]>(page, "GET", "/api/download-health/rules")).body;
const entries = async (page: Page) => (await api<Entry[]>(page, "GET", "/api/download-health/ignore-list")).body;
const open = async (page: Page) => { await openScreen(page, "/search-cycles/download-health"); await untilQuiet(page); };
const row = (page: Page, text: string) => page.getByRole("row").filter({ hasText: text }).first();
const orderOf = async (page: Page, kinds: string[]) => {
  const texts = await page.getByRole("table").first().getByRole("row").allTextContents();
  return texts.map((text) => kinds.find((kind) => text.startsWith(kind))).filter(Boolean);
};

test.describe("Download health lists", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/download-health");
    for (const existing of await rules(page)) await api(page, "DELETE", `/api/download-health/rules/${existing.id}`);
    for (const entry of await entries(page)) await api(page, "DELETE", `/api/download-health/ignore-list/${entry.id}`);
    await page.close();
  });

  test("with no rule the card reads 0 rules and says nothing is on by default, with a New rule button", async ({ page }) => {
    await open(page);
    // A fresh install carries its old rules switched off; they are deleted so the card has none.
    for (const existing of await rules(page)) expect((await api(page, "DELETE", `/api/download-health/rules/${existing.id}`)).status).toBeLessThan(300);
    expect(await rules(page)).toEqual([]);
    await page.reload();
    await untilQuiet(page);

    await expect(page.getByText("0 rules", { exact: true })).toBeVisible();
    await expect(page.getByText("No rules configured", { exact: true })).toBeVisible();
    await expect(page.getByText("Nothing is on by default.", { exact: false })).toBeVisible();
    await expect(page.getByRole("button", { name: "New rule", exact: true })).toHaveCount(2);
  });

  test("each rule row reads its kind with On or Off, its scope, its completion range, its strikes with the check interval and its action", async ({ page }) => {
    await open(page);
    for (const body of [
      rule({ kind: "too-slow", threshold: 500, strikeLimit: 1, appliesTo: "torrents", privacyScope: "public", minCompletionPercent: 10, maxCompletionPercent: 90, actionPreset: "replace" }),
      rule({ kind: "no-progress", strikeLimit: 4, enabled: false, actionPreset: "retry-elsewhere" }),
      rule({ kind: "dead-torrent", threshold: 60, strikeLimit: 2, appliesTo: "torrents", privacyScope: "private", actionPreset: "just-tell-me" })
    ]) expect((await api(page, "PUT", "/api/download-health/rules/new", body)).status).toBe(200);
    await page.reload();
    await untilQuiet(page);

    await expect(page.getByText("3 rules", { exact: true })).toBeVisible();
    const slow = row(page, "Too slow");
    await expect(slow).toContainText("On");
    await expect(slow).toContainText("Torrents only · Public only");
    await expect(slow).toContainText("10-90% complete");
    await expect(slow).toContainText("1 strike");
    await expect(slow).not.toContainText("1 strikes");
    await expect(slow).toContainText("Checked every 5 min");
    await expect(slow).toContainText("Replace it");
    const none = row(page, "No progress");
    await expect(none).toContainText("Off");
    await expect(none).toContainText("Torrents and Usenet");
    await expect(none).not.toContainText("Public");
    await expect(none).toContainText("0-100% complete");
    await expect(none).toContainText("4 strikes");
    await expect(none).toContainText("Retry elsewhere, keep files");
    await expect(row(page, "Dead torrent")).toContainText("Torrents only · Private only");
    // Rows run by kind name.
    expect(await orderOf(page, ["Dead torrent", "No progress", "Too slow"])).toEqual(["Dead torrent", "No progress", "Too slow"]);
  });

  test("the columns sort by rule, scope, strikes and action", async ({ page }) => {
    await open(page);
    const kinds = ["Dead torrent", "No progress", "Too slow"];
    const sortBy = async (name: string, times = 1) => {
      const header = page.getByRole("columnheader", { name: new RegExp(`^${name}`) });
      for (let i = 0; i < times; i += 1) await header.getByRole("button").click();
      return header;
    };

    await sortBy("Rule", 2);
    expect(await orderOf(page, kinds)).toEqual(["Too slow", "No progress", "Dead torrent"]);
    await sortBy("Rule", 1);
    await sortBy("Scope");
    // Torrents and Usenet, then Torrents only · Private only, then Torrents only · Public only.
    expect(await orderOf(page, kinds)).toEqual(["No progress", "Dead torrent", "Too slow"]);
    await sortBy("Strikes");
    expect(await orderOf(page, kinds)).toEqual(["Too slow", "Dead torrent", "No progress"]); // 1, 2, 4 strikes
    const action = await sortBy("Action");
    await expect(action).toHaveAttribute("aria-sort", "ascending");
    // Just tell me, Replace it, Retry elsewhere, keep files.
    expect(await orderOf(page, kinds)).toEqual(["Dead torrent", "Too slow", "No progress"]);
  });

  test("an ignored download is listed with its hash, category, tracker domain and tag joined, and its note; Remove takes it off the server and says so", async ({ page }) => {
    await open(page);
    const added = await api(page, "POST", "/api/download-health/ignore-list", { hash: "abc123def456", category: "tv-sonarr", trackerDomain: "tracker.example.org", tag: "keepme", note: "Seeding for a friend" });
    expect(added.status, JSON.stringify(added.body)).toBeLessThan(300);
    await page.reload();
    await untilQuiet(page);

    const listed = row(page, "abc123def456");
    await expect(listed).toContainText("abc123def456 · tv-sonarr · tracker.example.org · tag keepme");
    await expect(listed).toContainText("Seeding for a friend");
    await expect(page.getByText("1 entry", { exact: true })).toBeVisible();

    await listed.getByRole("button", { name: "Remove", exact: true }).click();
    await expect(page.locator("[data-sonner-toast]").filter({ hasText: "Removed from the ignore list" })).toBeVisible();
    await expect.poll(async () => (await entries(page)).length).toBe(0);
    await expect(page.getByText("abc123def456", { exact: false })).toHaveCount(0);
  });
});
