import { execFileSync } from "node:child_process";
import { request, type APIRequestContext } from "@playwright/test";

/**
 * The end-to-end stack (tests/e2e/compose.yaml) as the specs see it: Deluno, qBittorrent, Weir and the fixture, each on a
 * port published on the machine running the specs. A spec reads every application's OWN API (qBittorrent's, Weir's, the
 * shared folders through the fixture), never Deluno's word about what another application did.
 *
 * Nothing here is private: these are the stack's own throwaway logins, made for a container that exists for one run.
 */

const port = (name: string, fallback: number) => Number(process.env[name] ?? fallback);

export const URLS = {
  deluno: `http://127.0.0.1:${port("E2E_DELUNO_PORT", 5099)}`,
  qbittorrent: `http://127.0.0.1:${port("E2E_QBIT_PORT", 18081)}`,
  weir: `http://127.0.0.1:${port("E2E_WEIR_PORT", 19347)}`,
  fixture: `http://127.0.0.1:${port("E2E_FIXTURE_PORT", 18765)}`
};

/** Where the applications reach each other, on the stack's own network. */
export const INSIDE = {
  deluno: "http://deluno:8080",
  qbittorrent: { host: "qbittorrent", port: 8080 },
  weir: "http://weir:9347"
};

export const LOGIN = {
  deluno: { username: "e2e", displayName: "End to end", password: "e2e-deluno-password-123" },
  qbittorrent: { username: "e2e", password: "e2e-qbittorrent-pass" },
  weir: { username: "e2e", password: "tidal-quarry-lantern-77" }
};

/** Folders as every container sees them (one volume each, mounted at the same path). */
export const FOLDERS = {
  moviesLibrary: "/media/Movies",
  moviesDownloads: "/downloads/completed/Movies",
  weirMoviesOutput: "/weir/Ready/Movies"
};

export const MOVIES_CATEGORY = "deluno-movies";

// ------------------------------------------------------------------ docker (for the two things only a container can say)

/** `docker` as this machine runs it: E2E_DOCKER="wsl -d Ubuntu -- docker" when Docker lives in WSL; "docker" otherwise. */
export function docker(...args: string[]): string {
  const parts = (process.env.E2E_DOCKER ?? "docker").split(" ").filter(Boolean);
  return execFileSync(parts[0], [...parts.slice(1), ...args], { encoding: "utf8", timeout: 60_000 });
}

const project = process.env.E2E_PROJECT ?? "deluno-e2e";
export const containerOf = (service: string) => `${project}-${service}-1`;

// ------------------------------------------------------------------ a small api client

export interface Reply<T = unknown> { status: number; body: T; text: string }

async function reply<T>(response: { status(): number; text(): Promise<string> }): Promise<Reply<T>> {
  const text = await response.text();
  let body: unknown = text;
  try { body = text ? JSON.parse(text) : null; } catch { /* a reply that is not JSON stays text */ }
  return { status: response.status(), body: body as T, text };
}

export class DelunoApi {
  private constructor(private readonly context: APIRequestContext) {}

  static async signIn(): Promise<DelunoApi> {
    const anonymous = await request.newContext({ baseURL: URLS.deluno });
    const status = await (await anonymous.get("/api/auth/bootstrap-status")).json() as { requiresSetup?: boolean };
    if (status.requiresSetup) {
      const made = await anonymous.post("/api/auth/bootstrap", { data: LOGIN.deluno });
      if (!made.ok()) throw new Error(`Deluno would not make the first account: ${made.status()} ${await made.text()}`);
    }
    const login = await anonymous.post("/api/auth/login", { data: { username: LOGIN.deluno.username, password: LOGIN.deluno.password } });
    if (!login.ok()) throw new Error(`Deluno would not sign in: ${login.status()} ${await login.text()}`);
    const { accessToken } = await login.json() as { accessToken: string };
    await anonymous.dispose();
    const context = await request.newContext({ baseURL: URLS.deluno, extraHTTPHeaders: { Authorization: `Bearer ${accessToken}` } });
    return new DelunoApi(context);
  }

  async call<T = any>(method: "GET" | "POST" | "PUT" | "DELETE", path: string, data?: unknown): Promise<Reply<T>> {
    return reply<T>(await this.context.fetch(path, { method, data, timeout: 60_000 }));
  }

  /** The body of a call that must have worked. */
  async must<T = any>(method: "GET" | "POST" | "PUT" | "DELETE", path: string, data?: unknown): Promise<T> {
    const r = await this.call<T>(method, path, data);
    if (r.status < 200 || r.status >= 300) throw new Error(`${method} ${path} answered ${r.status}: ${r.text.slice(0, 600)}`);
    return r.body;
  }

  async dispose() { await this.context.dispose(); }
}

/** qBittorrent's own Web API, signed in as the stack's login. */
export class QbittorrentApi {
  private constructor(private readonly context: APIRequestContext) {}

  static async signIn(): Promise<QbittorrentApi> {
    const context = await request.newContext({ baseURL: URLS.qbittorrent });
    const login = await context.post("/api/v2/auth/login", { form: LOGIN.qbittorrent });
    const said = (await login.text()).trim();
    if (!login.ok() || (said !== "" && !/^ok/i.test(said))) throw new Error(`qBittorrent would not sign in: ${login.status()} ${said}`);
    return new QbittorrentApi(context);
  }

  async get<T = any>(path: string): Promise<T> {
    const response = await this.context.get(path);
    if (!response.ok()) throw new Error(`qBittorrent GET ${path} answered ${response.status()}`);
    return await response.json() as T;
  }

  async post(path: string, form: Record<string, string>): Promise<Reply> {
    return reply(await this.context.post(path, { form }));
  }

  /** The torrent with this info hash, as qBittorrent itself lists it, or undefined. */
  async torrent(hash: string): Promise<QbitTorrent | undefined> {
    const list = await this.get<QbitTorrent[]>(`/api/v2/torrents/info?hashes=${hash}`);
    return list[0];
  }

  async files(hash: string): Promise<Array<{ name: string; size: number; progress: number }>> {
    return this.get(`/api/v2/torrents/files?hash=${hash}`);
  }

  async dispose() { await this.context.dispose(); }
}

export interface QbitTorrent {
  hash: string; name: string; state: string; progress: number; size: number; downloaded: number;
  category: string; save_path: string; content_path: string; amount_left: number; completion_on: number;
}

/** Weir's own API: a session cookie and a CSRF token, as its web screens use them. */
export class WeirApi {
  private constructor(private readonly context: APIRequestContext) {}

  static async signIn(): Promise<WeirApi> {
    const headers = { Origin: URLS.weir, "X-Requested-With": "XMLHttpRequest" };
    const context = await request.newContext({ baseURL: URLS.weir, extraHTTPHeaders: headers });
    const weir = new WeirApi(context);
    const status = await (await context.get("/api/v1/auth/bootstrap/status")).json() as { bootstrap_allowed?: boolean; requires_setup_code?: boolean };
    if (status.bootstrap_allowed) {
      // From another container's address Weir wants the one-time code it wrote when it started.
      const setup_code = status.requires_setup_code ? docker("exec", containerOf("weir"), "cat", "/data/weir/setup-code").trim() : undefined;
      const made = await weir.post("/api/v1/auth/bootstrap", { ...LOGIN.weir, setup_code });
      if (made.status >= 300) throw new Error(`Weir would not make the first account: ${made.status} ${made.text}`);
    }
    const login = await weir.post("/api/v1/auth/login", LOGIN.weir);
    if (login.status >= 300) throw new Error(`Weir would not sign in: ${login.status} ${login.text}`);
    return weir;
  }

  async get<T = any>(path: string): Promise<Reply<T>> {
    return reply<T>(await this.context.get(path));
  }

  async send<T = any>(method: "POST" | "PUT" | "DELETE", path: string, data: Record<string, unknown> = {}): Promise<Reply<T>> {
    const csrf = (await (await this.context.get("/api/v1/auth/csrf")).json() as { csrf_token: string }).csrf_token;
    return reply<T>(await this.context.fetch(path, { method, data: { ...data, csrf_token: csrf } }));
  }

  post<T = any>(path: string, data: Record<string, unknown> = {}) { return this.send<T>("POST", path, data); }
  put<T = any>(path: string, data: Record<string, unknown> = {}) { return this.send<T>("PUT", path, data); }

  async dispose() { await this.context.dispose(); }
}

// ------------------------------------------------------------------ the fixture

export interface FixtureFilm {
  title: string; year: number; release: string; infohash: string; size: number; url: string;
  files: Array<{ path: string; size: number }>;
}

export async function makeFilm(title: string, extra: boolean): Promise<FixtureFilm> {
  const response = await fetch(`${URLS.fixture}/films`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ title, year: 2020, extra }) });
  if (!response.ok) throw new Error(`The fixture would not make ${title}: ${response.status} ${await response.text()}`);
  return await response.json() as FixtureFilm;
}

/** The files under a shared folder whose path contains the text, read by the fixture from its own mount. */
export async function filesUnder(root: "media" | "downloads" | "weir", contains: string): Promise<Array<{ path: string; size: number }>> {
  const response = await fetch(`${URLS.fixture}/inspect?root=${root}&contains=${encodeURIComponent(contains)}`);
  if (!response.ok) throw new Error(`The fixture could not list ${root}: ${response.status}`);
  return await response.json() as Array<{ path: string; size: number }>;
}

// ------------------------------------------------------------------ waiting

export async function until<T>(what: string, seconds: number, read: () => Promise<T | undefined | null | false>, everyMs = 1000): Promise<{ value: T; seconds: number }> {
  const started = Date.now();
  let last: unknown;
  while (Date.now() - started < seconds * 1000) {
    try {
      const value = await read();
      if (value) return { value: value as T, seconds: Math.round((Date.now() - started) / 100) / 10 };
    } catch (error) { last = error; }
    await new Promise((resolve) => setTimeout(resolve, everyMs));
  }
  throw new Error(`Gave up after ${seconds} s waiting for ${what}.${last ? ` Last error: ${String(last).slice(0, 300)}` : ""}`);
}

// ------------------------------------------------------------------ setting the stack up, once, through the applications' own APIs

export interface Stack {
  deluno: DelunoApi;
  qbittorrent: QbittorrentApi;
  weir: WeirApi;
  libraryId: string;
  clientId: string;
  indexerId: string;
  processorId: string;
}

/**
 * Deluno's setup as a person does it: the first account, the qBittorrent connection, the feed as an indexer, the Weir
 * connection (an API key for Weir to call Deluno back with, Weir's webhook secret), a quality profile and the movies
 * library, that hands its downloads to Weir before it imports. Safe to run again: what exists is reused.
 */
export async function setUpStack(): Promise<Stack> {
  const deluno = await DelunoApi.signIn();
  const qbittorrent = await QbittorrentApi.signIn();
  const weir = await WeirApi.signIn();

  // qBittorrent: where it keeps unfinished and finished torrents, and the category Deluno puts films in.
  await qbittorrent.post("/api/v2/app/setPreferences", {
    json: JSON.stringify({ save_path: "/downloads/completed", temp_path_enabled: true, temp_path: "/downloads/incomplete", auto_tmm_enabled: true, dht: false, pex: false, lsd: false })
  });
  const categories = await qbittorrent.get<Record<string, unknown>>("/api/v2/torrents/categories");
  if (!categories[MOVIES_CATEGORY]) await qbittorrent.post("/api/v2/torrents/createCategory", { category: MOVIES_CATEGORY, savePath: FOLDERS.moviesDownloads });

  // The download client.
  const clients = await deluno.must<any[]>("GET", "/api/download-clients");
  let client = clients.find((c) => c.protocol === "qbittorrent" && c.host === INSIDE.qbittorrent.host);
  if (!client) {
    client = await deluno.must("POST", "/api/download-clients", {
      name: "qBittorrent", protocol: "qbittorrent", host: INSIDE.qbittorrent.host, port: INSIDE.qbittorrent.port,
      username: LOGIN.qbittorrent.username, password: LOGIN.qbittorrent.password,
      moviesCategory: MOVIES_CATEGORY, tvCategory: "deluno-tv", priority: 1, isEnabled: true
    });
  }

  // The fixture's feed as a public Torznab indexer. Nothing automatic: the spec sends each release itself (#936).
  const indexers = await deluno.must<any[]>("GET", "/api/indexers");
  let indexer = indexers.find((i) => String(i.baseUrl).startsWith("http://fixture:8765"));
  if (!indexer) {
    indexer = await deluno.must("POST", "/api/indexers", {
      name: "E2E feed", protocol: "torznab", privacy: "public", baseUrl: "http://fixture:8765", apiKey: "e2e",
      priority: 25, categories: "2000,2040,5000,5040", mediaScope: "both", isEnabled: true, keepName: true, routeToLibraries: true,
      rssEnabled: false, automaticSearchEnabled: false
    });
  }

  // Deluno sends a release only to a client whose connection test passed, so test both as a person does.
  const clientTest = await deluno.call("POST", `/api/download-clients/${client.id}/test`);
  if (clientTest.status >= 300) throw new Error(`Deluno's test of qBittorrent failed: ${clientTest.status} ${clientTest.text.slice(0, 400)}`);
  const indexerTest = await deluno.call("POST", `/api/indexers/${indexer.id}/test`);
  if (indexerTest.status >= 300) throw new Error(`Deluno's test of the feed failed: ${indexerTest.status} ${indexerTest.text.slice(0, 400)}`);

  // Weir: Deluno's API key for Weir to call back with, Weir's connection to Deluno and its webhook secret, then Weir as Deluno's processor.
  const processors = await deluno.must<any[]>("GET", "/api/integrations/processors/connections");
  let processor = processors.find((p) => String(p.submissionUrl ?? "").startsWith(INSIDE.weir));
  if (!processor) {
    const key = await deluno.must<{ apiKey: string }>("POST", "/api/api-keys", { name: "Weir (set up by the end-to-end suite)", scopes: "read,imports" });
    const managers = await weir.get<any[]>("/api/v1/media-managers/connections");
    let manager = (managers.body as any[]).find((m) => m.kind === "deluno");
    if (!manager) {
      const made = await weir.post("/api/v1/media-managers/connections", { kind: "deluno", name: "Deluno", base_url: INSIDE.deluno, api_key: key.apiKey, enabled: true });
      if (made.status >= 300) throw new Error(`Weir would not take Deluno as a media manager: ${made.status} ${made.text}`);
      manager = made.body;
    }
    const secret = await weir.post<any>(`/api/v1/media-managers/connections/${manager.id}/webhook-secret`);
    if (secret.status >= 300) throw new Error(`Weir would not make a webhook secret: ${secret.status} ${secret.text}`);
    processor = await deluno.must("POST", "/api/integrations/processors/connections", {
      name: "Weir", provider: "generic-webhook", submissionUrl: `${INSIDE.weir}${secret.body.webhook_url_path}`,
      authHeaderName: secret.body.header_name ?? "X-Webhook-Secret", secret: secret.body.webhook_secret, isEnabled: true,
      healthCheckUrl: `${INSIDE.weir}/ready`
    });
  }

  // A quality profile and the movies library, refining before import with Weir's output folder.
  const profiles = await deluno.must<any[]>("GET", "/api/quality-profiles");
  let profile = profiles.find((p) => p.mediaType === "movies");
  if (!profile) {
    profile = await deluno.must("POST", "/api/quality-profiles", {
      name: "Balanced 1080p - E2E", mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p,Bluray 720p,WEB 1080p,Bluray 1080p",
      customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false
    });
  }
  const libraries = await deluno.must<any[]>("GET", "/api/libraries");
  let library = libraries.find((l) => l.mediaType === "movies");
  if (!library) {
    library = await deluno.must("POST", "/api/libraries", {
      name: "Movies", mediaType: "movies", purpose: "Main library", rootPath: FOLDERS.moviesLibrary, downloadsPath: FOLDERS.moviesDownloads,
      qualityProfileId: profile.id, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false,
      removeEmptySourceFolders: false, searchIntervalMinutes: 720, retryDelayHours: 6, maxItemsPerRun: 10
    });
  }
  await deluno.must("PUT", `/api/libraries/${library.id}/workflow`, {
    importWorkflow: "refine-before-import", processorConnectionId: processor.id, processorName: processor.name,
    processorOutputPath: FOLDERS.weirMoviesOutput, processorTimeoutMinutes: 2880, processorFailureMode: "block",
    cleanupMode: "keep-source", removeEmptySourceFolders: false
  });

  // Route the library to the one client and the feed (what a person does in the library's settings).
  await deluno.must("PUT", `/api/libraries/${library.id}/routing`, {
    sources: [{ indexerId: indexer.id, priority: 1, requiredTags: "", excludedTags: "" }],
    downloadClients: [{ downloadClientId: client.id, priority: 1 }]
  });

  // Weir's first-run step "Finish setup": the library Weir was offered from Deluno's (its folders, linked to the connection).
  await linkWeirLibrary(weir, "movie", FOLDERS.moviesDownloads, FOLDERS.weirMoviesOutput);
  await until("Weir to watch the movies library's folder", 120, async () => {
    const libs = await weir.get<any[]>("/api/v1/processing/libraries");
    return libs.body.some((l) => l.enabled && l.watched_folder === FOLDERS.moviesDownloads);
  }, 2000);

  // Automatic jobs on, as the scenario suite does: the planner that hands a finished download to Weir is one of them.
  await deluno.must("PUT", "/api/settings/automation", { isEnabled: true });

  return { deluno, qbittorrent, weir, libraryId: library.id, clientId: client.id, indexerId: indexer.id, processorId: processor.id };
}

/** The fields a Weir library write takes (Weir refuses any other), taken from what it reads back. */
const WEIR_LIBRARY_WRITE_FIELDS = [
  "name", "media_type", "enabled", "watched_folder", "work_folder", "output_folder", "media_extensions_csv", "exclude_markers_csv",
  "include_patterns_csv", "exclude_patterns_csv", "min_file_size_mb", "max_file_size_mb", "rejected_file_action", "ready_after_seconds",
  "created_after", "created_before", "modified_after", "modified_before", "exclude_hidden", "top_level_only", "scan_interval_seconds",
  "sidecar_patterns_csv", "preserve_original_timestamps", "remove_original_after_success", "minimum_free_disk_space_mb", "remux_writer",
  "output_collision_policy", "ffmpeg_strictness", "ignore_size_changes", "skip_access_tests", "file_system_events_enabled", "max_attempts",
  "retry_backoff_seconds", "retry_execution_failures", "retry_preflight_failures", "failure_policy", "schedule_grid", "schedule_enabled",
  "schedule_hours_limited", "schedule_days", "schedule_start", "schedule_end", "max_concurrent_files", "priority", "rule_set_id",
  "manager_connection_ids"
];

/** Fills in Weir's empty library of this type with the folders Deluno offered, and links it to Deluno, as Weir's setup wizard does. */
async function linkWeirLibrary(weir: WeirApi, mediaType: "movie" | "tv", watched: string, output: string) {
  const libs = await weir.get<any[]>("/api/v1/processing/libraries");
  const library = libs.body.find((l) => l.media_type === mediaType);
  if (!library) throw new Error(`Weir has no ${mediaType} library to fill in.`);
  const managers = await weir.get<any[]>("/api/v1/media-managers/connections");
  const manager = managers.body.find((m) => m.kind === "deluno");
  const body: Record<string, unknown> = {};
  for (const field of WEIR_LIBRARY_WRITE_FIELDS) body[field] = library[field];
  body.watched_folder = watched;
  body.output_folder = output;
  body.manager_connection_ids = [...new Set([...(library.manager_connection_ids ?? []), manager.id])];
  const saved = await weir.put(`/api/v1/processing/libraries/${library.id}`, body);
  if (saved.status >= 300) throw new Error(`Weir would not save its ${mediaType} library: ${saved.status} ${saved.text.slice(0, 500)}`);
}
