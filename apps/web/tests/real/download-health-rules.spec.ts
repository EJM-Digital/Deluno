import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation > Download health rules against the real server, nothing stubbed (measure 4, issue 1270): a rule row opens the rule for
 * editing in a drawer titled by its kind, with Save rule instead of Create rule and a Delete section; a new rule has neither. What
 * the drawer saves is read back from the server.
 */

interface Rule { id: string; kind: string; enabled: boolean; strikeLimit: number; threshold: number; actionPreset: string }

const rulesOnServer = async (page: Page) => (await api<Rule[]>(page, "GET", "/api/download-health/rules")).body;

async function clearTheRules(page: Page) {
  for (const rule of await rulesOnServer(page)) await api(page, "DELETE", `/api/download-health/rules/${rule.id}`);
}

test.describe("Download health rules", () => {
  test.describe.configure({ mode: "serial" });

  test("clicking a rule row opens it for editing: the title is the kind, Save rule replaces Create rule and a Delete section appears", async ({ page }) => {
    await openScreen(page, "/search-cycles/download-health");
    await clearTheRules(page);
    const made = await api<Rule>(page, "PUT", "/api/download-health/rules/new", {
      kind: "too-slow", enabled: true, strikeLimit: 4, timerMinutes: 30, appliesTo: "torrents", minCompletionPercent: 0, maxCompletionPercent: 100,
      resetStrikesOnProgress: true, actionPreset: "replace", threshold: 500, privacyScope: "both"
    });
    expect(made.status, JSON.stringify(made.body)).toBe(200);
    await page.reload();
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    await page.getByText("Too slow", { exact: true }).first().click();

    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Too slow", { exact: true }).first()).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Save rule", exact: true })).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Create rule", exact: true })).toHaveCount(0);
    await expect(drawer.getByText("Delete this rule", { exact: true })).toBeVisible();
    await expect(drawer.getByLabel("Strikes before it acts")).toHaveValue("4");
  });

  test("saving the opened rule sends it to its own id and the server keeps the change", async ({ page }) => {
    await openScreen(page, "/search-cycles/download-health");
    await untilQuiet(page);
    const [rule] = await rulesOnServer(page);
    expect(rule?.kind).toBe("too-slow");
    const requests = recordRequests(page);

    await page.getByText("Too slow", { exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Strikes before it acts").fill("7");
    await drawer.getByRole("button", { name: "Save rule", exact: true }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();

    requests.stop();
    const puts = requests.find("PUT", new RegExp(`^/api/download-health/rules/${rule.id}$`));
    expect(puts, JSON.stringify(requests.seen)).toHaveLength(1);
    expect(puts[0].body).toMatchObject({ kind: "too-slow", strikeLimit: 7 });
    expect((await rulesOnServer(page)).find((item) => item.id === rule.id)?.strikeLimit).toBe(7);
  });

  test("a new rule starts with Create rule and no Delete section", async ({ page }) => {
    await openScreen(page, "/search-cycles/download-health");
    await untilQuiet(page);

    await page.getByRole("button", { name: "New rule", exact: true }).first().click();

    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("New download health rule", { exact: true })).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Create rule", exact: true })).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Save rule", exact: true })).toHaveCount(0);
    await expect(drawer.getByText("Delete this rule", { exact: true })).toHaveCount(0);
  });

  test("Delete asks first and the rule is gone from the server afterwards", async ({ page }) => {
    await openScreen(page, "/search-cycles/download-health");
    await untilQuiet(page);
    await page.getByText("Too slow", { exact: true }).first().click();
    const drawer = page.getByRole("dialog");

    await drawer.getByRole("button", { name: "Delete", exact: true }).click();
    const confirm = page.getByRole("dialog").last();
    await confirm.getByRole("button", { name: /^Delete/ }).click();

    await expect.poll(async () => (await rulesOnServer(page)).length).toBe(0);
  });
});
