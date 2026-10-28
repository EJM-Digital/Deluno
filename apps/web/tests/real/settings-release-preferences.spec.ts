import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Release Preferences (the advanced release rules) against the real server (measure 4, issue 1329). The rules, a
 * quality profile and a library are real records named with this run's stamp; nothing reaches another app.
 */

const stamp = `rp${Date.now()}`;
interface Format { id: string; name: string; score: number; mediaType: string }
const formats = async (page: Page) => (await api<Format[]>(page, "GET", "/api/custom-formats")).body;
const mine = async (page: Page) => (await formats(page)).filter((format) => format.name.includes(stamp));
const rule = async (page: Page, name: string) => (await mine(page)).find((format) => format.name === name);
const row = (page: Page, text: string) => page.getByRole("row").filter({ hasText: text }).first();
const HDR = JSON.stringify([{ type: "releaseTitle", value: `HDR10${stamp}`, negate: false, required: true }]);

test.describe("Release preferences", () => {
  test.setTimeout(120_000);
  let folder = "";
  let profileId = "";
  let libraryId = "";
  let firstId = "";

  test.beforeEach(async ({ page }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-rp-"));
    await openScreen(page, "/settings/custom-formats");
    await untilQuiet(page);
    for (const [name, mediaType, score, conditions] of [[`Alpha rule ${stamp}`, "movies", 100, HDR], [`Beta rule ${stamp}`, "movies", -100, HDR], [`Gamma rule ${stamp}`, "tv", 0, "[]"]] as const) {
      const made = await api<{ id: string }>(page, "POST", "/api/custom-formats", { name, mediaType, score, trashId: null, conditions, upgradeAllowed: true });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      if (name.startsWith("Alpha")) firstId = made.body.id;
    }
    const profile = await api<{ id: string }>(page, "POST", "/api/quality-profiles", { name: `Uses rule ${stamp}`, mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p,WEB 1080p", customFormatIds: firstId, upgradeUntilCutoff: true, upgradeUnknownItems: false });
    expect(profile.status, JSON.stringify(profile.body)).toBeLessThan(300);
    profileId = profile.body.id;
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Rule library ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder, qualityProfileId: profileId });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await api(page, "DELETE", `/api/quality-profiles/${profileId}`);
    for (const format of await mine(page)) await api(page, "DELETE", `/api/custom-formats/${format.id}`);
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("the list title counts the rules, and each row names its source, what it matches and how many conditions, what intent it carries and which library uses it", async ({ page }) => {
    const all = await formats(page);
    await expect(page.getByText(`${all.length} ${all.length === 1 ? "rule" : "rules"} · full TRaSH and custom controls`, { exact: true })).toBeVisible();
    const alpha = row(page, `Alpha rule ${stamp}`);
    await expect(alpha).toContainText("Written here");
    await expect(alpha).toContainText(`HDR10${stamp}`);
    await expect(alpha).toContainText("1 condition");
    await expect(alpha).toContainText("Prefer");
    await expect(alpha).toContainText(`Rule library ${stamp}`);
    await expect(alpha).toContainText("Applied through the library's quality profile");
    const beta = row(page, `Beta rule ${stamp}`);
    await expect(beta).toContainText("Avoid");
    await expect(beta).toContainText("Not applied to any library yet");
    await expect(row(page, `Gamma rule ${stamp}`)).toContainText("never matches anything");
    // The same description is written out once; the second rule that reads the same says so.
    const reads = await page.getByRole("row").filter({ hasText: stamp }).filter({ hasText: "Reads the same as the rule above" }).count();
    expect(reads).toBe(1);
  });

  test("a rule with no name is refused with Give this rule a name and nothing is sent", async ({ page }) => {
    await page.getByRole("button", { name: "New custom rule" }).first().click();
    const drawer = page.getByRole("dialog");
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Create rule" }).click();
    await expect(drawer.getByText("Give this rule a name.")).toBeVisible();
    requests.stop();
    expect(requests.seen.filter((r) => r.method === "POST" && /custom-formats/.test(r.path))).toEqual([]);
  });

  test("each intent choice is stored as its number and reads back as the same choice", async ({ page }) => {
    const choices: Array<[string, number]> = [["Must not have", -10000], ["Avoid", -100], ["I do not care", 0], ["Prefer", 100], ["Strongly prefer", 500]];
    for (const [label, score] of choices) {
      await row(page, `Gamma rule ${stamp}`).click();
      const drawer = page.getByRole("dialog");
      const choice = drawer.getByRole("radio", { name: new RegExp(`^${label}`) });
      await choice.click();
      if (await drawer.getByRole("button", { name: "Save rule" }).isEnabled()) {
        await drawer.getByRole("button", { name: "Save rule" }).click();
        await expect(drawer.getByText("Saved just now")).toBeVisible();
      }
      await expect.poll(async () => (await rule(page, `Gamma rule ${stamp}`))?.score, label).toBe(score);
      await page.keyboard.press("Escape");
      await expect(drawer).toHaveCount(0);
      await row(page, `Gamma rule ${stamp}`).click();
      await expect(page.getByRole("dialog").getByRole("radio", { name: new RegExp(`^${label}`) })).toBeChecked();
      await page.keyboard.press("Escape");
      await expect(page.getByRole("dialog")).toHaveCount(0);
    }
  });

  test("Advanced legacy input shows the preset for the stored number and a custom number is stored as typed", async ({ page }) => {
    await row(page, `Alpha rule ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("button", { name: /Advanced legacy input/ }).click();
    const preset = drawer.getByRole("combobox").filter({ has: page.locator("option", { hasText: "Prefer (legacy input)" }) });
    await expect(preset.locator("option:checked")).toHaveText("Prefer (legacy input)");
    expect(await preset.locator("option").allInnerTexts()).toEqual(expect.arrayContaining(["Block this release (legacy input)", "Strongly avoid (legacy input)", "Avoid (legacy input)", "Mild preference (legacy input)", "Prefer (legacy input)", "Strongly prefer (legacy input)", "Custom legacy input"]));
    await preset.selectOption({ label: "Custom legacy input" });
    await drawer.getByRole("spinbutton", { name: "Custom legacy input" }).fill("42");
    await drawer.getByRole("button", { name: "Save rule" }).click();
    await expect.poll(async () => (await rule(page, `Alpha rule ${stamp}`))?.score).toBe(42);
  });

  test("a rule's Library impact lists the library that reaches it through its quality profile", async ({ page }) => {
    await row(page, `Alpha rule ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByRole("link", { name: new RegExp(`^Rule library ${stamp}`) })).toHaveAttribute("href", `/settings/libraries?libraryId=${libraryId}`);
    await page.keyboard.press("Escape");
    await row(page, `Beta rule ${stamp}`).click();
    await expect(page.getByRole("dialog").getByRole("link", { name: new RegExp(`^Rule library ${stamp}`) })).toHaveCount(0);
  });

  test("leaving with an unsaved rule edit asks first, and Discard and continue leaves the stored rule as it was", async ({ page }) => {
    await row(page, `Beta rule ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Name", { exact: true }).fill(`Edited ${stamp}`);
    await page.getByRole("link", { name: "Dashboard", includeHidden: true }).first().evaluate((link) => (link as HTMLElement).click()); // the open drawer makes the page behind it inert for the mouse
    const ask = page.getByRole("dialog", { name: "Unsaved changes" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page).toHaveURL(/\/settings\/custom-formats/);
    await expect(drawer.getByLabel("Name", { exact: true })).toHaveValue(`Edited ${stamp}`);
    await page.getByRole("link", { name: "Dashboard", includeHidden: true }).first().evaluate((link) => (link as HTMLElement).click()); // the open drawer makes the page behind it inert for the mouse
    await ask.getByRole("button", { name: "Discard and continue", exact: true }).click();
    await expect(page).not.toHaveURL(/custom-formats/);
    expect(await rule(page, `Beta rule ${stamp}`)).toBeTruthy();
    expect(await rule(page, `Edited ${stamp}`)).toBeFalsy();
  });

  test("a rule's Library impact reads 1 library for one library", async ({ page }) => {
    await row(page, `Alpha rule ${stamp}`).click();
    await expect(page.getByRole("dialog").getByText("1 library", { exact: true })).toBeVisible();
  });

  test("closing the rule drawer with an unsaved edit asks first, Cancel keeps the edit, Discard leaves the stored rule, and with no edit it closes at once", async ({ page }) => {
    const name = `Alpha rule ${stamp}`;
    const edited = `${name} edited`;
    await row(page, name).click();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);

    await row(page, name).click();
    const drawer = page.getByRole("dialog", { name });
    const field = drawer.getByRole("textbox", { name: "Name" });
    await field.fill(edited);
    const ask = page.getByRole("alertdialog").or(page.getByRole("dialog", { name: "Discard unsaved changes?" }));
    for (const close of [() => page.keyboard.press("Escape"), () => drawer.getByRole("button", { name: "Cancel", exact: true }).click()]) {
      await close();
      await expect(ask).toContainText("Discard unsaved changes?");
      await ask.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(ask).toHaveCount(0);
      await expect(field).toHaveValue(edited);
    }
    await page.keyboard.press("Escape");
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    expect(await rule(page, name)).toBeTruthy();
    expect(await rule(page, edited)).toBeUndefined();
  });
});
