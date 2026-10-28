import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The library drawer (Settings > Libraries) against the real server (measure 4, issue 1296).
 * The libraries are real folders in a temp directory and the quality profiles are made through the real API; nothing reaches another app.
 */

const stamp = `sld${Date.now()}`;

interface Library { id: string; name: string; mediaType: string; rootPath: string; downloadsPath?: string | null; qualityProfileId?: string | null }
interface Profile { id: string }
const libraries = async (page: Page) => (await api<Library[]>(page, "GET", "/api/libraries")).body;
const profileBody = (name: string, mediaType: string, cutoff: string, allowed: string) => ({ name, mediaType, cutoffQuality: cutoff, allowedQualities: allowed, customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false });

test.describe("Library drawer", () => {
  test.setTimeout(90_000);
  let folder = "";
  const libraryIds: string[] = [];
  const profileIds: string[] = [];
  let moviesProfile = "";
  let tvProfile = "";

  test.beforeEach(async ({ page }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-sld-"));
    await openScreen(page, "/settings/libraries");
    await untilQuiet(page);
    for (const [name, type, cutoff, allowed] of [[`Movie profile ${stamp}`, "movies", "WEB 1080p", "WEB 720p,WEB 1080p"], [`Show profile ${stamp}`, "tv", "WEB 1080p", "WEB 720p,WEB 1080p"]]) {
      const made = await api<Profile>(page, "POST", "/api/quality-profiles", profileBody(name, type, cutoff, allowed));
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      profileIds.push(made.body.id);
      if (type === "movies") moviesProfile = made.body.id; else tvProfile = made.body.id;
    }
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    for (const id of libraryIds.splice(0)) await api(page, "DELETE", `/api/libraries/${id}`);
    for (const id of profileIds.splice(0)) await api(page, "DELETE", `/api/quality-profiles/${id}`);
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("on a new library, switching the media type keeps the typed name and lists only that type's quality profiles", async ({ page }) => {
    await page.getByRole("button", { name: "New library" }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Library name").fill(`Typed name ${stamp}`);
    const profileSelect = drawer.getByRole("combobox", { name: "Quality profile" });
    expect(await profileSelect.locator("option").allInnerTexts()).toContain(`Movie profile ${stamp}`);
    expect(await profileSelect.locator("option").allInnerTexts()).not.toContain(`Show profile ${stamp}`);
    await profileSelect.selectOption({ label: `Movie profile ${stamp}` });
    await drawer.getByRole("radio", { name: "TV shows" }).click();
    await expect(drawer.getByLabel("Library name")).toHaveValue(`Typed name ${stamp}`);
    const tvOptions = await profileSelect.locator("option").allInnerTexts();
    expect(tvOptions).toContain(`Show profile ${stamp}`);
    expect(tvOptions).not.toContain(`Movie profile ${stamp}`);
    await expect(profileSelect).toHaveValue("");
  });

  test("a new library needs a name and a folder, and a downloads folder must be a full path; typing clears each message", async ({ page }) => {
    await page.getByRole("button", { name: "New library" }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("textbox", { name: "Library folder" }).fill("");
    await drawer.getByRole("textbox", { name: "Downloads folder" }).fill("downloads/relative");
    await drawer.getByRole("button", { name: "Create library" }).click();
    await expect(drawer.getByText("Give this library a name.")).toBeVisible();
    await expect(drawer.getByText("Choose a folder for this library.")).toBeVisible();
    await expect(drawer.getByText(/^Write the downloads folder as a full path, such as C:.Downloads.Completed or ..nas.downloads.$/)).toBeVisible();
    await drawer.getByLabel("Library name").fill(`Fixed ${stamp}`);
    await expect(drawer.getByText("Give this library a name.")).toHaveCount(0);
    expect((await libraries(page)).some((library) => library.name === `Fixed ${stamp}`)).toBe(false);
  });

  test("an existing library keeps its media type, offers only its type's profiles, and saves a changed profile, name and folders", async ({ page }) => {
    const made = await api<Library>(page, "POST", "/api/libraries", { name: `Existing ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder, qualityProfileId: moviesProfile });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryIds.push(made.body.id);
    const other = await api<Profile>(page, "POST", "/api/quality-profiles", profileBody(`Second movie profile ${stamp}`, "movies", "Bluray 1080p", "WEB 1080p,Bluray 1080p"));
    profileIds.push(other.body.id);
    await page.reload();
    await untilQuiet(page);
    await page.getByRole("row").filter({ hasText: `Existing ${stamp}` }).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByRole("radio", { name: "TV shows" })).toBeDisabled();
    await expect(drawer.getByRole("radio", { name: "Movies" })).toBeDisabled();
    const profileSelect = drawer.getByRole("combobox", { name: "Quality profile" });
    const options = await profileSelect.locator("option").allInnerTexts();
    expect(options).toContain(`Second movie profile ${stamp}`);
    expect(options).not.toContain(`Show profile ${stamp}`);
    await profileSelect.selectOption({ label: `Second movie profile ${stamp}` });
    const downloads = path.join(folder, "incoming");
    fs.mkdirSync(downloads);
    await drawer.getByLabel("Library name").fill(`Renamed ${stamp}`);
    await drawer.getByRole("textbox", { name: "Downloads folder" }).fill(downloads);
    await drawer.getByRole("button", { name: "Save library" }).click();
    await expect.poll(async () => (await libraries(page)).find((library) => library.id === made.body.id)?.qualityProfileId).toBe(other.body.id);
    const saved = (await libraries(page)).find((library) => library.id === made.body.id)!;
    expect(saved.name).toBe(`Renamed ${stamp}`);
    expect(saved.downloadsPath).toBe(downloads);
    expect(saved.mediaType).toBe("movies");
  });

  test("editing a library that does not exist answers 404 and changes nothing", async ({ page }) => {
    const before = (await libraries(page)).length;
    const reply = await api(page, "PUT", "/api/libraries/00000000-0000-0000-0000-000000000000", { name: "Ghost", rootPath: folder, downloadsPath: null });
    expect(reply.status).toBe(404);
    expect((await libraries(page)).length).toBe(before);
  });
});
