import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Indexers, the list, against the real server (measure 4, issue 1296): the Protocol cell, sorting by Protocol and
 * Used for, the Last test cell after a failing test, and Select mode with its header checkbox and "N selected" bar. Every indexer
 * points at an address nobody listens on and stays switched off; nothing reaches another app.
 */

const stamp = `ixl${Date.now()}`;
const url = (name: string) => `http://${name}-${stamp}.example.test`;

interface Indexer { id: string; name: string; baseUrl: string; consecutiveFailures?: number }
const indexers = async (page: Page) => (await api<Indexer[]>(page, "GET", "/api/indexers")).body;
const seed = async (page: Page, name: string, extra: Record<string, unknown>) => {
  const made = await api<Indexer>(page, "POST", "/api/indexers", { name: `${name} ${stamp}`, protocol: "torznab", baseUrl: url(name), apiPath: "/api", apiKey: "example-key", priority: 25, categories: "2000", isEnabled: false, ...extra });
  expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
  return made.body;
};
const row = (page: Page, name: string) => page.getByRole("row").filter({ hasText: `${name} ${stamp}` }).first();
const order = async (page: Page) => (await page.getByRole("row").filter({ hasText: stamp }).allInnerTexts()).map((text) => /(alpha|bravo|charlie)/.exec(text)![0]).join();

test.describe("Indexers list", () => {
  test.setTimeout(90_000);

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/indexers");
    await seed(page, "alpha", { protocol: "torznab", mediaScope: "movies" });
    await seed(page, "bravo", { protocol: "newznab", mediaScope: "tv" });
    await seed(page, "charlie", { protocol: "rss", mediaScope: "both" });
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    for (const item of await indexers(page)) if (item.name.endsWith(stamp)) await api(page, "DELETE", `/api/indexers/${item.id}`);
  });

  test("the Protocol cell names the protocol and says Strict sharing only for a strict indexer", async ({ page }) => {
    await expect(row(page, "alpha")).toContainText("Torznab");
    await expect(row(page, "bravo")).toContainText("Newznab");
    await expect(row(page, "alpha")).not.toContainText("Strict sharing");
    await row(page, "alpha").click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("radio", { name: "Yes, it is strict" }).click();
    await drawer.getByRole("button", { name: "Save indexer" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    await drawer.getByRole("button", { name: "Cancel" }).click();
    await expect(drawer).toBeHidden();
    await expect(row(page, "alpha")).toContainText("Strict sharing");
    await expect(row(page, "bravo")).not.toContainText("Strict sharing");
  });

  test("clicking the Protocol and Used for headings sorts the list, and a second click reverses it", async ({ page }) => {
    const protocol = page.getByRole("columnheader", { name: "Protocol" });
    await protocol.getByRole("button").click();
    await expect(protocol).toHaveAttribute("aria-sort", "ascending");
    // Newznab, RSS feed, Torznab by their names.
    await expect.poll(() => order(page)).toBe("bravo,charlie,alpha");
    await protocol.getByRole("button").click();
    await expect(protocol).toHaveAttribute("aria-sort", "descending");
    await expect.poll(() => order(page)).toBe("alpha,charlie,bravo");
    const usedFor = page.getByRole("columnheader", { name: "Used for" });
    await usedFor.getByRole("button").click();
    await expect(usedFor).toHaveAttribute("aria-sort", "ascending");
    // Movies, Movies · TV, TV.
    await expect.poll(() => order(page)).toBe("alpha,charlie,bravo");
  });

  test("a test that fails shows its failure count in the Last test cell", async ({ page }) => {
    // Only an enabled indexer is really asked, so this one is on; its address does not exist, so the test fails.
    await seed(page, "delta", { isEnabled: true });
    await page.reload();
    await untilQuiet(page);
    await row(page, "delta").click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("button", { name: "Test", exact: true }).click();
    await expect.poll(async () => (await indexers(page)).find((item) => item.name === `delta ${stamp}`)?.consecutiveFailures ?? 0).toBeGreaterThanOrEqual(1);
    const failures = (await indexers(page)).find((item) => item.name === `delta ${stamp}`)!.consecutiveFailures;
    await drawer.getByRole("button", { name: "Cancel" }).click();
    await expect(drawer).toBeHidden();
    await expect(row(page, "delta")).toContainText(`${failures} consecutive failure${failures === 1 ? "" : "s"}`);
    await expect(row(page, "alpha")).not.toContainText("consecutive failure");
  });

  test("Select shows checkboxes; the bar says N selected and is gone at zero, the header checkbox is mixed for some and selects all, and Clear selection empties it", async ({ page }) => {
    await page.getByRole("button", { name: "Select", exact: true }).click();
    await expect(page.getByRole("button", { name: "Done", exact: true })).toBeVisible();
    const bar = page.getByRole("toolbar", { name: "Bulk indexer actions" });
    await expect(bar).toHaveCount(0);
    await page.getByRole("checkbox", { name: `Select alpha ${stamp}` }).check();
    await expect(bar).toContainText("1 selected");
    expect(await page.getByRole("checkbox", { name: "Select all indexers" }).evaluate((box: HTMLInputElement) => box.indeterminate), "a partial selection shows the mixed state").toBe(true);
    await page.getByRole("checkbox", { name: "Select all indexers" }).click();
    const visible = await page.getByRole("checkbox", { name: /^Select (?!all)/ }).count();
    await expect(bar).toContainText(`${visible} selected`);
    await expect(page.getByRole("checkbox", { name: "Deselect all indexers" })).toBeChecked();
    await bar.getByRole("button", { name: "Clear selection" }).click();
    await expect(bar).toHaveCount(0);
    await expect(page.getByRole("checkbox", { name: `Select alpha ${stamp}` })).not.toBeChecked();
    await page.getByRole("button", { name: "Done", exact: true }).click();
    await expect(page.getByRole("checkbox", { name: /^Select all indexers/ })).toHaveCount(0);
  });
});
