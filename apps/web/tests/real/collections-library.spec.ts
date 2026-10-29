import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * Collections as Radarr shows them, against the real server (issue 1425, measure 4). Two films of The Matrix Collection are in
 * the library, each naming the collection in its stored metadata. The metadata broker is a real address that answers with what the
 * project's gateway really answered for that collection (tests/fixtures/recorded/tmdb, collection-movie). The page reads both
 * sections, and its one action adds the missing films through the real server.
 */

const stamp = `cx${Date.now()}`;
const COLLECTION = "The Matrix Collection";
const MATRIX = JSON.stringify({ CollectionProviderId: "2344", Collection: COLLECTION });
let broker: RecordedAppServer;
let before: { metadataProviderMode?: string; metadataBrokerUrl?: string } = {};
let root = "";
let libraryId = "";

const addedFilms = async (page: Page) =>
  (await api<{ items: Array<{ id: string; title: string }> }>(page, "GET", "/api/movies/page?search=Matrix&pageSize=100&facets=false")).body.items;

test.describe("Collections from the library", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    broker = await startRecordedAppServer([loadRecording("tmdb", "2026-10-18", "collection-movie")]);
    root = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-collections-library-"));
    const page = await browser.newPage();
    await openScreen(page, "/collections");
    before = (await api<typeof before>(page, "GET", "/api/settings")).body;
    const set = await api(page, "PATCH", "/api/settings", { metadataProviderMode: "broker", metadataBrokerUrl: broker.baseUrl });
    expect(set.status, JSON.stringify(set.body)).toBeLessThan(300);
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Collections library ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: root });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
    for (const [title, year, providerId] of [["The Matrix", 1999, "603"], ["The Matrix Reloaded", 2003, "604"]] as const) {
      const film = await api(page, "POST", "/api/movies", {
        title, releaseYear: year, monitored: true, metadataProvider: "tmdb", metadataProviderId: providerId, metadataJson: MATRIX, libraryId
      });
      expect(film.status, JSON.stringify(film.body)).toBeLessThan(300);
    }
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/collections");
    const followed = await api<Array<{ id: string; providerId: string }>>(page, "GET", "/api/movie-collections");
    const ids = followed.body.filter((item) => item.providerId === "2344").map((item) => item.id);
    if (ids.length) await api(page, "POST", "/api/movie-collections/remove", { collectionIds: ids });
    for (const film of await addedFilms(page)) await api(page, "DELETE", `/api/movies/${film.id}`);
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await api(page, "PATCH", "/api/settings", { metadataProviderMode: before.metadataProviderMode, metadataBrokerUrl: before.metadataBrokerUrl ?? "" });
    await page.close();
    await broker.close();
    fs.rmSync(root, { recursive: true, force: true });
  });

  test("the page lists the collection the films belong to with each film's status, and recommends it with why", async ({ page }) => {
    await openScreen(page, "/collections");
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    const recommended = page.getByRole("region", { name: /Recommended/ });
    await expect(recommended.getByRole("heading", { name: COLLECTION })).toBeVisible();
    await expect(recommended.getByText(`You have 2 of 4 films, so 2 more would complete ${COLLECTION}.`)).toBeVisible();

    const films = page.getByRole("list", { name: `Films of ${COLLECTION}` });
    const status = (title: string) => films.getByRole("listitem").filter({ hasText: title }).first();
    await expect(status("The Matrix (1999)")).toContainText("Wanted");
    await expect(status("The Matrix Reloaded (2003)")).toContainText("Wanted");
    await expect(status("The Matrix Revolutions (2003)")).toContainText("Missing");
    await expect(status("The Matrix Resurrections (2021)")).toContainText("Missing");
    await expect(page.getByRole("region", { name: /Your collections/ }).getByText("Not followed").first()).toBeVisible();
    expect(broker.requests.some((line) => line.startsWith("GET /metadata/movie/2344/collection"))).toBe(true);
  });

  test("one action adds the missing films, follows the collection and the page then shows them all wanted", async ({ page }) => {
    await openScreen(page, "/collections");
    await untilQuiet(page);
    const requests = recordRequests(page);

    await page.getByRole("region", { name: /Your collections/ }).getByRole("button", { name: `Add 2 missing films of ${COLLECTION}` }).click();

    await expect.poll(() => requests.find("POST", /\/from-library\/2344\/add-missing$/).length).toBe(1);
    expect(requests.find("POST", /\/from-library\/2344\/add-missing$/)[0].body).toEqual({ libraryId: expect.any(String) });
    await expect(page.getByText(`Added 2 films of ${COLLECTION}, and Deluno now follows the collection.`).first()).toBeVisible();

    const films = page.getByRole("list", { name: `Films of ${COLLECTION}` }).first();
    await expect(films.getByRole("listitem").filter({ hasText: "The Matrix Revolutions (2003)" })).toContainText("Wanted");
    await expect(films.getByRole("listitem").filter({ hasText: "The Matrix Resurrections (2021)" })).toContainText("Wanted");
    await expect(page.getByRole("region", { name: /Your collections/ }).getByText("Following").first()).toBeVisible();
    await expect(page.getByRole("region", { name: /Recommended/ })).toHaveCount(0);
    expect((await addedFilms(page)).map((film) => film.title).sort()).toEqual([
      "The Matrix", "The Matrix Reloaded", "The Matrix Resurrections", "The Matrix Revolutions"
    ]);
    requests.stop();
  });
});
