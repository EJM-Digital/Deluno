import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { exerciseColumns } from "../helpers/columns";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Subtitles, the libraries table, against the real server (measure 4, issue 1329). The libraries are this spec's
 * own real folders; their subtitle settings are set through the real API and the table is read back.
 */

const stamp = Date.now();
const folders: string[] = [];
const made: string[] = [];

interface Lib { id: string; name: string; subtitleLanguages?: string[]; subtitleLanguageMode?: string; subtitleOmitLanguageCode?: boolean }
const libs = async (page: Page) => (await api<Lib[]>(page, "GET", "/api/libraries")).body;

async function makeLibrary(page: Page, name: string, mediaType: string, subtitles?: Record<string, unknown>) {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-subtable-"));
  folders.push(folder);
  const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name, mediaType, purpose: "collection", rootPath: folder });
  expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
  made.push(library.body.id);
  if (subtitles) {
    const put = await api(page, "PUT", `/api/libraries/${library.body.id}/subtitles`, subtitles);
    expect(put.status, JSON.stringify(put.body)).toBeLessThan(300);
  }
  return library.body.id;
}

test.describe("Subtitles, libraries table", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(60_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/settings/subtitles");
    await makeLibrary(page, `Films ${stamp}`, "movies", { languages: ["en", "fr"], mode: "all", unknownLanguage: "en", embeddedCounts: false });
    await makeLibrary(page, `Shows ${stamp}`, "tv", { languages: ["de"], mode: "first", embeddedCounts: true, pictureBasedCounts: false });
    await makeLibrary(page, `Bare ${stamp}`, "movies");
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/settings/subtitles");
    for (const id of made) await api(page, "DELETE", `/api/libraries/${id}`);
    await page.close();
    for (const folder of folders) fs.rmSync(folder, { recursive: true, force: true });
  });

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/subtitles");
    await untilQuiet(page);
  });

  const rowOf = (page: Page, name: string) => page.locator('#main-content [role="row"]').filter({ hasText: name });

  test("each library is a row with Movies or TV shows, its languages or None, how many per file, unnamed subtitles, embedded notes and the Asking chip, and the heading counts asking libraries", async ({ page }) => {
    const films = rowOf(page, `Films ${stamp}`);
    await expect(films).toContainText("Movies");
    await expect(films).toContainText("English, French");
    await expect(films).toContainText("All of them");
    await expect(films).toContainText("English");
    await expect(films).toContainText("Embedded tracks do not count");
    await expect(films.getByText("2", { exact: true })).toBeVisible();

    const shows = rowOf(page, `Shows ${stamp}`);
    await expect(shows).toContainText("TV shows");
    await expect(shows).toContainText("German");
    await expect(shows).toContainText("Any one of them");
    await expect(shows).toContainText("Left unknown");
    await expect(shows).toContainText("Picture-based embedded tracks do not count");

    const bare = rowOf(page, `Bare ${stamp}`);
    await expect(bare).toContainText("None");
    await expect(bare).toContainText("Nothing is fetched for this shelf");
    await expect(bare.getByText("Off", { exact: true })).toBeVisible();

    const all = await libs(page);
    const asking = all.filter((l) => (l.subtitleLanguages ?? []).length > 0).length;
    await expect(page.getByText(`${asking} of ${all.length} ${all.length === 1 ? "library" : "libraries"} asking`, { exact: true })).toBeVisible();
  });

  test("closing the library drawer after an edit discards it without asking, and the library keeps what it had", async ({ page }) => {
    await rowOf(page, `Films ${stamp}`).click();
    const drawer = page.getByRole("dialog", { name: `Films ${stamp}` });
    await expect(drawer).toBeVisible();
    await drawer.getByRole("switch", { name: /Name subtitles after the video only/ }).click();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(page.getByText("Discard unsaved changes?")).toHaveCount(0);
    await expect(page.getByRole("dialog", { name: "Unsaved changes" })).toHaveCount(0);
    const films = (await libs(page)).find((l) => l.name === `Films ${stamp}`)!;
    expect(films.subtitleOmitLanguageCode ?? false).toBe(false);
  });

  test("the Subtitle languages table sorts by a header, reorders by dragging, remembers both and resets", async ({ page }) => {
    await exerciseColumns(page, {
      scope: () => page.locator("#main-content"),
      defaultOrder: ["library", "languages", "howMany", "unnamed", "asking"],
      sortId: "library",
      cellsOf: async (scope) => {
        const names = await scope.locator('[role="rowgroup"] [role="row"]').evaluateAll((rows) => rows.map((row) => (row.querySelector('[role="cell"]') as HTMLElement | null)?.innerText.split("\n")[0] ?? ""));
        return names.filter((n) => n.endsWith(` ${stamp}`));
      },
      columnsButton: () => page.locator("#main-content").getByRole("button", { name: "Columns", exact: true }).first(),
      move: { from: "unnamed", onto: "library" }
    });
  });
});
