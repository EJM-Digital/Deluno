import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
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
});
