import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > General against the real server (measure 4, issue 1329): the log level and the connection checks. Each test
 * puts back what it changed.
 */

interface Settings { checkConnectionsRegularly?: boolean | null; keepComputerAwake?: boolean | null }
const settings = async (page: Page) => (await api<Settings>(page, "GET", "/api/settings")).body;
const level = async (page: Page) => (await api<{ level: string }>(page, "GET", "/api/system/log-level")).body.level;
const patch = (page: Page, body: Settings) => api(page, "PATCH" as "POST", "/api/settings", body);

test.describe("General settings", () => {
  test.setTimeout(90_000);

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/general");
    await untilQuiet(page);
  });

  test("the Log level card shows the level the server is using, and choosing another sets it at once and says so", async ({ page }) => {
    const original = await level(page);
    const select = page.getByRole("combobox", { name: "Log level" });
    await expect(select).toHaveValue(original);
    expect(await select.locator("option").allInnerTexts()).toEqual(["Trace", "Debug", "Info", "Warning", "Error"]);
    const next = original === "Warning" ? "Error" : "Warning";
    try {
      await select.selectOption(next);
      await expect(page.getByText(`Log level set to ${next} — takes effect immediately.`)).toBeVisible();
      expect(await level(page)).toBe(next);
      await page.reload();
      await untilQuiet(page);
      await expect(page.getByRole("combobox", { name: "Log level" })).toHaveValue(next);
    } finally {
      await api(page, "PUT", "/api/system/log-level", { level: original });
    }
  });

  test("the two Connection checks switches save the moment they are switched, with a plain sentence each way, and read back from the server", async ({ page }) => {
    const before = await settings(page);
    const regularly = page.getByRole("switch", { name: "Check connections regularly" });
    const awake = page.getByRole("switch", { name: "Keep this computer awake" });
    await expect(regularly).toBeChecked({ checked: before.checkConnectionsRegularly ?? true });
    await expect(awake).toBeChecked({ checked: before.keepComputerAwake ?? false });
    try {
      await regularly.click();
      await expect(page.getByText((before.checkConnectionsRegularly ?? true) ? "Deluno will only test a connection when you ask." : "Deluno will check connections regularly.")).toBeVisible();
      await expect.poll(async () => (await settings(page)).checkConnectionsRegularly).toBe(!(before.checkConnectionsRegularly ?? true));
      await awake.click();
      await expect(page.getByText((before.keepComputerAwake ?? false) ? "Windows will sleep by its own timers again." : "Deluno is keeping this computer awake.")).toBeVisible();
      await expect.poll(async () => (await settings(page)).keepComputerAwake).toBe(!(before.keepComputerAwake ?? false));
    } finally {
      await patch(page, { checkConnectionsRegularly: before.checkConnectionsRegularly ?? true, keepComputerAwake: before.keepComputerAwake ?? false });
    }
  });

  test("the How Deluno is reached card shows the port the server reports and Yes, No or Unknown for other devices", async ({ page }) => {
    const lan = (await api<{ state: string; port: number | null }>(page, "GET", "/api/system/lan-access")).body;
    const card = page;
    await expect(card.getByText("Read-only — set outside the app")).toBeVisible();
    await expect(card.locator("[role=row]:has-text('Port')").first()).toContainText(String(lan.port));
    const expected = lan.state === "open" ? "Yes" : lan.state === "blocked" ? "No" : "Unknown from here";
    await expect(card.locator("[role=row]:has-text('Other devices on your network can connect')").first()).toContainText(expected);
    await expect(card.getByText("Change either there and restart", { exact: false })).toBeVisible();
  });

  test("turning the tracker-definition schedule on sends {isEnabled, intervalHours}, shows Check every, and a new hour count saves when the field is left", async ({ page }) => {
    const get = async () => (await api<{ isEnabled: boolean; intervalHours: number }>(page, "GET", "/api/trackers/definitions/update-check")).body;
    const before = await get();
    const sw = page.getByRole("switch", { name: "Update tracker definitions automatically" });
    try {
      await api(page, "PUT", "/api/trackers/definitions/update-check", { isEnabled: false, intervalHours: 24 });
      await page.reload();
      await untilQuiet(page);
      await expect(sw).not.toBeChecked();
      await expect(page.getByLabel("Check every")).toHaveCount(0);
      const requests = recordRequests(page);
      await sw.click();
      await expect(page.getByLabel("Check every")).toHaveValue("24");
      await page.getByLabel("Check every").fill("48");
      await page.getByLabel("Check every").blur();
      await expect.poll(async () => (await get()).intervalHours).toBe(48);
      requests.stop();
      const puts = requests.find("PUT", /update-check$/);
      expect(puts[0].body).toEqual({ isEnabled: true, intervalHours: 24 });
      expect(puts.at(-1)?.body).toEqual({ isEnabled: true, intervalHours: 48 });
      expect((await get()).isEnabled).toBe(true);
      await sw.click();
      await expect(page.getByLabel("Check every")).toHaveCount(0);
      await expect.poll(async () => (await get()).isEnabled).toBe(false);
    } finally {
      await api(page, "PUT", "/api/trackers/definitions/update-check", { isEnabled: before.isEnabled, intervalHours: before.intervalHours });
    }
  });

  test("leaving with an unsaved helper address asks, Cancel stays, Discard and continue throws it away, Save and continue saves it", async ({ page }) => {
    const helper = async () => ((await settings(page)) as { cloudflareHelperUrl?: string | null }).cloudflareHelperUrl ?? "";
    const original = await helper();
    const address = `http://127.0.0.1:${8000 + (Date.now() % 900)}`;
    const field = page.getByLabel("Helper address");
    const prompt = page.getByRole("dialog", { name: "Unsaved changes" });
    try {
      await field.fill(address);
      await page.getByRole("link", { name: "Dashboard" }).first().click();
      await expect(prompt).toBeVisible();
      await prompt.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(page).toHaveURL(/\/settings\/general/);
      await expect(field).toHaveValue(address);
      await page.getByRole("link", { name: "Dashboard" }).first().click();
      await prompt.getByRole("button", { name: "Discard and continue", exact: true }).click();
      await expect(page).not.toHaveURL(/settings\/general/);
      expect(await helper()).toBe(original);

      await openScreen(page, "/settings/general");
      await untilQuiet(page);
      await field.fill(address);
      await page.getByRole("link", { name: "Dashboard" }).first().click();
      await prompt.getByRole("button", { name: "Save and continue", exact: true }).click();
      await expect(page).not.toHaveURL(/settings\/general/);
      await expect.poll(helper).toBe(address);
    } finally {
      await patch(page, { cloudflareHelperUrl: original } as Settings);
    }
  });
});
