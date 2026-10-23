import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * Find & Download > Media servers against the real server (measure 4, issue 1270). The server added is an Emby address
 * that answers with what Emby 4.10.1.0 really answered (tests/fixtures/recorded/emby); nothing in the app is stubbed.
 * Adding, testing (before and after saving), the Enabled switch, the token that is never sent back, folder links
 * between the two apps, discarding unsaved edits, and removing the server with a confirmation.
 */

const stamp = `ms${Date.now()}`;
let emby: RecordedAppServer;

interface Server { id: string; name: string; kind: string; baseUrl: string; isEnabled: boolean; hasToken?: boolean; serverName?: string | null; leavingSoonOnHome?: boolean }
const servers = async (page: Page) => (await api<Server[]>(page, "GET", "/api/media-servers")).body;
const mine = async (page: Page) => (await servers(page)).find((s) => s.baseUrl === emby.baseUrl);
const row = (page: Page) => page.getByRole("row").filter({ hasText: emby.baseUrl }).first();

test.describe("Media servers", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async () => {
    emby = await startRecordedAppServer([loadRecording("emby", "4.10.1.0", "identity"), loadRecording("emby", "4.10.1.0", "libraries"), loadRecording("emby", "4.10.1.0", "directory-contents")]);
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/media-servers");
    const left = await mine(page);
    if (left) await api(page, "DELETE", `/api/media-servers/${left.id}`);
    await page.close();
    await emby.close();
  });

  const open = async (page: Page) => { await openScreen(page, "/indexers/media-servers"); await untilQuiet(page); };

  test("Add server opens a drawer, Test checks what is typed before anything is saved, and Add server keeps it without sending the token back", async ({ page }) => {
    await open(page);
    const before = (await servers(page)).length;
    await page.getByRole("button", { name: "Add server", exact: true }).first().click();
    const drawer = page.getByRole("dialog", { name: "Jellyfin" });
    await expect(drawer).toBeVisible();
    await drawer.getByLabel("Server", { exact: true }).selectOption("emby");
    await drawer.getByLabel("Address").fill(emby.baseUrl);
    await drawer.getByLabel("API key").fill("example-emby-token");

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: /^Test connection/ }).click();
    await expect.poll(() => requests.find("POST", /^\/api\/media-servers\/test$/).length).toBe(1);
    await expect(drawer.getByText("18663f3536c2").first()).toBeVisible();
    expect((await servers(page)).length, "testing saves nothing").toBe(before);

    await drawer.getByRole("button", { name: "Add server", exact: true }).click();
    await expect.poll(() => requests.find("POST", /^\/api\/media-servers$/).length).toBe(1);
    requests.stop();
    expect(requests.find("POST", /^\/api\/media-servers$/)[0].body).toMatchObject({ kind: "emby", baseUrl: emby.baseUrl, token: "example-emby-token", isEnabled: true });
    const saved = (await mine(page))!;
    expect(saved).toMatchObject({ kind: "emby", isEnabled: true, hasToken: true });
    expect(JSON.stringify(saved)).not.toContain("example-emby-token");
    expect(emby.unanswered).toEqual([]);
  });

  test("the On switch pauses and enables it, and the token is not shown again", async ({ page }) => {
    await open(page);
    const requests = recordRequests(page);
    await row(page).getByRole("switch", { name: /^Pause / }).click();
    await expect.poll(async () => (await mine(page))?.isEnabled).toBe(false);
    await row(page).getByRole("switch", { name: /^Enable / }).click();
    await expect.poll(async () => (await mine(page))?.isEnabled).toBe(true);
    requests.stop();
    expect(requests.seen.filter((r) => r.method === "PUT" && /^\/api\/media-servers\//.test(r.path))).toHaveLength(2);
    expect((await mine(page))?.hasToken, "the switch keeps the token").toBe(true);

    await row(page).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Stored encrypted. Leave empty to keep the current one.")).toBeVisible();
    await expect(drawer.getByLabel("API key")).toHaveValue("");
    await expect(drawer.getByLabel("Server", { exact: true })).toBeDisabled();
  });

  test("Test connection on a saved server asks the server again, says what it answered, and the row then shows what the server called itself", async ({ page }) => {
    await open(page);
    await row(page).click();
    const drawer = page.getByRole("dialog");
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: /^Test connection/ }).click();
    await expect.poll(() => requests.find("POST", /\/api\/media-servers\/[^/]+\/test$/).length).toBe(1);
    requests.stop();
    await expect(drawer.getByText("18663f3536c2").first()).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(row(page)).toContainText("18663f3536c2");
  });

  test("Link paths ties a folder on this machine to the same folder on the server, Test it finds the folder, and removing the link takes it away", async ({ page }) => {
    await open(page);
    await row(page).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("button", { name: /Advanced path mapping/ }).click();
    const link = drawer.getByRole("button", { name: "Link paths", exact: true });
    await expect(link).toBeDisabled();
    await drawer.getByLabel("Path as Deluno sees it").fill("D:\\Media\\Movies");
    await drawer.getByLabel(/^Same path as .* sees it$/).fill("/media/Movies");
    const requests = recordRequests(page);
    await link.click();
    await expect.poll(() => requests.find("POST", /\/path-mappings$/).length).toBe(1);
    const id = (await mine(page))!.id;
    const mapped = (await api<Array<{ id: string; delunoPath: string; serverPath: string }>>(page, "GET", `/api/media-servers/${id}/path-mappings`)).body;
    expect(mapped.map((m) => [m.delunoPath, m.serverPath])).toEqual([["D:\\Media\\Movies", "/media/Movies"]]);

    const answered = page.waitForResponse((r) => /\/path-mappings\/[^/]+\/test$/.test(new URL(r.url()).pathname));
    await drawer.getByRole("button", { name: "Test", exact: true }).click();
    const verdict = await (await answered).json() as { found: boolean; message: string };
    await expect(drawer.getByText(verdict.message).first()).toBeVisible();
    await expect.poll(() => requests.find("POST", /\/path-mappings\/[^/]+\/test$/).length).toBe(1);
    requests.stop();

    await drawer.getByRole("button", { name: "Remove D:\\Media\\Movies link" }).click();
    await expect.poll(async () => (await api<unknown[]>(page, "GET", `/api/media-servers/${id}/path-mappings`)).body.length).toBe(0);
  });

  test("closing a drawer with unsaved edits asks first: Cancel keeps editing, Discard drops them without a save", async ({ page }) => {
    await open(page);
    await row(page).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("API key").fill("something-else");
    const requests = recordRequests(page);
    await page.keyboard.press("Escape");
    const ask = page.getByRole("dialog", { name: "Discard unsaved changes?" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
    await expect(drawer.getByLabel("API key")).toHaveValue("something-else");
    await page.keyboard.press("Escape");
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    requests.stop();
    expect(requests.seen.filter((r) => r.method !== "GET")).toEqual([]);
  });

  test("Show on home is saved with Save server, and Remove asks first, then takes the server away", async ({ page }) => {
    await open(page);
    await row(page).click();
    const drawer = page.getByRole("dialog");
    const home = drawer.getByRole("switch", { name: /^Show on home/ });
    await home.click();
    await drawer.getByRole("button", { name: "Save server", exact: true }).click();
    await expect.poll(async () => (await mine(page))?.leavingSoonOnHome).toBe(true);

    await drawer.getByRole("button", { name: "Remove…", exact: true }).click();
    const ask = page.getByRole("dialog", { name: /^Remove “/ });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
    expect(await mine(page)).toBeTruthy();
    await drawer.getByRole("button", { name: "Remove…", exact: true }).click();
    await ask.getByRole("button", { name: "Remove server", exact: true }).click();
    await expect.poll(async () => await mine(page)).toBeUndefined();
    await expect(row(page)).toHaveCount(0);
  });
});
