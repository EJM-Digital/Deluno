import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * Discover's lists and filters against the real server (issue 1429, measure 4). The metadata broker is a real address that answers
 * with what the project's gateway really answered for TMDb's popular lists (tests/fixtures/recorded/tmdb). TMDb's trending, top
 * rated, upcoming, on-air and filtered discover lists answer in that same list shape, so the recorded popular reply is the body
 * each of those routes returns. The server's own ask to the broker is read back from what the broker received: the list route
 * and every filter. Nothing in the app is stubbed.
 */

const VERSION = "2026-10-18";
let broker: RecordedAppServer;
let before: { metadataProviderMode?: string; metadataBrokerUrl?: string } = {};

function recorded(state: "popular-movies" | "popular-tv", paths: string[]) {
  const base = loadRecording("tmdb", VERSION, state);
  return {
    ...base,
    exchanges: paths.map((path) => ({ ...base.exchanges[0], request: { method: "GET", path } }))
  };
}

const firstTitle = (state: "popular-movies" | "popular-tv") => {
  const body = loadRecording("tmdb", VERSION, state).exchanges[0].reply.body as { results: Array<{ title?: string; name?: string }> };
  return (body.results[0].title ?? body.results[0].name)!;
};

const asked = (pattern: RegExp) => broker.requests.filter((line) => pattern.test(line));

async function open(page: Page) {
  await openScreen(page, "/discover");
  await untilQuiet(page);
  await expectNoErrorScreen(page);
}

test.describe("Discover lists and filters", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    const films = ["popular", "trending", "top-rated", "upcoming", "now-playing", "discover"].map((list) => `/metadata/movies/${list}`);
    const shows = ["popular", "trending", "top-rated", "now-playing", "discover"].map((list) => `/metadata/tv/${list}`);
    broker = await startRecordedAppServer([recorded("popular-movies", films), recorded("popular-tv", shows)]);
    const page = await browser.newPage();
    await openScreen(page, "/discover");
    before = (await api<typeof before>(page, "GET", "/api/settings")).body;
    const set = await api(page, "PATCH", "/api/settings", { metadataProviderMode: "broker", metadataBrokerUrl: broker.baseUrl });
    expect(set.status, JSON.stringify(set.body)).toBeLessThan(300);
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/discover");
    await api(page, "PATCH", "/api/settings", { metadataProviderMode: before.metadataProviderMode, metadataBrokerUrl: before.metadataBrokerUrl ?? "" });
    await page.close();
    await broker.close();
  });

  test("with nothing typed, Discover opens on the popular list and offers trending, top rated, upcoming and in cinemas for films", async ({ page }) => {
    await open(page);
    await page.evaluate(() => localStorage.setItem("deluno-discover-type", "movies"));
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByRole("heading", { name: "Popular on TMDb" })).toBeVisible();
    await expect(page.getByText(firstTitle("popular-movies")).first()).toBeVisible();
    expect(asked(/^GET \/metadata\/movies\/popular\?page=1/).length).toBeGreaterThan(0);

    const lists = page.getByRole("radiogroup", { name: "Discover list" });
    for (const name of ["Popular", "Trending", "Top rated", "Upcoming", "In cinemas"]) await expect(lists.getByRole("radio", { name })).toBeVisible();
  });

  test("each list is read from the broker's own route for it", async ({ page }) => {
    await open(page);
    const lists = page.getByRole("radiogroup", { name: "Discover list" });
    const cases: Array<[string, RegExp, string]> = [
      ["Trending", /^GET \/metadata\/movies\/trending\?page=1&window=week/, "Trending this week on TMDb"],
      ["Top rated", /^GET \/metadata\/movies\/top-rated\?page=1/, "Top rated on TMDb"],
      ["Upcoming", /^GET \/metadata\/movies\/upcoming\?page=1/, "Upcoming films on TMDb"],
      ["In cinemas", /^GET \/metadata\/movies\/now-playing\?page=1/, "In cinemas now, on TMDb"]
    ];
    for (const [name, route, heading] of cases) {
      await lists.getByRole("radio", { name }).click();
      await expect(page.getByRole("heading", { name: heading })).toBeVisible();
      await expect(page.getByText(firstTitle("popular-movies")).first()).toBeVisible();
      expect(asked(route).length, `${name} asked the broker`).toBeGreaterThan(0);
    }
  });

  test("shows have their own lists: popular, trending, top rated and on air, with no upcoming", async ({ page }) => {
    await open(page);
    await page.getByRole("radio", { name: "TV" }).click();
    await expect(page.getByText(firstTitle("popular-tv")).first()).toBeVisible();
    const lists = page.getByRole("radiogroup", { name: "Discover list" });
    await expect(lists.getByRole("radio", { name: "Upcoming" })).toHaveCount(0);
    for (const [name, route] of [["Trending", /^GET \/metadata\/tv\/trending\?page=1&window=week/], ["Top rated", /^GET \/metadata\/tv\/top-rated\?page=1/], ["On air", /^GET \/metadata\/tv\/now-playing\?page=1/]] as const) {
      await lists.getByRole("radio", { name }).click();
      await expect(page.getByText(firstTitle("popular-tv")).first()).toBeVisible();
      expect(asked(route).length, `${name} asked the broker`).toBeGreaterThan(0);
    }
  });

  test("each film filter reaches the broker as TMDb's own discover field, and clearing them goes back to the list", async ({ page }) => {
    await open(page);
    await page.evaluate(() => localStorage.setItem("deluno-discover-type", "movies"));
    await page.reload();
    await untilQuiet(page);
    await page.getByRole("button", { name: "Filters" }).click();
    const bar = page.getByRole("group", { name: "Discover filters" });

    await bar.getByRole("combobox", { name: "Release" }).selectOption("upcoming");
    await expect(page.getByRole("heading", { name: "Titles that match your filters" })).toBeVisible();
    await bar.getByRole("textbox", { name: "From year" }).fill("2027");
    await bar.getByRole("textbox", { name: "To year" }).fill("2030");
    await bar.getByRole("button", { name: "Action", exact: true }).click();
    await bar.getByRole("button", { name: "Adventure", exact: true }).click();
    await bar.getByRole("combobox", { name: "Lowest rating" }).selectOption("7");
    await bar.getByRole("combobox", { name: "Original language" }).selectOption("ja");
    await bar.getByRole("combobox", { name: "Order" }).selectOption("rating");
    await untilQuiet(page);
    await expect(page.getByText(firstTitle("popular-movies")).first()).toBeVisible();

    const last = asked(/^GET \/metadata\/movies\/discover\?/).at(-1)!;
    const query = new URL(last.replace(/^GET /, ""), "http://broker.invalid").searchParams;
    expect(query.get("with_genres")).toBe("28,12");
    expect(query.get("vote_average.gte")).toBe("7");
    expect(query.get("with_original_language")).toBe("ja");
    expect(query.get("sort_by")).toBe("vote_average.desc");
    expect(query.get("primary_release_date.lte")).toBe("2030-12-31");
    // Upcoming means from tomorrow on, and later than the year asked from, which is the later of the two.
    expect(query.get("primary_release_date.gte")! >= "2027-01-01").toBe(true);
    expect(query.get("vote_count.gte")).toBe("200");
    await expect(page.getByText(/Showing films: not out yet, 2027 to 2030, Action and Adventure, rated 7\+, in Japanese\./)).toBeVisible();

    await bar.getByRole("button", { name: "Clear filters" }).click();
    await expect(page.getByRole("heading", { name: "Popular on TMDb" })).toBeVisible();
  });

  test("a show is filtered on its first air date and its own genres, and released means up to today", async ({ page }) => {
    await open(page);
    await page.getByRole("radio", { name: "TV" }).click();
    await page.getByRole("button", { name: "Filters" }).click();
    const bar = page.getByRole("group", { name: "Discover filters" });
    await expect(bar.getByRole("button", { name: "Kids", exact: true })).toBeVisible();
    await bar.getByRole("combobox", { name: "Release" }).selectOption("released");
    await bar.getByRole("button", { name: "Drama", exact: true }).click();
    await bar.getByRole("combobox", { name: "Order" }).selectOption("newest");
    await untilQuiet(page);
    await expect(page.getByText(firstTitle("popular-tv")).first()).toBeVisible();

    const last = asked(/^GET \/metadata\/tv\/discover\?/).at(-1)!;
    const query = new URL(last.replace(/^GET /, ""), "http://broker.invalid").searchParams;
    expect(query.get("with_genres")).toBe("18");
    expect(query.get("sort_by")).toBe("first_air_date.desc");
    expect(query.get("first_air_date.lte")).toBe(new Date().toISOString().slice(0, 10));
    expect(query.has("primary_release_date.lte")).toBe(false);
  });

  test("Show more asks the broker for the next page", async ({ page }) => {
    await open(page);
    await page.evaluate(() => localStorage.setItem("deluno-discover-type", "movies"));
    await page.reload();
    await untilQuiet(page);
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Show more" }).click();
    await expect.poll(() => asked(/^GET \/metadata\/movies\/popular\?page=2/).length).toBeGreaterThan(0);
    expect(requests.find("GET", /^\/api\/discover$/).some((r) => r.search.includes("page=2"))).toBe(true);
  });
});
