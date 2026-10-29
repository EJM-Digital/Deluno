import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * A movie's own page against the real server, nothing stubbed (measure 4, issue 1270). The movie and the library it sits
 * in are this spec's own (named with a stamp, in a folder of its own), made through the real API; each button is pressed
 * and checked against the request the server received, the state it ends in, and the words on screen.
 */

const stamp = `md${Date.now()}`;
let folder = "";
let libraryId = "";

interface Movie { id: string; title: string; monitored: boolean }

async function addMovie(page: Page, title: string, extra: Record<string, unknown> = {}) {
  const made = await api<Movie>(page, "POST", "/api/movies/", { title, releaseYear: 2019, monitored: true, libraryId, ...extra });
  expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
  return made.body;
}

async function openMovie(page: Page, movie: Movie) {
  await page.goto(`/movies/${movie.id}`);
  await untilQuiet(page);
  await expectNoErrorScreen(page);
  await expect(page.getByRole("heading", { name: movie.title, level: 1 })).toBeVisible();
}

test.describe("A movie's page", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-moviepage-"));
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Movie page ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    const list = await api<{ items: Movie[] }>(page, "GET", `/api/movies/page?search=${stamp}&pageSize=100&facets=false`);
    for (const item of list.body.items ?? []) await api(page, "DELETE", `/api/movies/${item.id}`);
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("All movies goes back to the list, and Search now sends one search for this movie and says how it ended", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Search ${stamp}`);
    await openMovie(page, movie);

    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Search now" }).first().click();
    const result = page.getByRole("status", { name: "Result of the last search" });
    await expect(result).toBeVisible();
    expect((await result.innerText()).trim().length).toBeGreaterThan(10);
    requests.stop();
    expect(requests.find("POST", new RegExp(`^/api/movies/${movie.id}/search`))).toHaveLength(1);

    await page.getByRole("link", { name: "All movies" }).click();
    await expect(page).toHaveURL(/\/movies$/);
  });

  test("Refresh metadata asks the server once and says in words what it could not do without a provider record", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Refresh ${stamp}`);
    await openMovie(page, movie);
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Refresh metadata", exact: true }).click();
    await expect.poll(() => requests.find("POST", new RegExp(`^/api/movies/${movie.id}/metadata/refresh`)).length).toBe(1);
    requests.stop();
    await expectNoErrorScreen(page);
    await expect(page.getByRole("region", { name: /Notifications/ }).locator("li").first()).toBeVisible();
  });

  test("Edit movie changes whether it is monitored: Save is held until something changed, Cancel leaves it, Save writes it to the server", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Edit ${stamp}`);
    await openMovie(page, movie);

    await page.getByRole("button", { name: "Edit movie", exact: true }).click();
    const drawer = page.getByRole("dialog", { name: "Edit" });
    await expect(drawer.getByRole("button", { name: "Save", exact: true })).toBeDisabled();
    await expect(drawer.getByRole("combobox", { name: "Quality profile" })).toContainText("Library default");
    await drawer.getByRole("switch", { name: "Monitored" }).click();
    await expect(drawer.getByRole("button", { name: "Save", exact: true })).toBeEnabled();
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    expect((await api<Movie>(page, "GET", `/api/movies/${movie.id}`)).body.monitored).toBe(true);

    await page.getByRole("button", { name: "Edit movie", exact: true }).click();
    await drawer.getByRole("switch", { name: "Monitored" }).click();
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect.poll(async () => (await api<Movie>(page, "GET", `/api/movies/${movie.id}`)).body.monitored).toBe(false);
  });

  test("Edit metadata opens the provider record, Find asks the provider, and Fine-tune writes a value by hand that the server keeps", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Meta ${stamp}`);
    await openMovie(page, movie);

    await page.getByRole("button", { name: "Edit metadata", exact: true }).click();
    const drawer = page.getByRole("dialog", { name: "Metadata" });
    await expect(drawer.getByText("Not linked").first()).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Save metadata" })).toBeDisabled();

    const requests = recordRequests(page);
    await drawer.getByRole("textbox", { name: "Search the provider" }).fill(`Meta ${stamp}`);
    await drawer.getByRole("button", { name: "Find", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => r.method === "GET" && /metadata|search/i.test(r.path) && r.path !== `/api/movies/${movie.id}`).length).toBeGreaterThan(0);
    requests.stop();
    const refreshing = recordRequests(page);
    await drawer.getByRole("button", { name: "Refresh from provider", exact: true }).click();
    await expect.poll(() => refreshing.find("POST", new RegExp(`^/api/movies/${movie.id}/metadata/refresh`)).length).toBe(1);
    refreshing.stop();

    await drawer.getByRole("button", { name: /^Fine-tune/ }).click();
    const overview = drawer.getByRole("textbox").last();
    await overview.fill(`Written by hand ${stamp}`);
    await expect(drawer.getByRole("button", { name: "Save metadata" })).toBeEnabled();
    await drawer.getByRole("button", { name: "Save metadata" }).click();
    await expect.poll(async () => (await api<{ overview: string | null }>(page, "GET", `/api/movies/${movie.id}`)).body.overview).toBe(`Written by hand ${stamp}`);
    await drawer.getByRole("button", { name: "Close", exact: true }).first().click();
    await expect(drawer).toHaveCount(0);
    await expect(page.getByText(`Written by hand ${stamp}`).first()).toBeVisible();
  });

  test("Preview rename says in words that a movie with no file has nothing to rename", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Rename ${stamp}`);
    await openMovie(page, movie);
    await page.getByRole("button", { name: /^Preview the folder rename/ }).click();
    const dialog = page.getByRole("dialog", { name: "Rename preview" });
    await expect(dialog.getByText("There is no file on record for this title yet, so there is no folder to rename.")).toBeVisible();
    await dialog.getByRole("button", { name: "Close", exact: true }).first().click();
    await expect(dialog).toHaveCount(0);
  });

  test("Remove from Deluno asks first, Cancel keeps the movie, and Remove movie takes it from the server", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Remove ${stamp}`);
    await openMovie(page, movie);

    await page.getByRole("button", { name: "Remove from Deluno", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: `Remove ${movie.title}?` });
    await expect(dialog.getByText("Deluno has no imported files recorded for this movie.")).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    expect((await api(page, "GET", `/api/movies/${movie.id}`)).status).toBe(200);

    await page.getByRole("button", { name: "Remove from Deluno", exact: true }).click();
    const requests = recordRequests(page);
    await dialog.getByRole("button", { name: "Remove movie", exact: true }).click();
    await expect(page).toHaveURL(/\/movies$/);
    requests.stop();
    expect(requests.find("POST", /^\/api\/movies\/bulk$/)).toHaveLength(1);
    expect((await api(page, "GET", `/api/movies/${movie.id}`)).status).toBe(404);
  });

  test("the Automation switches and buttons act on this movie alone: monitoring, downgrade protection, Defer and Skip once each reach the server and say so", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Auto ${stamp}`);
    await openMovie(page, movie);
    const requests = recordRequests(page);

    // Background automation is the monitoring switch.
    await page.getByRole("switch", { name: "Background automation" }).click();
    await expect.poll(async () => (await api<Movie>(page, "GET", `/api/movies/${movie.id}`)).body.monitored).toBe(false);
    expect(requests.find("PUT", /^\/api\/movies\/monitoring$/)[0].body).toMatchObject({ movieIds: [movie.id], monitored: false });
    await expect(page.getByRole("switch", { name: "Background automation" })).not.toBeChecked();
    await page.getByRole("switch", { name: "Background automation" }).click();
    await expect.poll(async () => (await api<Movie>(page, "GET", `/api/movies/${movie.id}`)).body.monitored).toBe(true);

    await page.getByRole("switch", { name: "Protect against downgrades" }).click();
    await expect.poll(() => requests.find("PUT", new RegExp(`^/api/movies/${movie.id}/replacement-protection$`)).length).toBe(1);
    expect(requests.find("PUT", new RegExp(`^/api/movies/${movie.id}/replacement-protection$`))[0].body).toMatchObject({ preventLowerQualityReplacements: false });
    await expect(page.getByRole("switch", { name: "Protect against downgrades" })).not.toBeChecked();

    await page.getByRole("button", { name: "Defer", exact: true }).click();
    await expect(page.getByText("Deferred for 24 hours. Manual searches still work.").first()).toBeVisible();
    expect(requests.find("POST", new RegExp(`^/api/movies/${movie.id}/automation/defer$`))[0].body).toMatchObject({ libraryId, hours: 24 });

    await page.getByRole("button", { name: "Skip once", exact: true }).click();
    await expect(page.getByText("The next scheduled search will be skipped.").first()).toBeVisible();
    expect(requests.find("POST", new RegExp(`^/api/movies/${movie.id}/automation/skip-once$`))[0].body).toMatchObject({ libraryId });
    requests.stop();
  });

  test("the monitored mark at the top toggles monitoring, and Choose a release searches by hand and says how it ended", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Mark ${stamp}`);
    await openMovie(page, movie);
    await page.getByRole("button", { name: "Monitored — click to unmonitor" }).click();
    await expect(page.getByRole("button", { name: "Unmonitored — click to monitor" })).toBeVisible();
    await expect.poll(async () => (await api<Movie>(page, "GET", `/api/movies/${movie.id}`)).body.monitored).toBe(false);
    await page.getByRole("button", { name: "Unmonitored — click to monitor" }).click();
    await expect(page.getByRole("button", { name: "Monitored — click to unmonitor" })).toBeVisible();

    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Choose a release", exact: true }).click();
    const result = page.getByRole("status", { name: "Result of the last search" });
    await expect(result).toBeVisible();
    expect((await result.innerText()).trim().length).toBeGreaterThan(10);
    requests.stop();
    expect(requests.find("POST", new RegExp(`^/api/movies/${movie.id}/search`))).toHaveLength(1);
  });

  test("a tag chosen in the movie's Tags box is kept by the server on Save, and Cancel keeps nothing", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Tag ${stamp}`);
    const tagName = `mdtag${stamp}`;
    const made = await api<{ id: string }>(page, "POST", "/api/tags", { name: tagName, color: "#3b82f6" });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    try {
      await openMovie(page, movie);
      await page.getByRole("button", { name: "Add", exact: true }).first().click();
      await page.getByRole("textbox", { name: "Title tags" }).fill(tagName);
      await page.getByRole("button", { name: `Add ${tagName} to Title tags` }).click();
      await page.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(page.getByText("None assigned")).toBeVisible();
      expect(JSON.stringify((await api(page, "GET", `/api/movies/${movie.id}/tags`)).body)).not.toContain(tagName);

      await page.getByRole("button", { name: "Add", exact: true }).first().click();
      await page.getByRole("textbox", { name: "Title tags" }).fill(tagName);
      await page.getByRole("button", { name: `Add ${tagName} to Title tags` }).click();
      const requests = recordRequests(page);
      await page.getByRole("button", { name: "Save", exact: true }).click();
      await expect.poll(async () => JSON.stringify((await api(page, "GET", `/api/movies/${movie.id}/tags`)).body)).toContain(tagName);
      requests.stop();
      expect(requests.find("POST", /^\/api\/movies\/bulk\/tags$/)).toHaveLength(1);
      await expect(page.getByText(tagName).first()).toBeVisible();
    } finally {
      await api(page, "DELETE", `/api/tags/${made.body.id}`);
    }
  });

  test("Rescan folder on a title with no file asks first and says in words there was nothing to check", async ({ page }) => {
    await openScreen(page, "/movies");
    const title = `Rescan ${stamp}`;
    const movie = await addMovie(page, title);
    await openMovie(page, movie);

    await page.getByRole("button", { name: `Rescan the folder for ${title}`, exact: true }).click();
    const ask = page.getByRole("alertdialog").or(page.getByRole("dialog")).filter({ hasText: `Rescan the folder for ${title}?` });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);

    await page.getByRole("button", { name: `Rescan the folder for ${title}`, exact: true }).click();
    const requests = recordRequests(page);
    await ask.getByRole("button", { name: "Rescan now", exact: true }).click();
    const report = page.getByRole("dialog", { name: "Folder rescanned" });
    await expect(report).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/filesystem\/rescan-title$/)).toHaveLength(1);
    expect((await report.innerText()).length).toBeGreaterThan(30);
    await expect(report.getByText(`${title} has no file on record, so there was nothing to check.`)).toBeVisible();
    await report.getByRole("button", { name: "Close", exact: true }).click();
    await expect(report).toHaveCount(0);
  });

  test("a synopsis too long for the header is cut, Read the full synopsis shows all of it, and Close the synopsis (or Escape) gives focus back", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Synopsis ${stamp}`);
    const sentence = "A long and winding story about a lighthouse keeper who counts the ships that never arrive. ";
    const overview = `${sentence.repeat(14)}THE END ${stamp}`;
    const saved = await api(page, "PUT", `/api/movies/${movie.id}/metadata/override`, { overview, originalTitle: null, posterUrl: null, backdropUrl: null, rating: null, genres: null, externalUrl: null, imdbId: null });
    expect(saved.status, JSON.stringify(saved.body)).toBeLessThan(300);
    await page.setViewportSize({ width: 1440, height: 760 });
    await page.goto(`/movies/${movie.id}`);
    await untilQuiet(page);

    const more = page.getByRole("button", { name: "Read the full synopsis" });
    await expect(more).toBeVisible();
    await more.click();
    const close = page.getByRole("button", { name: "Close the synopsis" });
    await expect(close).toBeVisible();
    await expect(page.getByText(`THE END ${stamp}`).last()).toBeVisible();
    await close.click();
    await expect(close).toHaveCount(0);
    await expect(more).toBeFocused();

    await more.click();
    await expect(close).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(close).toHaveCount(0);
  });

  test("the Next step card offers the one thing to do: Search now for a movie with no file, and Resume automation for an unmonitored one", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Next ${stamp}`);
    await openMovie(page, movie);
    const card = page.getByLabel("Next Step");

    const requests = recordRequests(page);
    await card.getByRole("button", { name: "Search now" }).click();
    await expect.poll(() => requests.find("POST", new RegExp(`^/api/movies/${movie.id}/search`)).length).toBe(1);
    requests.stop();

    await api(page, "PUT", "/api/movies/monitoring", { movieIds: [movie.id], monitored: false });
    await openMovie(page, movie);
    await card.getByRole("button", { name: "Resume automation" }).click();
    await expect.poll(async () => (await api<{ monitored: boolean }>(page, "GET", `/api/movies/${movie.id}`)).body.monitored).toBe(true);
  });

  test("Everything about this film opens Activity narrowed to that film", async ({ page }) => {
    await openScreen(page, "/movies");
    const movie = await addMovie(page, `Activity ${stamp}`);
    await openMovie(page, movie);
    await page.getByRole("tab", { name: "History", exact: true }).click();
    await page.getByRole("link", { name: "Everything about this film", exact: true }).click();
    await expect(page).toHaveURL(/\/queue\?view=history&/);
    await expectNoErrorScreen(page);
  });
});
