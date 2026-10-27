import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * A film's metadata drawer against the real server and the real metadata service, nothing stubbed (measure 4, issue 1270): Find
 * asks the provider, Preview shows what linking to a match would change and writes nothing, Cancel preview drops it, Apply remap
 * links the film to that match, Open provider page then points at the provider's own page, and Fine-tune writes values by hand.
 */

const stamp = `mt${Date.now()}`;
let folder = "";
let libraryId = "";
let movieId = "";
const title = "Arrival";

interface MovieDetail { id: string; metadataProvider?: string | null; metadataProviderId?: string | null; imdbId?: string | null; originalTitle?: string | null; overview?: string | null }
const movie = async (page: Page) => (await api<MovieDetail>(page, "GET", `/api/movies/${movieId}`)).body;

test.describe("A film's metadata drawer", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-metadata-"));
    const lib = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Metadata ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(lib.status, JSON.stringify(lib.body)).toBeLessThan(300);
    libraryId = lib.body.id;
    const made = await api<{ id: string }>(page, "POST", "/api/movies/", { title, releaseYear: 2016, monitored: false, libraryId });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    movieId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    await api(page, "DELETE", `/api/movies/${movieId}`);
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  async function openDrawer(page: Page) {
    await openScreen(page, "/movies");
    await page.goto(`/movies/${movieId}`);
    await untilQuiet(page);
    await expectNoErrorScreen(page);
    await page.getByRole("button", { name: "Edit metadata", exact: true }).click();
    const drawer = page.getByRole("dialog", { name: "Metadata" });
    await expect(drawer).toBeVisible();
    return drawer;
  }

  test("Find lists matches; Preview shows what linking would change and writes nothing, and Cancel preview drops it", async ({ page }) => {
    const drawer = await openDrawer(page);
    const search = drawer.getByRole("textbox", { name: "Search the provider" });
    await search.fill(`${title} 2016`);
    await search.press("Enter");
    const preview = drawer.getByRole("button", { name: "Preview", exact: true }).first();
    await expect(preview).toBeVisible({ timeout: 30_000 });

    const requests = recordRequests(page);
    await preview.click();
    const region = drawer.getByRole("region", { name: "Metadata remap preview" });
    await expect(region.getByText("Review this remap")).toBeVisible({ timeout: 30_000 });
    await expect(region.getByText("Nothing changes until you apply this reviewed preview.")).toBeVisible();
    await expect(region.getByText("Current", { exact: true })).toBeVisible();
    await expect(region.getByText("Proposed", { exact: true })).toBeVisible();
    expect(requests.seen.filter((r) => r.method !== "GET" && /link|remap/i.test(r.path) && !/preview/i.test(r.path))).toHaveLength(0);

    await region.getByRole("button", { name: "Cancel preview", exact: true }).click();
    await expect(region).toHaveCount(0);
    requests.stop();
    expect((await movie(page)).metadataProviderId ?? null, "looking did not link the film").toBeNull();
  });

  test("Apply remap links the film to the chosen match, and Open provider page then points at the provider's own page", async ({ page }) => {
    const drawer = await openDrawer(page);
    const search = drawer.getByRole("textbox", { name: "Search the provider" });
    await search.fill(`${title} 2016`);
    await drawer.getByRole("button", { name: "Find", exact: true }).click();
    await drawer.getByRole("button", { name: "Preview", exact: true }).first().click();
    const region = drawer.getByRole("region", { name: "Metadata remap preview" });
    await expect(region.getByText("Review this remap")).toBeVisible({ timeout: 30_000 });
    await region.getByRole("button", { name: "Apply remap", exact: true }).click();

    await expect.poll(async () => (await movie(page)).metadataProviderId ?? "", { timeout: 30_000 }).not.toBe("");
    await expect(drawer.getByRole("link", { name: "Open provider page" })).toHaveAttribute("href", /^https?:\/\//);
    await expect(drawer.getByRole("button", { name: "Refresh from provider", exact: true })).toBeEnabled();
  });

  test("Fine-tune writes the original title, IMDb ID and overview by hand and the server keeps them", async ({ page }) => {
    const drawer = await openDrawer(page);
    await drawer.getByRole("button", { name: /^Fine-tune/ }).click();
    const inputs = drawer.getByRole("textbox");
    const imdb = drawer.getByPlaceholder("tt0000000");
    await imdb.fill("tt2543164");
    const original = inputs.nth(await inputs.evaluateAll((all, ph) => (all as HTMLInputElement[]).indexOf((all as HTMLInputElement[]).find((e) => e.placeholder === ph)!), "tt0000000") - 1);
    await original.fill(`Original ${stamp}`);
    const overview = inputs.last();
    await overview.fill(`By hand ${stamp}`);
    await drawer.getByRole("button", { name: "Save metadata" }).click();
    await expect.poll(async () => (await movie(page)).overview).toBe(`By hand ${stamp}`);
    const saved = await movie(page);
    expect(saved.imdbId).toBe("tt2543164");
    expect(saved.originalTitle).toBe(`Original ${stamp}`);
  });
});
