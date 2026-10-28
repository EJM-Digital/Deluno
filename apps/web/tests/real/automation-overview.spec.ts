import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation overview against the real server, nothing stubbed (measure 4, issue 1270): the empty list, the summary strip, the library
 * rows (what each searches for, its schedule, its next and last search), the media-type filter, the tab titles, the schedule drawer
 * and the cycles the worker really records. The database starts empty; the libraries are made through the API, a cycle is a real
 * "Search now" the worker runs, and every library is removed again.
 */

interface Library { id: string; name: string; autoSearchEnabled: boolean; maxItemsPerRun: number; maxMissingItemsPerRun: number; maxUpgradeItemsPerRun: number; searchWindowDays: number }
interface Cycle { id: string; libraryName: string; triggerKind: string; status: string }

const stamp = `ov${Date.now()}`;
const names = { films: `Films ${stamp}`, shows: `Shows ${stamp}`, idle: `Idle ${stamp}` };
const folders: string[] = [];
const ids: Record<string, string> = {};

const folder = () => { const made = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-overview-")); folders.push(made); return made; };
const libraries = async (page: Page) => (await api<Library[]>(page, "GET", "/api/libraries")).body;
const cycles = async (page: Page) => (await api<{ items: Cycle[] }>(page, "GET", "/api/search-cycles?pageSize=50")).body.items;
const automation = (over: Record<string, unknown>) => ({
  autoSearchEnabled: true, missingSearchEnabled: true, upgradeSearchEnabled: true, searchIntervalMinutes: 360, retryDelayHours: 6, maxItemsPerRun: 10,
  maxMissingItemsPerRun: 10, maxUpgradeItemsPerRun: 10, searchWindowStartHour: null, searchWindowEndHour: null, searchWindowDays: 127, randomizeSearchOrder: false, ...over
});
const filmsAutomation = (over: Record<string, unknown> = {}) => automation({ upgradeSearchEnabled: false, maxMissingItemsPerRun: 25, maxUpgradeItemsPerRun: 5, searchWindowStartHour: 2, searchWindowEndHour: 6, ...over });
const showsAutomation = (over: Record<string, unknown> = {}) => automation({ searchIntervalMinutes: 180, ...over });
const row = (page: Page, name: string) => page.getByRole("row").filter({ hasText: name }).first();
const open = async (page: Page, route: string) => { await openScreen(page, route); await untilQuiet(page); };
const strip = (page: Page) => page.getByText("Sent to downloads", { exact: true }).locator("xpath=ancestor::*[.//text()[normalize-space()='Searching now']][1]");

test.describe("Automation overview", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles");
    for (const library of await libraries(page)) await api(page, "DELETE", `/api/libraries/${library.id}`);
    await api(page, "PUT", "/api/settings/automation", { isEnabled: false });
    await page.close();
    for (const made of folders) fs.rmSync(made, { recursive: true, force: true });
  });

  test("with no library the list says so, the strip reads Paused and nothing is due; Resume turns it to Running", async ({ page }) => {
    await open(page, "/search-cycles");
    expect(await libraries(page)).toEqual([]);

    await expect(page.getByText("No libraries yet", { exact: true })).toBeVisible();
    await expect(page.getByText("Create a library first, then Deluno can search it on a schedule.", { exact: true })).toBeVisible();
    await open(page, "/search-cycles/missing");
    await expect(page.getByText("No libraries yet", { exact: true })).toBeVisible();

    await open(page, "/search-cycles");
    await expect(strip(page)).toContainText("Paused");
    await expect(strip(page)).toContainText("Queued work is held safely");
    await expect(strip(page)).toContainText("all caught up");
    await expect(strip(page)).toContainText("nothing running");
    await expect(page.getByText("No search cycles yet", { exact: true })).toBeVisible();
    await expect(page.getByText("Once a library runs a missing or upgrade search, each cycle and what it queued shows up here.", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Resume automation", exact: true }).click();
    await expect(strip(page)).toContainText("Running");
    await expect(strip(page)).toContainText("Deluno searches on schedule");
    // Held again, so the worker does not run a scheduled cycle under the rows below until the cycle test lets it.
    await page.getByRole("button", { name: "Pause automation", exact: true }).click();
    await expect(strip(page)).toContainText("Paused");
  });

  test("each library row reads what it searches for, its schedule and its window; a library with nothing on reads Nothing selected and Off", async ({ page }) => {
    await open(page, "/search-cycles");
    const films = await api<Library>(page, "POST", "/api/libraries", { name: names.films, mediaType: "movies", purpose: "collection", rootPath: folder() });
    const shows = await api<Library>(page, "POST", "/api/libraries", { name: names.shows, mediaType: "tv", purpose: "collection", rootPath: folder() });
    const idle = await api<Library>(page, "POST", "/api/libraries", { name: names.idle, mediaType: "movies", purpose: "collection", rootPath: folder() });
    ids.films = films.body.id; ids.shows = shows.body.id; ids.idle = idle.body.id;
    expect((await api(page, "PUT", `/api/libraries/${ids.films}/automation`, filmsAutomation())).status).toBe(200);
    expect((await api(page, "PUT", `/api/libraries/${ids.shows}/automation`, showsAutomation())).status).toBe(200);
    expect((await api(page, "PUT", `/api/libraries/${ids.idle}/automation`, automation({ autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false }))).status).toBe(200);
    await page.reload();
    await untilQuiet(page);

    const filmsRow = row(page, names.films);
    await expect(filmsRow).toContainText("Movies");
    await expect(filmsRow).toContainText("Missing");
    await expect(filmsRow).not.toContainText("Missing · Upgrades");
    await expect(filmsRow).toContainText("Up to 25 missing · 5 upgrades per run");
    await expect(filmsRow).toContainText("Missing: Every 6 hours");
    await expect(filmsRow).not.toContainText("Upgrades: Every");
    await expect(filmsRow).toContainText("Only 02:00–06:00");
    const showsRow = row(page, names.shows);
    await expect(showsRow).toContainText("TV shows");
    await expect(showsRow).toContainText("Missing · Upgrades");
    await expect(showsRow).toContainText("Missing: Every 3 hours · Upgrades: Every 3 hours");
    await expect(showsRow).toContainText("Any time of day");
    const idleRow = row(page, names.idle);
    await expect(idleRow).toContainText("Nothing selected");
    await expect(idleRow).toContainText("Off");
    await expect(idleRow).toContainText("Manual");
  });

  test("before any search the Next / last cell says Not run yet, and every library whose state is not paused counts as due", async ({ page }) => {
    await open(page, "/search-cycles");
    await expect(row(page, names.films)).toContainText("Not run yet");
    await expect(strip(page)).toContainText("libraries ready for a cycle");
    await expect(strip(page)).toContainText("Due3libraries ready for a cycle");
  });

  test("the Schedule card's title and count follow the tab, and a tab lists only the libraries that search for that kind", async ({ page }) => {
    await open(page, "/search-cycles");
    await expect(page.getByRole("heading", { name: /^Library schedules$/i })).toBeVisible();
    await expect(page.getByText("3 libraries", { exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: /^When a download finishes$/i })).toBeVisible();

    await open(page, "/search-cycles/missing");
    await expect(page.getByRole("heading", { name: /^Missing search schedules$/i })).toBeVisible();
    await expect(page.getByText("2 libraries", { exact: true })).toBeVisible();
    await expect(row(page, names.idle)).toHaveCount(0);
    await expect(page.getByText("Sent to downloads", { exact: true })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: /^When a download finishes$/i })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Save automation settings", exact: true })).toHaveCount(0);

    await open(page, "/search-cycles/upgrades");
    await expect(page.getByRole("heading", { name: /^Upgrade search schedules$/i })).toBeVisible();
    await expect(page.getByText("1 library", { exact: true })).toBeVisible();
    await expect(row(page, names.films)).toHaveCount(0);
    await expect(row(page, names.shows)).toBeVisible();
    await expect(page.getByText("Sent to downloads", { exact: true })).toHaveCount(0);

    await open(page, "/search-cycles/blocklist");
    await expect(page.getByText("Sent to downloads", { exact: true })).toHaveCount(0);

    await open(page, "/search-cycles/failed-downloads");
    await expect(page.getByRole("heading", { name: /^Library schedules$/i })).toHaveCount(0);
    await expect(page.getByText("Sent to downloads", { exact: true })).toHaveCount(0);
  });

  test("the When a download finishes card's heading count follows the three modes", async ({ page }) => {
    await open(page, "/search-cycles");
    const mode = page.getByRole("radiogroup", { name: "What Deluno does with the original" });
    await expect(page.getByText("Shares, then tidies up", { exact: true })).toBeVisible();
    await mode.getByRole("radio", { name: "Tidy up now" }).click();
    await expect(page.getByText("Tidies up straight away", { exact: true })).toBeVisible();
    await mode.getByRole("radio", { name: "Leave it alone" }).click();
    await expect(page.getByText("Left alone", { exact: true })).toBeVisible();
    await mode.getByRole("radio", { name: "Share, then tidy up" }).click();
    await expect(page.getByText("Shares, then tidies up", { exact: true })).toBeVisible();
  });

  test("Failed Downloads: the Download health card says where the rules live and its button opens Download Health", async ({ page }) => {
    await open(page, "/search-cycles/failed-downloads");
    await expect(page.getByText("each rule has its own strike count and its own action", { exact: false })).toBeVisible();
    await page.getByRole("link", { name: "Open Download Health", exact: true }).click();
    await expect(page).toHaveURL(/\/search-cycles\/download-health$/);
  });

  test("a tab with no library that searches for that kind says so", async ({ page }) => {
    await open(page, "/search-cycles/upgrades");
    await api(page, "PUT", `/api/libraries/${ids.shows}/automation`, showsAutomation({ upgradeSearchEnabled: false }));
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("No libraries search for upgrades", { exact: true })).toBeVisible();
    await expect(page.getByText("Open a library's automation settings to turn this search on.", { exact: true })).toBeVisible();
    await api(page, "PUT", `/api/libraries/${ids.shows}/automation`, showsAutomation());

    await open(page, "/search-cycles/missing");
    await api(page, "PUT", `/api/libraries/${ids.films}/automation`, filmsAutomation({ missingSearchEnabled: false }));
    await api(page, "PUT", `/api/libraries/${ids.shows}/automation`, showsAutomation({ missingSearchEnabled: false }));
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("No libraries search for missing titles", { exact: true })).toBeVisible();
    await api(page, "PUT", `/api/libraries/${ids.films}/automation`, filmsAutomation());
    await api(page, "PUT", `/api/libraries/${ids.shows}/automation`, showsAutomation());
  });

  test("the media-type filter narrows the list to Movies or TV and the All view heads each group", async ({ page }) => {
    await open(page, "/search-cycles");
    await expect(page.getByRole("columnheader", { name: "Movies", exact: true })).toBeVisible();
    await expect(page.getByRole("columnheader", { name: "TV shows", exact: true })).toBeVisible();
    const filter = page.getByRole("radiogroup", { name: "Filter by media type" });

    await filter.getByRole("radio", { name: /^TV/ }).click();
    await expect(row(page, names.shows)).toBeVisible();
    await expect(row(page, names.films)).toHaveCount(0);
    await expect(row(page, names.idle)).toHaveCount(0);
    await filter.getByRole("radio", { name: /^Movies/ }).click();
    await expect(row(page, names.films)).toBeVisible();
    await expect(row(page, names.idle)).toBeVisible();
    await expect(row(page, names.shows)).toHaveCount(0);
    await filter.getByRole("radio", { name: /^All/ }).click();
    await expect(row(page, names.shows)).toBeVisible();
  });

  test("a real Search now records a Manual cycle: the Recent cycles card lists it with its result and the schedule shows when the library is next due", async ({ page }) => {
    await open(page, "/search-cycles");
    expect((await api(page, "PUT", "/api/settings/automation", { isEnabled: true })).status).toBe(200);
    expect((await api(page, "POST", `/api/libraries/${ids.films}/search-now`)).status).toBe(202);
    await expect.poll(async () => (await cycles(page)).some((c) => c.triggerKind === "manual" && c.libraryName === names.films), { timeout: 90_000, intervals: [2_000] }).toBe(true);
    await api(page, "PUT", "/api/settings/automation", { isEnabled: false });
    await page.reload();
    await untilQuiet(page);
    const total = (await cycles(page)).length;
    await expect(page.getByText(total === 1 ? "1 run" : `${total} runs`, { exact: true })).toBeVisible();

    const manual = page.getByRole("row").filter({ hasText: names.films }).filter({ hasText: "Manual" }).filter({ hasText: "queued" }).first();
    await expect(manual).toContainText("0 queued");
    await expect(manual).toContainText("0 planned · 0 skipped");
    await expect(manual).toContainText("just now");
    await expect(manual).toContainText("Took");
    await expect(manual).toContainText("empty");

    const filmsRow = row(page, names.films);
    await expect(filmsRow).toContainText("Missing in 6 h");
    await expect(filmsRow).toContainText("Last ran");
    await expect(filmsRow).not.toContainText("Not run yet");
    await expect(strip(page)).toContainText("0 B");
    await expect(strip(page)).toContainText("in the last 50 cycles");
  });

  test("on the Missing tab the next search reads only the missing time", async ({ page }) => {
    await open(page, "/search-cycles/missing");
    const filmsRow = row(page, names.films);
    await expect(filmsRow).toContainText("Missing in 6 h");
    await expect(filmsRow).not.toContainText("Upgrades in");
  });

  test("clicking a row opens its schedule drawer titled by the library and highlights the row", async ({ page }) => {
    await open(page, "/search-cycles");
    await row(page, names.shows).click();
    const drawer = page.getByRole("dialog", { name: names.shows });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText("Search schedule · TV shows", { exact: true })).toBeVisible();
    await expect(page.getByRole("row", { includeHidden: true }).filter({ hasText: names.shows }).first()).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("row", { includeHidden: true }).filter({ hasText: names.films }).first()).toHaveAttribute("aria-selected", "false");
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await row(page, names.films).click();
    await expect(page.getByRole("dialog", { name: names.films }).getByText("Search schedule · Movies", { exact: true })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });

  test("Run now shows when the library is next due beside its heading", async ({ page }) => {
    await open(page, "/search-cycles");
    await row(page, names.films).click();
    const drawer = page.getByRole("dialog", { name: names.films });
    await expect(drawer.getByText("next in 6 h", { exact: true })).toBeVisible();
  });

  test("a change in the drawer holds Search now and Skip next cycle and says Save your changes first", async ({ page }) => {
    await open(page, "/search-cycles");
    await row(page, names.films).click();
    const drawer = page.getByRole("dialog", { name: names.films });
    await expect(drawer.getByRole("button", { name: "Search now", exact: true })).toBeEnabled();
    await drawer.getByLabel("Search every").selectOption("720");
    await expect(drawer.getByText("Save your changes first.", { exact: true })).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Search now", exact: true })).toBeDisabled();
    await expect(drawer.getByRole("button", { name: "Skip next cycle", exact: true })).toBeDisabled();
  });

  test("a row switch changes a clean open drawer's own Search automatically switch", async ({ page }) => {
    await open(page, "/search-cycles");
    await row(page, names.films).click();
    const drawer = page.getByRole("dialog", { name: names.films });
    const inDrawer = drawer.getByRole("switch", { name: /^Search automatically/ });
    await expect(inDrawer).toBeChecked();
    await page.getByRole("switch", { name: `Pause automation for ${names.films}`, exact: true, includeHidden: true }).evaluate((el) => (el as HTMLElement).click());
    await expect.poll(async () => (await libraries(page)).find((l) => l.id === ids.films)?.autoSearchEnabled).toBe(false);
    await expect(inDrawer).not.toBeChecked();
    await expect(drawer.getByText("Save your changes first.")).toHaveCount(0);
    await page.getByRole("switch", { name: `Resume automation for ${names.films}`, exact: true, includeHidden: true }).evaluate((el) => (el as HTMLElement).click());
    await expect.poll(async () => (await libraries(page)).find((l) => l.id === ids.films)?.autoSearchEnabled).toBe(true);
  });

  test("turning all seven days off saves as every day, and the combined cap still goes with the save", async ({ page }) => {
    await open(page, "/search-cycles");
    await row(page, names.shows).click();
    const drawer = page.getByRole("dialog", { name: names.shows });
    for (const day of ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]) {
      await drawer.getByRole("button", { name: `Searches on ${day}`, exact: true }).click();
    }
    await expect(drawer.getByRole("button", { name: "Searches off Sunday", exact: true })).toBeVisible();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Save schedule", exact: true }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    requests.stop();
    const put = requests.find("PUT", new RegExp(`^/api/libraries/${ids.shows}/automation$`));
    expect(put).toHaveLength(1);
    expect(put[0].body).toMatchObject({ searchWindowDays: 127, maxItemsPerRun: 10 });
    expect((await libraries(page)).find((l) => l.id === ids.shows)?.searchWindowDays).toBe(127);
  });

  test("the combined cap alone is still used: a save without the two split caps gives both the combined number", async ({ page }) => {
    await open(page, "/search-cycles");
    const saved = await api(page, "PUT", `/api/libraries/${ids.idle}/automation`, { ...automation({ autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false, maxItemsPerRun: 17 }), maxMissingItemsPerRun: undefined, maxUpgradeItemsPerRun: undefined });
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    const stored = (await libraries(page)).find((l) => l.id === ids.idle)!;
    expect(stored.maxMissingItemsPerRun).toBe(17);
    expect(stored.maxUpgradeItemsPerRun).toBe(17);
  });

  test("skip-cycle answers 404 for a library that does not exist and 202 for one that does", async ({ page }) => {
    await open(page, "/search-cycles");
    expect((await api(page, "POST", "/api/libraries/does-not-exist/skip-cycle")).status).toBe(404);
    expect((await api(page, "POST", `/api/libraries/${ids.films}/skip-cycle`)).status).toBe(202);
  });

  test("a save that fails shows the footer error and keeps the form unsaved", async ({ page }) => {
    await open(page, "/search-cycles");
    await row(page, names.idle).click();
    const drawer = page.getByRole("dialog", { name: names.idle });
    await drawer.getByLabel("Search every").selectOption("720");
    // The library goes away while its drawer is open, so the server has nothing to save to.
    expect((await api(page, "DELETE", `/api/libraries/${ids.idle}`)).status).toBeLessThan(300);
    await drawer.getByRole("button", { name: "Save schedule", exact: true }).click();
    await expect(drawer.getByText("Automation could not be saved.", { exact: true })).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Save schedule", exact: true })).toBeVisible();
    await expect(drawer.getByText("Saved just now")).toHaveCount(0);
  });
});
