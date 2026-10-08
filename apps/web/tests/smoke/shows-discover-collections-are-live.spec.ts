import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1179: a show changing from any path changes its page, the TV shelf, Discover, Collections and Season
// Pass by themselves. Where the server really announces the write (a show added or edited over the API,
// as another tab would) the test makes that change and the server's own push does the rest. A screen
// that reads from an outside provider or needs a whole catalogue is given one stubbed answer that flips
// when the change is made, so the test can see the screen take it. Nothing reloads.

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/** Calls a Testing-only endpoint as the signed-in user. */
async function server(page: Page, path: string, body?: unknown) {
  return page.evaluate(async ({ path, body }) => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch(path, {
      method: body === undefined ? "GET" : "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    return { status: response.status, json: response.status === 200 ? await response.json() : null };
  }, { path, body });
}

// These titles must not outlive the test: later specs in the same shared database assume an empty library.
const seededSeries: string[] = [];
const seededMovies: string[] = [];

test.afterEach(async ({ page }) => {
  const headers = await authHeaders(page);
  for (const seriesId of seededSeries.splice(0)) {
    await page.request.post("/api/series/bulk", { headers, data: { seriesIds: [seriesId], operation: "remove" } });
  }
  for (const movieId of seededMovies.splice(0)) {
    await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movieId], operation: "remove" } });
  }
});

async function addSeries(page: Page, title: string) {
  const series = await (await page.request.post("/api/series/", {
    headers: await authHeaders(page),
    data: { title, startYear: 2024, monitored: true }
  })).json() as { id: string };
  seededSeries.push(series.id);
  return series.id;
}

async function addMovie(page: Page, title: string) {
  const movie = await (await page.request.post("/api/movies/", {
    headers: await authHeaders(page),
    data: { title, releaseYear: 1962, monitored: true }
  })).json() as { id: string };
  seededMovies.push(movie.id);
  return movie.id;
}

function countNavigations(page: Page) {
  const state = { count: 0 };
  page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) state.count += 1; });
  return state;
}

async function watching(page: Page, subject: "dashboard" | "title", titleId?: string) {
  await expect.poll(async () => {
    const result = await server(page, `/api/testing/realtime/watchers${titleId ? `?title=${titleId}` : ""}`);
    const counts = result.json as { dashboard: number; title: number };
    return subject === "dashboard" ? counts.dashboard : counts.title;
  }, { timeout: 15_000, message: `the page never joined the ${subject} subject` }).toBeGreaterThan(0);
}

test.describe("shows, Discover, Collections and Season Pass are live (#1179)", () => {
  test("a show's page follows a change made elsewhere, from the database write itself", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/tv");
    const stamp = Date.now();
    const seriesId = await addSeries(page, `Live page show ${stamp}`);
    const navigations = countNavigations(page);

    await page.goto(`/tv/${seriesId}`);
    await expect(page.getByRole("heading", { name: `Live page show ${stamp}` }).first()).toBeVisible();
    const afterLoad = navigations.count;
    await watching(page, "title", seriesId);

    // Written by something other than this page, as a background job would.
    const overview = `Show written elsewhere ${stamp}`;
    const response = await page.request.put(`/api/series/${seriesId}/metadata/override`, {
      headers: await authHeaders(page),
      data: { overview }
    });
    expect(response.ok()).toBe(true);
    await expect(page.getByText(overview).first()).toBeVisible({ timeout: 15_000 });

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });

  test("a show's page follows a pushed change to it with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/tv");
    const stamp = Date.now();
    const seriesId = await addSeries(page, `Live push show ${stamp}`);
    let renamed: string | null = null;
    await page.route(new RegExp(`/api/series/${seriesId}$`), async (route) => {
      const response = await route.fetch();
      const body = await response.json();
      await route.fulfill({ response, json: renamed ? { ...body, title: renamed } : body });
    });
    const navigations = countNavigations(page);

    await page.goto(`/tv/${seriesId}`);
    await expect(page.getByRole("heading", { name: `Live push show ${stamp}` }).first()).toBeVisible();
    const afterLoad = navigations.count;
    await watching(page, "title", seriesId);

    renamed = `Live push show renamed ${stamp}`;
    expect((await server(page, "/api/testing/realtime/title-changed", { entityType: "Series", id: seriesId })).status).toBe(204);
    await expect(page.getByRole("heading", { name: `Live push show renamed ${stamp}` }).first()).toBeVisible({ timeout: 15_000 });

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });

  test("the TV shelf shows a show added elsewhere, and a changed card, with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/tv");
    const stamp = Date.now();
    const firstId = await addSeries(page, `Live shelf show ${stamp}`);
    let renamed: string | null = null;
    // The shelf reads a changed card on its own (/api/series/{id}) and a whole page (/api/series/page) when it must.
    await page.route(new RegExp(`/api/series/${firstId}$`), async (route) => {
      const response = await route.fetch();
      const body = await response.json();
      await route.fulfill({ response, json: renamed ? { ...body, title: renamed } : body });
    });
    await page.route(/\/api\/series\/page\?/, async (route) => {
      const response = await route.fetch();
      const body = await response.json();
      const items = (body.items ?? []).map((item: { id: string; title: string }) =>
        item.id === firstId && renamed ? { ...item, title: renamed } : item);
      await route.fulfill({ response, json: { ...body, items } });
    });
    const navigations = countNavigations(page);

    await page.goto("/tv");
    await expect(page.getByText(`Live shelf show ${stamp}`).first()).toBeVisible();
    const afterLoad = navigations.count;
    await watching(page, "dashboard");

    // A second show is added over the API, as another tab or a list sync would: the open shelf picks it up.
    await addSeries(page, `Live shelf second show ${stamp}`);
    await expect(page.getByText(`Live shelf second show ${stamp}`).first()).toBeVisible({ timeout: 15_000 });

    // And a change to the first card arrives in place.
    renamed = `Live shelf show renamed ${stamp}`;
    expect((await server(page, "/api/testing/realtime/title-changed", { entityType: "Series", id: firstId })).status).toBe(204);
    await expect(page.getByText(`Live shelf show renamed ${stamp}`).first()).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(`Live shelf show ${stamp}`, { exact: true })).toHaveCount(0);

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });

  test("Discover marks a show In library the moment it is added elsewhere", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const stamp = Date.now();
    const title = `Discover probe show ${stamp}`;
    let inLibrary = false;
    await page.route(/\/api\/discover\?/, (route) => route.fulfill({
      json: {
        popular: [{
          provider: "tmdb", providerId: `probe-${stamp}`, title, year: 2024, overview: "A probe.", posterUrl: null, backdropUrl: null,
          rating: 7.5, imdbId: null, externalUrl: null, inLibrary, libraryEntryId: inLibrary ? "probe-entry" : null, excluded: false
        }],
        lists: []
      }
    }));
    const navigations = countNavigations(page);

    await page.goto("/discover");
    await page.getByRole("radio", { name: /TV|Shows|Series/i }).first().click();
    await expect(page.getByText(title).first()).toBeVisible();
    await expect(page.getByRole("button", { name: `Add ${title}` })).toBeVisible();
    const afterLoad = navigations.count;
    await watching(page, "dashboard");

    // The show is added over the API: the server announces the new show and Discover reads again.
    inLibrary = true;
    await addSeries(page, `Discover real show ${stamp}`);
    await expect(page.getByText("In library", { exact: true }).first()).toBeAttached({ timeout: 15_000 });
    await expect(page.getByRole("button", { name: `Add ${title}` })).toHaveCount(0);

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });

  test("Collections follows a film change with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const stamp = Date.now();
    const collection = (held: number) => ({
      id: "probe-collection", provider: "tmdb", providerId: "9999", name: `Probe Collection ${stamp}`, overview: null, posterUrl: null, backdropUrl: null,
      libraryId: "probe-library", libraryName: "Probe Movies", rootPath: "/media/movies", monitored: true, monitorMovies: true,
      qualityProfileId: null, qualityProfileName: null, minimumAvailability: "released", searchOnAdd: false,
      memberCount: 4, heldCount: held, missingCount: 4 - held, lastSyncedUtc: null, nextSyncUtc: null, lastSyncError: null, tags: "", createdUtc: new Date().toISOString(), updatedUtc: new Date().toISOString()
    });
    let held = 1;
    await page.route(/\/api\/movie-collections(\?.*)?$/, (route) => route.request().method() === "GET" ? route.fulfill({ json: [collection(held)] }) : route.continue());
    await page.route(/\/api\/movie-collections\/probe-collection\/members/, (route) => route.fulfill({ json: [] }));
    const navigations = countNavigations(page);

    await page.goto("/collections");
    await expect(page.getByRole("heading", { name: `Probe Collection ${stamp}` })).toBeVisible();
    await expect(page.getByText("1/4 held")).toBeVisible();
    const afterLoad = navigations.count;
    await watching(page, "dashboard");

    held = 3;
    await addMovie(page, `Collections trigger film ${stamp}`);
    await expect(page.getByText("3/4 held")).toBeVisible({ timeout: 15_000 });

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });

  test("Season Pass follows a show change with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const stamp = Date.now();
    let monitored = 2;
    await page.route(/\/api\/series\/season-pass$/, (route) => route.fulfill({
      json: [{ seriesId: "probe-series", seriesTitle: `Season pass probe ${stamp}`, seasonNumber: 1, episodeCount: 10, monitoredCount: monitored }]
    }));
    const navigations = countNavigations(page);

    await page.goto("/tv/season-pass");
    await expect(page.getByText(`Season pass probe ${stamp}`).first()).toBeVisible();
    await expect(page.getByText("2 of 10 episodes").or(page.getByText("of 10 episodes")).first()).toBeAttached();
    const afterLoad = navigations.count;
    await watching(page, "dashboard");

    monitored = 7;
    await addSeries(page, `Season pass trigger show ${stamp}`);
    await expect.poll(
      () => page.getByText("7", { exact: true }).count(),
      { timeout: 15_000, message: "the Monitored count never changed to 7" }
    ).toBeGreaterThan(0);

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });
});
