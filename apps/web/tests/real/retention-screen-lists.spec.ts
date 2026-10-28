import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation > Retention, the lists and the drawer, against the real server with nothing stubbed (#1297): with no rule the page opens on its
 * empty states and reads "0 rules"; the rules card title counts the rules that are on; "Add a title…" is offered only while a rule is on;
 * closing a drawer with an edit in it asks to discard the changes. Every rule made here is removed again.
 */

interface Rule { id: string; name: string; isEnabled: boolean }

const rules = async (page: Page) => (await api<Rule[]>(page, "GET", "/api/retention/rules")).body;
const rule = (name: string, isEnabled: boolean) => ({
  name, level: "movie", isEnabled, steps: [{ type: "condition", op: "keep", condition: "year:max:2100" }], mediaServerIds: [], removeFromServers: false,
  waitDays: 7, removalMode: "ask", checkIntervalHours: 8
});

async function clearTheRules(page: Page) {
  for (const existing of await rules(page)) await api(page, "DELETE", `/api/retention/rules/${existing.id}`);
}

test.describe("Retention lists and drawer", () => {
  
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/retention");
    await clearTheRules(page);
    await page.close();
  });

  test("with no rule the page opens on its empty states and the rules card reads 0 rules", async ({ page }) => {
    await openScreen(page, "/search-cycles/retention");
    await clearTheRules(page);
    expect((await api<unknown[]>(page, "GET", "/api/retention/leaving-soon")).body).toEqual([]);
    await page.reload();
    await untilQuiet(page);

    await expect(page.getByText("Nothing is leaving soon", { exact: true })).toBeVisible();
    await expect(page.getByText("No retention rules", { exact: true })).toBeVisible();
    await expect(page.getByText("0 rules", { exact: true })).toBeVisible();
    expect((await api<unknown[]>(page, "GET", "/api/retention/keeps")).body).toEqual([]);
    await expect(page.getByText("Kept", { exact: true })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Add a title…", exact: true })).toHaveCount(0);
  });

  test("the rules card counts the rules that are on, and Add a title is offered only while one is on", async ({ page }) => {
    await openScreen(page, "/search-cycles/retention");
    await clearTheRules(page);
    const off = await api(page, "POST", "/api/retention/rules", rule("Rule off", false));
    expect(off.status, JSON.stringify(off.body)).toBe(201);
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("0 of 1 on", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Add a title…", exact: true })).toHaveCount(0);

    const on = await api(page, "POST", "/api/retention/rules", rule("Rule on", true));
    expect(on.status, JSON.stringify(on.body)).toBe(201);
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("1 of 2 on", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Add a title…", exact: true })).toBeVisible();
  });

  test("closing the rule drawer with an edit in it asks to discard the changes, and a clean drawer closes at once", async ({ page }) => {
    await openScreen(page, "/search-cycles/retention");
    await untilQuiet(page);
    await page.getByRole("button", { name: "New rule", exact: true }).first().click();
    const drawer = page.getByRole("dialog").first();
    await drawer.getByLabel("Name", { exact: true }).fill("Edited and abandoned");
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    const ask = page.getByRole("dialog", { name: "Discard unsaved changes?" }).or(page.getByRole("alertdialog"));
    await expect(ask.getByText("Discard unsaved changes?")).toBeVisible();
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    expect((await rules(page)).some((existing) => existing.name === "Edited and abandoned")).toBe(false);

    await page.getByRole("button", { name: "New rule", exact: true }).first().click();
    await page.getByRole("dialog").first().getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });
});
