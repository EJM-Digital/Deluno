import { expect, test, type Page } from "@playwright/test";
import { exerciseColumns } from "../helpers/columns";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Auto-tagging against the real server, nothing stubbed (measure 4, issue 1329): changing Applies to drops the
 * conditions that no longer fit, a changed rule drawer asks before it is thrown away, and the rules table sorts, reorders and resets.
 * Every rule is made through the API with a stamped name and removed afterwards.
 */

interface Rule { id: string; name: string; appliesTo: string; specifications: Array<{ type: string; negate: boolean; value: string }> }
const rules = async (page: Page) => (await api<Rule[]>(page, "GET", "/api/auto-tag-rules")).body;

test.describe("Settings > Auto-tagging", () => {
  test.setTimeout(120_000);
  const stamp = Date.now();
  const made: string[] = [];

  async function makeRule(page: Page, name: string, extra: Record<string, unknown> = {}) {
    const response = await api<Rule>(page, "POST", "/api/auto-tag-rules", {
      name, tags: [`t-${stamp}`], removeAutomatically: false, appliesTo: "both", isEnabled: true,
      specifications: [{ type: "genre", negate: false, value: "Animation" }], ...extra
    });
    expect(response.status, JSON.stringify(response.body)).toBeLessThan(300);
    made.push(response.body.id);
    return response.body;
  }

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/auto-tagging");
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    for (const id of made.splice(0)) await api(page, "DELETE", `/api/auto-tag-rules/${id}`);
  });

  test("choosing Movies drops a TV-only condition from the rule, the kind list stops offering it, and Save stores the narrower rule", async ({ page }) => {
    const name = `Scope ${stamp}`;
    const rule = await makeRule(page, name, { specifications: [{ type: "genre", negate: false, value: "Animation" }, { type: "seriesType", negate: false, value: "anime" }] });
    await page.reload();
    await untilQuiet(page);
    await page.getByText(name, { exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByLabel("Condition 2 kind")).toHaveValue("seriesType");
    await expect(drawer.getByLabel("Condition 2 kind").locator("option", { hasText: "Series type" })).toHaveCount(1);

    await drawer.getByRole("radio", { name: "Movies", exact: true }).click();
    await expect(drawer.getByLabel("Condition 2 kind")).toHaveCount(0);
    await expect(drawer.getByLabel("Condition 1 kind").locator("option", { hasText: "Series type" })).toHaveCount(0);
    await drawer.getByRole("button", { name: "Save rule", exact: true }).click();
    await expect(drawer.getByText("Saved just now", { exact: true })).toBeVisible();

    const stored = (await rules(page)).find((r) => r.id === rule.id)!;
    expect(stored.appliesTo).toBe("movies");
    expect(stored.specifications.map((s) => s.type)).toEqual(["genre"]);
  });

  test("closing a rule drawer with an edit asks to discard; Discard closes and the stored name stays", async ({ page }) => {
    const name = `Dirty ${stamp}`;
    const rule = await makeRule(page, name);
    await page.reload();
    await untilQuiet(page);
    await page.getByText(name, { exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Name").fill(`${name} edited`);
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    const ask = page.getByRole("alertdialog").or(page.getByRole("dialog").last());
    await expect(ask.getByText("Discard unsaved changes?")).toBeVisible();
    await expect(ask.getByText("Your edits to this rule haven't been saved.")).toBeVisible();
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    expect((await rules(page)).find((r) => r.id === rule.id)?.name).toBe(name);
  });

  test("the rules table sorts by a header, reorders by dragging a header, remembers both, and Reset columns puts them back", async ({ page }) => {
    await makeRule(page, `Zeta ${stamp}`);
    await makeRule(page, `Alpha ${stamp}`);
    await makeRule(page, `Mid ${stamp}`);
    await page.reload();
    await untilQuiet(page);
    await exerciseColumns(page, {
      scope: () => page.locator("#main-content"),
      defaultOrder: ["name", "when", "gives", "status", "on"],
      sortId: "name",
      cellsOf: async (scope) => {
        const names = await scope.locator('[role="rowgroup"] [role="row"]').evaluateAll((rows) => rows.map((row) => (row.querySelector('[role="cell"]') as HTMLElement | null)?.innerText.split("\n")[0] ?? ""));
        return names.filter((n) => n.endsWith(` ${stamp}`));
      },
      move: { from: "gives", onto: "name" }
    });
  });
});
