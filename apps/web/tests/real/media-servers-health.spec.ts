import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * Find & Download > Media servers: the list heading, the Last test cell, the drawer's Health block and its Leaving soon section,
 * against the real server (measure 4, issue 1296). The server is an Emby address that answers with what Emby 4.10.1.0 really
 * answered (tests/fixtures/recorded/emby); nothing in the app is stubbed.
 */

const stamp = `msh${Date.now()}`;
let emby: RecordedAppServer;
let serverId = "";

interface Server { id: string; name: string; baseUrl: string; isEnabled: boolean; serverName?: string | null; lastHealthTestUtc?: string | null; lastHealthLatencyMs?: number | null; lastHealthMessage?: string | null; healthStatus?: string }
const servers = async (page: Page) => (await api<Server[]>(page, "GET", "/api/media-servers")).body;
const row = (page: Page) => page.getByRole("row").filter({ hasText: emby.baseUrl }).first();

test.describe("Media server health", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    emby = await startRecordedAppServer([loadRecording("emby", "4.10.1.0", "identity"), loadRecording("emby", "4.10.1.0", "libraries"), loadRecording("emby", "4.10.1.0", "directory-contents")]);
    const page = await browser.newPage();
    await openScreen(page, "/indexers/media-servers");
    const made = await api<Server>(page, "POST", "/api/media-servers", { name: `Health ${stamp}`, kind: "emby", baseUrl: emby.baseUrl, token: "example-emby-token", isEnabled: true });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    serverId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/media-servers");
    if (serverId) await api(page, "DELETE", `/api/media-servers/${serverId}`);
    await page.close();
    await emby.close();
  });

  test("an untested server says Not tested yet; after Test the heading counts the working servers, the row shows its age and the drawer's Health block shows the chip, latency, what it reports and its message", async ({ page }) => {
    await openScreen(page, "/indexers/media-servers");
    await untilQuiet(page);
    await expect(row(page)).toContainText("Not tested yet");
    await row(page).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Last test", { exact: true })).toHaveCount(0);
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: /^Test connection/ }).click();
    await expect.poll(() => requests.find("POST", /\/api\/media-servers\/[^/]+\/test$/).length).toBe(1);
    requests.stop();
    const tested = async () => (await servers(page)).find((item) => item.id === serverId)!;
    await expect.poll(async () => (await tested()).lastHealthTestUtc ?? null).not.toBeNull();
    const after = await tested();
    expect(after.serverName).toBe("18663f3536c2");
    await expect(drawer.getByText("Last test", { exact: true })).toBeVisible();
    const health = drawer.locator("dl").filter({ hasText: "Last test" }).first();
    await expect(health).toContainText("Reports itself as");
    await expect(health).toContainText("18663f3536c2");
    await expect(health).toContainText(new RegExp(`${after.lastHealthLatencyMs} ms`));
    if (after.lastHealthMessage) {
      await expect(health.getByText("Message", { exact: true })).toBeVisible();
      await expect(health).toContainText(after.lastHealthMessage);
    }
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);
    const all = await servers(page);
    const working = all.filter((item) => item.isEnabled && item.healthStatus === "healthy").length;
    await expect(page.getByText(`${working} of ${all.length} working`, { exact: true })).toBeVisible();
    await expect(row(page)).toContainText(/ago|just now|now/i);
    await expect(row(page)).not.toContainText("Not tested yet");
    if (after.lastHealthMessage) await expect(row(page)).toContainText(after.lastHealthMessage);
  });

  test("a server no retention rule has marked anything on says nothing is leaving, and what a Leaving soon collection is", async ({ page }) => {
    await openScreen(page, "/indexers/media-servers");
    await untilQuiet(page);
    await row(page).click();
    const section = page.getByRole("dialog").locator("section, div").filter({ hasText: "Leaving soon" }).filter({ hasText: "nothing leaving" }).last();
    await expect(section).toBeVisible();
    await expect(page.getByRole("dialog").getByText("When a retention rule reading this server marks a title, it appears in a Leaving soon collection here.")).toBeVisible();
    const collections = await api<Array<{ serverId: string }>>(page, "GET", "/api/retention/collections");
    expect(collections.body.some((item) => item.serverId === serverId)).toBe(false);
  });
});
