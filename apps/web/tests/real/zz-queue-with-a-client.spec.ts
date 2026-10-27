import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * Transfers with a download client that answers as qBittorrent really answered (tests/fixtures/recorded/qbittorrent),
 * against the real server, nothing stubbed (measure 4, issue 1270). The download is one Deluno sent (a dispatch written by the
 * smoke host's seeding route) whose hash the client lists; every button in its drawer sends the call the client receives, and the
 * screen says in words what came of it. The client, library and seeding are taken out again at the end, which is why this file
 * sorts last.
 */

const HASH = "69ca35bca234b9cb822abc435ddbababf6147591";
const RELEASE = "Recorded Swarm deluge qbittorrent mkv";
const stamp = `qc${Date.now()}`;
const PAUSED_HASH = "8d878a21c3d4180ae08b4e044904667d9f30bec5";
const PAUSED_RELEASE = "Recorded Stalled One qbittorrent";
let qb: RecordedAppServer;
let qbPaused: RecordedAppServer;
let pausedClientId = "";
let clientId = "";
let libraryId = "";
let folder = "";

const asked = (re: RegExp) => qb.requests.filter((request) => re.test(request));
const askedOfPaused = (re: RegExp) => qbPaused.requests.filter((request) => re.test(request));
const rowOf = (page: Page) => page.getByRole("row").filter({ hasText: RELEASE }).first();

test.describe("Transfers with a download client", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    qb = await startRecordedAppServer([
      ...["sign-in", "version", "downloading", "paused", "resumed", "failed", "removed-with-data", "trackers", "settings"].map((state) => loadRecording("qbittorrent", "5.2.1", state)),
      ...["queue-moved", "force-start-set"].map((state) => loadRecording("qbittorrent", "5.2.4", state))
    ]);
    // A second client holds a download that is paused, so Resume has something to resume.
    qbPaused = await startRecordedAppServer(["sign-in", "version", "paused", "resumed", "trackers", "settings"].map((state) => loadRecording("qbittorrent", "5.2.1", state)));
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-queue-"));
    const page = await browser.newPage();
    await openScreen(page, "/queue");
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Queue films ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    const client = await api<{ id: string }>(page, "POST", "/api/download-clients", {
      protocol: "qbittorrent", host: "127.0.0.1", port: qb.port, username: "admin", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: true
    });
    expect(client.status, JSON.stringify(client.body)).toBeLessThan(300);
    clientId = client.body.id;
    const second = await api<{ id: string }>(page, "POST", "/api/download-clients", {
      protocol: "qbittorrent", host: "127.0.0.1", port: qbPaused.port, username: "admin", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 20, isEnabled: true
    });
    expect(second.status, JSON.stringify(second.body)).toBeLessThan(300);
    pausedClientId = second.body.id;
    const seeded = await api(page, "POST", "/api/testing/transfers/handoff", { libraryId, releaseName: RELEASE, clientId, queueItemId: HASH });
    expect(seeded.status, JSON.stringify(seeded.body)).toBe(200);
    const seededPaused = await api(page, "POST", "/api/testing/transfers/handoff", { libraryId, releaseName: PAUSED_RELEASE, clientId: pausedClientId, queueItemId: PAUSED_HASH });
    expect(seededPaused.status, JSON.stringify(seededPaused.body)).toBe(200);
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/queue");
    await api(page, "POST", "/api/testing/transfers/clear");
    for (const blocked of (await api<Array<{ id: string; releaseName?: string }>>(page, "GET", "/api/blocked-releases")).body ?? []) {
      if (blocked.releaseName === RELEASE) await api(page, "DELETE", `/api/blocked-releases/${blocked.id}`);
    }
    for (const id of [clientId, pausedClientId]) if (id) await api(page, "DELETE", `/api/download-clients/${id}`);
    if (libraryId) await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    await qb.close();
    await qbPaused.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  const openDrawer = async (page: Page, release = RELEASE) => {
    await openScreen(page, "/queue");
    await untilQuiet(page);
    const target = page.getByRole("row").filter({ hasText: release }).first();
    await expect(target).toBeVisible({ timeout: 45_000 });
    await target.click();
    const drawer = page.getByRole("dialog").filter({ hasText: release }).first();
    await expect(drawer.getByRole("button", { name: "Pause", exact: true })).toBeVisible();
    return drawer;
  };

  test("the download the client lists is a row of its own, in the client's words, and its drawer names the release, the client and how far it got", async ({ page }) => {
    await openScreen(page, "/queue");
    await untilQuiet(page);
    await expect(rowOf(page)).toBeVisible({ timeout: 45_000 });
    await expect(rowOf(page)).toContainText("Downloading");
    const drawer = await openDrawer(page);
    await expect(drawer.getByText("Downloading").first()).toBeVisible();
    await expect(drawer.getByText("550 MB", { exact: false }).first()).toBeVisible();
    await expect(drawer.getByText("Public", { exact: true })).toBeVisible();
  });

  test("Show columns lists the optional columns, unticking one hides it from the table and it stays hidden after a reload; ticking it brings it back", async ({ page }) => {
    await openScreen(page, "/queue");
    await untilQuiet(page);
    await expect(rowOf(page)).toBeVisible({ timeout: 45_000 });
    const picker = page.getByRole("button", { name: "Show columns", exact: true });
    await picker.click();
    const menu = page.getByRole("menu", { name: "Show columns: choose which columns to show" });
    const items = menu.getByRole("menuitemcheckbox");
    expect(await items.count()).toBeGreaterThan(0);
    const first = items.first();
    const label = (await first.innerText()).trim();
    const was = (await first.getAttribute("aria-checked")) === "true";
    const headers = () => page.getByRole("columnheader", { name: label, exact: true });
    await first.click();
    await expect(first).toHaveAttribute("aria-checked", String(!was));
    await expect(headers()).toHaveCount(was ? 0 : 1);
    await page.keyboard.press("Escape");
    await expect(menu).toHaveCount(0);
    await page.reload();
    await untilQuiet(page);
    await expect(headers()).toHaveCount(was ? 0 : 1);
    await page.getByRole("button", { name: "Show columns", exact: true }).click();
    await page.getByRole("menuitemcheckbox", { name: label, exact: true }).click();
    await expect(headers()).toHaveCount(was ? 1 : 0);
  });

  test("Pause sends the client its stop call and says in words when the client still has the download running; Re-check sends its recheck call", async ({ page }) => {
    const drawer = await openDrawer(page);
    const stops = asked(/POST .api.v2.torrents.stop/).length;
    await drawer.getByRole("button", { name: "Pause", exact: true }).click();
    await expect.poll(() => asked(/POST .api.v2.torrents.stop/).length).toBe(stops + 1);
    await expect(page.getByText("qBittorrent still has this download running after being told to pause it, so it was not paused.").first()).toBeVisible();
    const rechecks = asked(/POST .api.v2.torrents.recheck/).length;
    await drawer.getByRole("button", { name: "Re-check", exact: true }).click();
    await expect.poll(() => asked(/POST .api.v2.torrents.recheck/).length).toBe(rechecks + 1);
    expect(qb.unanswered).toEqual([]);
  });

  test("Resume on a paused download sends the client its start call", async ({ page }) => {
    const drawer = await openDrawer(page, PAUSED_RELEASE);
    const starts = askedOfPaused(/POST .api.v2.torrents.start/).length;
    await drawer.getByRole("button", { name: "Resume", exact: true }).click();
    await expect.poll(() => askedOfPaused(/POST .api.v2.torrents.start/).length).toBe(starts + 1);
    expect(qbPaused.unanswered).toEqual([]);
  });

  test("the four queue moves and Force start reach the client as its own priority and force-start calls", async ({ page }) => {
    const drawer = await openDrawer(page);
    const moves: Array<[string, RegExp]> = [
      ["Move to top of queue", /topPrio/], ["Move up in queue", /increasePrio/], ["Move down in queue", /decreasePrio/], ["Move to bottom of queue", /bottomPrio/], ["Force start", /setForceStart/]
    ];
    for (const [name, call] of moves) {
      const before = asked(call).length;
      await drawer.getByRole("button", { name, exact: true }).click();
      await expect.poll(() => asked(call).length, { message: `${name} reaches the client` }).toBe(before + 1);
    }
  });

  test("What would removing do? says what it would do and why without touching the client, and Block asks first and can be lifted", async ({ page }) => {
    const drawer = await openDrawer(page);
    const deletesBefore = asked(/torrents\/delete/).length;
    await drawer.getByRole("button", { name: "What would removing do?", exact: true }).click();
    await expect(drawer.getByText("Would do", { exact: true })).toBeVisible();
    await expect(drawer.getByText("Because", { exact: true })).toBeVisible();
    await expect(drawer.getByText("Allowed", { exact: true })).toBeVisible();
    expect(asked(/torrents\/delete/).length, "a preview deletes nothing").toBe(deletesBefore);

    await drawer.getByRole("button", { name: `Block "${RELEASE}"` }).click();
    const ask = page.getByRole("dialog", { name: `Block "${RELEASE}"?` });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
  });

  test("Preview import says in words what it found, and Queue import is held while the download is still going", async ({ page }) => {
    const drawer = await openDrawer(page);
    await expect(drawer.getByRole("button", { name: "Queue import", exact: true })).toBeDisabled();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Preview import", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => r.method !== "GET" && /import/i.test(r.path)).length).toBeGreaterThan(0);
    requests.stop();
    await expect(page.locator("[data-sonner-toast]").first().or(drawer.getByText(/would land|nothing to import|no files|could not/i).first())).toBeVisible();
  });

  test("a sent download that is still in its client offers Remove from client in Activity's drawer too, asking why first, and Just stop tracking it leaves the client alone", async ({ page }) => {
    await openScreen(page, "/activity");
    await untilQuiet(page);
    const row = page.getByRole("row").filter({ hasText: PAUSED_RELEASE }).first();
    await expect(row).toBeVisible({ timeout: 45_000 });
    await row.click();
    const drawer = page.getByRole("dialog").filter({ hasText: PAUSED_RELEASE }).first();
    const remove = drawer.getByRole("button", { name: /^Remove .* from / });
    await expect(remove).toBeVisible({ timeout: 45_000 });
    const deletesBefore = askedOfPaused(/torrents\/delete/).length;
    await remove.click();
    const why = page.getByRole("dialog", { name: `Why remove “${PAUSED_RELEASE}”?` });
    await expect(why).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(why).toHaveCount(0);
    expect(askedOfPaused(/torrents\/delete/).length, "Escape removes nothing").toBe(deletesBefore);

    await remove.click();
    await why.getByRole("button", { name: `Remove “${PAUSED_RELEASE}”: Just stop tracking it` }).click();
    await expect(why).toHaveCount(0);
    expect(askedOfPaused(/torrents\/delete/).length, "forgetting a download leaves the client's copy").toBe(deletesBefore);
  });

  test("Remove from client asks why: Escape keeps the download; Just stop tracking it makes Deluno forget it and leaves the client alone", async ({ page }) => {
    const drawer = await openDrawer(page);
    const deletesBefore = asked(/torrents\/delete/).length;
    await drawer.getByRole("button", { name: "Remove from client", exact: true }).click();
    const why = page.getByRole("dialog", { name: `Why remove “${RELEASE}”?` });
    await expect(why).toBeVisible();
    for (const choice of ["It's bad", "I don't want it", "Just stop tracking it"]) {
      await expect(why.getByRole("button", { name: `Remove “${RELEASE}”: ${choice}` })).toBeVisible();
    }
    await page.keyboard.press("Escape");
    await expect(why).toHaveCount(0);
    expect(asked(/torrents.delete/).length, "Escape removes nothing").toBe(deletesBefore);
    await expect(drawer.getByRole("button", { name: "Remove from client", exact: true })).toBeVisible();

    await drawer.getByRole("button", { name: "Remove from client", exact: true }).click();
    await why.getByRole("button", { name: `Remove “${RELEASE}”: Just stop tracking it` }).click();
    await expect(drawer).toHaveCount(0);
    await expect(rowOf(page)).toHaveCount(0, { timeout: 45_000 });
    expect(asked(/torrents\/delete/).length, "forgetting a download leaves the client's copy").toBe(deletesBefore);
  });
});
