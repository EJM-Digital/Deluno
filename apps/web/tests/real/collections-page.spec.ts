import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Collections against the real server and the real metadata service, nothing stubbed (measure 4, issue 1270): a TMDb collection
 * is followed into a library of this spec's own, with adding its missing films and searching both off, and every control on the
 * page is pressed and checked against the request the server received and the state it keeps.
 */

const stamp = `cl${Date.now()}`;
// TMDb's own id for a long-running film franchise; any collection id TMDb answers for would do.
const COLLECTION_ID = "645";
let folder = "";
let libraryId = "";
let collectionId = "";

interface Collection { id: string; name: string; monitored: boolean; monitorMovies: boolean; tags: string | null; libraryId: string; searchOnAdd?: boolean }
interface Member { providerId: string; title: string; isExcluded: boolean }

const collections = async (page: Page) => (await api<Collection[]>(page, "GET", "/api/movie-collections")).body;
const mine = async (page: Page) => (await collections(page)).find((item) => item.id === collectionId);
const members = async (page: Page) => (await api<Member[]>(page, "GET", `/api/movie-collections/${collectionId}/members`)).body;

test.describe("Collections", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/collections");
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-collections-"));
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Collections ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/collections");
    await api(page, "POST", "/api/movie-collections/remove", { ids: collectionId ? [collectionId] : [], removeMovies: false });
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("Follow collection opens the form, Cancel sends nothing, and a collection TMDb does not know is refused in words", async ({ page }) => {
    await openScreen(page, "/collections");
    await untilQuiet(page);
    await page.getByRole("button", { name: "Follow collection", exact: true }).first().click();
    const id = page.getByPlaceholder("645");
    await expect(id).toBeVisible();
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(id).toHaveCount(0);
    expect(requests.find("POST", /^\/api\/movie-collections$/)).toHaveLength(0);

    await page.getByRole("button", { name: "Follow collection", exact: true }).first().click();
    await page.getByPlaceholder("645").fill("999999999");
    await page.getByRole("combobox").first().selectOption(libraryId);
    await page.getByRole("button", { name: /^Add collection/ }).click();
    await expect.poll(() => requests.find("POST", /^\/api\/movie-collections$/).length).toBe(1);
    requests.stop();
    await expectNoErrorScreen(page);
    expect((await collections(page)).filter((item) => item.libraryId === libraryId)).toHaveLength(0);
  });

  test("a TMDb collection is followed into the chosen library with the switches as set, and shows with its members", async ({ page }) => {
    await openScreen(page, "/collections");
    await untilQuiet(page);
    await page.getByRole("button", { name: "Follow collection", exact: true }).first().click();
    await page.getByPlaceholder("645").fill(COLLECTION_ID);
    await page.getByRole("combobox").first().selectOption(libraryId);
    await page.getByRole("switch", { name: "Monitor movies" }).uncheck();
    await page.getByRole("switch", { name: "Search on add" }).uncheck();
    const requests = recordRequests(page);
    await page.getByRole("button", { name: /^Add collection/ }).click();
    await expect.poll(() => requests.find("POST", /^\/api\/movie-collections$/).length).toBe(1);
    requests.stop();
    expect(requests.find("POST", /^\/api\/movie-collections$/)[0].body).toMatchObject({ providerId: COLLECTION_ID, libraryId, monitorMovies: false, searchOnAdd: false });

    await expect.poll(async () => (await collections(page)).filter((item) => item.libraryId === libraryId).length).toBe(1);
    collectionId = (await collections(page)).find((item) => item.libraryId === libraryId)!.id;
    await untilQuiet(page);
    await expect(page.getByRole("checkbox", { name: /^Select / }).first()).toBeVisible();
    expect((await members(page)).length).toBeGreaterThan(1);
  });

  test("Monitor and Add missing switch the collection on the server, and Refresh asks the metadata service again", async ({ page }) => {
    await openScreen(page, "/collections");
    await untilQuiet(page);
    const card = page.locator("div").filter({ has: page.getByRole("button", { name: /^Stop following/ }) }).filter({ hasText: `Collections ${stamp}` }).last();
    const monitor = card.getByRole("switch", { name: "Monitor", exact: true });
    const before = await monitor.isChecked();
    await monitor.click();
    await expect.poll(async () => (await mine(page))?.monitored).toBe(!before);
    await monitor.click();
    await expect.poll(async () => (await mine(page))?.monitored).toBe(before);

    const addMissing = card.getByRole("switch", { name: "Add missing", exact: true });
    await addMissing.click();
    await expect.poll(async () => (await mine(page))?.monitorMovies).toBe(true);
    await addMissing.click();
    await expect.poll(async () => (await mine(page))?.monitorMovies).toBe(false);

    const requests = recordRequests(page);
    await card.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect.poll(() => requests.find("POST", /\/refresh$/).length).toBe(1);
    requests.stop();
    await untilQuiet(page);
    await expectNoErrorScreen(page);
  });

  test("a member can be excluded and included again, and Excluded shows while it is", async ({ page }) => {
    await openScreen(page, "/collections");
    await untilQuiet(page);
    const first = (await members(page))[0];
    const exclude = page.getByRole("button", { name: new RegExp(`^Exclude .*${first.title.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`) }).first();
    await exclude.click();
    await expect.poll(async () => (await members(page)).find((m) => m.providerId === first.providerId)?.isExcluded).toBe(true);
    await expect(page.getByText("Excluded", { exact: true }).first()).toBeVisible();
    await page.getByRole("button", { name: /^Include again / }).first().click();
    await expect.poll(async () => (await members(page)).find((m) => m.providerId === first.providerId)?.isExcluded).toBe(false);
  });

  test("tags for new movies are saved only once changed", async ({ page }) => {
    await openScreen(page, "/collections");
    await untilQuiet(page);
    const tag = `colltag${stamp}`;
    const made = await api<{ id: string }>(page, "POST", "/api/tags", { name: tag, color: "#3b82f6" });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    try {
      await page.reload();
      await untilQuiet(page);
      await expect(page.getByRole("button", { name: /^Save tags for / })).toHaveCount(0);
      const field = page.getByRole("textbox", { name: /^Tags .* gives to movies it adds$/ }).first();
      await field.fill(tag);
      await page.getByRole("button", { name: `Add ${tag} to Tags ${(await mine(page))!.name} gives to movies it adds` }).click();
      const requests = recordRequests(page);
      await page.getByRole("button", { name: /^Save tags for / }).click();
      await expect.poll(async () => (await mine(page))?.tags ?? "").toContain(tag);
      requests.stop();
      expect(requests.seen.filter((r) => r.method === "PUT" && /movie-collections/.test(r.path))).toHaveLength(1);
    } finally {
      await api(page, "DELETE", `/api/tags/${made.body.id}`);
    }
  });

  test("selecting the collection offers Monitor, Stop monitoring and the bulk settings, and Stop following asks first", async ({ page }) => {
    await openScreen(page, "/collections");
    await untilQuiet(page);
    const name = (await mine(page))!.name;
    // A bulk action may clear the selection when it finishes, so each step ticks the collection again if it has to.
    const select = async () => {
      const tick = page.getByRole("checkbox", { name: `Select ${name}` });
      if (!(await tick.isChecked())) await tick.check();
      await expect(page.getByText("1 selected", { exact: true })).toBeVisible();
    };

    await select();
    await page.getByRole("button", { name: "Monitor", exact: true }).first().click();
    await expect.poll(async () => (await mine(page))?.monitored).toBe(true);
    await untilQuiet(page);
    await select();
    await page.getByRole("button", { name: "Stop monitoring", exact: true }).click();
    await expect.poll(async () => (await mine(page))?.monitored).toBe(false);
    await untilQuiet(page);

    await select();
    await page.getByLabel(/^Add missing movies/).selectOption("yes");
    await page.getByLabel(/^Search when movies are added/).selectOption("no");
    await page.getByLabel(/^Look for movies/).selectOption({ index: 1 });
    await page.getByLabel(/^Movie folder/).selectOption(libraryId);
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Apply to 1 collection", exact: true }).click();
    await expect.poll(() => requests.find("PUT", /^\/api\/movie-collections\/bulk$/).length).toBe(1);
    requests.stop();
    await expect.poll(async () => (await mine(page))?.monitorMovies).toBe(true);
    await untilQuiet(page);

    await select();
    await page.getByRole("button", { name: "Stop following 1 selected", exact: true }).click();
    const confirm = page.getByRole("dialog").or(page.getByRole("alertdialog")).first();
    await expect(confirm).toBeVisible();
    await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(await mine(page)).toBeTruthy();

    // The card's own Stop following does the same for this one collection.
    await page.getByRole("checkbox", { name: `Select ${name}` }).uncheck();
    await page.getByRole("button", { name: `Stop following ${name}`, exact: true }).click();
    await expect(confirm).toBeVisible();
    await confirm.getByRole("button", { name: /^Stop following/ }).click();
    await expect.poll(async () => await mine(page)).toBeUndefined();
    collectionId = "";
  });
});
