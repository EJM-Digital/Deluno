import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Media management, Naming, against the real server (measure 4, issue 1296): the token buttons that build a custom
 * pattern, the sample-data previews, and the question before leaving with unsaved edits. Each test reads back through the real
 * API what Save changes stored, and the original naming is put back afterwards.
 */

const SEP = String.fromCharCode(92);
const MOVIES_PREFIX = new RegExp(`^Movies${SEP}${SEP}.+`);

interface Naming { movieFolderFormat: string; movieFileFormat: string; seriesFolderFormat: string }
const naming = async (page: Page) => (await api<Naming>(page, "GET", "/api/settings")).body;

test.describe("Naming editor", () => {
  test.setTimeout(90_000);
  let original: Naming;

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/media-management");
    await untilQuiet(page);
    original = await naming(page);
  });

  test.afterEach(async ({ page }) => {
    await api(page, "PATCH", "/api/settings", { movieFolderFormat: original.movieFolderFormat, movieFileFormat: original.movieFileFormat, seriesFolderFormat: original.seriesFolderFormat });
  });

  test("Custom pattern opens an editor whose token buttons add tokens with a separating space, whose preview uses sample data, and whose Apply pattern is saved by Save changes", async ({ page }) => {
    const styles = page.getByRole("radiogroup", { name: "movie-file naming style" });
    await styles.getByRole("radio", { name: "Custom pattern" }).click();
    const drawer = page.getByRole("dialog");
    const pattern = drawer.getByRole("textbox", { name: "Custom pattern" });
    await expect(pattern).toHaveValue("");
    await expect(drawer.getByRole("button", { name: "Season 01", exact: true }), "a movie pattern has no episode tokens").toHaveCount(0);
    await drawer.getByRole("button", { name: "Movie title", exact: true }).click();
    await expect(pattern).toHaveValue("{Movie Title}");
    await drawer.getByRole("button", { name: "Release year", exact: true }).click();
    await expect(pattern).toHaveValue("{Movie Title} {Release Year}");
    await drawer.getByRole("button", { name: "Quality", exact: true }).click();
    await expect(pattern).toHaveValue("{Movie Title} {Release Year} {Quality}");
    await expect(drawer.getByText("Arrival 2016 WEB 1080p", { exact: true })).toBeVisible();
    await drawer.getByRole("button", { name: "Apply pattern", exact: true }).click();
    await expect(page.getByText("Arrival 2016 WEB 1080p", { exact: true }).first()).toBeVisible();
    expect((await naming(page)).movieFileFormat, "applying alone saves nothing").toBe(original.movieFileFormat);
    await page.getByRole("button", { name: "Save changes", exact: true }).click();
    await expect.poll(async () => (await naming(page)).movieFileFormat).toBe("{Movie Title} {Release Year} {Quality}");
  });

  test("the live preview shows sample names for the movie folder, and a folder style change updates it before anything is saved", async ({ page }) => {
    await expect(page.getByText(MOVIES_PREFIX).first()).toBeVisible();
    const folder = page.getByRole("radiogroup", { name: "movie-folder naming style" });
    await folder.getByRole("radio", { name: "Title only" }).click();
    await expect(page.getByText(`Movies${SEP}Arrival`, { exact: true })).toBeVisible();
    await folder.getByRole("radio", { name: "Title + ID" }).click();
    await expect(page.getByText(`Movies${SEP}Arrival (2016) [tt2543164]`, { exact: true })).toBeVisible();
    expect((await naming(page)).movieFolderFormat, "a preview saves nothing").toBe(original.movieFolderFormat);
  });

  test("leaving with an unsaved naming edit asks first: Cancel stays with the edit, Discard and continue leaves and nothing was written", async ({ page }) => {
    const folder = page.getByRole("radiogroup", { name: "movie-folder naming style" });
    await folder.getByRole("radio", { name: original.movieFolderFormat === "{Movie Title}" ? "Title + ID" : "Title only" }).click();
    const prompt = page.getByRole("dialog", { name: "Unsaved changes" });
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    await expect(prompt).toBeVisible();
    await prompt.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(prompt).toHaveCount(0);
    await expect(page).toHaveURL(/\/settings\/media-management/);
    const requests = recordRequests(page);
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    await prompt.getByRole("button", { name: "Discard and continue", exact: true }).click();
    await expect(page).not.toHaveURL(/media-management/);
    requests.stop();
    expect(requests.seen.filter((r) => r.method !== "GET" && /^\/api\/settings/.test(r.path)), "discarding sends no save").toEqual([]);
    expect((await naming(page)).movieFolderFormat).toBe(original.movieFolderFormat);
  });
});
