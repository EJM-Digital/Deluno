import { request, type APIRequestContext } from "@playwright/test";
import { LOGIN, URLS, until, type FixtureFilm } from "./stack.ts";

/**
 * The four download clients beside qBittorrent, each through its OWN web API as the client publishes it: Transmission's RPC,
 * Deluge's web JSON-RPC, SABnzbd's API and NZBGet's JSON-RPC. A spec asks these what a client did, never Deluno.
 *
 * Each class also does the client's one-time setup for the stack (folders, and for the torrent clients DHT, PEX and local
 * discovery off so a torrent with a web seed downloads only from the fixture). Nothing here is private: they are the clients'
 * own shipped logins and the stack's throwaway key (build-rules.md, "Download client logins").
 */

export type ClientKind = "qbittorrent" | "transmission" | "deluge" | "sabnzbd" | "nzbget";
export const CLIENT_KINDS: ClientKind[] = ["qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget"];
export const isUsenet = (kind: ClientKind) => kind === "sabnzbd" || kind === "nzbget";

const MOVIES_DOWNLOADS = "/downloads/completed/Movies";
const TV_DOWNLOADS = "/downloads/completed/TV";

// ------------------------------------------------------------------ Transmission

export class TransmissionApi {
  private session = "";
  private constructor() {}

  static async connect(): Promise<TransmissionApi> {
    const api = new TransmissionApi();
    await until("Transmission's RPC to answer", 120, async () => (await api.rpc("session-get", { fields: ["version"] })).version, 2000);
    return api;
  }

  async rpc(method: string, args: Record<string, unknown> = {}): Promise<any> {
    for (let attempt = 0; attempt < 3; attempt++) {
      const response = await fetch(`${URLS.transmission}/transmission/rpc`, {
        method: "POST", headers: { "content-type": "application/json", "X-Transmission-Session-Id": this.session },
        body: JSON.stringify({ method, arguments: args })
      });
      if (response.status === 409) { this.session = response.headers.get("X-Transmission-Session-Id") ?? ""; continue; }
      if (!response.ok) throw new Error(`Transmission ${method} answered ${response.status}`);
      const body = await response.json() as { result: string; arguments: any };
      if (body.result !== "success") throw new Error(`Transmission ${method}: ${body.result}`);
      return body.arguments;
    }
    throw new Error(`Transmission would not take a session id for ${method}`);
  }

  async setUp() {
    await this.rpc("session-set", {
      "download-dir": "/downloads/completed", "incomplete-dir": "/downloads/incomplete", "incomplete-dir-enabled": true,
      "dht-enabled": false, "pex-enabled": false, "lpd-enabled": false, "utp-enabled": false
    });
  }

  /** The torrent as Transmission itself lists it. */
  async torrent(hash: string): Promise<{ name: string; percentDone: number; leftUntilDone: number; downloadDir: string; status: number } | undefined> {
    const reply = await this.rpc("torrent-get", { ids: [hash.toLowerCase()], fields: ["name", "percentDone", "leftUntilDone", "downloadDir", "status"] });
    return reply.torrents?.[0];
  }

  async finished(film: FixtureFilm) {
    const t = await this.torrent(film.infohash);
    return !!t && t.percentDone >= 1 && t.leftUntilDone === 0;
  }
}

// ------------------------------------------------------------------ Deluge

export class DelugeApi {
  private constructor(private readonly context: APIRequestContext) {}

  static async connect(): Promise<DelugeApi> {
    const context = await request.newContext({ baseURL: URLS.deluge });
    const api = new DelugeApi(context);
    const login = await until("Deluge's web interface to accept its shipped login", 120, async () => (await api.call("auth.login", [LOGIN.deluge.password])) === true, 2000);
    void login;
    // The web interface must be connected to the daemon before it answers core calls.
    if (!(await api.call("web.connected"))) {
      const hosts = await api.call("web.get_hosts") as string[][];
      await api.call("web.connect", [hosts[0][0]]);
    }
    await until("Deluge's web interface to be connected to its daemon", 60, async () => (await api.call("web.connected")) === true, 1000);
    return api;
  }

  async call(method: string, params: unknown[] = []): Promise<any> {
    const response = await this.context.post("/json", { data: { method, params, id: 1 } });
    if (!response.ok()) throw new Error(`Deluge ${method} answered ${response.status()}`);
    const body = await response.json() as { result: unknown; error: { message: string } | null };
    if (body.error) throw new Error(`Deluge ${method}: ${body.error.message}`);
    return body.result;
  }

  async setUp() {
    await this.call("core.set_config", [{
      download_location: "/downloads/incomplete", move_completed: false,
      dht: false, lsd: false, utpex: false, upnp: false, natpmp: false
    }]);
    // The Label plugin is how a person gives Deluge a category.
    const enabled = await this.call("core.get_enabled_plugins") as string[];
    if (!enabled.includes("Label")) await this.call("core.enable_plugin", ["Label"]);
  }

  async torrent(hash: string): Promise<{ state: string; progress: number; save_path: string; name: string } | undefined> {
    const status = await this.call("core.get_torrent_status", [hash.toLowerCase(), ["state", "progress", "save_path", "name"]]);
    return status && status.state ? status : undefined;
  }

  async finished(film: FixtureFilm) {
    const t = await this.torrent(film.infohash);
    // Deluge moves a finished torrent to the folder Deluno named; "Moving" is still part of the finish.
    return !!t && t.progress >= 100 && t.state !== "Moving" && t.state !== "Checking";
  }
}

// ------------------------------------------------------------------ SABnzbd

export class SabnzbdApi {
  private constructor(private readonly context: APIRequestContext) {}

  static async connect(): Promise<SabnzbdApi> {
    const api = new SabnzbdApi(await request.newContext({ baseURL: URLS.sabnzbd }));
    await until("SABnzbd's API to answer", 120, async () => (await api.call("version")).version, 2000);
    return api;
  }

  async call(mode: string, extra: Record<string, string> = {}): Promise<any> {
    const query = new URLSearchParams({ mode, output: "json", apikey: LOGIN.sabnzbd.apiKey, ...extra });
    const response = await this.context.get(`/api?${query}`);
    if (!response.ok()) throw new Error(`SABnzbd ${mode} answered ${response.status()}`);
    return await response.json();
  }

  /** The job as SABnzbd's history lists it (a finished job leaves the queue for the history). */
  async historyJob(release: string): Promise<{ name: string; status: string; storage: string; category: string; fail_message: string } | undefined> {
    const history = await this.call("history", { search: release, limit: "50" });
    return (history.history?.slots as any[] | undefined)?.find((s) => s.name === release);
  }

  async finished(film: FixtureFilm) {
    return (await this.historyJob(film.release))?.status === "Completed";
  }
}

// ------------------------------------------------------------------ NZBGet

export class NzbgetApi {
  private constructor(private readonly context: APIRequestContext) {}

  static async connect(): Promise<NzbgetApi> {
    const context = await request.newContext({ baseURL: URLS.nzbget, httpCredentials: { username: LOGIN.nzbget.username, password: LOGIN.nzbget.password } });
    const api = new NzbgetApi(context);
    await until("NZBGet's RPC to answer", 120, async () => await api.call("version"), 2000);
    return api;
  }

  async call(method: string, params: unknown[] = []): Promise<any> {
    const response = await this.context.post("/jsonrpc", { data: { method, params, id: 1 }, timeout: 30_000 });
    if (!response.ok()) throw new Error(`NZBGet ${method} answered ${response.status()}`);
    const body = await response.json() as { result: unknown; error?: { message: string } };
    if (body.error) throw new Error(`NZBGet ${method}: ${body.error.message}`);
    return body.result;
  }

  /** Points NZBGet at the fixture's news server and the stack's folders, through its own settings call, then reloads it. */
  async setUp() {
    const wanted: Record<string, string> = {
      DestDir: "/downloads/completed", InterDir: "/downloads/incomplete",
      "Server1.Active": "yes", "Server1.Name": "fixture", "Server1.Host": "fixture", "Server1.Port": "1119",
      "Server1.Username": "e2e", "Server1.Password": "e2e", "Server1.Connections": "4", "Server1.Encryption": "no",
      "Category1.Name": "deluno-movies", "Category1.DestDir": MOVIES_DOWNLOADS,
      "Category2.Name": "deluno-tv", "Category2.DestDir": TV_DOWNLOADS
    };
    const current = await this.call("config") as Array<{ Name: string; Value: string }>;
    const byName = new Map(current.map((c) => [c.Name, c.Value]));
    if ([...Object.entries(wanted)].every(([name, value]) => byName.get(name) === value)) return;
    for (const [name, value] of Object.entries(wanted)) byName.set(name, value);
    // These four are what NZBGet reports about itself, not options: written back, it calls them invalid and pauses everything.
    for (const own of ["Version", "ConfigFile", "AppBin", "AppDir"]) byName.delete(own);
    const saved = await this.call("saveconfig", [[...byName.entries()].map(([Name, Value]) => ({ Name, Value }))]);
    if (saved !== true) throw new Error("NZBGet would not save its settings");
    await this.call("reload");
    await new Promise((resolve) => setTimeout(resolve, 3000));
    await until("NZBGet to be back after its reload", 120, async () => {
      const now = await this.call("config") as Array<{ Name: string; Value: string }>;
      return now.find((c) => c.Name === "Server1.Host")?.Value === "fixture";
    }, 2000);
  }

  async historyJob(release: string): Promise<{ Name: string; Status: string; DestDir: string; Category: string } | undefined> {
    const history = await this.call("history", [false]) as Array<{ Name: string; Status: string; DestDir: string; Category: string }>;
    return history.find((h) => h.Name === release);
  }

  async finished(film: FixtureFilm) {
    return /^SUCCESS/.test((await this.historyJob(film.release))?.Status ?? "");
  }
}

export { MOVIES_DOWNLOADS };
