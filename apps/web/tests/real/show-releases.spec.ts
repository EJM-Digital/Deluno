import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

/**
 * A show's page with a download sent for it, against the real server, nothing stubbed (measure 4, issue 1270). The download client
 * answers as qBittorrent 5.2.1 really answered (tests/fixtures/recorded) and the sent download is written by the smoke host's seeding
 * route; the show's History lists it under Sent to downloads, and Open In progress goes to In progress.
 * Not here: choosing and forcing a release for an episode needs an indexer's recorded answer for the Pirate Bay test call (t=search),
 * which tests/fixtures/recorded/jackett does not hold.
 */

const stamp = `sr${Date.now()}`;
const title = `Sent show ${stamp}`;
let qb: RecordedAppServer;
let folder = "";
let libraryId = "";
let clientId = "";
let seriesId = "";
const lastWeek = new Date(Date.now() - 7 * 86_400_000).toISOString().slice(0, 10);

test.describe("A show's sent downloads", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    qb = await startRecordedAppServer(["sign-in", "version", "downloading", "paused", "trackers", "settings"].map((state) => loadRecording("qbittorrent", "5.2.1", state)));
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-showreleases-"));
    const page = await browser.newPage();
    await openScreen(page, "/tv");
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Show releases ${stamp}`, mediaType: "tv", purpose: "collection", rootPath: folder, importWorkflow: "copy" });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    const client = await api<{ id: string }>(page, "POST", "/api/download-clients", {
      protocol: "qbittorrent", host: "127.0.0.1", port: qb.port, username: "admin", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: true
    });
    expect(client.status, JSON.stringify(client.body)).toBeLessThan(300);
    clientId = client.body.id;
    await api(page, "POST", `/api/download-clients/${clientId}/test`);
    const routed = await api(page, "PUT", `/api/libraries/${libraryId}/routing`, {
      sources: [], downloadClients: [{ downloadClientId: clientId, priority: 10 }]
    });
    expect(routed.status, JSON.stringify(routed.body)).toBeLessThan(300);
    const seeded = await api<{ seriesId: string }>(page, "POST", "/api/testing/series/seed", { libraryId, title, year: 2008, episodeCount: 2, firstAirDate: lastWeek });
    expect(seeded.status, JSON.stringify(seeded.body)).toBe(200);
    seriesId = seeded.body.seriesId;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/tv");
    await api(page, "POST", "/api/testing/transfers/clear");
    for (const blocked of (await api<Array<{ id: string }>>(page, "GET", "/api/blocked-releases")).body ?? []) await api(page, "DELETE", `/api/blocked-releases/${blocked.id}`);
    await api(page, "POST", "/api/series/bulk", { seriesIds: [seriesId], operation: "remove" });
    await api(page, "DELETE", `/api/download-clients/${clientId}`);
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    await qb.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("a download sent for the show lists under Sent to downloads with Open In progress, which goes to In progress", async ({ page }) => {
    await openScreen(page, "/tv");
    const sent = await api(page, "POST", "/api/testing/transfers/handoff", {
      libraryId, mediaType: "tv", entityId: seriesId, releaseName: "Breaking.Bad.S01E02.1080p-LIST", clientId, queueItemId: "69ca35bca234b9cb822abc435ddbababf6147591"
    });
    expect(sent.status, JSON.stringify(sent.body)).toBe(200);
    try {
      await page.goto(`/tv/${seriesId}`);
      await untilQuiet(page);
      await page.getByRole("tab", { name: "History", exact: true }).click();
      await expect(page.getByText("Breaking.Bad.S01E02.1080p-LIST").first()).toBeVisible();
      await page.getByRole("link", { name: "Open In progress", exact: true }).first().click();
      await expect(page).toHaveURL(/\/queue/);
    } finally {
      await api(page, "POST", "/api/testing/transfers/clear");
    }
  });
});
