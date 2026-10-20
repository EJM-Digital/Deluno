import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * A film kept in more than one library, against the real server and real folders, nothing stubbed (measure 4,
 * issue 1270): the Libraries card on the movie page keeps it in another library, picks the one searches go through,
 * moves it, takes it out again, and asks again whether a copy that lost its file should be wanted.
 */

const stamp = `lm${Date.now()}`;
const roots: string[] = [];
const libs: Record<"a" | "b" | "c", { id: string; name: string }> = { a: { id: "", name: `Alpha lib ${stamp}` }, b: { id: "", name: `Bravo lib ${stamp}` }, c: { id: "", name: `Charlie lib ${stamp}` } };
let movieId = "";
const title = `Member ${stamp}`;

interface Membership { libraryId: string; libraryName: string; hasFile: boolean; notWantedHere?: boolean }
const memberships = async (page: Page) => (await api<Membership[]>(page, "GET", `/api/movies/${movieId}/libraries/`)).body;
const card = (page: Page) => page.getByRole("region", { name: "Libraries" }).or(page.locator("section, div").filter({ has: page.getByRole("heading", { name: "Libraries", exact: true }) })).last();
const rowOf = (page: Page, name: string) => page.getByRole("row").filter({ hasText: name }).first();

test.describe("Libraries card on a movie", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    for (const key of ["a", "b", "c"] as const) {
      const root = fs.mkdtempSync(path.join(os.tmpdir(), `deluno-real-member-${key}-`));
      roots.push(root);
      const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: libs[key].name, mediaType: "movies", purpose: "collection", rootPath: root });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      libs[key].id = made.body.id;
    }
    const movie = await api<{ id: string }>(page, "POST", "/api/movies/", { title, releaseYear: 2020, monitored: true, libraryId: libs.a.id });
    expect(movie.status, JSON.stringify(movie.body)).toBeLessThan(300);
    movieId = movie.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    await api(page, "DELETE", `/api/movies/${movieId}`);
    for (const key of ["a", "b", "c"] as const) await api(page, "DELETE", `/api/libraries/${libs[key].id}`);
    await page.close();
    for (const root of roots) fs.rmSync(root, { recursive: true, force: true });
  });

  async function openMovie(page: Page) {
    await openScreen(page, "/movies");
    await page.goto(`/movies/${movieId}`);
    await untilQuiet(page);
    await expect(page.getByRole("heading", { name: title, level: 1 })).toBeVisible();
  }

  test("Keep in another library asks which, Cancel keeps nothing, and keeping it there too makes a second copy the server lists", async ({ page }) => {
    await openMovie(page);
    await page.getByRole("button", { name: "Keep in another library…", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Keep this film in another library too?" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("combobox", { name: "Library" }).or(dialog.locator("select")).first()).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    expect((await memberships(page)).map((m) => m.libraryId)).toEqual([libs.a.id]);

    await page.getByRole("button", { name: "Keep in another library…", exact: true }).click();
    await dialog.locator("select").selectOption(libs.b.id);
    const requests = recordRequests(page);
    await dialog.getByRole("button", { name: "Keep it there too", exact: true }).click();
    await expect(page.getByText(`Also kept in ${libs.b.name}.`).first()).toBeVisible();
    requests.stop();
    expect(requests.find("POST", new RegExp(`^/api/movies/${movieId}/libraries/?$`))[0].body).toMatchObject({ libraryId: libs.b.id });
    expect((await memberships(page)).map((m) => m.libraryId).sort()).toEqual([libs.a.id, libs.b.id].sort());
    await expect(rowOf(page, libs.b.name)).toBeVisible();
  });

  test("Use for search chooses the library searches and downloads go through", async ({ page }) => {
    await openMovie(page);
    await expect(page.getByText("Search and download go through this library")).toBeVisible();
    const before = await rowOf(page, libs.a.name).innerText();
    const other = before.includes("Search and download go through this library") ? libs.b : libs.a;
    await rowOf(page, other.name).getByRole("button", { name: "Use for search", exact: true }).click();
    await expect(rowOf(page, other.name)).toContainText("Search and download go through this library");
    await expect(rowOf(page, other.name).getByRole("button", { name: "Use for search" })).toHaveCount(0);
  });

  test("Move takes a copy out of one library into another; Remove asks first, and Remove from library stops it being wanted there", async ({ page }) => {
    await openMovie(page);
    await rowOf(page, libs.b.name).getByRole("button", { name: "Move…", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: `Move out of ${libs.b.name}?` });
    await expect(dialog).toBeVisible();
    await dialog.locator("select").selectOption(libs.c.id);
    const requests = recordRequests(page);
    await dialog.getByRole("button", { name: "Move", exact: true }).click();
    await expect(page.getByText(`Moved to ${libs.c.name}.`).first()).toBeVisible();
    requests.stop();
    expect(requests.find("PUT", new RegExp(`^/api/movies/${movieId}/libraries/${libs.b.id}$`))[0].body).toMatchObject({ libraryId: libs.c.id });
    expect((await memberships(page)).map((m) => m.libraryId).sort()).toEqual([libs.a.id, libs.c.id].sort());

    await rowOf(page, libs.c.name).getByRole("button", { name: "Remove", exact: true }).click();
    const ask = page.getByRole("alertdialog").or(page.getByRole("dialog")).filter({ hasText: `Stop wanting it in ${libs.c.name}?` });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
    expect((await memberships(page)).length).toBe(2);

    await rowOf(page, libs.c.name).getByRole("button", { name: "Remove", exact: true }).click();
    await ask.getByRole("button", { name: "Remove from library", exact: true }).click();
    await expect(page.getByText(`No longer in ${libs.c.name}.`).first()).toBeVisible();
    expect((await memberships(page)).map((m) => m.libraryId)).toEqual([libs.a.id]);
  });
});
