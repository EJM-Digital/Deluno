import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Final destinations against the real server (measure 4, issue 1296). The rules point at real temp folders; nothing reaches
 * another app. The movie and TV roots in Settings are set for the media-type test and put back afterwards.
 */

const stamp = `dr${Date.now()}`;

interface Rule { id: string; name: string }
interface Settings { movieRootPath: string | null; seriesRootPath: string | null }
const rules = async (page: Page) => (await api<Rule[]>(page, "GET", "/api/destination-rules")).body;
const row = (page: Page, name: string) => page.getByRole("row").filter({ hasText: name }).first();

test.describe("Destination rules", () => {
  test.setTimeout(90_000);
  let movies = "";
  let shows = "";
  let before: Settings = { movieRootPath: null, seriesRootPath: null };

  test.beforeEach(async ({ page }) => {
    movies = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-dr-movies-"));
    shows = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-dr-shows-"));
    await openScreen(page, "/settings/destination-rules");
    before = (await api<Settings>(page, "GET", "/api/settings")).body;
    const patched = await api(page, "PATCH" as "POST", "/api/settings", { movieRootPath: movies, seriesRootPath: shows });
    expect(patched.status, JSON.stringify(patched.body)).toBeLessThan(300);
    for (const [name, template] of [[`With template ${stamp}`, "{Title} ({Year})"], [`Plain ${stamp}`, ""]]) {
      const made = await api(page, "POST", "/api/destination-rules", { name, mediaType: "movies", matchKind: "genre", matchValue: "Action", rootPath: movies, folderTemplate: template, priority: 100, isEnabled: true });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    }
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    for (const rule of await rules(page)) if (rule.name.includes(stamp)) await api(page, "DELETE", `/api/destination-rules/${rule.id}`);
    await api(page, "PATCH" as "POST", "/api/settings", { movieRootPath: before.movieRootPath ?? "", seriesRootPath: before.seriesRootPath ?? "" });
    fs.rmSync(movies, { recursive: true, force: true });
    fs.rmSync(shows, { recursive: true, force: true });
  });

  test("each rule's row shows what it matches, where it goes, its folder template or the standard naming, and that no Library Profile uses it", async ({ page }) => {
    const templated = row(page, `With template ${stamp}`);
    await expect(templated).toContainText("Genre is Action");
    await expect(templated).toContainText(movies);
    await expect(templated).toContainText("{Title} ({Year})");
    const plain = row(page, `Plain ${stamp}`);
    await expect(plain).toContainText("Standard folder naming");
    await expect(plain).toContainText("Library folder remains the default");
    await row(page, `Plain ${stamp}`).click();
    await expect(page.getByRole("dialog").getByText("No Library Profile uses this rule yet.")).toBeVisible();
  });

  test("switching a new rule from Movies to TV moves the destination folder to the TV root only while it is still the default", async ({ page }) => {
    await page.getByRole("button", { name: "New rule" }).first().click();
    const drawer = page.getByRole("dialog");
    const folder = drawer.getByLabel("Destination folder");
    await expect(folder).toHaveValue(movies);
    await drawer.getByRole("radio", { name: "TV shows" }).click();
    await expect(folder).toHaveValue(shows);
    await drawer.getByRole("radio", { name: "Movies" }).click();
    await expect(folder).toHaveValue(movies);
    const custom = path.join(movies, "elsewhere");
    await folder.fill(custom);
    await drawer.getByRole("radio", { name: "TV shows" }).click();
    await expect(folder).toHaveValue(custom);
  });

  test("closing a changed rule asks before throwing the edit away", async ({ page }) => {
    await row(page, `Plain ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Name", { exact: true }).fill(`Renamed ${stamp}`);
    await page.keyboard.press("Escape");
    const ask = page.getByRole("dialog", { name: "Discard unsaved changes?" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer.getByLabel("Name", { exact: true })).toHaveValue(`Renamed ${stamp}`);
    await page.keyboard.press("Escape");
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    expect((await rules(page)).some((rule) => rule.name === `Renamed ${stamp}`)).toBe(false);
  });

  test("the page title counts the rules and how many are active, from what the server holds", async ({ page }) => {
    const all = await rules(page) as Array<Rule & { isEnabled: boolean }>;
    const active = all.filter((rule) => rule.isEnabled).length;
    await expect(page.getByText(`${all.length} ${all.length === 1 ? "rule" : "rules"} · ${active} active`, { exact: true })).toBeVisible();
  });

  test("a new rule offers the kinds Deluno can place by, an old Quality rule says it cannot place a title", async ({ page }) => {
    const old = await api(page, "POST", "/api/destination-rules", { name: `Old quality ${stamp}`, mediaType: "movies", matchKind: "quality", matchValue: "1080p", rootPath: movies, folderTemplate: "", priority: 100, isEnabled: true });
    expect(old.status, JSON.stringify(old.body)).toBeLessThan(300);
    await page.reload();
    await untilQuiet(page);
    await page.getByRole("button", { name: "New rule" }).first().click();
    const kinds = await page.getByRole("dialog").getByRole("combobox", { name: "Match" }).locator("option").allInnerTexts();
    expect(kinds).toEqual(["Genre", "Tag", "Original language", "Anime", "Certification", "Requested in 4K"]);
    await page.keyboard.press("Escape");
    await row(page, `Old quality ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByRole("combobox", { name: "Match" }).locator("option:checked")).toHaveText("Quality (cannot place a title)");
    await expect(drawer.getByText("This kind of rule cannot place anything")).toBeVisible();
  });

  test("Rule order offers First 10, Early 50, Normal 100 and Late 500 or a custom number, and the chosen number is what the server keeps", async ({ page }) => {
    const priorityOf = async () => ((await rules(page)) as Array<Rule & { priority: number }>).find((rule) => rule.name === `Plain ${stamp}`)?.priority;
    await row(page, `Plain ${stamp}`).click();
    const drawer = page.getByRole("dialog");
    const order = drawer.getByRole("combobox").filter({ has: page.locator("option", { hasText: "Normal (100)" }) });
    expect(await order.locator("option").allInnerTexts()).toEqual(["First (10)", "Early (50)", "Normal (100)", "Late (500)", "Custom order"]);
    await order.selectOption({ label: "Late (500)" });
    await drawer.getByRole("button", { name: "Save rule" }).click();
    await expect.poll(priorityOf).toBe(500);
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    await order.selectOption({ label: "Custom order" });
    await drawer.getByRole("spinbutton", { name: "Custom order" }).fill("7");
    await drawer.getByRole("button", { name: "Save rule" }).click();
    await expect.poll(priorityOf).toBe(7);
  });

  test("a rule's Library impact names the library that reaches it through a Library Profile", async ({ page }) => {
    const rule = (await rules(page)).find((item) => item.name === `With template ${stamp}`)!;
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Impact library ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: movies });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    const plan = await api<{ id: string }>(page, "POST", "/api/policy-sets", { name: `Impact profile ${stamp}`, mediaType: "movies", qualityProfileId: null, destinationRuleId: rule.id, customFormatIds: "", searchIntervalOverrideHours: null, retryDelayOverrideHours: null, upgradeUntilCutoff: true, isEnabled: true, notes: null, automationIntent: null });
    expect(plan.status, JSON.stringify(plan.body)).toBeLessThan(300);
    try {
      const assigned = await api(page, "PUT", `/api/libraries/${library.body.id}/media-plan`, { policySetId: plan.body.id });
      expect(assigned.status, JSON.stringify(assigned.body)).toBeLessThan(300);
      await page.reload();
      await untilQuiet(page);
      await row(page, `With template ${stamp}`).click();
      const drawer = page.getByRole("dialog");
      await expect(drawer.getByRole("link", { name: new RegExp(`^Impact library ${stamp}`) })).toHaveAttribute("href", `/settings/libraries?libraryId=${library.body.id}`);
      await expect(drawer.getByText("No Library Profile uses this rule yet.")).toHaveCount(0);
    } finally {
      await api(page, "DELETE", `/api/libraries/${library.body.id}`);
      await api(page, "DELETE", `/api/policy-sets/${plan.body.id}`);
    }
  });

  test("a rule's Library impact reads 1 library for one library and N libraries for more", async ({ page }) => {
    const rule = (await rules(page)).find((item) => item.name === `With template ${stamp}`)!;
    const made: string[] = [];
    const folders: string[] = [];
    const plan = await api<{ id: string }>(page, "POST", "/api/policy-sets", { name: `Count profile ${stamp}`, mediaType: "movies", qualityProfileId: null, destinationRuleId: rule.id, customFormatIds: "", searchIntervalOverrideHours: null, retryDelayOverrideHours: null, upgradeUntilCutoff: true, isEnabled: true, notes: null, automationIntent: null });
    expect(plan.status, JSON.stringify(plan.body)).toBeLessThan(300);
    try {
      for (const label of ["one", "two"]) {
        const folder = fs.mkdtempSync(path.join(os.tmpdir(), `deluno-real-dr-${label}-`));
        folders.push(folder);
        const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Count ${label} ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
        expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
        made.push(library.body.id);
        const assigned = await api(page, "PUT", `/api/libraries/${library.body.id}/media-plan`, { policySetId: plan.body.id });
        expect(assigned.status, JSON.stringify(assigned.body)).toBeLessThan(300);
        await page.reload();
        await untilQuiet(page);
        await row(page, `With template ${stamp}`).click();
        const drawer = page.getByRole("dialog");
        await expect(drawer.getByText(label === "one" ? "1 library" : "2 libraries", { exact: true })).toBeVisible();
        await page.keyboard.press("Escape");
        await expect(drawer).toHaveCount(0);
      }
    } finally {
      for (const id of made) await api(page, "DELETE", `/api/libraries/${id}`);
      for (const folder of folders) fs.rmSync(folder, { recursive: true, force: true });
      await api(page, "DELETE", `/api/policy-sets/${plan.body.id}`);
    }
  });
});
