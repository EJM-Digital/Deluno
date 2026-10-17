import { execFileSync } from "node:child_process";
import { request, type APIRequestContext } from "@playwright/test";
import { CLIENT_KINDS, DelugeApi, NzbgetApi, SabnzbdApi, TransmissionApi, isUsenet, type ClientKind } from "./clients.ts";

/**
 * The end-to-end stack (tests/e2e/compose.yaml) as the specs see it: Deluno, the five download clients, Weir and the fixture,
 * each on a port published on the machine running the specs. A spec reads every application's OWN API (the clients', Weir's,
 * the shared folders through the fixture), never Deluno's word about what another application did.
 *
 * Nothing here is private: these are the stack's own throwaway logins, made for a container that exists for one run.
 */

const port = (name: string, fallback: number) => Number(process.env[name] ?? fallback);

export const URLS = {
  deluno: `http://127.0.0.1:${port("E2E_DELUNO_PORT", 5099)}`,
  qbittorrent: `http://127.0.0.1:${port("E2E_QBIT_PORT", 18081)}`,
  transmission: `http://127.0.0.1:${port("E2E_TRANSMISSION_PORT", 19091)}`,
  deluge: `http://127.0.0.1:${port("E2E_DELUGE_PORT", 18112)}`,
  sabnzbd: `http://127.0.0.1:${port("E2E_SABNZBD_PORT", 18083)}`,
  nzbget: `http://127.0.0.1:${port("E2E_NZBGET_PORT", 16789)}`,
  weir: `http://127.0.0.1:${port("E2E_WEIR_PORT", 19347)}`,
  fixture: `http://127.0.0.1:${port("E2E_FIXTURE_PORT", 18765)}`
};

/** Where the applications reach each other, on the stack's own network. */
export const INSIDE = {
  deluno: "http://deluno:8080",
  qbittorrent: { host: "qbittorrent", port: 8080 },
  transmission: { host: "transmission", port: 9091 },
  deluge: { host: "deluge", port: 8112 },
  sabnzbd: { host: "sabnzbd", port: 8080 },
  nzbget: { host: "nzbget", port: 6789 },
  weir: "http://weir:9347"
};

export const LOGIN = {
  deluno: { username: "e2e", displayName: "End to end", password: "e2e-deluno-password-123" },
  qbittorrent: { username: "e2e", password: "e2e-qbittorrent-pass" },
  // The other clients keep their own shipped logins (Transmission has none); SABnzbd's key is the one in tests/e2e/sabnzbd/sabnzbd.ini.
  deluge: { password: "deluge" },
  sabnzbd: { apiKey: "e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0" },
  nzbget: { username: "nzbget", password: "tegbzn6789" },
  weir: { username: "e2e", password: "tidal-quarry-lantern-77" }
};

/** Folders as every container sees them (one volume each, mounted at the same path). */
export const FOLDERS = {
  moviesLibrary: "/media/Movies",
  moviesDownloads: "/downloads/completed/Movies",
  weirMoviesOutput: "/weir/Ready/Movies"
};

export const MOVIES_CATEGORY = "deluno-movies";
export { isUsenet };

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
  title: string; year: number; release: string; protocol?: "usenet"; infohash: string; size: number; url: string;
  files: Array<{ path: string; size: number }>;
}

/** A release the fixture makes: a torrent (with a web seed) or, with usenet, a folder job posted as yEnc articles with its .nzb. */
export async function makeFilm(title: string, extra: boolean, usenet = false): Promise<FixtureFilm> {
  const response = await fetch(`${URLS.fixture}/films`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ title, year: 2020, extra, usenet }) });
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
  transmission: TransmissionApi;
  deluge: DelugeApi;
  sabnzbd: SabnzbdApi;
  nzbget: NzbgetApi;
  weir: WeirApi;
  libraryId: string;
  /** Deluno's id for each download client, by kind. */
  clientIds: Record<ClientKind, string>;
  /** The qBittorrent client (the first one the stack had). */
  clientId: string;
  /** The Torznab feed (torrent releases) and the Newznab feed (Usenet releases). */
  indexerId: string;
  usenetIndexerId: string;
  processorId: string;
}

/** Routes the movies library to this one client, as a person does in the library's settings, so the next send goes there. */
export async function routeLibraryTo(stack: Stack, kind: ClientKind) {
  await stack.deluno.must("PUT", `/api/libraries/${stack.libraryId}/routing`, {
    sources: [{ indexerId: stack.indexerId, priority: 1, requiredTags: "", excludedTags: "" }, { indexerId: stack.usenetIndexerId, priority: 2, requiredTags: "", excludedTags: "" }],
    downloadClients: [{ downloadClientId: stack.clientIds[kind], priority: 1 }]
  });
}

/** The client's own word on whether it has finished downloading the film. */
export function clientFinished(stack: Stack, kind: ClientKind, film: FixtureFilm): Promise<boolean> {
  switch (kind) {
    case "qbittorrent": return stack.qbittorrent.torrent(film.infohash).then((t) => !!t && t.progress >= 1 && t.amount_left === 0);
    case "transmission": return stack.transmission.finished(film);
    case "deluge": return stack.deluge.finished(film);
    case "sabnzbd": return stack.sabnzbd.finished(film);
    case "nzbget": return stack.nzbget.finished(film);
  }
}

/**
 * Deluno's setup as a person does it: the first account, the qBittorrent connection, the feed as an indexer, the Weir
 * connection (an API key for Weir to call Deluno back with, Weir's webhook secret), a quality profile and the movies
 * library, that hands its downloads to Weir before it imports. Safe to run again: what exists is reused.
 */
export async function setUpStack(): Promise<Stack> {
  const deluno = await DelunoApi.signIn();
  const qbittorrent = await QbittorrentApi.signIn();
  const transmission = await TransmissionApi.connect();
  const deluge = await DelugeApi.connect();
  const sabnzbd = await SabnzbdApi.connect();
  const nzbget = await NzbgetApi.connect();
  const weir = await WeirApi.signIn();

  // qBittorrent: where it keeps unfinished and finished torrents, and the category Deluno puts films in.
  await qbittorrent.post("/api/v2/app/setPreferences", {
    json: JSON.stringify({ save_path: "/downloads/completed", temp_path_enabled: true, temp_path: "/downloads/incomplete", auto_tmm_enabled: true, dht: false, pex: false, lsd: false })
  });
  const categories = await qbittorrent.get<Record<string, unknown>>("/api/v2/torrents/categories");
  if (!categories[MOVIES_CATEGORY]) await qbittorrent.post("/api/v2/torrents/createCategory", { category: MOVIES_CATEGORY, savePath: FOLDERS.moviesDownloads });

  // The other clients' folders (and for the torrent clients, no DHT, PEX or local discovery), through each one's own API.
  await transmission.setUp();
  await deluge.setUp();
  await nzbget.setUp();

  // The download clients, each as a person adds it: the address, its login (or SABnzbd's key) and the two categories.
  const logins: Record<ClientKind, { name: string; username?: string; password?: string }> = {
    qbittorrent: { name: "qBittorrent", ...LOGIN.qbittorrent },
    transmission: { name: "Transmission" },
    deluge: { name: "Deluge", password: LOGIN.deluge.password },
    sabnzbd: { name: "SABnzbd", password: LOGIN.sabnzbd.apiKey },
    nzbget: { name: "NZBGet", ...LOGIN.nzbget }
  };
  const existing = await deluno.must<any[]>("GET", "/api/download-clients");
  const clientRows = {} as Record<ClientKind, any>;
  for (const kind of CLIENT_KINDS) {
    let row = existing.find((c) => c.protocol === kind && c.host === INSIDE[kind].host);
    if (!row) {
      row = await deluno.must("POST", "/api/download-clients", {
        ...logins[kind], protocol: kind, host: INSIDE[kind].host, port: INSIDE[kind].port,
        moviesCategory: MOVIES_CATEGORY, tvCategory: "deluno-tv", priority: 1, isEnabled: true
      });
    }
    clientRows[kind] = row;
  }

  // Deluno runs in its own container, so it cannot tell that Transmission and Deluge see the same folders it does. A person adds a
  // path mapping for each ("Add a path mapping" in the client's settings); with it Deluno names the library's downloads folder
  // on every send, so a finished torrent lands where Weir watches. The folders are the same on both sides: /downloads.
  for (const kind of ["transmission", "deluge"] as const) {
    const mappings = await deluno.must<any[]>("GET", `/api/download-clients/${clientRows[kind].id}/path-mappings`);
    if (!mappings.some((m) => m.remotePath === "/downloads" && m.localPath === "/downloads")) {
      await deluno.must("POST", `/api/download-clients/${clientRows[kind].id}/path-mappings`, { remotePath: "/downloads", localPath: "/downloads", isEnabled: true, priority: 1 });
    }
  }
  const client = clientRows.qbittorrent;

  // The fixture's feeds: a public Torznab indexer for the torrents and a Newznab one for the Usenet releases. Nothing automatic:
  // the spec sends each release itself (#936).
  const indexers = await deluno.must<any[]>("GET", "/api/indexers");
  const feed = async (name: string, protocol: string, baseUrl: string) => {
    return indexers.find((i) => i.baseUrl === baseUrl) ?? await deluno.must("POST", "/api/indexers", {
      name, protocol, privacy: "public", baseUrl, apiKey: "e2e",
      priority: 25, categories: "2000,2040,5000,5040", mediaScope: "both", isEnabled: true, keepName: true, routeToLibraries: true,
      rssEnabled: false, automaticSearchEnabled: false
    });
  };
  const indexer = await feed("E2E feed", "torznab", "http://fixture:8765");
  const usenetIndexer = await feed("E2E Usenet feed", "newznab", "http://fixture:8765/newznab");

  // Deluno sends a release only to a client whose connection test passed, so test them all as a person does.
  for (const kind of CLIENT_KINDS) {
    const clientTest = await deluno.call("POST", `/api/download-clients/${clientRows[kind].id}/test`);
    if (clientTest.status >= 300) throw new Error(`Deluno's test of ${logins[kind].name} failed: ${clientTest.status} ${clientTest.text.slice(0, 400)}`);
  }
  for (const [name, row] of [["the Torznab feed", indexer], ["the Newznab feed", usenetIndexer]] as const) {
    const indexerTest = await deluno.call("POST", `/api/indexers/${row.id}/test`);
    if (indexerTest.status >= 300) throw new Error(`Deluno's test of ${name} failed: ${indexerTest.status} ${indexerTest.text.slice(0, 400)}`);
  }

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

  // Route the library to qBittorrent and the feeds (what a person does in the library's settings); a spec re-routes it per send.
  const clientIds = Object.fromEntries(CLIENT_KINDS.map((kind) => [kind, clientRows[kind].id])) as Record<ClientKind, string>;
  await deluno.must("PUT", `/api/libraries/${library.id}/routing`, {
    sources: [{ indexerId: indexer.id, priority: 1, requiredTags: "", excludedTags: "" }, { indexerId: usenetIndexer.id, priority: 2, requiredTags: "", excludedTags: "" }],
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

  return {
    deluno, qbittorrent, transmission, deluge, sabnzbd, nzbget, weir, libraryId: library.id, clientIds, clientId: client.id,
    indexerId: indexer.id, usenetIndexerId: usenetIndexer.id, processorId: processor.id
  };
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
