import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { loadRecording, startRecordedAppServer, type RecordedAppServer } from "../helpers/recorded-app-server";

// The guided setup, start to finish, on the real host with nothing stubbed. The download client and the search source are the
// real replies of qBittorrent 5.2.1 and Jackett 0.24.2850 (tests/fixtures/recorded), served over a real loopback port. The
// answers go in through the real draft route, the folders are real temporary folders, and everything the guide makes is read
// back from the real API. The install is shared with the rest of the suite, so what the guide changed is put back afterwards
// and the spec checks that it was (this file sorts last, and runs on the desktop project only).

type Json = Record<string, unknown>;

interface Snapshot {
  settings: Json;
  localNetworkAuth: Json;
  updatePreferences: Json;
  contentBlocklist: Json;
  rules: Json[];
  progress: Json;
  ids: Record<"libraries" | "indexers" | "clients" | "profiles" | "formats", string[]>;
}

const LISTS = {
  libraries: "/api/libraries",
  indexers: "/api/indexers",
  clients: "/api/download-clients",
  profiles: "/api/quality-profiles",
  formats: "/api/custom-formats"
} as const;

class Api {
  constructor(private readonly request: APIRequestContext, private readonly token: string) {}
  private headers() { return { Authorization: `Bearer ${this.token}` }; }
  async get<T = Json>(url: string): Promise<T> {
    const response = await this.request.get(url, { headers: this.headers() });
    expect(response.ok(), `${url} answered ${response.status()}`).toBe(true);
    return (await response.json()) as T;
  }
  async put(url: string, data: unknown) {
    const response = await this.request.put(url, { headers: this.headers(), data });
    expect(response.ok(), `PUT ${url} answered ${response.status()}: ${await response.text()}`).toBe(true);
  }
  async remove(url: string) {
    await this.request.delete(url, { headers: this.headers() });
  }
  async items(url: string): Promise<Json[]> {
    const body = await this.get<Json[] | { items: Json[] }>(url);
    return Array.isArray(body) ? body : body.items;
  }
}

/** The time a settings row was last written changes whenever it is written, even with the same values. */
function withoutClock(settings: Json): Json {
  const { updatedUtc: _updated, ...rest } = settings;
  return rest;
}

async function snapshot(api: Api): Promise<Snapshot> {
  const ids = {} as Snapshot["ids"];
  for (const [name, url] of Object.entries(LISTS)) ids[name as keyof Snapshot["ids"]] = (await api.items(url)).map((item) => String(item.id));
  return {
    settings: await api.get("/api/settings"),
    localNetworkAuth: await api.get("/api/security/local-network-auth"),
    updatePreferences: await api.get("/api/updates/preferences"),
    contentBlocklist: await api.get("/api/download-health/content-blocklist"),
    rules: await api.items("/api/download-health/rules"),
    progress: await api.get("/api/setup/progress"),
    ids
  };
}

/** Puts the install back as it was: what the guide created is removed, and every setting it wrote is written again. */
async function restore(api: Api, before: Snapshot) {
  for (const [name, url] of Object.entries(LISTS).reverse()) {
    for (const item of await api.items(url)) {
      if (!before.ids[name as keyof Snapshot["ids"]].includes(String(item.id))) await api.remove(`${url}/${item.id}`);
    }
  }
  await api.put("/api/settings", before.settings);
  await api.put("/api/security/local-network-auth", {
    enabled: before.localNetworkAuth.enabled, additionalLocalNetworks: before.localNetworkAuth.additionalLocalNetworks,
    allowList: before.localNetworkAuth.allowList, trustedProxies: before.localNetworkAuth.trustedProxies
  });
  await api.put("/api/updates/preferences", before.updatePreferences);
  await api.put("/api/download-health/content-blocklist", before.contentBlocklist);
  for (const rule of before.rules) await api.put(`/api/download-health/rules/${rule.id}`, rule);
  const progress = before.progress;
  await api.put("/api/setup/progress", { lastCompletedStep: progress.lastCompletedStep ?? 0, isSkipped: progress.isSkipped ?? false, isCompleted: progress.isCompleted ?? false });
}

const AUTOMATION_NO = {
  startAutomation: false, searchMissingTitles: false, rssGrabs: false, qualityUpgrades: false, renameOnImport: false, subtitleFetching: false,
  removeBadDownloads: false, deleteBadDownloadFiles: false, stallStrikes: false, leftoverCleanup: false, leftoverDownloadCleanup: false,
  forceImport: false, pauseSearchWhenQueueLong: false, dangerousFileCheck: false
};

function draftFor(roots: { movies: string; tv: string }, client: RecordedAppServer, indexer: RecordedAppServer, automation: Record<string, boolean>) {
  return {
    mode: "simple", mediaIntent: "both", movieRootPath: roots.movies, seriesRootPath: roots.tv, movieDownloadsPath: "", seriesDownloadsPath: "",
    qualityPreset: "balanced1080p", formatGoal: "balanced",
    indexerName: "Torznab", indexerProtocol: "torznab", indexerUrl: `${indexer.baseUrl}/api/v2.0/indexers/yts/results/torznab/api`,
    clientName: "qBittorrent", clientProtocol: "qbittorrent", clientHost: client.host, clientPort: String(client.port),
    backupEnabled: true, firstTitleType: "movies", firstTitle: "", firstTitleYear: "", firstTitleMonitored: true,
    skipSignInLocalNetwork: true, updateAutomatically: false,
    automation: { ...AUTOMATION_NO, ...automation, restrictAllowedHosts: false }
  };
}

async function openStep(page: Page, name: RegExp) {
  await page.getByRole("button", { name }).first().click();
}

async function passTheTests(page: Page) {
  await openStep(page, /Find & Download/);
  await page.getByPlaceholder("API key, if required").fill("test-api-key");
  await page.getByRole("button", { name: "Test search source" }).click();
  await expect(page.getByText(/Passed/).first()).toBeVisible({ timeout: 20_000 });
  await page.getByPlaceholder("Username, if required").fill("admin");
  await page.getByPlaceholder("Password/API key").fill("adminadmin");
  await page.getByRole("button", { name: "Test download client" }).click();
  await expect(page.getByText(/Passed/).nth(1).or(page.getByText(/Passed/).first())).toBeVisible({ timeout: 20_000 });
}

test.describe("the guided setup on the real host", () => {
  test.describe.configure({ mode: "serial" });

  let api: Api;
  let before: Snapshot;
  let client: RecordedAppServer;
  let indexer: RecordedAppServer;
  const folders: string[] = [];

  test.beforeEach(async ({ page, request, isMobile }) => {
    test.skip(isMobile, "One walk on the desktop project is enough; the install is shared.");
    await authenticateAndNavigate(page, "/");
    const token = (await page.evaluate(() => window.localStorage.getItem("deluno-auth-token")))!;
    api = new Api(request, token);
    before = await snapshot(api);
    client = await startRecordedAppServer(["sign-in", "version", "settings", "idle"].map((state) => loadRecording("qbittorrent", "5.2.1", state)));
    indexer = await startRecordedAppServer(["caps", "search-feed"].map((state) => loadRecording("jackett", "0.24.2850", state)));
  });

  test.afterEach(async () => {
    if (!api) return;
    await restore(api, before);
    const after = await snapshot(api);
    expect(after.ids).toEqual(before.ids);
    expect(withoutClock(after.settings)).toEqual(withoutClock(before.settings));
    await client?.close();
    await indexer?.close();
    await api.remove("/api/setup/draft");
    for (const folder of folders.splice(0)) fs.rmSync(folder, { recursive: true, force: true });
  });

  function makeFolder(name: string) {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), `deluno-guide-${name}-`));
    folders.push(folder);
    return folder;
  }

  test("the tests of the search source and the download client show Testing while they wait, then Passed, or the client's own refusal", async ({ page }) => {
    await indexer.close();
    await client.close();
    const slowIndexer = await startRecordedAppServer(["caps", "search-feed"].map((state) => loadRecording("jackett", "0.24.2850", state)), { delayMs: 1500 });
    const refusingClient = await startRecordedAppServer(["login-refused", "version"].map((state) => loadRecording("qbittorrent", "5.2.1", state)), { delayMs: 1500 });
    try {
      await api.put("/api/setup/draft", draftFor({ movies: makeFolder("movies"), tv: makeFolder("tv") }, refusingClient, slowIndexer, {}));
      await page.goto("/setup-guide");
      await openStep(page, /Find & Download/);

      await page.getByPlaceholder("API key, if required").fill("test-api-key");
      await expect(page.getByText("Not tested").first()).toBeVisible();
      await page.getByRole("button", { name: "Test search source" }).click();
      await expect(page.getByText("Testing...").first()).toBeVisible();
      await expect(page.getByText("Passed").first()).toBeVisible({ timeout: 20_000 });
      expect(slowIndexer.unanswered, "Jackett was asked something nobody recorded").toEqual([]);

      await page.getByPlaceholder("Username, if required").fill("admin");
      await page.getByPlaceholder("Password/API key").fill("wrong-password");
      await page.getByRole("button", { name: "Test download client" }).click();
      await expect(page.getByText("Testing...").first()).toBeVisible();
      await expect(page.getByText("Failed").first()).toBeVisible({ timeout: 20_000 });
      // qBittorrent's own refusal reaches the screen as the reason, not an invented sentence.
      expect(refusingClient.requests.some((request) => request.startsWith("POST /api/v2/auth/login"))).toBe(true);
      await expect(page.locator("p.text-destructive").first()).toHaveText(/\S/);

      // A change to any client field takes the verdict back to "Not tested".
      await page.getByPlaceholder("Password/API key").fill("adminadmin");
      await expect(page.getByText("Not tested").first()).toBeVisible();
    } finally {
      await slowIndexer.close();
      await refusingClient.close();
    }
  });

  test("a root that does not exist is refused, everything made before it is taken away again, and none of the answers is applied", async ({ page }) => {
    const missing = path.join(os.tmpdir(), `deluno-guide-missing-${Date.now()}`);
    await api.put("/api/setup/draft", draftFor({ movies: missing, tv: makeFolder("tv") }, client, indexer, { deleteBadDownloadFiles: true }));

    await page.goto("/setup-guide");
    await passTheTests(page);
    await openStep(page, /^(?:\d+ )?Start\b/);
    await page.getByRole("button", { name: "Create baseline" }).click();

    await expect(page.getByText(/Setup failed, so Deluno cleaned up \d+ partially created items?\./)).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(/None of your answers were applied/)).toBeVisible();
    const after = await snapshot(api);
    expect(after.ids).toEqual(before.ids);
    expect(withoutClock(after.settings)).toEqual(withoutClock(before.settings));
    expect(after.localNetworkAuth).toEqual(before.localNetworkAuth);
    // The answers are still in the draft, so the person can fix the folder and press the button again.
    expect(((await api.get("/api/setup/draft")).movieRootPath)).toBe(missing);
  });

  test("Create baseline builds the libraries, profiles, release rules, search source and client, applies the answers and ends at Setup complete", async ({ page }) => {
    const roots = { movies: makeFolder("movies"), tv: makeFolder("tv") };
    await api.put("/api/setup/draft", draftFor(roots, client, indexer, { deleteBadDownloadFiles: true, stallStrikes: true }));

    await page.goto("/setup-guide");
    await passTheTests(page);
    await openStep(page, /^(?:\d+ )?Start\b/);
    await page.getByRole("button", { name: "Create baseline" }).click();

    await expect(page.getByText("Setup complete")).toBeVisible({ timeout: 60_000 });
    expect(client.unanswered, "qBittorrent was asked something nobody recorded").toEqual([]);
    expect(indexer.unanswered, "Jackett was asked something nobody recorded").toEqual([]);

    const libraries = await api.items("/api/libraries");
    const movies = libraries.find((library) => library.name === "Movies")!;
    const tv = libraries.find((library) => library.name === "TV Shows")!;
    expect(movies.rootPath).toBe(roots.movies);
    expect(tv.rootPath).toBe(roots.tv);
    expect(movies.mediaType).toBe("movies");
    expect(tv.mediaType).toBe("tv");

    const profiles = (await api.items("/api/quality-profiles")).filter((profile) => !before.ids.profiles.includes(String(profile.id)));
    // One profile per media type, each named for the preset and the goal.
    expect(profiles.map((profile) => `${profile.mediaType}: ${profile.name}`).sort()).toEqual(["movies: Balanced 1080p - Balanced library", "tv: Balanced 1080p - Balanced library"]);
    const formats = (await api.items("/api/custom-formats")).filter((format) => !before.ids.formats.includes(String(format.id)));
    expect(formats.length).toBeGreaterThan(0);

    const indexers = (await api.items("/api/indexers")).filter((item) => !before.ids.indexers.includes(String(item.id)));
    expect(indexers).toHaveLength(1);
    const clients = (await api.items("/api/download-clients")).filter((item) => !before.ids.clients.includes(String(item.id)));
    expect(clients).toHaveLength(1);
    expect(clients[0].host).toBe(client.host);
    expect(String(clients[0].port)).toBe(String(client.port));

    // The answers were applied through the real routes.
    const settings = await api.get("/api/settings");
    expect(settings.autoStartJobs).toBe(false);
    expect(settings.cleanupPurgePayloadAfterThreshold).toBe(true);
    expect(settings.downloadHealthNoProgressEnabled).toBe(true);
    expect(settings.downloadHealthMinSpeedEnabled).toBe(true);
    expect(settings.downloadHealthMinSpeedKbps).toBe(50);
    expect(settings.downloadHealthStrikeThreshold).toBe(3);
    const rules = await api.items("/api/download-health/rules");
    expect(rules.filter((rule) => rule.enabled && (rule.kind === "no-progress" || rule.kind === "too-slow"))).toHaveLength(2);
    expect((await api.get("/api/security/local-network-auth")).enabled).toBe(true);
    expect((await api.get("/api/setup/progress")).isCompleted).toBe(true);
    const activity = await api.get<{ items: Json[] }>("/api/activity?pageSize=50");
    expect(activity.items.some((item) => item.message === "Guided setup completed.")).toBe(true);
  });
});
