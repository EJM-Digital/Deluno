import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * "Already have files here?" on the Libraries screen against the real server and real folders, nothing stubbed
 * (measure 4, issue 1270): the review lists what is really in the library folder, nothing is added until the
 * person ticks it, Import selected adds exactly the ticked ones, and what was added has its file on record.
 */

const stamp = `ex${Date.now()}`;
let root = "";
let libraryId = "";
const libraryName = `Existing ${stamp}`;
const films = [`Alpha ${stamp}`, `Bravo ${stamp}`, `Charlie ${stamp}`];

function makeFilm(title: string) {
  const folder = path.join(root, `${title} (2018)`);
  fs.mkdirSync(folder, { recursive: true });
  fs.writeFileSync(path.join(folder, `${title} (2018) 1080p WEB-DL.mkv`), Buffer.alloc(4096, 7));
}

async function openReview(page: Page) {
  await openScreen(page, "/settings/libraries");
  await untilQuiet(page);
  await page.getByRole("row").filter({ hasText: libraryName }).first().click();
  const drawer = page.getByRole("dialog", { name: libraryName });
  await expect(drawer).toBeVisible();
  await drawer.getByRole("button", { name: "Review existing files", exact: true }).click();
  const review = page.getByRole("dialog").filter({ hasText: "Review before import" });
  await expect(review).toBeVisible();
  return review;
}

test.describe("Review existing files", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    root = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-existing-"));
    for (const film of films) makeFilm(film);
    const page = await browser.newPage();
    await openScreen(page, "/settings/libraries");
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: root });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    const list = await api<{ items: Array<{ id: string }> }>(page, "GET", `/api/movies/page?search=${stamp}&pageSize=100&facets=false`);
    for (const item of list.body.items ?? []) await api(page, "DELETE", `/api/movies/${item.id}`);
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(root, { recursive: true, force: true });
  });

  const found = async (page: Page) => (await api<{ items: Array<{ id: string; title: string }> }>(page, "GET", `/api/movies/page?search=${stamp}&pageSize=100&facets=false`)).body.items.map((i) => i.title).sort();

  test("lists what is in the folder and adds nothing until it is ticked; Close shuts the review", async ({ page }) => {
    const review = await openReview(page);
    for (const film of films) await expect(review.getByRole("checkbox", { name: `Select ${film}`, exact: true })).toBeVisible();
    await expect(review.getByText("0 selected on this page")).toBeVisible();
    await expect(review.getByRole("button", { name: "Import selected", exact: true })).toBeDisabled();
    // Only one page of three: both page buttons are held.
    await expect(review.getByRole("button", { name: "Previous", exact: true })).toBeDisabled();
    await expect(review.getByRole("button", { name: "Next", exact: true })).toBeDisabled();
    expect(await found(page), "looking adds nothing").toEqual([]);

    await review.getByRole("button", { name: "Close review existing media", exact: true }).click();
    await expect(review).toHaveCount(0);
    expect(await found(page)).toEqual([]);
  });

  test("Import selected adds exactly the ticked title with its file on record, and Select all on this page ticks the rest", async ({ page }) => {
    const review = await openReview(page);
    await review.getByRole("checkbox", { name: `Select ${films[0]}`, exact: true }).click();
    await expect(review.getByText("1 selected on this page")).toBeVisible();

    const requests = recordRequests(page);
    await review.getByRole("button", { name: "Import selected", exact: true }).click();
    await expect(page.getByText("1 movie reviewed and added.").first()).toBeVisible();
    const posted = requests.find("POST", new RegExp(`^/api/libraries/${libraryId}/import-existing/selected$`));
    expect(posted).toHaveLength(1);
    expect(JSON.stringify(posted[0].body)).toContain(films[0]);
    expect(JSON.stringify(posted[0].body)).not.toContain(films[1]);
    expect(await found(page)).toEqual([films[0]]);

    // The file Deluno found is on the title's record.
    const added = (await api<{ items: Array<{ id: string; title: string }> }>(page, "GET", `/api/movies/page?search=${films[0]}&pageSize=10&facets=false`)).body.items[0];
    const copy = await api<{ filePath?: string | null; path?: string | null }>(page, "GET", `/api/movies/${added.id}/copy`);
    expect(JSON.stringify(copy.body)).toContain(films[0]);

    const again = await openReview(page);
    await again.getByRole("checkbox", { name: "Select all on this page" }).click();
    await again.getByRole("button", { name: "Import selected", exact: true }).click();
    await expect(page.getByText(/movies? reviewed and added\./).first()).toBeVisible();
    requests.stop();
    expect(await found(page)).toEqual([...films].sort());
  });

  const movieId = async (page: Page, title: string) =>
    (await api<{ items: Array<{ id: string; title: string }> }>(page, "GET", `/api/movies/page?search=${encodeURIComponent(title)}&pageSize=10&facets=false`)).body.items.find((i) => i.title === title)!.id;

  test("Rescan folder on a title with a file reports the folder, lists a video it is not tracking, and Review for import sends it to Import issues", async ({ page }) => {
    await openScreen(page, "/movies");
    const id = await movieId(page, films[0]);
    const stray = `${films[0]} (2018) Extended Cut.mkv`;
    fs.writeFileSync(path.join(root, `${films[0]} (2018)`, stray), Buffer.alloc(4096, 9));
    await page.goto(`/movies/${id}`);
    await untilQuiet(page);

    await page.getByRole("button", { name: `Rescan the folder for ${films[0]}`, exact: true }).click();
    const ask = page.getByRole("alertdialog").or(page.getByRole("dialog")).filter({ hasText: `Rescan the folder for ${films[0]}?` });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);

    await page.getByRole("button", { name: `Rescan the folder for ${films[0]}`, exact: true }).click();
    const requests = recordRequests(page);
    await ask.getByRole("button", { name: "Rescan now", exact: true }).click();
    const report = page.getByRole("dialog", { name: "Folder rescanned" });
    await expect(report).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/filesystem\/rescan-title$/)).toHaveLength(1);
    await expect(report.getByText("Videos Deluno is not tracking")).toBeVisible();

    await report.getByRole("button", { name: `Review ${stray} for import`, exact: true }).click();
    await expect(page.getByText("Added to Import issues so you can review it.").first()).toBeVisible();
    await report.getByRole("button", { name: "Close", exact: true }).click();
    await expect(report).toHaveCount(0);
  });

  test("Preview rename of a title whose folder also holds another video says in words that Deluno leaves the folder alone", async ({ page }) => {
    await openScreen(page, "/movies");
    const id = await movieId(page, films[0]);
    await page.goto(`/movies/${id}`);
    await untilQuiet(page);
    await page.getByRole("button", { name: `Preview the folder rename for ${films[0]}`, exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Rename preview" });
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText("so it is not this title's own folder. Deluno leaves it alone.");
    await dialog.getByRole("button", { name: /^(Close|Not now)$/ }).first().click();
    await expect(dialog).toHaveCount(0);
  });

  test("Preview rename of a title in an oddly named folder shows the folder now and after, Not now leaves it, and Rename folder renames it on disk", async ({ page }) => {
    const delta = `Delta ${stamp}`;
    const odd = path.join(root, `${delta} (2018) [rip by someone]`);
    fs.mkdirSync(odd, { recursive: true });
    fs.writeFileSync(path.join(odd, `${delta} (2018) 1080p WEB-DL.mkv`), Buffer.alloc(4096, 5));
    const review = await openReview(page);
    await review.getByRole("checkbox", { name: `Select ${delta}`, exact: true }).click();
    await review.getByRole("button", { name: "Import selected", exact: true }).click();
    await expect(page.getByText(/movies? reviewed and added\./).first()).toBeVisible();

    await page.goto(`/movies/${await movieId(page, delta)}`);
    await untilQuiet(page);
    await page.getByRole("button", { name: `Preview the folder rename for ${delta}`, exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Rename preview" });
    await expect(dialog.getByText("Folder now")).toBeVisible();
    await expect(dialog.getByText("Folder after")).toBeVisible();
    await expect(dialog).toContainText(`${delta} (2018) [rip by someone]`);
    await dialog.getByRole("button", { name: "Not now", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    expect(fs.existsSync(odd)).toBe(true);

    await page.getByRole("button", { name: `Preview the folder rename for ${delta}`, exact: true }).click();
    const requests = recordRequests(page);
    await dialog.getByRole("button", { name: "Rename folder", exact: true }).click();
    await expect(page.getByText("The folder was renamed.").first()).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/movies\/bulk\/rename-apply$/)).toHaveLength(1);
    await expect.poll(() => fs.existsSync(odd)).toBe(false);
    expect(fs.existsSync(path.join(root, `${delta} (2018)`))).toBe(true);
  });

  test("Remove from Deluno with its files ticked moves the file to the recycle bin and takes the title out", async ({ page }) => {
    await openScreen(page, "/movies");
    const id = await movieId(page, films[1]);
    const file = path.join(root, `${films[1]} (2018)`, `${films[1]} (2018) 1080p WEB-DL.mkv`);
    expect(fs.existsSync(file)).toBe(true);
    await page.goto(`/movies/${id}`);
    await untilQuiet(page);

    await page.getByRole("button", { name: "Remove from Deluno", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: `Remove ${films[1]}?` });
    const files = dialog.getByRole("checkbox", { name: /Move imported files to the recycle bin/ });
    await expect(files).toBeEnabled();
    await expect(files).not.toBeChecked();
    await expect(dialog.getByRole("link", { name: "Open the recycle bin" })).toBeVisible();
    await files.click();
    await expect(files).toBeChecked();
    const requests = recordRequests(page);
    await dialog.getByRole("button", { name: "Remove movie", exact: true }).click();
    await expect(page).toHaveURL(/\/movies$/);
    requests.stop();
    expect(requests.find("POST", /^\/api\/movies\/bulk$/)[0].body).toMatchObject({ operation: "remove", deleteFiles: true });
    await expect.poll(() => fs.existsSync(file)).toBe(false);
    expect((await api(page, "GET", `/api/movies/${id}`)).status).toBe(404);
  });
});
