import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation > Download health, the New rule drawer, against the real server (measure 4, issue 1270): every field of a
 * "Too slow" rule is filled, Create rule sends them, and the server keeps what was typed. The rule is removed afterwards.
 */

interface Rule { id: string; kind: string; enabled: boolean; strikeLimit: number; threshold: number; timerMinutes?: number; minCompletionPercent: number; maxCompletionPercent: number; tooSlowMaxTotalMinutes?: number | null; ignoreAboveSizeBytes?: number | null; resetStrikesOnProgress: boolean }
const rules = async (page: Page) => (await api<Rule[]>(page, "GET", "/api/download-health/rules")).body;

test.describe("New download health rule", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);
  let before = new Set<string>();

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/download-health");
    for (const rule of await rules(page)) if (!before.has(rule.id)) await api(page, "DELETE", `/api/download-health/rules/${rule.id}`);
    await page.close();
  });

  test("every field of a Too slow rule is typed in the drawer, Create rule sends them, and the server keeps them", async ({ page }) => {
    await openScreen(page, "/search-cycles/download-health");
    before = new Set((await rules(page)).map((rule) => rule.id));
    await untilQuiet(page);
    await page.getByRole("button", { name: "New rule", exact: true }).first().click();
    const drawer = page.getByRole("dialog", { name: "New download health rule" });
    await drawer.getByLabel("Kind", { exact: true }).selectOption({ label: "Too slow" });
    const slowest = drawer.getByRole("spinbutton", { name: /slower than|Slowest|speed/i }).first();
    await slowest.fill("123");
    await drawer.getByLabel("Speed unit").selectOption("kb");
    await drawer.getByLabel("Maximum total time").fill("600");
    await drawer.getByLabel("Ignore above this size").fill("40000");
    await drawer.getByLabel("Strikes before it acts").fill("5");
    await drawer.getByLabel("From this % complete").fill("10");
    await drawer.getByLabel("Up to this % complete").fill("90");

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Create rule", exact: true }).click();
    await expect.poll(() => requests.find("PUT", /\/api\/download-health\/rules\//).length).toBe(1);
    requests.stop();
    const made = (await rules(page)).find((rule) => !before.has(rule.id))!;
    expect(made).toMatchObject({ kind: "too-slow", strikeLimit: 5, minCompletionPercent: 10, maxCompletionPercent: 90, tooSlowMaxTotalMinutes: 600, ignoreAboveSizeBytes: 40000000000 });
    expect(requests.find("PUT", /\/api\/download-health\/rules\//)[0].body).toMatchObject({ kind: "too-slow", strikeLimit: 5 });
  });

  test("a strike limit out of range is refused in words and nothing is created", async ({ page }) => {
    await openScreen(page, "/search-cycles/download-health");
    const count = (await rules(page)).length;
    await untilQuiet(page);
    await page.getByRole("button", { name: "New rule", exact: true }).first().click();
    const drawer = page.getByRole("dialog", { name: "New download health rule" });
    await drawer.getByLabel("Strikes before it acts").fill("500");
    await drawer.getByRole("button", { name: "Create rule", exact: true }).click();
    await expect(drawer.getByText(/1 and 100|between 1/i).first()).toBeVisible();
    expect((await rules(page)).length).toBe(count);
  });
});
