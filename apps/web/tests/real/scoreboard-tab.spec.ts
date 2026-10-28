import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Scoreboard against the real server (measure 4, issue 1296). The indexers are switched off and point at addresses
 * nobody listens on; nothing reaches another app. What the screen shows is compared with what the real scoreboard route answers.
 */

const stamp = `sb${Date.now()}`;
// Set while proving each test can fail: the expected outcome is flipped, and every test below must then go red.
const BREAK = Boolean(process.env.DELUNO_SPEC_BREAK);

interface Row { name: string; totalQueries: number; successfulGrabs: number }
interface Snapshot { activeIndexers: number; totalIndexers: number; totalQueries: number; totalGrabs: number; successfulGrabs: number; windowDays: number; insight: string; indexers: Row[] }
const snapshot = async (page: Page) => (await api<Snapshot>(page, "GET", "/api/indexers/scoreboard")).body;
const open = async (page: Page) => { await openScreen(page, "/indexers/scoreboard"); await untilQuiet(page); };

test.describe("Scoreboard", () => {
  test.setTimeout(90_000);
  const ids: string[] = [];

  test.beforeEach(async ({ page }) => {
    await open(page);
    for (const name of ["alpha", "beta"]) {
      const made = await api<{ id: string }>(page, "POST", "/api/indexers", { name: `${name} ${stamp}`, protocol: "torznab", baseUrl: `http://${name}-${stamp}.example.test`, apiPath: "/api", apiKey: "example-key", priority: 25, categories: "2000", isEnabled: false });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      ids.push(made.body.id);
    }
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    for (const id of ids.splice(0)) await api(page, "DELETE", `/api/indexers/${id}`);
  });

  test("the summary strip shows the active and total indexers, the queries, the grabs and the grab rate the server counted", async ({ page }) => {
    const server = await snapshot(page);
    await expect(page.getByText("Active indexers")).toBeVisible();
    await expect(page.getByText(`${server.activeIndexers}/${server.totalIndexers}`, { exact: true }).first()).toBeVisible();
    await expect(page.getByText(`last ${server.windowDays} days`, { exact: true })).toBeVisible();
    await expect(page.getByText("Total queries")).toBeVisible();
    await expect(page.getByText("Total grabs")).toBeVisible();
    await expect(page.getByText(`${server.successfulGrabs.toLocaleString("en-US")} successful`, { exact: true })).toBeVisible();
    await expect(page.getByText("Query → grab")).toBeVisible();
    await expect(page.getByText("successful grabs per query", { exact: true }).first()).toBeVisible();
    expect(server.totalIndexers).toBeGreaterThanOrEqual(BREAK ? 99 : 2);
  });

  test("Deluno's read is the server's own sentence, with the window and the retention note under it", async ({ page }) => {
    const server = await snapshot(page);
    await expect(page.getByText("Deluno's read", { exact: true })).toBeVisible();
    await expect(page.getByText(BREAK ? "a sentence the server never says" : server.insight, { exact: true })).toBeVisible();
    await expect(page.getByText(`Window: ${server.windowDays} days. Query history is retained for 30 days and excludes credentials and full request URLs.`)).toBeVisible();
  });

  test("clicking the Indexer column sorts the rows by name and clicking it again reverses them", async ({ page }) => {
    const names = async () => (await page.getByRole("row").filter({ hasText: stamp }).allInnerTexts()).map((text) => /alpha|beta/.exec(text)![0]);
    const header = page.getByRole("columnheader", { name: "Indexer" });
    const sorter = header.getByRole("button");
    await sorter.click();
    await expect(header).toHaveAttribute("aria-sort", "ascending");
    await expect.poll(async () => (await names()).length).toBe(2);
    const first = await names();
    await sorter.click();
    await expect(header).toHaveAttribute("aria-sort", "descending");
    await expect.poll(async () => (await names()).join()).not.toBe(first.join());
    const second = await names();
    expect([first, second].map((order) => order.join()).sort()).toEqual(["alpha,beta", "beta,alpha"]);
    // Proving the check can fail: a header that did not reverse would leave both clicks with the same order.
    if (BREAK) expect(second).toEqual(first);
  });
});
