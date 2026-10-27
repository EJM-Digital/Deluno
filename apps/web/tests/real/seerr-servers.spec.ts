import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * Request servers (Seerr) on the Media servers screen, against the real server (measure 4, issue 1270). The Seerr
 * address answers with what Seerr 3.5.0 really answered (tests/fixtures/recorded/seerr); nothing in the app is stubbed.
 * Adding with a fallback library, Test (before and after saving), the On switch, the webhook secret (shown once, never
 * sent back), and Remove with its question.
 */

const stamp = `sr${Date.now()}`;
const movieLibrary = `Seerr movies ${stamp}`;
const tvLibrary = `Seerr shows ${stamp}`;
let seerr: RecordedAppServer;
let folders: string[] = [];
let libraryIds: string[] = [];

interface SeerrRow { id: string; baseUrl: string; isEnabled: boolean; hasApiKey?: boolean; hasWebhookSecret?: boolean; movieLibraryId?: string | null; seriesLibraryId?: string | null }
const servers = async (page: Page) => (await api<SeerrRow[]>(page, "GET", "/api/seerr-servers")).body;
const mine = async (page: Page) => (await servers(page)).find((s) => s.baseUrl === seerr.baseUrl);
const card = (page: Page) => page.getByRole("region", { name: "Request servers" });
const row = (page: Page) => card(page).getByRole("row").filter({ hasText: seerr.baseUrl }).first();

test.describe("Request servers (Seerr)", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    seerr = await startRecordedAppServer([
      loadRecording("seerr", "3.5.0", "status"),
      loadRecording("seerr", "3.5.0", "settings-main"),
      loadRecording("seerr", "3.5.0", "requests-empty")
    ]);
    const page = await browser.newPage();
    await openScreen(page, "/indexers/media-servers");
    for (const [name, mediaType] of [[movieLibrary, "movies"], [tvLibrary, "tv"]] as const) {
      const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-seerr-"));
      folders.push(folder);
      const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name, mediaType, purpose: "collection", rootPath: folder });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      libraryIds.push(made.body.id);
    }
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/media-servers");
    const left = await mine(page);
    if (left) await api(page, "DELETE", `/api/seerr-servers/${left.id}`);
    for (const id of libraryIds) await api(page, "DELETE", `/api/libraries/${id}`);
    await page.close();
    await seerr.close();
    for (const folder of folders) fs.rmSync(folder, { recursive: true, force: true });
  });

  const open = async (page: Page) => { await openScreen(page, "/indexers/media-servers"); await untilQuiet(page); };

  test("Add Seerr opens a drawer, Test checks what is typed first, and Add server keeps the key hidden and the fallback libraries chosen", async ({ page }) => {
    await open(page);
    await expect(card(page).getByText("No Seerr server", { exact: true })).toBeVisible();
    await card(page).getByRole("button", { name: "Add Seerr", exact: true }).click();
    const drawer = page.getByRole("dialog", { name: "Seerr" });
    await expect(drawer).toBeVisible();
    await drawer.getByLabel("Address").fill(seerr.baseUrl);
    await drawer.getByLabel("API key").fill("example-seerr-key");
    await drawer.getByLabel("Fallback movie library").selectOption({ label: movieLibrary });
    await drawer.getByLabel("Fallback TV library").selectOption({ label: tvLibrary });

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Test connection", exact: true }).click();
    await expect.poll(() => requests.find("POST", /^\/api\/seerr-servers\/test$/).length).toBe(1);
    // The footer is a status too, so name the Test result by its words: its reply arrives after the request is seen.
    await expect(drawer.getByText(/^Connected to Seerr/)).toBeVisible();
    expect(await servers(page), "testing saves nothing").toEqual([]);

    await drawer.getByRole("button", { name: "Add server", exact: true }).click();
    await expect.poll(() => requests.find("POST", /^\/api\/seerr-servers$/).length).toBe(1);
    requests.stop();
    const sent = requests.find("POST", /^\/api\/seerr-servers$/)[0].body as Record<string, unknown>;
    expect(sent).toMatchObject({ baseUrl: seerr.baseUrl, apiKey: "example-seerr-key", isEnabled: true, movieLibraryId: libraryIds[0], seriesLibraryId: libraryIds[1] });
    const saved = (await mine(page))!;
    expect(saved).toMatchObject({ isEnabled: true, hasApiKey: true });
    expect(JSON.stringify(saved)).not.toContain("example-seerr-key");
    expect(seerr.unanswered).toEqual([]);
  });

  test("the On switch pauses and enables it without losing the key, and Test connection on the saved server lands in its Health facts", async ({ page }) => {
    await open(page);
    const requests = recordRequests(page);
    await row(page).getByRole("switch", { name: /^Pause / }).click();
    await expect.poll(async () => (await mine(page))?.isEnabled).toBe(false);
    await row(page).getByRole("switch", { name: /^Enable / }).click();
    await expect.poll(async () => (await mine(page))?.isEnabled).toBe(true);
    requests.stop();
    expect(requests.seen.filter((r) => r.method === "PUT" && /^\/api\/seerr-servers\//.test(r.path))).toHaveLength(2);
    expect((await mine(page))?.hasApiKey, "the switch keeps the key").toBe(true);

    await row(page).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Stored encrypted. Leave empty to keep the current one.")).toBeVisible();
    await expect(drawer.getByLabel("API key")).toHaveValue("");
    await drawer.getByRole("button", { name: "Test connection", exact: true }).click();
    await expect(drawer.getByText("Last test", { exact: true })).toBeVisible();
  });

  test("Generate makes the webhook secret, shows it once, and the server only says it exists; Regenerate replaces it", async ({ page }) => {
    await open(page);
    await row(page).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByLabel("Webhook URL")).toHaveValue(new RegExp(`/api/seerr-servers/[^/]+/webhook$`));
    await expect(drawer.getByLabel("Authorization header value")).toHaveValue("");
    await drawer.getByRole("button", { name: "Generate", exact: true }).click();
    const shown = drawer.getByLabel("Authorization header value");
    await expect(drawer.getByText("Shown once. Paste it into Seerr's Authorization Header field now.")).toBeVisible();
    const first = await shown.inputValue();
    expect(first.length).toBeGreaterThan(16);
    expect((await mine(page))?.hasWebhookSecret).toBe(true);
    expect(JSON.stringify(await servers(page))).not.toContain(first);

    await page.keyboard.press("Escape");
    await row(page).click();
    await expect(drawer.getByLabel("Authorization header value")).toHaveValue("••••••••••••••••");
    await drawer.getByRole("button", { name: "Regenerate", exact: true }).click();
    await expect(drawer.getByText("Shown once. Paste it into Seerr's Authorization Header field now.")).toBeVisible();
    expect(await drawer.getByLabel("Authorization header value").inputValue()).not.toBe(first);
  });

  test("closing with unsaved edits asks first, and Remove asks first, then takes the server away", async ({ page }) => {
    await open(page);
    await row(page).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("API key").fill("another-key");
    await page.keyboard.press("Escape");
    const discard = page.getByRole("dialog", { name: "Discard unsaved changes?" });
    await expect(discard).toBeVisible();
    await discard.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer.getByLabel("API key")).toHaveValue("another-key");
    await page.keyboard.press("Escape");
    await discard.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);

    await row(page).click();
    await drawer.getByRole("button", { name: "Remove…", exact: true }).click();
    const ask = page.getByRole("dialog", { name: /^Remove “/ });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(await mine(page)).toBeTruthy();
    await drawer.getByRole("button", { name: "Remove…", exact: true }).click();
    await ask.getByRole("button", { name: "Remove server", exact: true }).click();
    await expect.poll(async () => await mine(page)).toBeUndefined();
    await expect(row(page)).toHaveCount(0);
  });
});
