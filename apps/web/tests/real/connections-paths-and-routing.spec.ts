import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Remote path mappings and Library routing against the real server (measure 4, issue 1270): a mapping is
 * tested (the folder Deluno would read is really there, or really is not), edited, cancelled mid-edit and removed; and a
 * library nothing is routed to says so and its Fix button opens the routing drawer. The client is a disabled qBittorrent
 * address nobody listens on; nothing here reaches another app.
 */

const stamp = `cp${Date.now()}`;
const host = `maps-${stamp}.example.test`;
let clientId = "";
let libraryId = "";
let realFolder = "";
let libraryFolder = "";
const libraryName = `Fix ${stamp}`;

interface Mapping { id: string; remotePath: string; localPath: string; host?: string | null }
const mappings = async (page: Page) => (await api<Mapping[]>(page, "GET", `/api/download-clients/${clientId}/path-mappings`)).body;
const row = (page: Page, text: string) => page.getByRole("row").filter({ hasText: text }).first();

test.describe("Remote path mappings and Library routing", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    realFolder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-mapping-"));
    libraryFolder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-fix-"));
    const page = await browser.newPage();
    await openScreen(page, "/indexers/path-mappings");
    const client = await api<{ id: string }>(page, "POST", "/api/download-clients", {
      protocol: "qbittorrent", host, port: 8080, username: "u", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false
    });
    expect(client.status, JSON.stringify(client.body)).toBeLessThan(300);
    clientId = client.body.id;
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: libraryFolder });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/path-mappings");
    if (libraryId) await api(page, "DELETE", `/api/libraries/${libraryId}`);
    if (clientId) await api(page, "DELETE", `/api/download-clients/${clientId}`);
    await page.close();
    fs.rmSync(realFolder, { recursive: true, force: true });
    fs.rmSync(libraryFolder, { recursive: true, force: true });
  });

  test("Test says in words whether the folder Deluno would read is there: reachable for a real folder, not for one that is missing", async ({ page }) => {
    await openScreen(page, "/indexers/path-mappings");
    await untilQuiet(page);
    const select = page.getByLabel("Download client this mapping is filed under");
    const add = page.getByRole("button", { name: "Add mapping", exact: true });
    for (const [remote, local] of [["/downloads/there", realFolder], ["/downloads/gone", path.join(realFolder, "no-such-folder")]]) {
      await select.selectOption(clientId);
      await page.getByLabel("Client path").fill(remote);
      await page.getByLabel("Deluno path").fill(local);
      await add.click();
      await expect(row(page, remote)).toBeVisible();
    }

    const requests = recordRequests(page);
    await row(page, "/downloads/there").getByRole("button", { name: "Test", exact: true }).click();
    await expect.poll(() => requests.find("POST", /\/path-mappings\/[^/]+\/test$/).length).toBe(1);
    const good = page.locator("[data-sonner-toast]").first();
    await expect(good).toBeVisible();
    const goodWords = await good.innerText();
    await page.locator("[data-sonner-toast]").first().click().catch(() => undefined);

    await row(page, "/downloads/gone").getByRole("button", { name: "Test", exact: true }).click();
    await expect.poll(() => requests.find("POST", /\/path-mappings\/[^/]+\/test$/).length).toBe(2);
    requests.stop();
    await expect(page.locator("[data-sonner-toast]").filter({ hasNotText: goodWords }).first()).toBeVisible();
  });

  test("Edit loads a mapping into the form, Cancel leaves it alone, and Save changes keeps the new client and Deluno paths", async ({ page }) => {
    await openScreen(page, "/indexers/path-mappings");
    await untilQuiet(page);
    await row(page, "/downloads/gone").getByRole("button", { name: "Edit", exact: true }).click();
    await expect(page.getByLabel("Client path")).toHaveValue("/downloads/gone");
    await page.getByLabel("Deluno path").fill("/somewhere/else");
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page.getByLabel("Client path")).toHaveValue("");
    expect((await mappings(page)).find((m) => m.remotePath === "/downloads/gone")?.localPath).toBe(path.join(realFolder, "no-such-folder"));

    await row(page, "/downloads/gone").getByRole("button", { name: "Edit", exact: true }).click();
    await page.getByLabel("Client path").fill("/downloads/moved");
    await page.getByLabel("Deluno path").fill(realFolder);
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Save changes", exact: true }).click();
    // An edit puts the replacement in first and only then takes the old one out, so a failed save never loses the original.
    await expect.poll(() => requests.find("POST", /\/path-mappings$/).length).toBe(1);
    await expect.poll(() => requests.find("DELETE", /\/path-mappings\/[^/]+$/).length).toBe(1);
    requests.stop();
    await expect.poll(async () => (await mappings(page)).filter((m) => m.remotePath.startsWith("/downloads/g") || m.remotePath === "/downloads/moved").map((m) => [m.remotePath, m.localPath])).toEqual([["/downloads/moved", realFolder]]);
    await expect(page.getByText("Remote path mapping updated").first()).toBeVisible();
  });

  // FOUND BY THIS ROW: editing only the Deluno path of a mapping fails. The screen adds the edited mapping first and removes the old one
  // after, but the server keeps one mapping per client and client path, so the add is refused with HTTP 500 and the screen says
  // "Remote path mapping could not be saved." The only way to change where a client path points is to remove the mapping and add it again.
  test.fixme("Edit of only the Deluno path saves the new path (the server answers 500 for the same client path)", async ({ page }) => {
    await openScreen(page, "/indexers/path-mappings");
    await untilQuiet(page);
    await row(page, "/downloads/there").getByRole("button", { name: "Edit", exact: true }).click();
    await page.getByLabel("Deluno path").fill(realFolder + "-other");
    await page.getByRole("button", { name: "Save changes", exact: true }).click();
    await expect(page.getByText("Remote path mapping updated").first()).toBeVisible();
    expect((await mappings(page)).filter((m) => m.remotePath === "/downloads/there").map((m) => m.localPath)).toEqual([realFolder + "-other"]);
  });

  test("the × button removes a mapping from the list and from the server", async ({ page }) => {
    await openScreen(page, "/indexers/path-mappings");
    await untilQuiet(page);
    for (const remote of ["/downloads/moved", "/downloads/there"]) {
      await row(page, remote).getByRole("button", { name: /^Remove the .* mapping$/ }).click();
      await expect(row(page, remote)).toHaveCount(0);
    }
    expect(await mappings(page)).toEqual([]);
  });

  test("a library nothing is routed to says No indexers in words, offers no Fix, and its row opens the routing drawer", async ({ page }) => {
    await openScreen(page, "/indexers/library-routing");
    await untilQuiet(page);
    await api(page, "PUT", `/api/libraries/${libraryId}/routing`, { sources: [], downloadClients: [] });
    await page.reload();
    await untilQuiet(page);
    const mine = row(page, libraryName);
    await expect(mine).toContainText("No indexers");
    await expect(mine.getByRole("button", { name: /^Fix / })).toHaveCount(0);
    await mine.click();
    const drawer = page.getByRole("dialog").filter({ hasText: "Save routing" });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText(libraryName).first()).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);
  });
});
