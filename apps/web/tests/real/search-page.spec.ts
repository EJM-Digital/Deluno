import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * Search (a free search of every indexer, no title needed) against the real server (measure 4, issue 1270). The indexer
 * is a real Torznab address that answers with what Jackett 0.24.2850 really answered (tests/fixtures/recorded/jackett),
 * so the results, the filters and the grab drawer are what a person sees. Nothing is stubbed in the app.
 */

const stamp = `srch${Date.now()}`;
const indexerName = `Search ${stamp}`;
let jackett: RecordedAppServer;
let other: RecordedAppServer;
let indexerId = "";
let secondId = "";
const secondName = `Second ${stamp}`;

test.describe("Search", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    jackett = await startRecordedAppServer([loadRecording("jackett", "0.24.2850", "caps"), loadRecording("jackett", "0.24.2850", "search-text")]);
    // A second indexer that answers with a different release list (Jackett's own feed), so the two can be told apart.
    other = await startRecordedAppServer([loadRecording("jackett", "0.24.2850", "caps"), loadRecording("jackett", "0.24.2850", "search-feed")]);
    const page = await browser.newPage();
    await openScreen(page, "/search");
    const made = await api<{ id: string }>(page, "POST", "/api/indexers", {
      name: indexerName, protocol: "torznab", baseUrl: `${jackett.baseUrl}/api/v2.0/indexers/yts/results/torznab/api`, apiKey: "0123456789abcdef0123456789abcdef",
      privacy: "public", priority: 10, categories: "2000,2040", tags: "", mediaScope: "movies", isEnabled: true
    });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    indexerId = made.body.id;
    const second = await api<{ id: string }>(page, "POST", "/api/indexers", {
      name: secondName, protocol: "torznab", baseUrl: `${other.baseUrl}/api/v2.0/indexers/yts/results/torznab/api`, apiKey: "0123456789abcdef0123456789abcdef",
      privacy: "public", priority: 20, categories: "2000,2040", tags: "", mediaScope: "movies", isEnabled: true
    });
    expect(second.status, JSON.stringify(second.body)).toBeLessThan(300);
    secondId = second.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search");
    for (const id of [indexerId, secondId]) if (id) await api(page, "DELETE", `/api/indexers/${id}`);
    await page.close();
    await jackett.close();
    await other.close();
  });

  const open = async (page: Page) => { await openScreen(page, "/search"); await untilQuiet(page); };

  test("Search with nothing typed says so and sends no search", async ({ page }) => {
    await open(page);
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Search", exact: true }).click();
    await expect(page.getByText("Type something to search for.").first()).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/search$/)).toEqual([]);
  });

  const sintel = "Sintel (2010) 720p BRRip x264 -YTS";
  const queryBox = (page: Page) => page.getByLabel("Query");
  const search = async (page: Page, words: string) => {
    await queryBox(page).fill(words);
    await page.getByRole("button", { name: "Search", exact: true }).click();
    await expect(page.getByText("Results", { exact: true }).first()).toBeVisible();
  };

  test("a search sends the words, type and indexers chosen, and the list shows what each indexer really answered", async ({ page }) => {
    await open(page);
    await expect(page.getByRole("checkbox", { name: indexerName })).not.toBeChecked();
    await page.getByRole("checkbox", { name: indexerName }).click();
    await page.getByRole("checkbox", { name: secondName }).click();
    const requests = recordRequests(page);
    await queryBox(page).fill("Sintel");
    await queryBox(page).press("Enter");
    await expect(page.getByText("Results", { exact: true }).first()).toBeVisible();
    requests.stop();
    const sent = requests.find("POST", /^\/api\/search$/);
    expect(sent).toHaveLength(1);
    expect(sent[0].body).toMatchObject({ query: "Sintel", searchType: "movie", indexerIds: expect.arrayContaining([indexerId, secondId]) });

    await expect(page.getByText(/^1 of \d+ releases$/)).toBeVisible();
    const rows = page.getByRole("row").filter({ hasText: sintel });
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toContainText("Torrent");
    await expect(rows.first()).toContainText("137 MB");
    await expect(rows.first()).toContainText("13 / 14");
    expect(jackett.requests.filter((r) => r.includes("q=Sintel")), "the first indexer was asked").toHaveLength(1);
    expect(other.requests.length, "the second indexer was asked too").toBe(1);
    expect([...jackett.unanswered, ...other.unanswered]).toEqual([]);
  });

  // The second indexer's feed is Jackett's own; its releases are not named Sintel.
  const otherCount = (loadRecording("jackett", "0.24.2850", "search-feed").exchanges[0]!.reply.bodyText ?? "").split("<item>").length - 1;
  const uncheck = async (page: Page) => page.getByRole("checkbox", { name: /^Only names with your words/ }).click();

  test("words that no release name holds are hidden and counted until Only names with your words is unticked", async ({ page }) => {
    await open(page);
    await search(page, "Sintel");
    const total = 1 + otherCount;
    await expect(page.getByText(`1 of ${total} releases`, { exact: true })).toBeVisible();
    const only = page.getByRole("checkbox", { name: /^Only names with your words/ });
    await expect(only).toBeChecked();
    await expect(page.getByText(`Only names with your words (${otherCount} others hidden)`)).toBeVisible();
    await uncheck(page);
    await expect(page.getByText(`${total} of ${total} releases`, { exact: true })).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: "Caterpillar" }).first()).toBeVisible();
  });

  test("the indexer menu narrows the list to one indexer, and Any indexer brings the rest back", async ({ page }) => {
    await open(page);
    await search(page, "Sintel");
    await uncheck(page);
    const total = 1 + otherCount;
    await expect(page.getByText(`${total} of ${total} releases`, { exact: true })).toBeVisible();
    // Both indexers answer as Torrent, so there is nothing to pick between by protocol: its menu is not offered.
    await expect(page.getByRole("combobox", { name: /Filter by protocol/ })).toHaveCount(0);
    await page.getByRole("combobox", { name: /Filter by indexer/ }).click();
    await page.getByRole("listbox").last().getByRole("option", { name: secondName }).dispatchEvent("click");
    await expect(page.getByText(`${otherCount} of ${total} releases`, { exact: true })).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: sintel })).toHaveCount(0);
    await page.getByRole("combobox", { name: /Filter by indexer/ }).click();
    await page.getByRole("listbox").last().getByRole("option", { name: indexerName }).dispatchEvent("click");
    await expect(page.getByText(`1 of ${total} releases`, { exact: true })).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: sintel })).toHaveCount(1);
    await page.getByRole("combobox", { name: /Filter by indexer/ }).click();
    await page.getByRole("listbox").last().getByRole("option", { name: "Any indexer" }).dispatchEvent("click");
    await expect(page.getByText(`${total} of ${total} releases`, { exact: true })).toBeVisible();
  });

  test("Grab opens the release's own drawer, Cancel closes it, and Send to download client says in words why it could not be sent when no client is linked", async ({ page }) => {
    await open(page);
    await search(page, "Sintel");
    const row = page.getByRole("row").filter({ hasText: sintel }).first();
    await row.getByRole("button", { name: "Grab", exact: true }).click();
    const drawer = page.getByRole("dialog", { name: sintel });
    await expect(drawer.getByText("Indexer", { exact: true })).toBeVisible();
    await expect(drawer.getByText("There is no title here to route through a library", { exact: false })).toBeVisible();
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer).toHaveCount(0);

    await row.click();
    await expect(drawer).toBeVisible();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Send to download client", exact: true }).click();
    await expect.poll(() => requests.find("POST", /^\/api\/search\/grab$/).length).toBe(1);
    requests.stop();
    const grab = requests.find("POST", /^\/api\/search\/grab$/)[0];
    expect(grab.body).toMatchObject({ releaseName: sintel, indexerName: expect.stringContaining("srch"), searchType: "movies" });
    // Nothing was sent anywhere, so the drawer stays open on the release and a toast says why.
    await expect(drawer).toBeVisible();
    await expect(page.locator("[data-sonner-toast]").first()).toBeVisible();
  });

  test("Block this release in the drawer asks first, blocks it on the server, and Lift the block puts it back", async ({ page }) => {
    await open(page);
    await search(page, "Sintel");
    await page.getByRole("row").filter({ hasText: sintel }).first().click();
    const drawer = page.getByRole("dialog", { name: sintel });
    await drawer.getByRole("button", { name: `Block "${sintel}"` }).click();
    const ask = page.getByRole("dialog", { name: `Block "${sintel}"?` });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
    expect((await api<Array<{ releaseName?: string }>>(page, "GET", "/api/blocked-releases")).body.some((b) => b.releaseName === sintel)).toBe(false);

    try {
      await drawer.getByRole("button", { name: `Block "${sintel}"` }).click();
      await ask.getByRole("button", { name: "Block this release", exact: true }).click();
      await expect(page.getByText(/blocked; every indexer's copy is refused/).first()).toBeVisible();
      await expect.poll(async () => (await api<Array<{ releaseName?: string }>>(page, "GET", "/api/blocked-releases")).body.some((b) => b.releaseName === sintel)).toBe(true);
      await expect(drawer.getByRole("button", { name: `Lift the block on "${sintel}"` })).toBeVisible();
      await drawer.getByRole("button", { name: `Lift the block on "${sintel}"` }).click();
      await page.getByRole("dialog", { name: `Lift the block on "${sintel}"?` }).getByRole("button", { name: "Lift the block", exact: true }).click();
      await expect.poll(async () => (await api<Array<{ releaseName?: string }>>(page, "GET", "/api/blocked-releases")).body.some((b) => b.releaseName === sintel)).toBe(false);
      await expect(drawer.getByRole("button", { name: `Block "${sintel}"` })).toBeVisible();
    } finally {
      // Whatever happened above, this release is not left blocked.
      const left = (await api<Array<{ id: string; releaseName?: string }>>(page, "GET", "/api/blocked-releases")).body ?? [];
      for (const entry of left.filter((b) => b.releaseName === sintel)) await api(page, "DELETE", `/api/blocked-releases/${entry.id}`);
    }
  });

  test("Indexer History lists the search just made, narrows by indexer and by action, opens the event's detail, and Clear asks first", async ({ page }) => {
    await openScreen(page, "/indexers/history");
    await untilQuiet(page);
    const mine = page.getByRole("row").filter({ hasText: indexerName });
    await expect(mine.first()).toBeVisible();
    await expect(mine.first()).toContainText("Search");

    const find = page.getByPlaceholder("Find an indexer");
    await find.fill(secondName);
    await expect(page.getByRole("row").filter({ hasText: indexerName })).toHaveCount(0);
    await expect(page.getByRole("row").filter({ hasText: secondName }).first()).toBeVisible();
    await find.fill("");
    await expect(mine.first()).toBeVisible();

    // The Action group lives behind More filters; Grab shows only grabs (none from this spec's indexers), Any action brings the rest back.
    await page.getByRole("button", { name: "More filters", exact: true }).click();
    const group = page.getByRole("radiogroup", { name: "Filter by action type" });
    await group.getByRole("radio", { name: "Grab", exact: true }).click();
    await expect(page.getByRole("row").filter({ hasText: indexerName })).toHaveCount(0);
    await group.getByRole("radio", { name: "Any action", exact: true }).click();
    await expect(mine.first()).toBeVisible();
    await page.keyboard.press("Escape");

    await mine.first().click();
    const drawer = page.getByRole("dialog", { name: "Search" });
    await expect(drawer.getByText(indexerName)).toBeVisible();
    await expect(drawer.getByText("Outcome", { exact: true })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);

    const clear = page.getByRole("button", { name: "Clear", exact: true });
    await clear.click();
    const ask = page.getByRole("dialog", { name: "Clear Indexer History?" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
    await expect(mine.first()).toBeVisible();

    const requests = recordRequests(page);
    await clear.click();
    await ask.getByRole("button", { name: "Clear history", exact: true }).click();
    await expect(page.getByText("No indexer activity yet", { exact: true })).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/indexer-history\/clear$/)).toHaveLength(1);
    expect((await api<{ items: unknown[] }>(page, "GET", "/api/indexer-history?pageSize=50")).body.items).toEqual([]);
    await expect(clear).toBeDisabled();
  });
});
