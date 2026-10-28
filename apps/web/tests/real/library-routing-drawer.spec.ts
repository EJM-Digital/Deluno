import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Library routing: the list and the routing drawer's two warnings, against the real server (measure 4, issue
 * 1296). The indexer and the client are switched off and point at addresses nobody listens on; the library is a real folder in
 * a temp directory. Nothing reaches another app. Each step reads back through the real API what the drawer saved.
 */

const stamp = `lrd${Date.now()}`;
const libraryName = `Routed ${stamp}`;
const indexerName = `Route indexer ${stamp}`;
const clientHost = `route-${stamp}.example.test`;

interface Routing { sources: Array<{ indexerId: string }>; downloadClients: Array<{ downloadClientId: string }> }

test.describe("Library routing drawer", () => {
  test.setTimeout(90_000);
  let folder = "";
  let libraryId = "";
  let indexerId = "";
  let clientId = "";

  test.beforeAll(async ({ browser }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-lrd-"));
    const page = await browser.newPage();
    await openScreen(page, "/indexers/library-routing");
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    const indexer = await api<{ id: string }>(page, "POST", "/api/indexers", { name: indexerName, protocol: "torznab", baseUrl: `http://route-${stamp}.example.test`, apiPath: "/api", apiKey: "example-key", priority: 25, categories: "2000", mediaScope: "movies", isEnabled: false });
    expect(indexer.status, JSON.stringify(indexer.body)).toBeLessThan(300);
    indexerId = indexer.body.id;
    const client = await api<{ id: string }>(page, "POST", "/api/download-clients", { protocol: "qbittorrent", host: clientHost, port: 8080, username: "u", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false });
    expect(client.status, JSON.stringify(client.body)).toBeLessThan(300);
    clientId = client.body.id;
    await api(page, "PUT", `/api/libraries/${libraryId}/routing`, { sources: [], downloadClients: [] });
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/library-routing");
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await api(page, "DELETE", `/api/indexers/${indexerId}`);
    await api(page, "DELETE", `/api/download-clients/${clientId}`);
    await page.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  const routing = async (page: Page) => (await api<Routing>(page, "GET", `/api/libraries/${libraryId}/routing`)).body;

  test("the list counts the libraries and names each with Movies or TV shows; the drawer warns about no indexers, then no client, then neither once both are chosen and saved", async ({ page }) => {
    await openScreen(page, "/indexers/library-routing");
    await untilQuiet(page);
    const libraries = (await api<unknown[]>(page, "GET", "/api/libraries")).body.length;
    await expect(page.getByText(`${libraries} ${libraries === 1 ? "library" : "libraries"}`, { exact: true })).toBeVisible();
    const mine = page.getByRole("row").filter({ hasText: libraryName });
    await expect(mine).toContainText("Movies");
    await expect(mine).toContainText("No indexers");
    await mine.click();
    const drawer = page.getByRole("dialog").filter({ hasText: "Save routing" });
    const noIndexers = drawer.getByText("No indexers selected — Deluno can't look for releases for this library.");
    const noClient = drawer.getByText("No download client selected — approved releases have nowhere to go.");
    await expect(noIndexers).toBeVisible();
    await expect(noClient).toHaveCount(0);

    await drawer.getByRole("switch", { name: new RegExp(`^${indexerName}`) }).click();
    await expect(noIndexers).toHaveCount(0);
    await expect(noClient).toBeVisible();
    await drawer.getByRole("switch", { name: new RegExp(`^${clientHost}|qBittorrent`) }).first().click();
    await expect(noClient).toHaveCount(0);

    await drawer.getByRole("button", { name: "Save routing", exact: true }).click();
    await expect.poll(async () => (await routing(page)).sources.map((source) => source.indexerId)).toEqual([indexerId]);
    expect((await routing(page)).downloadClients.map((target) => target.downloadClientId)).toEqual([clientId]);

    await drawer.getByRole("button", { name: "Cancel" }).click();
    await expect(drawer).toHaveCount(0);
    await expect(mine).toContainText(indexerName);
    await expect(mine).not.toContainText("No indexers");
  });
});
