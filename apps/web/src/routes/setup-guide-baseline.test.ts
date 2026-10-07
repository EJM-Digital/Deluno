import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());
const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: fetchJsonMock
}));
vi.mock("../lib/use-auth", () => ({
  authedFetch: authedFetchMock,
  useAuth: () => ({ user: null })
}));

const { createBaseline } = await import("./setup-guide-page");
type Input = Parameters<typeof createBaseline>[0];

const NO = {
  searchMissingTitles: false, rssGrabs: false, qualityUpgrades: false, renameOnImport: false, subtitleFetching: false,
  removeBadDownloads: false, deleteBadDownloadFiles: false, leftoverCleanup: false, leftoverDownloadCleanup: false,
  forceImport: false, pauseSearchWhenQueueLong: false, dangerousFileCheck: false
};

// The five clients the component picker installed, connected with "Connect all" (#1132).
const installerClients = [
  { id: "qb", name: "qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8081, isEnabled: true, healthStatus: "healthy" },
  { id: "sab", name: "SABnzbd", protocol: "sabnzbd", host: "localhost", port: 8085, isEnabled: true, healthStatus: "healthy" }
];

function input(overrides: Partial<Input> = {}): Input {
  return {
    data: {
      settings: { movieRootPath: "", seriesRootPath: "", hostBindAddress: "127.0.0.1", hostPort: 5099 },
      libraries: [],
      qualityProfiles: [],
      customFormats: [],
      indexers: [],
      clients: installerClients,
      guide: { bundles: [] },
      updatePreferences: { mode: "ask", autoCheck: true, channel: "stable" },
      contentBlocklist: { enabled: false, mode: "block", patterns: [] }
    } as unknown as Input["data"],
    form: {
      mediaIntent: "both",
      movieRootPath: "D:\\Media\\Movies",
      seriesRootPath: "D:\\Media\\TV",
      qualityPreset: "balanced1080p",
      formatGoal: "simpleClean",
      indexerUrl: "",
      // The form still holds its blank default (qBittorrent at localhost:8080), untested.
      clientName: "qBittorrent",
      clientProtocol: "qbittorrent",
      clientHost: "localhost",
      clientPort: "8080",
      clientUsername: "",
      clientPassword: "",
      automation: NO,
      restrictAllowedHosts: false,
      skipSignInLocalNetwork: false,
      localNetworkAllowList: [],
      updateAutomatically: false,
      uiLanguage: "en-AU", calendarFirstDayOfWeek: "monday", shortDateFormat: "dmy", longDateFormat: "full", timeFormat: "12", timezone: "UTC",
      firstTitle: ""
    } as unknown as Input["form"],
    liveClients: installerClients as unknown as Input["liveClients"],
    indexerTested: false,
    clientTested: false,
    hasMovies: true,
    hasTv: true,
    canCreateMovies: true,
    canCreateTv: true,
    ...overrides
  };
}

describe("guided setup after installing with the picker (#1132)", () => {
  afterEach(() => {
    fetchJsonMock.mockReset();
    authedFetchMock.mockReset();
  });

  function wire() {
    let libraryCount = 0;
    fetchJsonMock.mockImplementation(async (url: string, init?: { method?: string }) => {
      if (url === "/api/quality-profiles") return { id: "qp1", name: "Balanced 1080p - Just make it work", mediaType: "movies" };
      if (url === "/api/libraries") return { id: `lib${++libraryCount}`, name: "Library" };
      if (url === "/api/settings") return {};
      return {};
    });
    authedFetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({}), text: async () => "" });
  }

  it("creates the baseline and routes Movies and TV to the connected clients without testing the blank form", async () => {
    wire();
    const created: Parameters<typeof createBaseline>[1] = [];

    const result = await createBaseline(input(), created);

    expect(result.libraries.length).toBe(2);
    expect(result.clientName).toBe("qBittorrent, SABnzbd");

    // No download client was created or changed, and none was tested.
    const clientCalls = fetchJsonMock.mock.calls.filter(([url]) => String(url).startsWith("/api/download-clients"));
    expect(clientCalls).toEqual([]);

    // Both libraries were routed to both connected clients, in order.
    const routing = authedFetchMock.mock.calls.filter(([url]) => String(url).endsWith("/routing"));
    expect(routing).toHaveLength(2);
    for (const [, init] of routing) {
      expect(JSON.parse(init.body).downloadClients).toEqual([
        { downloadClientId: "qb", priority: 1 },
        { downloadClientId: "sab", priority: 2 }
      ]);
    }
  });

  it("applies the settings only after everything else worked, so a refusal leaves none of the answers applied", async () => {
    wire();
    fetchJsonMock.mockImplementation(async (url: string) => {
      if (url === "/api/quality-profiles") return { id: "qp1", name: "p", mediaType: "movies" };
      if (url === "/api/libraries") return { id: "lib1", name: "Library" };
      if (url === "/api/settings") throw new Error("Settings could not be saved.");
      return {};
    });
    const created: Parameters<typeof createBaseline>[1] = [];

    await expect(createBaseline(input(), created)).rejects.toThrow("Settings could not be saved.");

    // The libraries made before the refusal are in the list so they can be taken back.
    expect(created.some((entity) => entity.kind === "library")).toBe(true);
    // Nothing that writes an answer ran after the refusal.
    const urls = fetchJsonMock.mock.calls.map(([url]) => String(url));
    expect(urls.indexOf("/api/settings")).toBeGreaterThan(urls.indexOf("/api/libraries"));
    expect(urls.some((url) => url.startsWith("/api/updates/preferences"))).toBe(false);
  });
});
