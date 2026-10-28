import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The indexer drawer's search details against the real server (measure 4, issue 1296): the category picker, the TV scope, anime
 * categories, the proxy choice, priority presets, the search switches and the number rules. Every indexer points at an address
 * nobody listens on and stays switched off; nothing reaches another app. Each test reads back through the real API what was saved.
 */

const stamp = `ixs${Date.now()}`;
const url = (name: string) => `http://${name}-${stamp}.example.test`;
const plain = (list?: string | null) => (list ?? "").split(",").map((id) => id.trim()).join(",");
const MOVIES = "2000,2010,2020,2030,2040,2045,2050,2060,2070";
const TV = "5000,5010,5020,5030,5040,5045,5050,5060,5070";

interface Indexer {
  id: string; name: string; baseUrl: string; protocol: string; categories: string; animeCategories?: string | null; seasonPackSearchMode?: string | null;
  proxyId?: string | null; priority: number; rssEnabled?: boolean; automaticSearchEnabled?: boolean; interactiveSearchEnabled?: boolean;
  minimumAgeMinutes?: number | null; retentionDays?: number | null; maximumSizeMb?: number | null; availabilityDelayDays?: number | null;
}
const indexers = async (page: Page) => (await api<Indexer[]>(page, "GET", "/api/indexers")).body;
const byUrl = async (page: Page, base: string) => (await indexers(page)).find((item) => item.baseUrl === base);
const seed = async (page: Page, name: string, extra: Record<string, unknown> = {}) => {
  const made = await api<Indexer>(page, "POST", "/api/indexers", { name: `${name} ${stamp}`, protocol: "torznab", baseUrl: url(name), apiPath: "/api", apiKey: "example-key", priority: 25, categories: MOVIES, mediaScope: "movies", isEnabled: false, ...extra });
  expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
  return made.body;
};
const openIndexer = async (page: Page, name: string) => {
  await page.reload();
  await untilQuiet(page);
  await page.getByRole("row").filter({ hasText: `${name} ${stamp}` }).first().click();
  return page.getByRole("dialog");
};
const save = async (page: Page) => {
  const drawer = page.getByRole("dialog");
  await drawer.getByRole("button", { name: "Save indexer" }).click();
  await expect(drawer.getByText("Saved just now")).toBeVisible();
};

test.describe("Indexer drawer search details", () => {
  test.setTimeout(90_000);
  const proxyIds: string[] = [];

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/indexers");
    await untilQuiet(page);
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers");
    for (const item of await indexers(page)) if (item.name.endsWith(stamp)) await api(page, "DELETE", `/api/indexers/${item.id}`);
    for (const id of proxyIds) await api(page, "DELETE", `/api/indexer-proxies/${id}`);
    await page.close();
  });

  test("the category picker ticks and unticks names, lists a custom id with its nearest family, and the summary says standard or custom", async ({ page }) => {
    await seed(page, "cats");
    const drawer = await openIndexer(page, "cats");
    const disclosure = drawer.getByRole("button", { name: /^Categories/ });
    await expect(disclosure).toContainText("The standard Movies categories for Torznab.");
    await disclosure.click();
    const ids = drawer.getByRole("textbox", { name: "Category ids" });
    await drawer.getByRole("group", { name: "Categories" }).getByRole("checkbox", { name: /2040/ }).uncheck();
    await expect(ids).toHaveValue("2000,2010,2020,2030,2045,2050,2060,2070");
    await expect(disclosure).toContainText("Custom: 8 categories chosen for this indexer.");
    await ids.fill("2000,2045,2999");
    await expect(drawer.getByText("(custom, near Movies 2000)")).toBeVisible();
    await expect(disclosure).toContainText("Custom: 3 categories chosen for this indexer.");
    await save(page);
    expect(plain((await byUrl(page, url("cats")))?.categories)).toBe("2000,2045,2999");
    await drawer.getByRole("button", { name: "Use the standard Movies categories" }).click();
    await expect(ids).toHaveValue(MOVIES);
    await expect(disclosure).toContainText("The standard Movies categories for Torznab.");
  });

  test("changing Used for resets the category ids to that scope's standard set, and TV shows the TV scope and anime categories that save and clear", async ({ page }) => {
    await seed(page, "scope");
    const drawer = await openIndexer(page, "scope");
    await drawer.getByRole("button", { name: /^Categories/ }).click();
    await expect(drawer.getByText("TV search scope", { exact: true })).toHaveCount(0);
    await expect(drawer.getByText("Anime categories", { exact: true })).toHaveCount(0);
    await drawer.getByRole("radio", { name: "TV", exact: true }).click();
    const ids = drawer.getByRole("textbox", { name: "Category ids" });
    await expect(ids).toHaveValue(TV);
    await drawer.getByRole("radio", { name: "Season packs only" }).click();
    await drawer.getByRole("textbox", { name: "Anime categories" }).fill("5070,100099");
    await save(page);
    let saved = await byUrl(page, url("scope"));
    expect(plain(saved?.categories)).toBe(TV);
    expect(saved?.seasonPackSearchMode).toBe("season-packs-only");
    expect(plain(saved?.animeCategories)).toBe("5070,100099");
    await drawer.getByRole("radio", { name: "Episodes only" }).click();
    await drawer.getByRole("textbox", { name: "Anime categories" }).fill("");
    await save(page);
    saved = await byUrl(page, url("scope"));
    expect(saved?.seasonPackSearchMode).toBe("episodes-only");
    expect(saved?.animeCategories ?? "").toBe("");
    await drawer.getByRole("radio", { name: "Movies", exact: true }).click();
    await expect(ids).toHaveValue(MOVIES);
    await expect(drawer.getByText("TV search scope", { exact: true })).toHaveCount(0);
  });

  test("a new indexer offers Tracker, Torznab, Newznab, RSS feed and Custom, choosing a protocol resets its category ids, and a saved indexer locks the protocol", async ({ page }) => {
    await page.getByRole("button", { name: "New indexer" }).first().click();
    const drawer = page.getByRole("dialog");
    const protocol = drawer.getByRole("combobox", { name: "Protocol" });
    expect(await protocol.locator("option").allInnerTexts()).toEqual(["Tracker (built in)", "Torznab", "Newznab", "RSS feed", "Custom"]);
    await protocol.selectOption({ label: "Newznab" });
    await expect(drawer.getByText("NZBGeek, DrunkenSlug, NZBCat, or any Newznab-compatible Usenet indexer.")).toBeVisible();
    await drawer.getByRole("button", { name: /^Categories/ }).click();
    const ids = drawer.getByRole("textbox", { name: "Category ids" });
    await expect(ids).toHaveValue(/^2000,.*5050$/);
    await ids.fill("1234");
    await protocol.selectOption({ label: "RSS feed" });
    await expect(ids).toHaveValue("");
    await protocol.selectOption({ label: "Torznab" });
    await expect(ids).toHaveValue(/^2000,.*5050$/);
    await seed(page, "locked");
    const saved = await openIndexer(page, "locked");
    await expect(saved.getByRole("combobox", { name: "Protocol" })).toBeDisabled();
    await expect(saved.getByText("Any Torznab-compatible tracker or private site.")).toHaveCount(0);
  });

  test("the proxy select offers Direct connection and each configured proxy, and the choice is saved and cleared", async ({ page }) => {
    const proxy = await api<{ id: string }>(page, "POST", "/api/indexer-proxies", { proxyType: "http", address: `http://${stamp}-proxy.example.test:8080`, isEnabled: true });
    expect(proxy.status, JSON.stringify(proxy.body)).toBeLessThan(300);
    proxyIds.push(proxy.body.id);
    await seed(page, "proxy");
    const drawer = await openIndexer(page, "proxy");
    await drawer.getByRole("button", { name: /Fine-tune/ }).click();
    const select = drawer.getByRole("combobox", { name: "Proxy" });
    await expect(select).toHaveValue("");
    const options = await select.locator("option").allInnerTexts();
    expect(options[0]).toBe("Direct connection (no proxy)");
    expect(options.length).toBeGreaterThanOrEqual(2);
    await select.selectOption(proxy.body.id);
    await save(page);
    expect((await byUrl(page, url("proxy")))?.proxyId).toBe(proxy.body.id);
    await select.selectOption("");
    await save(page);
    expect((await byUrl(page, url("proxy")))?.proxyId ?? null).toBeNull();
  });

  test("priority presets save their number, a custom number is kept, and a new indexer starts at Normal", async ({ page }) => {
    await page.getByRole("button", { name: "New indexer" }).first().click();
    const fresh = page.getByRole("dialog");
    const normal = fresh.getByRole("combobox").filter({ hasText: "Normal (10)" });
    await expect(normal).toHaveValue("10");
    expect(await normal.locator("option").allInnerTexts()).toEqual(["Highest (1)", "High (5)", "Normal (10)", "Low (25)", "Fallback only (50)", "Custom priority"]);
    await seed(page, "prio");
    const drawer = await openIndexer(page, "prio");
    const select = drawer.getByRole("combobox").filter({ hasText: "Low (25)" });
    await select.selectOption({ label: "Highest (1)" });
    await save(page);
    expect((await byUrl(page, url("prio")))?.priority).toBe(1);
    await select.selectOption({ label: "Custom priority" });
    await drawer.getByPlaceholder("1–50").fill("37");
    await save(page);
    expect((await byUrl(page, url("prio")))?.priority).toBe(37);
  });

  test("the RSS feed, Automatic search and Interactive search switches are saved with the indexer", async ({ page }) => {
    await seed(page, "switches");
    const drawer = await openIndexer(page, "switches");
    for (const name of ["RSS feed", "Automatic search", "Interactive search"]) await expect(drawer.getByRole("switch", { name: new RegExp(`^${name}`) })).toBeChecked();
    await drawer.getByRole("switch", { name: /^RSS feed/ }).click();
    await drawer.getByRole("switch", { name: /^Interactive search/ }).click();
    await save(page);
    const saved = await byUrl(page, url("switches"));
    expect([saved?.rssEnabled, saved?.automaticSearchEnabled, saved?.interactiveSearchEnabled]).toEqual([false, true, false]);
  });

  test("minimum age, retention, maximum size and availability delay take whole non-negative numbers only", async ({ page }) => {
    await seed(page, "numbers");
    const drawer = await openIndexer(page, "numbers");
    await drawer.getByRole("button", { name: /Timing, limits, and flags/ }).click();
    for (const [label, value] of [["Minimum age", "-5"], ["Retention", "2.5"], ["Maximum size", "-8"], ["Availability delay", "-1"]]) await drawer.getByLabel(label, { exact: true }).fill(value);
    await drawer.getByRole("button", { name: "Save indexer" }).click();
    await expect(drawer.getByText(/whole/i).first()).toBeVisible();
    expect(await drawer.getByText(/whole/i).count()).toBeGreaterThanOrEqual(4);
    expect((await byUrl(page, url("numbers")))?.minimumAgeMinutes ?? null).toBeNull();
    for (const [label, value] of [["Minimum age", "30"], ["Retention", "1500"], ["Maximum size", "8000"], ["Availability delay", "2"]]) await drawer.getByLabel(label, { exact: true }).fill(value);
    await save(page);
    const saved = await byUrl(page, url("numbers"));
    expect([saved?.minimumAgeMinutes, saved?.retentionDays, saved?.maximumSizeMb, saved?.availabilityDelayDays]).toEqual([30, 1500, 8000, 2]);
  });

  test("the API key stays hidden until Reveal is pressed and hides again on Hide", async ({ page }) => {
    await seed(page, "key");
    const drawer = await openIndexer(page, "key");
    const key = drawer.getByLabel("API key");
    await expect(key).toHaveAttribute("type", "password");
    await drawer.getByRole("button", { name: "Reveal" }).click();
    await expect(key).toHaveAttribute("type", "text");
    await drawer.getByRole("button", { name: "Hide" }).click();
    await expect(key).toHaveAttribute("type", "password");
  });
});
