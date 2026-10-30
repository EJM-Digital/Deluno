import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * A film's page with a release to choose from, against the real server, nothing stubbed (measure 4, issue 1270). The indexer is
 * a real Torznab address that answers with what Jackett 0.24.2850 really answered, and the download client answers as qBittorrent
 * 5.2.1 really answered (tests/fixtures/recorded). The one release it lists is too small for the film's quality, so Deluno turns
 * it down: the page says why, offers to force the download, and every control on that path is pressed against the request the
 * server received and the words on screen.
 */

const stamp = `mr${Date.now()}`;
const RELEASE = "Sintel (2010) 720p BRRip x264 -YTS";
let jackett: RecordedAppServer;
let qb: RecordedAppServer;
let folder = "";
let libraryId = "";
let indexerId = "";
let clientId = "";
let movieId = "";

async function openMovie(page: Page) {
  await openScreen(page, "/movies");
  await page.goto(`/movies/${movieId}`);
  await untilQuiet(page);
  await expectNoErrorScreen(page);
}

test.describe("A film's releases", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    jackett = await startRecordedAppServer([loadRecording("jackett", "0.24.2850", "caps"), loadRecording("jackett", "0.24.2850", "search-text"), loadRecording("jackett", "0.24.2850", "search-movie-imdb")]);
    qb = await startRecordedAppServer(["sign-in", "version", "downloading", "paused", "trackers", "settings"].map((state) => loadRecording("qbittorrent", "5.2.1", state)));
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-releases-"));
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Releases ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    const indexer = await api<{ id: string }>(page, "POST", "/api/indexers", {
      name: `Releases indexer ${stamp}`, protocol: "torznab", baseUrl: `${jackett.baseUrl}/api/v2.0/indexers/yts/results/torznab/api`, apiKey: "0123456789abcdef0123456789abcdef",
      privacy: "public", priority: 10, categories: "2000,2040", tags: "", mediaScope: "movies", isEnabled: true
    });
    expect(indexer.status, JSON.stringify(indexer.body)).toBeLessThan(300);
    indexerId = indexer.body.id;
    expect((await api(page, "POST", `/api/indexers/${indexerId}/test`)).status).toBeLessThan(300);
    const client = await api<{ id: string }>(page, "POST", "/api/download-clients", {
      protocol: "qbittorrent", host: "127.0.0.1", port: qb.port, username: "admin", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: true
    });
    expect(client.status, JSON.stringify(client.body)).toBeLessThan(300);
    clientId = client.body.id;
    expect((await api(page, "POST", `/api/download-clients/${clientId}/test`)).status).toBeLessThan(300);
    const routed = await api(page, "PUT", `/api/libraries/${libraryId}/routing`, {
      sources: [{ indexerId, priority: 10, requiredTags: "", excludedTags: "" }], downloadClients: [{ downloadClientId: clientId, priority: 10 }]
    });
    expect(routed.status, JSON.stringify(routed.body)).toBeLessThan(300);
    const movie = await api<{ id: string }>(page, "POST", "/api/movies/", { title: "Sintel", releaseYear: 2010, monitored: true, libraryId });
    expect(movie.status, JSON.stringify(movie.body)).toBeLessThan(300);
    movieId = movie.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    await api(page, "POST", "/api/testing/transfers/clear");
    for (const blocked of (await api<Array<{ id: string }>>(page, "GET", "/api/blocked-releases")).body ?? []) await api(page, "DELETE", `/api/blocked-releases/${blocked.id}`);
    await api(page, "DELETE", `/api/movies/${movieId}`);
    await api(page, "DELETE", `/api/download-clients/${clientId}`);
    await api(page, "DELETE", `/api/indexers/${indexerId}`);
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    await jackett.close();
    await qb.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("Choose a release lists what the indexer answered and Deluno's decision on it, in words", async ({ page }) => {
    await openMovie(page);
    await page.getByRole("button", { name: "Choose a release", exact: true }).click();
    await expect(page.getByLabel("Result of the last search").getByText("1 release compared. Choose one below.")).toBeVisible({ timeout: 30_000 });
    const row = page.getByRole("row").filter({ hasText: RELEASE }).first();
    await expect(row).toBeVisible();
    await expect(row).toContainText("Rejected");
    await expect(row).toContainText("below the 1.2 GB minimum");
  });

  test("a turned-down release opens its drawer; Force asks for a reason, Cancel drops it, and Send anyway sends the release with the reason", async ({ page }) => {
    await openMovie(page);
    await page.getByRole("button", { name: "Choose a release", exact: true }).click();
    const row = page.getByRole("row").filter({ hasText: RELEASE }).first();
    await expect(row).toBeVisible({ timeout: 30_000 });
    await row.click();
    const drawer = page.getByRole("dialog").filter({ hasText: "Send it anyway" }).first();
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText("How Deluno evaluated it")).toBeVisible();

    // A row that was chosen by pressing Force opens with the reason box already asking; one opened by its row shows Force.
    const reason = drawer.getByRole("textbox", { name: "Why force this release?" });
    const force = drawer.getByRole("button", { name: "Force", exact: true });
    if (await force.isVisible()) await force.click();
    await expect(reason).toBeVisible();
    await drawer.getByRole("button", { name: "Cancel", exact: true }).first().click();
    await expect(reason).toHaveCount(0);

    await drawer.getByRole("button", { name: "Force", exact: true }).click();
    await reason.fill(`Wanted anyway ${stamp}`);
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Send anyway", exact: true }).click();
    await expect.poll(() => requests.find("POST", new RegExp(`^/api/movies/${movieId}/grab`)).length).toBe(1);
    requests.stop();
    const sent = JSON.stringify(requests.find("POST", new RegExp(`^/api/movies/${movieId}/grab`))[0].body);
    expect(sent).toContain(`Wanted anyway ${stamp}`);
    expect(sent).toContain("Sintel");
    await untilQuiet(page);
    await expectNoErrorScreen(page);
  });

  test("Why This Will Not Download names the reason, and Force a re-download asks first, Cancel keeps things as they are, and confirming searches again", async ({ page }) => {
    await openMovie(page);
    await expect(page.getByText("Why This Will Not Download")).toBeVisible();
    const force = page.getByRole("button", { name: "Force a re-download", exact: true });
    await expect(force).toBeVisible();
    await force.click();
    const ask = page.getByRole("alertdialog").or(page.getByRole("dialog")).filter({ hasText: "Force a re-download?" });
    await expect(ask).toBeVisible();
    const requests = recordRequests(page);
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
    expect(requests.seen.filter((r) => r.method !== "GET")).toHaveLength(0);

    await force.click();
    await ask.getByRole("button", { name: "Clear it and search again", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => r.method !== "GET" && r.path.includes(movieId)).length).toBeGreaterThan(0);
    requests.stop();
    await untilQuiet(page);
    await expectNoErrorScreen(page);
  });

  test("sending a release for a film that is already downloading asks first: Cancel sends nothing, and Send as well sends it", async ({ page }) => {
    await openMovie(page);
    const seeded = await api(page, "POST", "/api/testing/transfers/handoff", {
      libraryId, mediaType: "movies", entityId: movieId, releaseName: "Sintel.2010.1080p.WEB-DL-OTHER", clientId, queueItemId: "69ca35bca234b9cb822abc435ddbababf6147591"
    });
    expect(seeded.status, JSON.stringify(seeded.body)).toBe(200);
    try {
      await page.reload();
      await untilQuiet(page);
      await page.getByRole("button", { name: "Choose a release", exact: true }).click();
      const row = page.getByRole("row").filter({ hasText: RELEASE }).first();
      await expect(row).toBeVisible({ timeout: 30_000 });
      await row.click();
      const drawer = page.getByRole("dialog").filter({ hasText: "Send it anyway" }).first();
      const reason = drawer.getByRole("textbox", { name: "Why force this release?" });
      const force = drawer.getByRole("button", { name: "Force", exact: true });
      if (await force.isVisible()) await force.click();
      await reason.fill(`Second copy ${stamp}`);
      await drawer.getByRole("button", { name: "Send anyway", exact: true }).click();

      const ask = page.getByRole("dialog", { name: "Already downloading" }).or(page.getByRole("alertdialog", { name: "Already downloading" }));
      await expect(ask).toBeVisible({ timeout: 30_000 });
      await expect(ask).toContainText("is already downloading in");
      await expect(ask.getByRole("button", { name: "Replace it", exact: true })).toBeVisible();
      const requests = recordRequests(page);
      await ask.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(ask).toHaveCount(0);
      expect(requests.find("POST", new RegExp(`^/api/movies/${movieId}/grab`))).toHaveLength(0);

      await drawer.getByRole("button", { name: "Force", exact: true }).click().catch(() => undefined);
      if (await reason.isVisible()) await reason.fill(`Second copy ${stamp}`);
      await drawer.getByRole("button", { name: "Send anyway", exact: true }).click();
      await expect(ask).toBeVisible({ timeout: 30_000 });
      await ask.getByRole("button", { name: "Send as well", exact: true }).click();
      await expect.poll(() => requests.find("POST", new RegExp(`^/api/movies/${movieId}/grab`)).length).toBeGreaterThan(0);
      requests.stop();
      expect(JSON.stringify(requests.find("POST", new RegExp(`^/api/movies/${movieId}/grab`)).at(-1)!.body)).toContain("send-as-well");
    } finally {
      await api(page, "POST", "/api/testing/transfers/clear");
    }
  });

  test("a download sent for the film lists under Sent to downloads with Open In progress, which goes to In progress", async ({ page }) => {
    await openMovie(page);
    const seeded = await api(page, "POST", "/api/testing/transfers/handoff", {
      libraryId, mediaType: "movies", entityId: movieId, releaseName: "Sintel.2010.1080p.WEB-DL-LIST", clientId, queueItemId: "69ca35bca234b9cb822abc435ddbababf6147591"
    });
    expect(seeded.status, JSON.stringify(seeded.body)).toBe(200);
    try {
      await page.reload();
      await untilQuiet(page);
      await page.getByRole("tab", { name: "History", exact: true }).click();
      await expect(page.getByText("Sintel.2010.1080p.WEB-DL-LIST").first()).toBeVisible();
      await page.getByRole("link", { name: "Open In progress", exact: true }).first().click();
      await expect(page).toHaveURL(/\/queue/);
    } finally {
      await api(page, "POST", "/api/testing/transfers/clear");
    }
  });

  test("an import the processor could not match is listed for the film, and Search, Import manually and Dismiss each act on it", async ({ page }) => {
    await openMovie(page);
    const seeded = await api(page, "POST", "/api/testing/transfers/handoff", {
      libraryId, mediaType: "movies", releaseName: "Sintel", recoveryCase: true, recoveryKind: "processor-unmatched-output", failureMessage: "The processor's output could not be matched to a film."
    });
    expect(seeded.status, JSON.stringify(seeded.body)).toBe(200);
    try {
      await page.reload();
      await untilQuiet(page);
      await page.getByRole("tab", { name: "History", exact: true }).click();
      const requests = recordRequests(page);

      await page.getByRole("button", { name: "Search", exact: true }).first().click();
      await expect.poll(() => requests.find("POST", new RegExp(`^/api/movies/${movieId}/search`)).length).toBeGreaterThan(0);
      await untilQuiet(page);

      await page.getByRole("button", { name: "Import manually", exact: true }).click();
      await expect(page).toHaveURL(/\/queue\?manualImportMediaType=movies/);
      await page.goBack();
      await untilQuiet(page);
      await page.getByRole("tab", { name: "History", exact: true }).click();

      await page.getByRole("button", { name: "Dismiss", exact: true }).first().click();
      await expect.poll(() => requests.seen.filter((r) => r.method === "DELETE" && /import-recovery/.test(r.path)).length).toBe(1);
      requests.stop();
      await expect(page.getByRole("button", { name: "Import manually", exact: true })).toHaveCount(0);
    } finally {
      await api(page, "POST", "/api/testing/transfers/clear");
    }
  });

  test("a film with a file on record offers This file's history, which opens Activity narrowed to that file", async ({ page }) => {
    await openScreen(page, "/movies");
    const name = `Filed ${stamp}`;
    const filmFolder = path.join(folder, `${name} (2018)`);
    fs.mkdirSync(filmFolder, { recursive: true });
    fs.writeFileSync(path.join(filmFolder, `${name} (2018) 1080p WEB-DL.mkv`), Buffer.alloc(4096, 7));
    const imported = await api(page, "POST", `/api/libraries/${libraryId}/import-existing/selected`, { sourcePaths: [filmFolder] });
    expect(imported.status, JSON.stringify(imported.body)).toBeLessThan(300);
    const found = (await api<{ items: Array<{ id: string; title: string }> }>(page, "GET", `/api/movies/page?search=${encodeURIComponent(name)}&pageSize=10&facets=false`)).body.items[0];
    expect(found, "the film in the folder was added").toBeTruthy();
    try {
      await page.goto(`/movies/${found.id}`);
      await untilQuiet(page);
      await page.getByRole("tab", { name: "History", exact: true }).click();
      await page.getByRole("link", { name: "This file's history", exact: true }).click();
      await expect(page).toHaveURL(/\/queue\?view=history&/);
      expect(decodeURIComponent(page.url()).replaceAll("+", " ")).toContain(name);
      await expectNoErrorScreen(page);
    } finally {
      await api(page, "DELETE", `/api/movies/${found.id}`);
    }
  });
});
