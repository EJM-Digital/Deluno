import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * Request servers (Seerr): the fallback movie and TV library and quality profile selects and the two tag pickers, against the
 * real server (measure 4, issue 1296). The Seerr address answers with what Seerr 3.5.0 really answered
 * (tests/fixtures/recorded/seerr); nothing in the app is stubbed. Each test reads back through the real API what was saved.
 */

const stamp = `srf${Date.now()}`;
const tagAll = `all${stamp}`;
const tag4k = `fourk${stamp}`;
let seerr: RecordedAppServer;
let serverId = "";
const folders: string[] = [];
const libraryIds: string[] = [];
const profileIds: string[] = [];
const tagIds: string[] = [];

interface SeerrRow {
  id: string; baseUrl: string; movieLibraryId?: string | null; seriesLibraryId?: string | null;
  movieQualityProfileId?: string | null; seriesQualityProfileId?: string | null; tags?: string | null; tags4k?: string | null;
}
const servers = async (page: Page) => (await api<SeerrRow[]>(page, "GET", "/api/seerr-servers")).body;
const mine = async (page: Page) => (await servers(page)).find((s) => s.id === serverId)!;
const split = (value?: string | null) => (value ?? "").split(",").map((item) => item.trim()).filter(Boolean);

test.describe("Seerr fallbacks and tags", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);
  const profile = { movies: "", tv: "" };

  test.beforeAll(async ({ browser }) => {
    seerr = await startRecordedAppServer([loadRecording("seerr", "3.5.0", "status"), loadRecording("seerr", "3.5.0", "settings-main"), loadRecording("seerr", "3.5.0", "requests-empty")]);
    const page = await browser.newPage();
    await openScreen(page, "/indexers/media-servers");
    for (const [name, mediaType] of [[`Fallback movies ${stamp}`, "movies"], [`Fallback shows ${stamp}`, "tv"]] as const) {
      const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-srf-"));
      folders.push(folder);
      const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name, mediaType, purpose: "collection", rootPath: folder });
      expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
      libraryIds.push(library.body.id);
      const made = await api<{ id: string }>(page, "POST", "/api/quality-profiles", { name: `${mediaType} profile ${stamp}`, mediaType, cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p,WEB 1080p", customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      profileIds.push(made.body.id);
      profile[mediaType] = made.body.id;
    }
    for (const name of [tagAll, tag4k]) {
      const tag = await api<{ id: string }>(page, "POST", "/api/tags", { name, color: "#3b82f6" });
      expect(tag.status, JSON.stringify(tag.body)).toBeLessThan(300);
      tagIds.push(tag.body.id);
    }
    const server = await api<{ id: string }>(page, "POST", "/api/seerr-servers", { baseUrl: seerr.baseUrl, apiKey: "example-seerr-key", isEnabled: false });
    expect(server.status, JSON.stringify(server.body)).toBeLessThan(300);
    serverId = server.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/media-servers");
    if (serverId) await api(page, "DELETE", `/api/seerr-servers/${serverId}`);
    for (const id of libraryIds) await api(page, "DELETE", `/api/libraries/${id}`);
    for (const id of profileIds) await api(page, "DELETE", `/api/quality-profiles/${id}`);
    for (const id of tagIds) await api(page, "DELETE", `/api/tags/${id}`);
    await page.close();
    await seerr.close();
    for (const folder of folders) fs.rmSync(folder, { recursive: true, force: true });
  });

  const openDrawer = async (page: Page) => {
    await openScreen(page, "/indexers/media-servers");
    await untilQuiet(page);
    await page.getByRole("region", { name: "Request servers" }).getByRole("row").filter({ hasText: seerr.baseUrl }).first().click();
    return page.getByRole("dialog", { name: "Seerr" });
  };

  test("the fallback selects offer only their media type's libraries, and the chosen libraries, quality profiles and both tag sets are saved and cleared", async ({ page }) => {
    const drawer = await openDrawer(page);
    const movieLibrary = drawer.getByLabel("Fallback movie library");
    const tvLibrary = drawer.getByLabel("Fallback TV library");
    expect(await movieLibrary.locator("option").allInnerTexts()).toContain(`Fallback movies ${stamp}`);
    expect(await movieLibrary.locator("option").allInnerTexts()).not.toContain(`Fallback shows ${stamp}`);
    expect(await tvLibrary.locator("option").allInnerTexts()).toContain(`Fallback shows ${stamp}`);
    expect(await tvLibrary.locator("option").allInnerTexts()).not.toContain(`Fallback movies ${stamp}`);
    await expect(drawer.getByLabel("Fallback movie quality profile")).toHaveValue("");

    await movieLibrary.selectOption({ label: `Fallback movies ${stamp}` });
    await tvLibrary.selectOption({ label: `Fallback shows ${stamp}` });
    await drawer.getByLabel("Fallback movie quality profile").selectOption({ label: `movies profile ${stamp}` });
    await drawer.getByLabel("Fallback TV quality profile").selectOption({ label: `tv profile ${stamp}` });
    await drawer.getByRole("textbox", { name: "Seerr tags for every title" }).fill(tagAll);
    await drawer.getByRole("button", { name: `Add ${tagAll} to Seerr tags for every title` }).click();
    await drawer.getByRole("textbox", { name: "Extra Seerr tags for a 4K request" }).fill(tag4k);
    await drawer.getByRole("button", { name: `Add ${tag4k} to Extra Seerr tags for a 4K request` }).click();
    await drawer.getByRole("button", { name: "Save server", exact: true }).click();
    await expect.poll(async () => (await mine(page)).movieLibraryId).toBe(libraryIds[0]);
    const saved = await mine(page);
    expect(saved).toMatchObject({ seriesLibraryId: libraryIds[1], movieQualityProfileId: profile.movies, seriesQualityProfileId: profile.tv });
    expect(split(saved.tags)).toEqual([tagAll]);
    expect(split(saved.tags4k)).toEqual([tag4k]);

    await drawer.getByLabel("Fallback movie library").selectOption("");
    await drawer.getByLabel("Fallback TV quality profile").selectOption("");
    await drawer.getByRole("button", { name: "Save server", exact: true }).click();
    await expect.poll(async () => (await mine(page)).movieLibraryId ?? null).toBeNull();
    const cleared = await mine(page);
    expect(cleared.seriesQualityProfileId ?? null).toBeNull();
    expect(cleared.seriesLibraryId).toBe(libraryIds[1]);
    expect(cleared.movieQualityProfileId).toBe(profile.movies);
  });
});
