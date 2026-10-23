import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation > Download health, the cards under the rules, against the real server (measure 4, issue 1270): the Dry run
 * switch, the strike inactivity window, Timing, History retention, the Connectivity check, the Ignored downloads form and
 * Purge all strikes. Each save is read back from the settings the server keeps, and each setting is put back afterwards.
 */

interface Settings {
  downloadHealthDryRunEnabled: boolean;
  downloadHealthStrikeInactivityHours: number;
  downloadHealthDefaultStrikeWindowMinutes: number;
  downloadHealthStrikeThreshold: number;
  downloadThroughputSamplerIntervalMinutes: number;
  historyRetentionDays: number;
  importRecoveryRetentionDays: number;
  downloadHealthConnectivityCheckEnabled: boolean;
  downloadHealthConnectivityCheckUrls: string;
}
interface Ignored { id: string; hash?: string | null; category?: string | null; trackerDomain?: string | null; tag?: string | null; note?: string | null }

const KEYS: Array<keyof Settings> = [
  "downloadHealthDryRunEnabled", "downloadHealthStrikeInactivityHours", "downloadHealthDefaultStrikeWindowMinutes", "downloadHealthStrikeThreshold",
  "downloadThroughputSamplerIntervalMinutes", "historyRetentionDays", "importRecoveryRetentionDays", "downloadHealthConnectivityCheckEnabled", "downloadHealthConnectivityCheckUrls"
];
const settingsOnServer = async (page: Page) => (await api<Settings>(page, "GET", "/api/settings")).body;
const card = (page: Page, title: string) => page.getByRole("region", { name: new RegExp(`^${title}$`, "i") });
let original: Settings;
const stamp = `dh${Date.now()}`;

test.describe("Download health settings", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/download-health");
    original = await settingsOnServer(page);
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/download-health");
    await api(page, "PATCH", "/api/settings", Object.fromEntries(KEYS.map((key) => [key, original[key]])));
    for (const entry of (await api<Ignored[]>(page, "GET", "/api/download-health/ignore-list")).body) {
      if (entry.note?.includes(stamp)) await api(page, "DELETE", `/api/download-health/ignore-list/${entry.id}`);
    }
    await page.close();
  });

  const open = async (page: Page) => { await openScreen(page, "/search-cycles/download-health"); await untilQuiet(page); };

  test("the Dry run switch is saved to the settings the server keeps", async ({ page }) => {
    await open(page);
    const dry = card(page, "Dry run").getByRole("switch", { name: /^Dry run/ });
    const was = original.downloadHealthDryRunEnabled;
    const requests = recordRequests(page);
    await dry.click();
    await expect.poll(async () => (await settingsOnServer(page)).downloadHealthDryRunEnabled).toBe(!was);
    requests.stop();
    expect(requests.find("PATCH", /^\/api\/settings$/)[0].body).toEqual({ downloadHealthDryRunEnabled: !was });
    await page.reload();
    await untilQuiet(page);
    await expect(card(page, "Dry run").getByRole("switch", { name: /^Dry run/ })).toBeChecked({ checked: !was });
  });

  test("Strike inactivity window refuses a number out of range in words, and Save keeps a good one", async ({ page }) => {
    await open(page);
    const box = card(page, "Strike inactivity window");
    const hours = box.getByLabel("Clear strikes after this long without a new one");
    const save = box.getByRole("button", { name: "Save", exact: true });
    await expect(save).toBeDisabled();
    await hours.fill("9999");
    await save.click();
    await expect(box.getByText("Must be between 1 and 720 hours.")).toBeVisible();
    expect((await settingsOnServer(page)).downloadHealthStrikeInactivityHours).toBe(original.downloadHealthStrikeInactivityHours);
    await hours.fill("36");
    await save.click();
    await expect.poll(async () => (await settingsOnServer(page)).downloadHealthStrikeInactivityHours).toBe(36);
    await expect(save).toBeDisabled();
  });

  test("Timing refuses each number out of range in words, and Save timing keeps the check interval, strike count and sampling", async ({ page }) => {
    await open(page);
    const box = card(page, "Timing");
    const every = box.getByLabel("Check every");
    const strikes = box.getByLabel("Strikes before a stalled download is acted on");
    const sampling = box.getByLabel("Speed chart sampling");
    const save = box.getByRole("button", { name: "Save timing", exact: true });
    await every.fill("5000");
    await strikes.fill("0");
    await sampling.fill("99");
    await save.click();
    await expect(box.getByText("Must be between 1 and 1440 minutes.")).toBeVisible();
    await expect(box.getByText("Must be between 1 and 100.")).toBeVisible();
    await expect(box.getByText("Must be between 1 and 60 minutes.")).toBeVisible();
    await every.fill("45");
    await strikes.fill("7");
    await sampling.fill("2");
    await save.click();
    await expect.poll(async () => (await settingsOnServer(page)).downloadHealthDefaultStrikeWindowMinutes).toBe(45);
    const now = await settingsOnServer(page);
    expect([now.downloadHealthStrikeThreshold, now.downloadThroughputSamplerIntervalMinutes]).toEqual([7, 2]);
  });

  test("History retention keeps how long Activity and import-recovery cases are kept", async ({ page }) => {
    await open(page);
    const box = card(page, "History retention");
    await box.getByLabel("Keep Activity and strike history for").fill("120");
    await box.getByLabel("Keep unresolved import-recovery cases for").fill("21");
    await box.getByRole("button", { name: "Save", exact: true }).click();
    await expect.poll(async () => (await settingsOnServer(page)).historyRetentionDays).toBe(120);
    expect((await settingsOnServer(page)).importRecoveryRetentionDays).toBe(21);
  });

  test("the Connectivity check switch and its address list are saved; the list is only editable while the check is on", async ({ page }) => {
    await open(page);
    const box = card(page, "Connectivity check");
    const check = box.getByRole("switch", { name: /^Check before every pass/ });
    const urls = box.getByLabel("URLs to check");
    if (!(await check.isChecked())) await check.click();
    await expect.poll(async () => (await settingsOnServer(page)).downloadHealthConnectivityCheckEnabled).toBe(true);
    await expect(urls).toBeEnabled();
    await urls.fill("https://example.com, https://example.org");
    await box.getByRole("button", { name: "Save", exact: true }).click();
    await expect.poll(async () => (await settingsOnServer(page)).downloadHealthConnectivityCheckUrls).toBe("https://example.com, https://example.org");
    await check.click();
    await expect.poll(async () => (await settingsOnServer(page)).downloadHealthConnectivityCheckEnabled).toBe(false);
    await expect(urls).toBeDisabled();
  });

  test("Ignored downloads: Add needs something to match, saves the entry, lists it in words, and Remove takes it away", async ({ page }) => {
    await open(page);
    const box = card(page, "Ignored downloads");
    const requests = recordRequests(page);
    await box.getByRole("button", { name: "Add", exact: true }).click();
    await expect(page.getByText("Give at least a hash, a client category, a tag, or a tracker domain to ignore.").first()).toBeVisible();
    expect(requests.find("POST", /ignore-list$/)).toEqual([]);

    await box.getByLabel("Tracker domain").fill(`tracker-${stamp}.example.test`);
    await box.getByPlaceholder("Note (optional)").fill(`kept for ${stamp}`);
    await box.getByRole("button", { name: "Add", exact: true }).click();
    await expect(page.getByText("Added to the ignore list").first()).toBeVisible();
    const sent = requests.find("POST", /ignore-list$/);
    expect(sent).toHaveLength(1);
    expect(sent[0].body).toMatchObject({ trackerDomain: `tracker-${stamp}.example.test` });
    const entry = box.getByRole("row").filter({ hasText: `kept for ${stamp}` });
    await expect(entry).toContainText(`tracker-${stamp}.example.test`);
    await entry.getByRole("button", { name: "Remove", exact: true }).click();
    await expect(entry).toHaveCount(0);
    requests.stop();
    expect((await api<Ignored[]>(page, "GET", "/api/download-health/ignore-list")).body.some((e) => e.note?.includes(stamp))).toBe(false);
  });

  test("Purge all strikes asks first; Cancel purges nothing and the confirmed purge says what it cleared", async ({ page }) => {
    await open(page);
    const requests = recordRequests(page);
    await card(page, "Strikes").getByRole("button", { name: "Purge all strikes", exact: true }).click();
    const ask = page.getByRole("dialog", { name: "Purge every recorded strike?" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
    expect(requests.find("POST", /purge-strikes$/)).toEqual([]);
    await card(page, "Strikes").getByRole("button", { name: "Purge all strikes", exact: true }).click();
    await ask.getByRole("button", { name: "Purge all strikes", exact: true }).click();
    await expect.poll(() => requests.find("POST", /purge-strikes$/).length).toBe(1);
    requests.stop();
    await expect(page.getByText(/^(Cleared \d+ recorded strikes?|There was nothing to clear)$/).first()).toBeVisible();
  });
});
