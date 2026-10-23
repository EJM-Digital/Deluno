import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation & Recovery (search cycles) against the real server, nothing stubbed (measure 4, issue 1270): the Pause/Resume
 * automation button, what happens when a download finishes, the Failed downloads settings (search cap, queue pause, integrity
 * check, content blocklist, what a run-out of strikes does), the Upgrades switch, and a library's schedule row and drawer.
 * Each save is read back from the server; every setting and the library are put back afterwards.
 */

interface Settings { [key: string]: unknown }
interface Blocklist { [key: string]: unknown }
const SETTING_KEYS = [
  "autoStartJobs", "sharingMode", "sharingPrivacyScope", "sharingForHours", "sharingUntilRatio", "sharingStuckAction", "sharingStuckAfterDays", "sharingCompletionAction",
  "maxSearchesPerHourEnabled", "maxSearchesPerHour", "pauseSearchWhenQueueLongEnabled", "pauseSearchWhenQueueLongThreshold",
  "downloadIntegrityCheckEnabled", "downloadIntegrityMinimumRuntimePercent",
  "cleanupBlockReleaseAfterThreshold", "cleanupQueueReplacementAfterThreshold", "cleanupRemoveClientEntryAfterThreshold", "cleanupPurgePayloadAfterThreshold"
];
const settingsOnServer = async (page: Page) => (await api<Settings>(page, "GET", "/api/settings")).body;
const stamp = `sc${Date.now()}`;
const libraryName = `Cycles ${stamp}`;
let original: Settings;
let originalBlocklist: Blocklist;
let originalUpgradeStop: unknown;
let tiers: unknown;
let libraryId = "";
let folder = "";

test.describe("Search cycles", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-cycles-"));
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles");
    original = await settingsOnServer(page);
    originalBlocklist = (await api<Blocklist>(page, "GET", "/api/download-health/content-blocklist")).body;
    const model = (await api<{ tiers: unknown; upgradeStop: unknown }>(page, "GET", "/api/quality-model")).body;
    tiers = model.tiers;
    originalUpgradeStop = model.upgradeStop;
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles");
    await api(page, "PATCH", "/api/settings", Object.fromEntries(SETTING_KEYS.map((key) => [key, original[key]])));
    await api(page, "PUT", "/api/download-health/content-blocklist", originalBlocklist);
    await api(page, "PUT", "/api/quality-model", { tiers, upgradeStop: originalUpgradeStop });
    if (libraryId) await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  const open = async (page: Page, route: string) => { await openScreen(page, route); await untilQuiet(page); };
  const save = async (page: Page) => {
    await page.getByRole("button", { name: "Save automation settings", exact: true }).click();
    await expect(page.getByText("Saved just now").first()).toBeVisible();
  };
  const row = (page: Page) => page.getByRole("row").filter({ hasText: libraryName }).first();

  test("Resume or Pause automation flips the whole app's automation, says so on the button, and is put back", async ({ page }) => {
    await open(page, "/search-cycles");
    const was = original.autoStartJobs === true;
    const button = page.getByRole("button", { name: was ? "Pause automation" : "Resume automation", exact: true });
    const requests = recordRequests(page);
    await button.click();
    await expect.poll(async () => (await settingsOnServer(page)).autoStartJobs).toBe(!was);
    requests.stop();
    expect(requests.find("PUT", /^\/api\/settings\/automation$/)[0].body).toEqual({ isEnabled: !was });
    await expect(page.getByRole("button", { name: was ? "Resume automation" : "Pause automation", exact: true })).toBeVisible();
    await page.getByRole("button", { name: was ? "Resume automation" : "Pause automation", exact: true }).click();
    await expect.poll(async () => (await settingsOnServer(page)).autoStartJobs).toBe(was);
  });

  test("When a download finishes: each choice is shown in words, Save automation settings keeps them, and the page shows them again", async ({ page }) => {
    await open(page, "/search-cycles");
    const mode = page.getByRole("radiogroup", { name: "What Deluno does with the original" });
    await mode.getByRole("radio", { name: "Tidy up now" }).click();
    await expect(page.getByText("Deluno reclaims the space as soon as the import is verified.", { exact: false })).toBeVisible();
    await expect(page.getByRole("radiogroup", { name: "If it can never get there" })).toHaveCount(0);
    await mode.getByRole("radio", { name: "Leave it alone" }).click();
    await expect(page.getByText("Deluno never removes anything from your download client.", { exact: false })).toBeVisible();
    await mode.getByRole("radio", { name: "Share, then tidy up" }).click();
    await page.getByRole("radiogroup", { name: "Tracker privacy this covers" }).getByRole("radio", { name: "Public only" }).click();
    await page.getByRole("radiogroup", { name: "If it can never get there" }).getByRole("radio", { name: "Keep waiting" }).click();
    await expect(page.getByText("Give up after", { exact: true })).toHaveCount(0);
    await page.getByRole("radiogroup", { name: "If it can never get there" }).getByRole("radio", { name: "Give up" }).click();
    await expect(page.getByText("Give up after", { exact: true })).toBeVisible();
    await page.getByRole("radiogroup", { name: "Once the target above is reached" }).getByRole("radio", { name: "Stop seeding without deleting" }).click();

    const requests = recordRequests(page);
    await save(page);
    requests.stop();
    expect(requests.find("PATCH", /^\/api\/settings$/)[0].body).toMatchObject({ sharingMode: "share-then-tidy", sharingPrivacyScope: "public", sharingStuckAction: "give-up", sharingCompletionAction: "stop" });
    expect(await settingsOnServer(page)).toMatchObject({ sharingMode: "share-then-tidy", sharingPrivacyScope: "public", sharingStuckAction: "give-up", sharingCompletionAction: "stop" });
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByRole("radiogroup", { name: "Tracker privacy this covers" }).getByRole("radio", { name: "Public only" })).toBeChecked();
    await expect(page.getByRole("radiogroup", { name: "Once the target above is reached" }).getByRole("radio", { name: "Stop seeding without deleting" })).toBeChecked();
  });

  test("Failed downloads: the search cap, queue pause and integrity check switches, and what a run-out of strikes does, are saved to the settings", async ({ page }) => {
    await open(page, "/search-cycles/failed-downloads");
    const switches = ["Hourly search cap", "Pause automatic searching while downloads pile up", "Download integrity check", "Block this release", "Search for a replacement", "Remove the client entry", "Purge residual files"];
    const before: boolean[] = [];
    for (const name of switches) {
      const toggle = page.getByRole("switch", { name: new RegExp(`^${name}`) });
      before.push(await toggle.isChecked());
      await toggle.click();
    }
    await save(page);
    const stored = await settingsOnServer(page);
    expect([stored.maxSearchesPerHourEnabled, stored.pauseSearchWhenQueueLongEnabled, stored.downloadIntegrityCheckEnabled, stored.cleanupBlockReleaseAfterThreshold, stored.cleanupQueueReplacementAfterThreshold, stored.cleanupRemoveClientEntryAfterThreshold, stored.cleanupPurgePayloadAfterThreshold])
      .toEqual(before.map((value) => !value));
    await page.reload();
    await untilQuiet(page);
    for (const [i, name] of switches.entries()) await expect(page.getByRole("switch", { name: new RegExp(`^${name}`) })).toBeChecked({ checked: !before[i] });

    const link = page.getByRole("link", { name: "Open Download Health", exact: true });
    await link.click();
    await expect(page).toHaveURL(/\/search-cycles\/download-health$/);
  });

  test("Failed downloads: the content blocklist's switch, mode, built-in patterns, custom patterns, address and qBittorrent push are saved and read back", async ({ page }) => {
    await open(page, "/search-cycles/failed-downloads");
    await page.getByRole("switch", { name: /^Check files inside downloads/ }).click();
    await page.getByRole("radio", { name: "Whitelist" }).click();
    await page.getByRole("switch", { name: /^Include Deluno's built-in patterns/ }).click();
    await page.getByLabel("Custom patterns").fill("*.exe\n*.scr");
    await page.getByLabel("Custom remote list URL").fill("https://example.test/blocklist.txt");
    await page.getByRole("switch", { name: /^Push into qBittorrent's Excluded file names/ }).click();
    await save(page);
    const stored = (await api<Blocklist>(page, "GET", "/api/download-health/content-blocklist")).body;
    expect(stored).toMatchObject({ mode: "whitelist", customPatterns: ["*.exe", "*.scr"], remoteUrl: "https://example.test/blocklist.txt" });
    expect(stored.enabled).toBe(!originalBlocklist.enabled);
    expect(stored.useBuiltInPatterns).toBe(!originalBlocklist.useBuiltInPatterns);
    expect(stored.syncToQbittorrentEnabled).toBe(!originalBlocklist.syncToQbittorrentEnabled);
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByLabel("Custom patterns")).toHaveValue("*.exe\n*.scr");
    await expect(page.getByRole("radio", { name: "Whitelist" })).toBeChecked();
  });

  test("Upgrades: Stop searching once the cutoff is met is saved to the quality model", async ({ page }) => {
    await open(page, "/search-cycles/upgrades");
    const toggle = page.getByRole("switch", { name: /^Stop searching once the cutoff is met/ });
    const was = await toggle.isChecked();
    await toggle.click();
    await save(page);
    const model = (await api<{ upgradeStop: { stopWhenCutoffMet: boolean } }>(page, "GET", "/api/quality-model")).body;
    expect(model.upgradeStop.stopWhenCutoffMet).toBe(!was);
  });

  test("a library's row switch pauses and resumes its searches, and its drawer saves the schedule the server then reads back", async ({ page }) => {
    await open(page, "/search-cycles");
    const library = async () => (await api<Array<Record<string, unknown>>>(page, "GET", "/api/libraries")).body.find((l) => l.id === libraryId)!;
    const was = (await library()).autoSearchEnabled === true;
    await row(page).getByRole("switch", { name: new RegExp(`^(Pause|Resume) automation for ${libraryName}$`) }).click();
    await expect.poll(async () => (await library()).autoSearchEnabled).toBe(!was);

    await row(page).click();
    const drawer = page.getByRole("dialog", { name: libraryName });
    await expect(drawer).toBeVisible();
    await drawer.getByLabel("Missing titles per run").selectOption("25");
    await drawer.getByLabel("Upgrade candidates per run").selectOption("5");
    await drawer.getByLabel("Search every").selectOption("180");
    await drawer.getByRole("button", { name: "Searches on Monday", exact: true }).click();
    await expect(drawer.getByRole("button", { name: "Searches off Monday", exact: true })).toBeVisible();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Save schedule", exact: true }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    requests.stop();
    const put = requests.find("PUT", new RegExp(`^/api/libraries/${libraryId}/automation$`));
    expect(put).toHaveLength(1);
    expect(put[0].body).toMatchObject({ maxMissingItemsPerRun: 25, maxUpgradeItemsPerRun: 5, searchIntervalMinutes: 180 });
    expect(await library()).toMatchObject({ maxMissingItemsPerRun: 25, maxUpgradeItemsPerRun: 5, searchIntervalMinutes: 180 });
  });

  test("Search now starts a cycle for the library and Skip next cycle pushes the next one back; both say so", async ({ page }) => {
    await open(page, "/search-cycles");
    await row(page).click();
    const drawer = page.getByRole("dialog", { name: libraryName });
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Search now", exact: true }).click();
    await expect.poll(() => requests.find("POST", new RegExp(`^/api/libraries/${libraryId}/search-now$`)).length).toBe(1);
    await drawer.getByRole("button", { name: "Skip next cycle", exact: true }).click();
    await expect.poll(() => requests.find("POST", new RegExp(`^/api/libraries/${libraryId}/skip-cycle$`)).length).toBe(1);
    requests.stop();
    await expect(page.locator("[data-sonner-toast]").first()).toBeVisible();
  });
});
