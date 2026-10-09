import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1179: a film changing from any path changes its page, its card on the Movies shelf and the
// Calendar by themselves. The pushes are raised by the server through the Testing-only
// endpoints (Testing:EnableRealtimePushEndpoint), the same announcement a database write
// raises; the one answer that differs afterwards is stood in for by a route so the test can
// see the screen take it. Nothing reloads.

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
const seeded: string[] = [];

test.afterEach(async ({ page }) => {
  const headers = await authHeaders(page);
  for (const movieId of seeded.splice(0)) {
    await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movieId], operation: "remove" } });
  }
});

async function seedMovie(page: Page, title: string) {
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);
  const movie = await (await page.request.post("/api/movies/", {
    headers,
    data: { title, releaseYear: 1962, monitored: true }
  })).json() as { id: string };
  seeded.push(movie.id);
  return movie.id;
}

/** Answers every read of this film with a new title once `renamed.to` is set, the way the server does after a change. */
async function renameOnRead(page: Page, movieId: string, renamed: { to: string | null }) {
  await page.route(new RegExp(`/api/movies/${movieId}$`), async (route) => {
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: renamed.to ? { ...body, title: renamed.to } : body });
  });
  await page.route(/\/api\/movies\/page\?/, async (route) => {
    const response = await route.fetch();
    const body = await response.json();
    const items = (body.items ?? []).map((item: { id: string; title: string }) =>
      item.id === movieId && renamed.to ? { ...item, title: renamed.to } : item);
    await route.fulfill({ response, json: { ...body, items } });
  });
}

/**
 * Waits until the page has joined the Dashboard subject, the one the shelf and the Calendar listen on (and the app shell
 * shares). An announcement made before that reaches nobody (#1191), so the announcement is sent once, after this.
 */
async function joinedDashboard(page: Page) {
  // Exactly one connection (this page's: the one from the page it replaced has gone) and it is in the Dashboard subject.
  await expect.poll(async () => {
    const seen = (await server(page, "/api/testing/realtime/watchers")).json as { connections: number; dashboard: number };
    return seen.connections === 1 && seen.dashboard === 1;
  }, { timeout: 20_000, message: "the page never became the one connection in the dashboard subject" }).toBe(true);
}

function countNavigations(page: Page) {
  const state = { count: 0 };
  page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) state.count += 1; });
  return state;
}

test.describe("titles, the shelf and the calendar are live (#1179)", () => {
  test("a film's page follows a change to it with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    const stamp = Date.now();
    const movieId = await seedMovie(page, `Live page movie ${stamp}`);
    const renamed = { to: null as string | null };
    await renameOnRead(page, movieId, renamed);
    const navigations = countNavigations(page);

    await page.goto(`/movies/${movieId}`);
    await expect(page.getByRole("heading", { name: `Live page movie ${stamp}` }).first()).toBeVisible();
    const afterLoad = navigations.count;

    // The page joined this film's own subject, and nobody else's.
    await expect.poll(async () => ((await server(page, `/api/testing/realtime/watchers?title=${movieId}`)).json as { title: number }).title, { timeout: 15_000 })
      .toBeGreaterThan(0);

    renamed.to = `Live page movie renamed ${stamp}`;
    expect((await server(page, "/api/testing/realtime/title-changed", { entityType: "Movie", id: movieId })).status).toBe(204);
    await expect(page.getByRole("heading", { name: `Live page movie renamed ${stamp}` }).first()).toBeVisible({ timeout: 15_000 });

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });

  test("a change made from anywhere shows on the film's page, from the database write itself", async ({ page }) => {
    test.setTimeout(90_000);
    const stamp = Date.now();
    const movieId = await seedMovie(page, `Live write movie ${stamp}`);
    await page.goto(`/movies/${movieId}`);
    await expect(page.getByRole("heading", { name: `Live write movie ${stamp}` }).first()).toBeVisible();
    await expect.poll(async () => ((await server(page, `/api/testing/realtime/watchers?title=${movieId}`)).json as { title: number }).title, { timeout: 15_000 })
      .toBeGreaterThan(0);

    // Written by something other than this page, as a background job would.
    const overview = `Written elsewhere ${stamp}`;
    const response = await page.request.put(`/api/movies/${movieId}/metadata/override`, {
      headers: await authHeaders(page),
      data: { overview }
    });
    expect(response.ok()).toBe(true);
    await expect(page.getByText(overview).first()).toBeVisible({ timeout: 15_000 });
  });

  test("the Movies shelf updates a card in place, keeping the filter, with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    const stamp = Date.now();
    const movieId = await seedMovie(page, `Live shelf movie ${stamp}`);
    const renamed = { to: null as string | null };
    await renameOnRead(page, movieId, renamed);
    const navigations = countNavigations(page);

    await page.goto("/movies");
    await expect(page.getByText(`Live shelf movie ${stamp}`).first()).toBeVisible();
    const afterLoad = navigations.count;

    // Cause of the flake (#1191): the card is on screen before the page has joined the subject the server announces on (the
    // connection opens after the first paint), so an announcement made at once reached nobody and the card never changed.
    await joinedDashboard(page);
    renamed.to = `Live shelf movie renamed ${stamp}`;
    expect((await server(page, "/api/testing/realtime/title-changed", { entityType: "Movie", id: movieId })).status).toBe(204);
    await expect(page.getByText(`Live shelf movie renamed ${stamp}`).first()).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(`Live shelf movie ${stamp}`, { exact: true })).toHaveCount(0);

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });

  test("the Calendar picks up a change with no reload and no Refresh button", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    const stamp = Date.now();
    const today = new Date().toISOString().slice(0, 10);
    const entry = (title: string) => ({
      movieId: "calendar-probe", title, releaseYear: 2026, posterUrl: null, kind: "digital", date: `${today}T12:00:00Z`, monitored: true, wantedStatus: "upcoming"
    });
    const shown = { title: `Calendar probe ${stamp}` };
    await page.route(/\/api\/movies\/calendar\?/, (route) => route.fulfill({ json: [entry(shown.title)] }));
    const navigations = countNavigations(page);

    await page.goto("/calendar");
    await page.getByRole("radio", { name: "List" }).click();
    await expect(page.getByText(`Calendar probe ${stamp}`).first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Refresh" })).toHaveCount(0);
    const afterLoad = navigations.count;

    shown.title = `Calendar probe added ${stamp}`;
    // Cause of the flake (#1191): same as the shelf above. Join first, then announce once.
    await joinedDashboard(page);
    expect((await server(page, "/api/testing/realtime/title-changed", { entityType: "Movie", id: "calendar-probe" })).status).toBe(204);
    await expect(page.getByText(`Calendar probe added ${stamp}`).first()).toBeVisible({ timeout: 15_000 });

    expect(navigations.count, "the page reloaded or navigated").toBe(afterLoad);
  });
});
