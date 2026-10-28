import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Your Libraries (the Library Profiles screen) against the real server (measure 4, issue 1329). The profiles,
 * quality profiles, destination rule and library are real records on temp folders, all named with this run's stamp; nothing
 * reaches another app.
 */

const stamp = `lp${Date.now()}`;
interface Plan { id: string; name: string; mediaType: string; isEnabled: boolean; notes: string | null; qualityProfileId: string | null; destinationRuleId: string | null }
const plans = async (page: Page) => (await api<Plan[]>(page, "GET", "/api/policy-sets")).body;
const mine = async (page: Page) => (await plans(page)).filter((plan) => plan.name.includes(stamp));
const row = (page: Page, text: string) => page.getByRole("row").filter({ hasText: text }).first();

test.describe("Library profiles", () => {
  test.setTimeout(120_000);
  let folderMovies = "";
  let made = { quality: [] as string[], rules: [] as string[], libraries: [] as string[] };
  let qualityMovie = "";
  let qualityShow = "";
  let ruleMovie = "";

  test.beforeEach(async ({ page }) => {
    folderMovies = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-lpm-"));
    made = { quality: [], rules: [], libraries: [] };
    await openScreen(page, "/settings/policy-sets");
    await untilQuiet(page);
    const profile = async (name: string, mediaType: string, cutoff: string) => {
      const res = await api<{ id: string }>(page, "POST", "/api/quality-profiles", { name, mediaType, cutoffQuality: cutoff, allowedQualities: `WEB 720p,${cutoff}`, customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false });
      expect(res.status, JSON.stringify(res.body)).toBeLessThan(300);
      made.quality.push(res.body.id);
      return res.body.id;
    };
    qualityMovie = await profile(`Movie quality ${stamp}`, "movies", "WEB 1080p");
    qualityShow = await profile(`Show quality ${stamp}`, "tv", "WEB 720p");
    const rule = await api<{ id: string }>(page, "POST", "/api/destination-rules", { name: `Movie rule ${stamp}`, mediaType: "movies", matchKind: "genre", matchValue: "Action", rootPath: folderMovies, folderTemplate: "{Movie Title}", priority: 100, isEnabled: true });
    expect(rule.status, JSON.stringify(rule.body)).toBeLessThan(300);
    ruleMovie = rule.body.id;
    made.rules.push(ruleMovie);
    const lib = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Movie library ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folderMovies });
    expect(lib.status, JSON.stringify(lib.body)).toBeLessThan(300);
    made.libraries.push(lib.body.id);
    for (const [name, mediaType, quality] of [[`Films one ${stamp}`, "movies", qualityMovie], [`Films two ${stamp}`, "movies", qualityMovie], [`Shows one ${stamp}`, "tv", qualityShow], [`Shows two ${stamp}`, "tv", qualityShow]]) {
      const plan = await api<{ id: string }>(page, "POST", "/api/policy-sets", { name, mediaType, qualityProfileId: quality, destinationRuleId: null, customFormatIds: "", searchIntervalOverrideHours: null, retryDelayOverrideHours: null, upgradeUntilCutoff: true, isEnabled: true, notes: null, automationIntent: null });
      expect(plan.status, JSON.stringify(plan.body)).toBeLessThan(300);
    }
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    for (const id of made.libraries) await api(page, "DELETE", `/api/libraries/${id}`);
    for (const plan of await mine(page)) await api(page, "DELETE", `/api/policy-sets/${plan.id}`);
    for (const id of made.rules) await api(page, "DELETE", `/api/destination-rules/${id}`);
    for (const id of made.quality) await api(page, "DELETE", `/api/quality-profiles/${id}`);
    fs.rmSync(folderMovies, { recursive: true, force: true });
  });

  test("the filter box narrows the list by name, and a filter that matches nothing says No profiles match with what was typed", async ({ page }) => {
    const filter = page.getByPlaceholder("Filter library profiles");
    await expect(filter).toBeVisible();
    await filter.fill(`Films two ${stamp}`);
    await expect(row(page, `Films two ${stamp}`)).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: `Shows one ${stamp}` })).toHaveCount(0);
    await filter.fill(`nothing-like-this-${stamp}`);
    await expect(page.getByText("No profiles match")).toBeVisible();
    await expect(page.getByText(`Nothing matches “nothing-like-this-${stamp}”.`)).toBeVisible();
  });

  test("the Movies and TV chips keep only that media type's profiles, with a count on each chip", async ({ page }) => {
    await page.getByPlaceholder("Filter library profiles").fill(stamp);
    const chips = page.getByRole("group", { name: "Filter by media type" }).or(page.getByRole("radiogroup", { name: "Filter by media type" }));
    await expect(chips).toContainText("All");
    await expect(chips).toContainText("4");
    await chips.getByText(/^TV/).first().click();
    await expect(row(page, `Shows one ${stamp}`)).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: `Films one ${stamp}` })).toHaveCount(0);
    await chips.getByText(/^Movies/).first().click();
    await expect(row(page, `Films one ${stamp}`)).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: `Shows one ${stamp}` })).toHaveCount(0);
  });

  test("a profile's Enabled switch and private note are saved and read back from the server", async ({ page }) => {
    await row(page, `Films one ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("switch", { name: "Enabled" }).click();
    await drawer.getByLabel("Profile note").fill(`Lounge films ${stamp}`);
    await drawer.getByRole("button", { name: "Save library profile" }).click();
    await expect.poll(async () => (await mine(page)).find((plan) => plan.name === `Films one ${stamp}`)?.isEnabled).toBe(false);
    expect((await mine(page)).find((plan) => plan.name === `Films one ${stamp}`)?.notes).toBe(`Lounge films ${stamp}`);
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);
    await expect(row(page, `Films one ${stamp}`)).toContainText("Off");
    await expect(row(page, `Films one ${stamp}`)).toContainText(`Lounge films ${stamp}`);
  });

  test("the Quality Profile select offers only the media type's profiles and says where the chosen one stops", async ({ page }) => {
    await row(page, `Films one ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    const select = drawer.getByRole("combobox", { name: "Quality Profile" });
    const options = await select.locator("option").allInnerTexts();
    expect(options).toContain(`Movie quality ${stamp}`);
    expect(options).not.toContain(`Show quality ${stamp}`);
    await expect(drawer.getByText("Stops at WEB 1080p. Size Rules are applied automatically.")).toBeVisible();
  });

  test("the Final Destination select offers only the media type's destination rules and defaults to the library folder", async ({ page }) => {
    await row(page, `Films one ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    const select = drawer.getByRole("combobox", { name: "Final Destination" });
    await expect(select).toHaveValue("");
    expect(await select.locator("option").allInnerTexts()).toEqual(expect.arrayContaining(["Use the library folder", `Movie rule ${stamp}`]));
    await select.selectOption({ label: `Movie rule ${stamp}` });
    await drawer.getByRole("button", { name: "Save library profile" }).click();
    await expect.poll(async () => (await mine(page)).find((plan) => plan.name === `Films one ${stamp}`)?.destinationRuleId).toBe(ruleMovie);
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    await page.keyboard.press("Escape");
    await row(page, `Shows one ${stamp}`).click();
    expect(await page.getByRole("dialog").getByRole("combobox", { name: "Final Destination" }).locator("option").allInnerTexts()).not.toContain(`Movie rule ${stamp}`);
  });

  test("switching a new profile from Movies to TV clears the quality profile and the libraries ticked for it", async ({ page }) => {
    await page.getByRole("button", { name: "New library profile" }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("combobox", { name: "Quality Profile" }).selectOption({ label: `Movie quality ${stamp}` });
    const library = drawer.getByRole("switch", { name: new RegExp(`Movie library ${stamp}`) });
    await library.click();
    await expect(library).toBeChecked();
    await drawer.getByRole("radio", { name: "TV shows" }).click();
    await expect(drawer.getByRole("combobox", { name: "Quality Profile" })).toHaveValue("");
    await expect(drawer.getByText(`Movie library ${stamp}`)).toHaveCount(0);
    await drawer.getByRole("radio", { name: "Movies" }).click();
    await expect(drawer.getByRole("combobox", { name: "Quality Profile" })).toHaveValue("");
    await expect(drawer.getByRole("switch", { name: new RegExp(`Movie library ${stamp}`) })).not.toBeChecked();
  });

  test("closing a changed drawer asks before throwing the edit away, and Discard leaves the stored profile as it was", async ({ page }) => {
    await row(page, `Films two ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Name").fill(`Changed ${stamp}`);
    await page.keyboard.press("Escape");
    const ask = page.getByRole("dialog", { name: "Discard unsaved changes?" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer.getByLabel("Name")).toHaveValue(`Changed ${stamp}`);
    await page.keyboard.press("Escape");
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    const names = (await mine(page)).map((plan) => plan.name);
    expect(names).toContain(`Films two ${stamp}`);
    expect(names).not.toContain(`Changed ${stamp}`);
  });
});
