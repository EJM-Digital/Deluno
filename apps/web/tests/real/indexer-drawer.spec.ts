import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Indexers, the indexer drawer, against the real server (measure 4, issue 1296). Every indexer here points at an
 * address nobody listens on and is switched off or never searched; nothing reaches another app. Each test reads back through the
 * real API what the drawer really saved.
 */

const stamp = `ix${Date.now()}`;
const url = (name: string) => `http://${name}-${stamp}.example.test`;
// Set while proving each test can fail: the expected outcome is flipped, and every test below must then go red.
const BREAK = Boolean(process.env.DELUNO_SPEC_BREAK);

interface Indexer {
  id: string; name: string; baseUrl: string; isEnabled: boolean; healthStatus?: string; requestIntervalSeconds?: number | null;
  seasonPackSeedForHours?: number | null; vipWarningAcknowledgedExpiryUtc?: string | null;
}
const indexers = async (page: Page) => (await api<Indexer[]>(page, "GET", "/api/indexers")).body;
const byUrl = async (page: Page, base: string) => (await indexers(page)).find((item) => item.baseUrl === base);
const seed = async (page: Page, name: string, extra: Record<string, unknown> = {}) => {
  const made = await api<Indexer>(page, "POST", "/api/indexers", { name: `${name} ${stamp}`, protocol: "torznab", baseUrl: url(name), apiPath: "/api", apiKey: "example-key", priority: 25, categories: "2000", isEnabled: false, ...extra });
  expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
  return made.body;
};
const open = async (page: Page) => { await openScreen(page, "/indexers"); await untilQuiet(page); };
const closeDrawer = async (page: Page) => {
  await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByRole("dialog")).toBeHidden();
};
const openIndexer = async (page: Page, name: string) => {
  await page.getByRole("row").filter({ hasText: `${name} ${stamp}` }).first().click();
  return page.getByRole("dialog");
};

test.describe("Indexer drawer", () => {
  test.setTimeout(90_000);
  let libraryId = "";
  let libraryFolder = "";

  test.beforeAll(async ({ browser }) => {
    libraryFolder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-ix-"));
    const page = await browser.newPage();
    await openScreen(page, "/indexers");
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Routed ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: libraryFolder });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers");
    for (const item of await indexers(page)) if (item.name.endsWith(stamp)) await api(page, "DELETE", `/api/indexers/${item.id}`);
    if (libraryId) await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(libraryFolder, { recursive: true, force: true });
  });

  test("a new indexer is switched on in the libraries by default, and with that switch off it is left unrouted", async ({ page }) => {
    await open(page);
    for (const [name, routed] of [["routed", true], ["unrouted", false]] as const) {
      await page.getByRole("button", { name: "New indexer" }).first().click();
      const drawer = page.getByRole("dialog");
      await drawer.getByLabel("Protocol").selectOption("torznab");
      await drawer.getByLabel("Name").fill(`${name} ${stamp}`);
      await drawer.getByLabel("Torznab URL").fill(url(name));
      await drawer.getByLabel("API key").fill("example-key");
      const toggle = drawer.getByRole("switch", { name: "Search it in my libraries" });
      await expect(toggle).toBeChecked();
      if (!routed) await toggle.click();
      await drawer.getByRole("button", { name: "Add indexer" }).click();
      await expect(drawer.getByText("Indexer added")).toBeVisible();
      await drawer.getByRole("button", { name: "Cancel" }).click();
      await expect(drawer).toBeHidden();
    }
    const routing = await api<{ sources: Array<{ indexerId: string }> }>(page, "GET", `/api/libraries/${libraryId}/routing`);
    const routedItem = await byUrl(page, url("routed"));
    const unroutedItem = await byUrl(page, url("unrouted"));
    expect(routedItem && unroutedItem).toBeTruthy();
    const ids = routing.body.sources.map((source) => source.indexerId);
    expect(ids).toContain(routedItem!.id);
    if (BREAK) expect(ids).toContain(unroutedItem!.id);
    else expect(ids).not.toContain(unroutedItem!.id);
  });

  test("a fresh create form has a usable Add, and an incomplete one says what is missing instead of doing nothing", async ({ page }) => {
    await open(page);
    const before = (await indexers(page)).length;
    await page.getByRole("button", { name: "New indexer" }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Protocol").selectOption("torznab");
    const add = drawer.getByRole("button", { name: "Add indexer" });
    await expect(add).toBeEnabled();
    await add.click();
    await expect(drawer.getByText(BREAK ? "never shown" : /API key/i).first()).toBeVisible();
    await expect(drawer).toBeVisible();
    expect((await indexers(page)).length).toBe(before);
  });

  test("the Enabled switch is saved with the indexer, and switching it back on starts its health again as untested", async ({ page }) => {
    await open(page);
    await seed(page, "enabled");
    await page.reload();
    await untilQuiet(page);
    const drawer = await openIndexer(page, "enabled");
    const toggle = drawer.getByRole("switch", { name: "Enabled" });
    await expect(toggle).not.toBeChecked();
    await toggle.click();
    await drawer.getByRole("button", { name: "Save indexer" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    await closeDrawer(page);
    const after = await byUrl(page, url("enabled"));
    expect(after?.isEnabled).toBe(!BREAK);
    expect(after?.healthStatus).toBe("untested");
  });

  test("the season-pack seed time shows only when the site has its own sharing rule", async ({ page }) => {
    await open(page);
    await seed(page, "seed");
    await page.reload();
    await untilQuiet(page);
    const drawer = await openIndexer(page, "seed");
    const field = drawer.getByLabel("Season-pack seed time");
    await expect(field).toHaveCount(BREAK ? 1 : 0);
    await drawer.getByRole("radio", { name: "Yes, it is strict" }).click();
    await expect(field).toBeVisible();
    await field.fill("720");
    await drawer.getByRole("button", { name: "Save indexer" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    await closeDrawer(page);
    expect((await byUrl(page, url("seed")))?.seasonPackSeedForHours).toBe(720);
    const again = await openIndexer(page, "seed");
    await again.getByRole("radio", { name: "No, my normal rule" }).click();
    await expect(again.getByLabel("Season-pack seed time")).toHaveCount(0);
  });

  test("the request interval is saved, cleared when blank, and refused outside 2 to 60 seconds", async ({ page }) => {
    await open(page);
    await seed(page, "interval");
    await page.reload();
    await untilQuiet(page);
    let drawer = await openIndexer(page, "interval");
    await drawer.getByRole("button", { name: /Fine-tune/ }).click();
    await drawer.getByLabel("Request interval").fill("10");
    await drawer.getByRole("button", { name: "Save indexer" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    await closeDrawer(page);
    expect((await byUrl(page, url("interval")))?.requestIntervalSeconds).toBe(BREAK ? 11 : 10);

    drawer = await openIndexer(page, "interval");
    await drawer.getByRole("button", { name: /Fine-tune/ }).click();
    await drawer.getByLabel("Request interval").fill("90");
    await drawer.getByRole("button", { name: "Save indexer" }).click();
    await expect(drawer.getByText(/between 2 and 60/i).first()).toBeVisible();
    await expect(drawer).toBeVisible();
    expect((await byUrl(page, url("interval")))?.requestIntervalSeconds).toBe(10);

    await drawer.getByLabel("Request interval").fill("");
    await drawer.getByRole("button", { name: "Save indexer" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    await closeDrawer(page);
    expect((await byUrl(page, url("interval")))?.requestIntervalSeconds ?? null).toBeNull();
  });

  test("a VIP date within a week raises the expiring chip and Acknowledge silences it for that date", async ({ page }) => {
    await open(page);
    await seed(page, "vip", { vipExpiresUtc: new Date(Date.now() + 3 * 86_400_000).toISOString() });
    await page.reload();
    await untilQuiet(page);
    const drawer = await openIndexer(page, "vip");
    await expect(drawer.getByText("VIP expiring soon")).toBeVisible();
    await drawer.getByRole("button", { name: "Acknowledge" }).click();
    const acknowledged = expect.poll(async () => (await byUrl(page, url("vip")))?.vipWarningAcknowledgedExpiryUtc);
    if (BREAK) await acknowledged.toBeFalsy();
    else await acknowledged.toBeTruthy();
    await expect(drawer.getByText("VIP expiring soon")).toHaveCount(0);
  });
});
