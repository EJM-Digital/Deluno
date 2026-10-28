import { expect, test, type Page } from "@playwright/test";
import { exerciseColumns } from "../helpers/columns";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Import lists (/settings/lists) against the real server, nothing stubbed (measure 4, issue 1329). Lists and
 * exclusions are made through the real API with stamped names. Nothing here fetches a list: a list is only read when it
 * is previewed or synced, and these tests do neither.
 */

interface Source { id: string; name: string; provider: string; feedUrl: string; mediaType: string; searchOnAdd: boolean; isEnabled: boolean; syncIntervalHours: number; libraryId: string | null; monitorMode: string | null; seriesType: string | null; seasonFolder: boolean | null; tags: string | null }
interface Exclusion { id: string; title: string }

const stamp = Date.now();
const sources = async (page: Page) => (await api<Source[]>(page, "GET", "/api/intake-sources")).body;
const exclusions = async (page: Page) => (await api<Exclusion[]>(page, "GET", "/api/exclusions")).body;

async function clearLists(page: Page) {
  for (const source of await sources(page)) await api(page, "DELETE", `/api/intake-sources/${source.id}`);
  for (const exclusion of await exclusions(page)) await api(page, "DELETE", `/api/exclusions/${exclusion.id}`);
}

async function makeSource(page: Page, name: string, extra: Record<string, unknown> = {}) {
  const made = await api<Source>(page, "POST", "/api/intake-sources", {
    name, provider: "url-list", feedUrl: `https://lists.example.test/${stamp}/${encodeURIComponent(name)}.json`, mediaType: "movies",
    libraryId: null, qualityProfileId: null, requiredGenres: "", minimumRating: null, minimumYear: null, maximumAgeDays: null,
    allowedCertifications: "", audience: "any", syncIntervalHours: 24, searchOnAdd: true, isEnabled: true,
    minimumAvailability: "announced", monitorMode: "movie", seriesType: null, seasonFolder: null, tags: "", ...extra
  });
  expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
  return made.body;
}

const namesBefore = (suffix: string) => async (scope: import("@playwright/test").Locator) => {
  const names = await scope.locator('[role="rowgroup"] [role="row"]').evaluateAll((rows) => rows.map((row) => (row.querySelector('[role="cell"]') as HTMLElement | null)?.innerText.split("\n")[0] ?? ""));
  return names.filter((n) => n.includes(suffix.trim()));
};

test.describe("Settings > Import lists", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(45_000);

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/lists");
    await untilQuiet(page);
  });

  test("with no lists the card says 0 lists and No import lists yet, its New list button opens the drawer, and the Never add card says No exclusions", async ({ page }) => {
    await clearLists(page);
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("0 lists", { exact: true })).toBeVisible();
    await expect(page.getByText("No import lists yet", { exact: true })).toBeVisible();
    await expect(page.getByText("Follow a watchlist, a curated list or an RSS feed.", { exact: false })).toBeVisible();
    await expect(page.getByText("No exclusions", { exact: true })).toBeVisible();
    await expect(page.getByText("When you exclude an entry during preview, it appears here so you can review or allow it again later.")).toBeVisible();

    await page.locator("#main-content").getByRole("button", { name: "New list", exact: true }).last().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("New import list", { exact: true }).first()).toBeVisible();
    await expect(drawer.getByText("A watchlist, curated list or feed Deluno should follow.")).toBeVisible();
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });

  test("the heading counts lists and enabled lists, singular for one, and a row reads provider, media type, address, Adds only, Never and Not synced yet", async ({ page }) => {
    await clearLists(page);
    await makeSource(page, `Solo ${stamp}`, { searchOnAdd: false, syncIntervalHours: 12 });
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("1 list · 1 enabled", { exact: true })).toBeVisible();

    const row = page.locator('#main-content [role="row"]').filter({ hasText: `Solo ${stamp}` });
    await expect(row).toContainText("Custom list URL · Movies");
    await expect(row).toContainText(`https://lists.example.test/${stamp}/Solo%20${stamp}.json`);
    await expect(row).toContainText("Adds only");
    await expect(row).toContainText("No library");
    await expect(row).toContainText("Choose a library before syncing");
    await expect(row).toContainText("Never");
    await expect(row).toContainText("Not synced yet");

    await makeSource(page, `Show ${stamp}`, { mediaType: "tv", isEnabled: false, monitorMode: "all", seriesType: "standard" });
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("2 lists · 1 enabled", { exact: true })).toBeVisible();
    const tvRow = page.locator('#main-content [role="row"]').filter({ hasText: `Show ${stamp}` });
    await expect(tvRow).toContainText("Custom list URL · TV");
    await expect(tvRow).toContainText("Adds and searches");
    await expect(tvRow).toContainText("Off");
  });

  test("the import-lists table sorts by a header, reorders by dragging, remembers both and resets", async ({ page }) => {
    await makeSource(page, `Zulu ${stamp}`);
    await makeSource(page, `Alpha ${stamp}`);
    await page.reload();
    await untilQuiet(page);
    await exerciseColumns(page, {
      scope: () => page.locator("#main-content"),
      defaultOrder: ["name", "list", "addsTo", "lastSync", "status", "on"],
      sortId: "name",
      cellsOf: namesBefore(` ${stamp}`),
      move: { from: "addsTo", onto: "name" }
    });
  });

  test("the Never add table lists a decision with its source and Allow again, and sorts, reorders and resets", async ({ page }) => {
    const source = await makeSource(page, `Gate ${stamp}`);
    for (const title of [`Zorro ${stamp}`, `Amelie ${stamp}`, `Matilda ${stamp}`]) {
      const made = await api(page, "POST", `/api/intake-sources/${source.id}/exclude-preview`, { title, year: 2001, imdbId: null, durationDays: null, reason: "Excluded during import list preview" });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    }
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText(/\d+ active decisions? · all automated sources/)).toBeVisible();
    const row = page.locator('#main-content [role="row"]').filter({ hasText: `Amelie ${stamp}` });
    await expect(row).toContainText("2001");
    await expect(row).toContainText(`Gate ${stamp}`);
    await expect(row).toContainText("Import list");
    await expect(row).toContainText("Excluded permanently");
    await expect(row.getByRole("button", { name: "Allow again", exact: true })).toBeVisible();

    await exerciseColumns(page, {
      scope: () => page.locator("#main-content").locator("section,div").filter({ hasText: "Never add decisions" }).last(),
      defaultOrder: ["entry", "source", "decision", "created", "action"],
      sortId: "entry",
      cellsOf: namesBefore(` ${stamp}`),
      move: { from: "source", onto: "entry" }
    });

    const requests = recordRequests(page);
    await row.getByRole("button", { name: "Allow again", exact: true }).click();
    await expect(page.getByText(`Amelie ${stamp} can be added again.`)).toBeVisible();
    requests.stop();
    expect(requests.find("DELETE", /^\/api\/exclusions\/[^/]+$/)).toHaveLength(1);
    expect((await exclusions(page)).some((e) => e.title === `Amelie ${stamp}`)).toBe(false);
  });

  test("the Provider select offers the documented providers and not MDbList or IMDb, and the address help changes with each provider", async ({ page }) => {
    await page.getByRole("button", { name: "New list", exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    const provider = drawer.getByLabel("Provider");
    expect(await provider.locator("option").allInnerTexts()).toEqual([
      "Custom list URL", "Trakt", "TMDb", "TMDb person", "TMDb Popular", "TMDb Discover/Keywords", "TMDb Collection", "TMDb Company Credits",
      "Letterboxd", "RSS feed", "StevenLu", "Trakt Trending (connected)", "Trakt Popular (connected)", "Trakt Anticipated (connected)",
      "Trakt Top Watched (connected)", "Trakt Recommended (connected)"
    ]);
    const help: Record<string, string> = {
      "url-list": "Paste a public list URL. Deluno recognises compatible list sites automatically.",
      trakt: "Paste a Trakt list or watchlist URL. A Trakt username also follows that person's watchlist.",
      tmdb: "Paste a TMDb list URL or list ID.",
      "tmdb-person": "Paste a TMDb person URL or numeric person ID. Choose the credit types Deluno should follow below.",
      "tmdb-popular": "No address needed — reads TMDb's current Popular list for the media type below.",
      "tmdb-discover": "Enter one or more TMDb keyword IDs, comma-separated.",
      "tmdb-collection": "Paste a TMDb collection URL or numeric collection ID. Every movie in the collection is added.",
      "tmdb-company-credits": "Paste a TMDb company URL or numeric company ID.",
      letterboxd: "Paste a public Letterboxd list URL or its RSS feed.",
      rss: "Paste a public RSS or Atom feed URL.",
      stevenlu: "Paste a StevenLu-format JSON feed URL — a plain list of { title, imdb_id } entries, movies only."
    };
    for (const [value, text] of Object.entries(help)) {
      await provider.selectOption(value);
      await expect(drawer.getByText(text, { exact: true }), value).toBeVisible();
    }
    await expect(drawer.getByText("MDbList", { exact: false })).toHaveCount(0);
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
  });

  test("the Check the list presets offer every interval, and the two switches and the interval go to the server as chosen", async ({ page }) => {
    await clearLists(page);
    await page.reload();
    await untilQuiet(page);
    await page.getByRole("button", { name: "New list", exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    expect(await drawer.getByLabel("Check the list").locator("option").allInnerTexts()).toEqual(["Every 6 hours", "Every 12 hours", "Daily", "Every 3 days", "Weekly", "Fortnightly", "Monthly", "Custom interval"]);

    await drawer.getByLabel("Provider").selectOption("rss");
    await drawer.getByLabel("List URL").fill(`https://lists.example.test/${stamp}/feed.xml`);
    await drawer.getByLabel("Check the list").selectOption("168");
    await drawer.getByRole("switch", { name: "Search when a title is added" }).click();
    await drawer.getByRole("switch", { name: /^Enabled/ }).click();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Add list", exact: true }).click();
    await expect(drawer.getByText("List added", { exact: true })).toBeVisible();
    requests.stop();
    const posts = requests.find("POST", /^\/api\/intake-sources$/);
    expect(posts, JSON.stringify(requests.seen)).toHaveLength(1);
    expect(posts[0].body).toMatchObject({ provider: "rss", feedUrl: `https://lists.example.test/${stamp}/feed.xml`, syncIntervalHours: 168, searchOnAdd: false, isEnabled: false });
    const stored = (await sources(page)).find((s) => s.feedUrl.endsWith(`${stamp}/feed.xml`))!;
    expect(stored).toMatchObject({ provider: "rss", syncIntervalHours: 168, searchOnAdd: false, isEnabled: false });
  });

  test("a TV list saves its Monitor, Series type, Season folder and tags, and reopens showing them", async ({ page }) => {
    await clearLists(page);
    await page.reload();
    await untilQuiet(page);
    const tag = `kids-${stamp}`;
    await api(page, "POST", "/api/tags", { name: tag, color: "blue" });
    await page.getByRole("button", { name: "New list", exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("List URL").fill(`https://lists.example.test/${stamp}/tv.json`);
    await drawer.getByRole("radio", { name: "TV shows", exact: true }).click();
    expect(await drawer.getByLabel("Monitor").locator("option").allInnerTexts()).toEqual(["All episodes", "Future episodes only", "Missing episodes", "Existing episodes only", "Most recent season only"]);
    await drawer.getByLabel("Monitor").selectOption("recent");
    await drawer.getByLabel("Series type").selectOption("anime");
    await drawer.getByLabel("Season folder").selectOption("true");
    await drawer.getByRole("textbox", { name: "Tags for titles this list adds" }).fill(tag);
    await drawer.getByRole("button", { name: `Add ${tag} to Tags for titles this list adds` }).click();
    await drawer.getByRole("button", { name: "Add list", exact: true }).click();
    await expect(drawer.getByText("List added", { exact: true })).toBeVisible();
    const stored = (await sources(page)).find((s) => s.feedUrl.endsWith(`${stamp}/tv.json`))!;
    expect(stored).toMatchObject({ mediaType: "tv", monitorMode: "recent", seriesType: "anime", seasonFolder: true, tags: tag });

    await drawer.getByRole("button", { name: "Close", exact: true }).click();
    await page.locator('#main-content [role="row"]').filter({ hasText: `${stamp}/tv` }).first().click();
    await expect(page.getByRole("dialog").getByLabel("Monitor")).toHaveValue("recent");
    await expect(page.getByRole("dialog").getByLabel("Series type")).toHaveValue("anime");
    await expect(page.getByRole("dialog").getByLabel("Season folder")).toHaveValue("true");
  });

  test("the origin lookup asks for a title id and a media type of movies or tv in words, and a title no list added has no origins", async ({ page }) => {
    const noId = await api<{ errors: Record<string, string[]> }>(page, "GET", "/api/intake-title-origins?mediaType=movies");
    expect(noId.status).toBe(400);
    expect(noId.body.errors.entityId).toEqual(["A title ID is required."]);
    const badType = await api<{ errors: Record<string, string[]> }>(page, "GET", "/api/intake-title-origins?mediaType=music&entityId=abc");
    expect(badType.status).toBe(400);
    expect(badType.body.errors.mediaType).toEqual(["Media type must be movies or tv."]);
    for (const type of ["movies", "tv"]) {
      const none = await api<unknown[]>(page, "GET", `/api/intake-title-origins?mediaType=${type}&entityId=no-such-title-${stamp}`);
      expect(none.status).toBe(200);
      expect(none.body).toEqual([]);
    }
  });

  test("the Trakt endpoints refuse a blank client id or secret, and connect and poll refuse while no credentials are stored", async ({ page }) => {
    const status = (await api<{ clientConfigured: boolean; connected: boolean }>(page, "GET", "/api/trakt/status")).body;
    // No spec stores Trakt app credentials (that needs a real Trakt account, #1343), so a fresh smoke host has none.
    expect(status.clientConfigured, "no Trakt app credentials are stored on the smoke host").toBe(false);
    for (const body of [{ clientId: "", clientSecret: "abc" }, { clientId: "abc", clientSecret: " " }, { clientId: "", clientSecret: "" }]) {
      const reply = await api<{ errors: Record<string, string[]> }>(page, "POST", "/api/trakt/credentials", body);
      expect(reply.status).toBe(400);
      expect(reply.body.errors.clientId).toEqual(["Enter the Client ID and Client Secret from your Trakt app."]);
    }
    const connect = await api<{ errors: Record<string, string[]> }>(page, "POST", "/api/trakt/connect");
    expect(connect.status).toBe(400);
    expect(connect.body.errors.clientId).toEqual(["Enter your Trakt app's Client ID and Client Secret first."]);
    const poll = await api<{ errors: Record<string, string[]> }>(page, "POST", "/api/trakt/connect/poll", { deviceCode: "x" });
    expect(poll.status).toBe(400);
    expect(poll.body.errors.clientId).toEqual(["Enter your Trakt app's Client ID and Client Secret first."]);
    expect((await api<{ clientConfigured: boolean }>(page, "GET", "/api/trakt/status")).body.clientConfigured).toBe(false);
  });
});
