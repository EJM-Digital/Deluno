import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Libraries and the old Settings addresses that land on its neighbours, against the real server (measure 4, issue 1296).
 * The libraries are real folders in a temp directory; nothing reaches another app.
 */

const stamp = `sl${Date.now()}`;

interface Library { id: string; name: string; rootPath: string }
const libraries = async (page: Page) => (await api<Library[]>(page, "GET", "/api/libraries")).body;

test.describe("Settings libraries", () => {
  test.setTimeout(90_000);
  let folderA = "";
  let folderB = "";
  let ids: string[] = [];

  test.beforeEach(async ({ page }) => {
    folderA = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-liba-"));
    folderB = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-libb-"));
    await openScreen(page, "/settings/libraries");
    await untilQuiet(page);
    ids = [];
    for (const [name, mediaType, rootPath] of [[`Alpha films ${stamp}`, "movies", folderA], [`Beta shows ${stamp}`, "tv", folderB]]) {
      const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name, mediaType, purpose: "collection", rootPath });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      ids.push(made.body.id);
    }
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    for (const id of ids) await api(page, "DELETE", `/api/libraries/${id}`);
    fs.rmSync(folderA, { recursive: true, force: true });
    fs.rmSync(folderB, { recursive: true, force: true });
  });

  test("the list shows each library's name with Movies or TV shows, its folder, its profile and its status, under the six column headings", async ({ page }) => {
    for (const heading of ["Name", "Folder", "Library Profile", "Search schedule", "Status", "On"]) await expect(page.getByRole("columnheader", { name: heading })).toBeVisible();
    const films = page.getByRole("row").filter({ hasText: `Alpha films ${stamp}` });
    await expect(films).toContainText("Movies");
    await expect(films).toContainText(folderA);
    await expect(films).toContainText("No profile assigned");
    await expect(films).toContainText("Not configured");
    await expect(page.getByRole("row").filter({ hasText: `Beta shows ${stamp}` })).toContainText("TV shows");
  });

  test("clicking the Name heading sorts the libraries by name and clicking it again reverses them", async ({ page }) => {
    const header = page.getByRole("columnheader", { name: "Name" });
    const order = async () => (await page.getByRole("row").filter({ hasText: stamp }).allInnerTexts()).map((text) => /Alpha|Beta/.exec(text)![0]).join();
    await header.getByRole("button").click();
    await expect(header).toHaveAttribute("aria-sort", "ascending");
    await expect.poll(order).toBe("Alpha,Beta");
    await header.getByRole("button").click();
    await expect(header).toHaveAttribute("aria-sort", "descending");
    await expect.poll(order).toBe("Beta,Alpha");
  });

  test("a library's address with ?libraryId= opens its drawer and takes the query off the address", async ({ page }) => {
    await page.goto(`/settings/libraries?libraryId=${ids[0]}`);
    await untilQuiet(page);
    const drawer = page.getByRole("dialog");
    await expect(drawer).toBeVisible();
    await expect(drawer.getByLabel("Name")).toHaveValue(`Alpha films ${stamp}`);
    await expect(page).toHaveURL(/\/settings\/libraries$/);
  });

  test("Save library stays off until something changes, and closing a changed drawer asks before throwing the edit away", async ({ page }) => {
    await page.getByRole("row").filter({ hasText: `Alpha films ${stamp}` }).click();
    const drawer = page.getByRole("dialog");
    const save = drawer.getByRole("button", { name: "Save library" });
    await expect(save).toBeDisabled();
    await drawer.getByLabel("Name").fill(`Alpha renamed ${stamp}`);
    await expect(save).toBeEnabled();
    await page.keyboard.press("Escape");
    const ask = page.getByRole("dialog", { name: "Discard unsaved changes?" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer.getByLabel("Name")).toHaveValue(`Alpha renamed ${stamp}`);
    await page.keyboard.press("Escape");
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    expect((await libraries(page)).some((library) => library.name === `Alpha renamed ${stamp}`)).toBe(false);
  });

  test("the old Settings addresses for media, root folders and quality sizes land on their new screens", async ({ page }) => {
    for (const [from, to] of [["/settings/media", "/settings/media-management"], ["/settings/root-folders", "/settings/destination-rules"], ["/settings/quality-sizes", "/settings/quality"]]) {
      await page.goto(from);
      await untilQuiet(page);
      await expect(page).toHaveURL(new RegExp(`${to}$`));
    }
  });
});
