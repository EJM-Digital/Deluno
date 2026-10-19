import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Retention from end to end on the real server, nothing stubbed (measure 4, issue 1270), with Jellyfin 12.2.0 answering from its
 * recordings on a loopback port: a rule is built in the drawer to take what nobody has watched, the film is marked from the server's
 * watch history, the Jellyfin collection "Leaving soon" is made on the server, and the film is kept, the keep undone, and its day
 * postponed, each through its dialog. The film's removal on its day, with the recycle bin, the exclusion, the processor and the media
 * server told, is RetentionRemovalReachesTheOtherAppsOnRealFilesTests (the day cannot be brought forward from a browser).
 */

interface Rule { id: string; name: string }
interface Mark { entityId: string; title: string; ruleId: string; state: string; removeAfterUtc: string }

test.describe("Retention from end to end", () => {
  test.describe.configure({ mode: "serial" });

  let jellyfin: RecordedAppServer;
  let root: string;
  let libraryId = "";
  let serverId = "";
  let movieId = "";

  test.beforeAll(async ({ browser }) => {
    jellyfin = await startRecordedAppServer([
      ...["identity", "libraries", "catalogue-movies", "watched", "sessions-idle", "collection-sync", "collection-unfavourited"].map((state) => loadRecording("jellyfin", "12.2.0", state))
    ]);
    root = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-retention-e2e-"));
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/retention");
    const folder = path.join(root, "Films", "Sintel (2010)");
    fs.mkdirSync(folder, { recursive: true });
    fs.writeFileSync(path.join(folder, "Sintel (2010) [WEB 1080p].mkv"), Buffer.alloc(4096, 0x42));
    const library = await api<{ id: string }>(page, "POST", "/api/libraries/", {
      name: "Retention films", mediaType: "movies", purpose: "collection", rootPath: path.join(root, "Films"), importWorkflow: "copy",
      autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false
    });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    const movie = await api<{ id: string }>(page, "POST", "/api/movies/", { title: "Sintel", releaseYear: 2010, imdbId: "tt1727587", monitored: true, libraryId, searchForMissingNow: false });
    expect(movie.status, JSON.stringify(movie.body)).toBeLessThan(300);
    movieId = movie.body.id;
    const server = await api<{ id: string }>(page, "POST", "/api/media-servers", { kind: "jellyfin", name: "Living room", baseUrl: jellyfin.baseUrl, token: "jf-secret-token-1", isEnabled: true });
    expect(server.status, JSON.stringify(server.body)).toBeLessThan(300);
    serverId = server.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/retention");
    for (const rule of (await api<Rule[]>(page, "GET", "/api/retention/rules")).body) await api(page, "DELETE", `/api/retention/rules/${rule.id}`);
    for (const keep of (await api<Array<{ id: string }>>(page, "GET", "/api/retention/keeps")).body) await api(page, "DELETE", `/api/retention/keeps/${keep.id}`);
    if (serverId) await api(page, "DELETE", `/api/media-servers/${serverId}`);
    if (movieId) await api(page, "DELETE", `/api/movies/${movieId}`);
    if (libraryId) await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    await jellyfin?.close();
    fs.rmSync(root, { recursive: true, force: true });
  });

  async function openRetention(page: Page) {
    await openScreen(page, "/search-cycles/retention");
    await untilQuiet(page);
    await expectNoErrorScreen(page);
  }

  test("a rule built in the drawer marks the film nobody has watched, from the media server's watch history", async ({ page }) => {
    await openRetention(page);
    const requests = recordRequests(page);

    await page.getByRole("button", { name: "New rule", exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Name").fill("Unwatched films");
    await drawer.getByLabel("Step 1 test").selectOption({ label: "nobody" });
    await drawer.getByRole("button", { name: "Remove step 2", exact: true }).click();
    await drawer.getByText(/^Jellyfin on /).first().click();
    await drawer.getByLabel("Leaving soon for").fill("3");
    await drawer.getByLabel("What happens").selectOption({ label: "Remove it (files to the recycle bin)" });
    await drawer.getByRole("button", { name: "Add rule", exact: true }).click();
    await expect(drawer.getByText("Rule added")).toBeVisible();

    requests.stop();
    const posts = requests.find("POST", /^\/api\/retention\/rules$/);
    expect(posts, JSON.stringify(requests.seen.map((r) => `${r.method} ${r.path}`))).toHaveLength(1);
    expect(posts[0].body).toMatchObject({ name: "Unwatched films", level: "movie", mediaServerIds: [serverId], waitDays: 3, expiryAction: "delete" });

    await drawer.getByRole("button", { name: "Run now", exact: true }).click();
    await expect(drawer.getByText(/1 title|Sintel/).first()).toBeVisible({ timeout: 15_000 });
    const marks = (await api<Mark[]>(page, "GET", "/api/retention/leaving-soon")).body;
    expect(marks.map((mark) => mark.title)).toContain("Sintel");
    expect(marks.find((mark) => mark.title === "Sintel")?.entityId).toBe(movieId);
    expect(jellyfin.requests.some((request) => request.startsWith("GET /Users/") && request.includes("IsPlayed"))).toBe(true);
    expect(jellyfin.unanswered, "every call the app made to Jellyfin was one that was recorded").toEqual([]);
  });

  test("the server is given its Leaving soon collection and the film waits in Leaving soon with Keep and Postpone", async ({ page }) => {
    await openRetention(page);

    await expect(page.getByRole("button", { name: "Keep Sintel", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Postpone Sintel", exact: true })).toBeVisible();
    expect(jellyfin.requests.some((request) => request.startsWith("POST /Collections?Name=Leaving%20soon"))).toBe(true);
    const collections = (await api<Array<{ serverId: string; members: number; problem: string | null }>>(page, "GET", "/api/retention/collections")).body;
    expect(collections.find((item) => item.serverId === serverId)?.problem ?? null).toBeNull();
  });

  test("Postpone moves the film's day back without keeping it", async ({ page }) => {
    await openRetention(page);
    const before = (await api<Mark[]>(page, "GET", "/api/retention/leaving-soon")).body.find((mark) => mark.title === "Sintel")!;

    await page.getByRole("button", { name: "Postpone Sintel", exact: true }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("Days to postpone").fill("10");
    await dialog.getByRole("button", { name: "Postpone", exact: true }).click();

    await expect(page.getByText(/Sintel is postponed by 10 days: .* It is not kept\./)).toBeVisible();
    const after = (await api<Mark[]>(page, "GET", "/api/retention/leaving-soon")).body.find((mark) => mark.title === "Sintel")!;
    expect(new Date(after.removeAfterUtc).getTime() - new Date(before.removeAfterUtc).getTime()).toBeGreaterThanOrEqual(9.9 * 86_400_000);
    expect(after.state).not.toBe("kept");
  });

  test("Keep takes the film off Leaving soon into the Kept card, and Undo takes the keep away again", async ({ page }) => {
    await openRetention(page);

    await page.getByRole("button", { name: "Keep Sintel", exact: true }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByPlaceholder("Kids' favourite").fill("For the family night");
    await dialog.getByRole("button", { name: "Keep", exact: true }).click();

    await expect(page.getByText("Sintel is kept from “Unwatched films”.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Keep Sintel", exact: true })).toHaveCount(0);
    await expect(page.getByText("For the family night", { exact: true })).toBeVisible();
    const keeps = (await api<Array<{ id: string; title: string; note: string | null }>>(page, "GET", "/api/retention/keeps")).body;
    expect(keeps.map((keep) => [keep.title, keep.note])).toEqual([["Sintel", "For the family night"]]);

    await page.getByRole("button", { name: "Undo", exact: true }).click();
    await expect(page.getByText("For the family night", { exact: true })).toHaveCount(0);
    expect((await api<unknown[]>(page, "GET", "/api/retention/keeps")).body).toEqual([]);
  });
});
