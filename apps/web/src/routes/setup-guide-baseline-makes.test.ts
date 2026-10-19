import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());
const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: fetchJsonMock
}));
vi.mock("../lib/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api/client")>()),
  fetchJson: fetchJsonMock
}));
vi.mock("../lib/use-auth", () => ({
  authedFetch: authedFetchMock,
  useAuth: () => ({ user: null })
}));

const { createBaseline } = await import("./setup-guide-page");
type Input = Parameters<typeof createBaseline>[0];

const NO = {
  startAutomation: false, searchMissingTitles: false, rssGrabs: false, qualityUpgrades: false, renameOnImport: false, subtitleFetching: false,
  removeBadDownloads: false, deleteBadDownloadFiles: false, stallStrikes: false, leftoverCleanup: false, leftoverDownloadCleanup: false,
  forceImport: false, pauseSearchWhenQueueLong: false, dangerousFileCheck: false
};

const guide = {
  bundles: [{ id: "balanced-1080p", mediaType: "all", includes: [{ trashId: "t-web", score: 100 }] }],
  customFormats: [{ trashId: "t-web", name: "WEB Tier 1", mediaTypes: [], patterns: ["x"], originalScore: 50 }]
};

/** The guide's input with nothing connected yet and the answers all No; each test changes what it is about. */
function build(overrides: { data?: Record<string, unknown>; form?: Record<string, unknown> } & Omit<Partial<Input>, "data" | "form"> = {}): Input {
  const { data, form, ...rest } = overrides;
  return {
    data: {
      settings: { movieRootPath: "", seriesRootPath: "", hostBindAddress: "127.0.0.1", hostPort: 5099 },
      libraries: [], qualityProfiles: [], customFormats: [], indexers: [], clients: [], guide,
      updatePreferences: { mode: "ask", autoCheck: true, channel: "stable" },
      contentBlocklist: { enabled: false, mode: "block", patterns: [] },
      ...data
    } as unknown as Input["data"],
    form: {
      mediaIntent: "both",
      movieRootPath: "D:\\Media\\Movies", seriesRootPath: "D:\\Media\\TV", movieDownloadsPath: "", seriesDownloadsPath: "",
      qualityPreset: "balanced1080p", formatGoal: "balanced",
      indexerUrl: "", indexerApiKey: "", indexerProtocol: "torznab",
      clientName: "qBittorrent", clientProtocol: "qbittorrent", clientHost: "", clientPort: "8080", clientUsername: "", clientPassword: "",
      automation: NO, restrictAllowedHosts: false, skipSignInLocalNetwork: false, localNetworkAllowList: [], allowedHostsSelection: [], updateAutomatically: false,
      uiLanguage: "en-AU", calendarFirstDayOfWeek: "monday", shortDateFormat: "dmy", longDateFormat: "full", timeFormat: "12", timezone: "UTC",
      firstTitle: "",
      ...form
    } as unknown as Input["form"],
    liveClients: [] as unknown as Input["liveClients"],
    indexerTested: false,
    clientTested: false,
    hasMovies: true, hasTv: true, canCreateMovies: true, canCreateTv: true,
    ...rest
  };
}

type Called = { method?: string; body?: string };

/** The server: every create answers with what was sent plus an id, every test says healthy; `answer` overrides a route. */
function wire(answer: (url: string, init?: Called) => unknown = () => undefined) {
  let counter = 0;
  fetchJsonMock.mockImplementation(async (url: string, init?: Called) => {
    const custom = answer(url, init);
    if (custom !== undefined) return custom;
    const id = `${url.replace(/\W+/g, "-")}-${++counter}`;
    const body = init?.body ? JSON.parse(init.body) : {};
    if (url === "/api/custom-formats" || url === "/api/quality-profiles" || url === "/api/libraries" || url === "/api/indexers" || url === "/api/download-clients") return { id, ...body };
    if (url.endsWith("/test")) return { healthStatus: "healthy", message: "ok" };
    return {};
  });
  authedFetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({}), text: async () => "" });
}

const posts = (url: string) => fetchJsonMock.mock.calls.filter(([called, init]) => called === url && init?.method === "POST").map(([, init]) => JSON.parse(init.body));
const writesTo = (prefix: string) => fetchJsonMock.mock.calls.filter(([url, init]) => String(url).startsWith(prefix) && ["POST", "PUT"].includes(init?.method) && !String(url).endsWith("/test"));

afterEach(() => {
  fetchJsonMock.mockReset();
  authedFetchMock.mockReset();
});

describe("what the baseline makes, and what it reuses (guided setup, Start)", () => {
  it("makes each release rule once per media type from the chosen bundle, sending only the guide id and score, and reuses one that is already there", async () => {
    wire();
    await createBaseline(build(), []);
    expect(posts("/api/custom-formats")).toEqual([
      { name: "WEB Tier 1", mediaType: "movies", score: 100, trashId: "t-web", conditions: null, upgradeAllowed: true },
      { name: "WEB Tier 1", mediaType: "tv", score: 100, trashId: "t-web", conditions: null, upgradeAllowed: true }
    ]);

    fetchJsonMock.mockReset();
    wire();
    const created: Parameters<typeof createBaseline>[1] = [];
    await createBaseline(build({ data: { customFormats: [{ id: "cf-movies", trashId: "t-web", mediaType: "movies" }] } }), created);
    expect(posts("/api/custom-formats").map((body) => body.mediaType)).toEqual(["tv"]);
    expect(created.filter((entity) => entity.kind === "customFormat")).toHaveLength(1);
  });

  it("makes one profile per media type named for the preset and the goal, with its cutoff, allowed qualities and rules, and reuses one of that name", async () => {
    wire();
    await createBaseline(build(), []);
    const [movies, tv] = posts("/api/quality-profiles");
    expect(movies).toMatchObject({ name: "Balanced 1080p - Balanced library", mediaType: "movies", upgradeUntilCutoff: true, upgradeUnknownItems: false });
    expect(tv).toMatchObject({ name: "Balanced 1080p - Balanced library", mediaType: "tv" });
    expect(movies.cutoffQuality).toBeTruthy();
    expect(String(movies.allowedQualities).split(",")).toContain(movies.cutoffQuality);
    expect(String(movies.customFormatIds).split(",").filter(Boolean)).toHaveLength(1);

    fetchJsonMock.mockReset();
    wire();
    await createBaseline(build({ data: { qualityProfiles: [{ id: "p-movies", name: "Balanced 1080p - Balanced library", mediaType: "movies" }] } }), []);
    expect(posts("/api/quality-profiles").map((body) => body.mediaType)).toEqual(["tv"]);
  });

  it("allows upgrading unknown items only for Premium 4K, which also searches 15 titles a run instead of 10", async () => {
    wire();
    await createBaseline(build({ form: { qualityPreset: "premium4k" } }), []);
    expect(posts("/api/quality-profiles")[0]).toMatchObject({ name: "Premium 4K - Balanced library", upgradeUntilCutoff: true, upgradeUnknownItems: true });
    expect(posts("/api/libraries")[0].maxItemsPerRun).toBe(15);
  });

  it("makes Movies and TV Shows libraries with their folders, profile and search settings, and leaves a library that is already there alone", async () => {
    wire();
    await createBaseline(build(), []);
    const [movies, tv] = posts("/api/libraries");
    expect(movies).toMatchObject({ name: "Movies", mediaType: "movies", rootPath: "D:\\Media\\Movies", searchIntervalMinutes: 720, retryDelayHours: 6, maxItemsPerRun: 10, downloadsPath: null });
    expect(tv).toMatchObject({ name: "TV Shows", mediaType: "tv", rootPath: "D:\\Media\\TV", searchIntervalMinutes: 720, retryDelayHours: 6, maxItemsPerRun: 10 });
    expect(movies.qualityProfileId).toBeTruthy();
    expect(movies).toMatchObject({ autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false });

    fetchJsonMock.mockReset();
    wire();
    const result = await createBaseline(build({ data: { libraries: [{ id: "old-movies", name: "My Films", mediaType: "movies" }] } }), []);
    expect(posts("/api/libraries").map((body) => body.mediaType)).toEqual(["tv"]);
    expect(result.libraries[0]).toBe("My Films");
    expect(writesTo("/api/libraries/old-movies")).toHaveLength(0);
  });

  it("passes the automation answers to the new library", async () => {
    wire();
    await createBaseline(build({ form: { automation: { ...NO, searchMissingTitles: true, qualityUpgrades: false, leftoverCleanup: true } } }), []);

    expect(posts("/api/libraries")[0]).toMatchObject({ autoSearchEnabled: true, missingSearchEnabled: true, upgradeSearchEnabled: false, removeEmptySourceFolders: true });
  });
});

describe("the download client and the search source the baseline saves", () => {
  it("saves a tested client once, and re-tests the saved one", async () => {
    wire();
    const created: Parameters<typeof createBaseline>[1] = [];

    await createBaseline(build({ form: { clientHost: "10.0.0.5", clientPort: "8080", clientUsername: "admin", clientPassword: "secret" }, clientTested: true }), created);

    expect(posts("/api/download-clients")).toHaveLength(1);
    expect(posts("/api/download-clients")[0]).toMatchObject({ protocol: "qbittorrent", host: "10.0.0.5", port: 8080, username: "admin", password: "secret" });
    expect(created.filter((entity) => entity.kind === "client")).toHaveLength(1);
    expect(fetchJsonMock.mock.calls.some(([url, init]) => /^\/api\/download-clients\/.+\/test$/.test(String(url)) && init?.method === "POST")).toBe(true);
  });

  it("reuses a client already at that host and port, and writes to it only when a password or a different login was typed", async () => {
    const saved = { id: "c1", protocol: "qbittorrent", host: "10.0.0.5", port: 8080, username: "admin", healthStatus: "healthy", isEnabled: true, name: "qBittorrent" };
    const same = { data: { clients: [saved] }, clientTested: true, liveClients: [saved] as unknown as Input["liveClients"] };
    wire();
    const created: Parameters<typeof createBaseline>[1] = [];
    await createBaseline(build({ ...same, form: { clientHost: "10.0.0.5", clientPort: "8080", clientUsername: "admin", clientPassword: "" } }), created);
    expect(writesTo("/api/download-clients")).toHaveLength(0);
    expect(created.some((entity) => entity.kind === "client")).toBe(false);

    fetchJsonMock.mockReset();
    wire();
    await createBaseline(build({ ...same, form: { clientHost: "10.0.0.5", clientPort: "8080", clientUsername: "admin", clientPassword: "new-secret" } }), []);
    expect(writesTo("/api/download-clients").map(([url, init]) => `${init.method} ${url}`)).toEqual(["PUT /api/download-clients/c1"]);
  });

  it("fails, naming the server's reason, when the saved client does not pass its own test", async () => {
    wire((url) => (url.endsWith("/test") ? { healthStatus: "unreachable", message: "qBittorrent refused the login." } : undefined));

    await expect(createBaseline(build({ form: { clientHost: "10.0.0.5", clientPort: "8080" }, clientTested: true }), [])).rejects.toThrow("qBittorrent refused the login.");
  });

  it("will not save a search source that was not tested", async () => {
    wire();

    await expect(createBaseline(build({ form: { indexerUrl: "http://indexer.example/api" }, indexerTested: false }), [])).rejects.toThrow("Test the search source before saving it.");
    expect(writesTo("/api/indexers")).toHaveLength(0);
  });

  it("creates a tested search source when none exists, and updates the first one when one does", async () => {
    wire();
    const created: Parameters<typeof createBaseline>[1] = [];
    await createBaseline(build({ form: { indexerUrl: "http://indexer.example/api" }, indexerTested: true }), created);
    expect(posts("/api/indexers")).toHaveLength(1);
    expect(created.some((entity) => entity.kind === "indexer")).toBe(true);

    fetchJsonMock.mockReset();
    wire();
    const again: Parameters<typeof createBaseline>[1] = [];
    await createBaseline(build({ data: { indexers: [{ id: "ix1", name: "Old" }] }, form: { indexerUrl: "http://indexer.example/api" }, indexerTested: true }), again);
    expect(writesTo("/api/indexers").map(([url, init]) => `${init.method} ${url}`)).toEqual(["PUT /api/indexers/ix1"]);
    expect(again.some((entity) => entity.kind === "indexer")).toBe(false);
  });

  it("needs the saved search source to be healthy, and says what the server said when it is not", async () => {
    wire((url) => (url.endsWith("/test") ? { healthStatus: "degraded", message: "The key was refused." } : undefined));

    await expect(createBaseline(build({ form: { indexerUrl: "http://indexer.example/api" }, indexerTested: true }), [])).rejects.toThrow("The key was refused.");
  });

  it("a refusal from Allowed Hosts does not stop the baseline: the message names the addresses that could not be saved", async () => {
    wire((url) => { if (url === "/api/security/allowed-hosts") throw new Error("That would lock you out."); return undefined; });

    const result = await createBaseline(build({ form: { restrictAllowedHosts: true, allowedHostsSelection: ["other.example"] } }), []);

    expect(result.message).toContain("These answers could not be saved, so set them in Settings: which addresses Deluno answers on");
  });
});

describe("what the baseline refuses, and the first title bonus", () => {
  it("refuses with 'Choose a picture quality' when no picture quality is chosen and no profile exists to reuse, and makes nothing", async () => {
    wire();
    await expect(createBaseline(build({ form: { qualityPreset: "" } }), [])).rejects.toThrow("Choose a picture quality before Deluno can create a quality profile.");
    expect(posts("/api/quality-profiles")).toEqual([]);
  });

  it("refuses with 'Choose a release preference' when a picture quality is chosen without a release preference", async () => {
    wire();
    await expect(createBaseline(build({ form: { formatGoal: "" } }), [])).rejects.toThrow("Choose a release preference before Deluno can create release rules.");
    expect(posts("/api/quality-profiles")).toEqual([]);
  });

  it("reuses the profile already there instead of refusing when nothing is chosen", async () => {
    wire();
    await createBaseline(build({ form: { qualityPreset: "" }, data: { qualityProfiles: [{ id: "p-movies", name: "Mine", mediaType: "movies" }, { id: "p-tv", name: "Mine TV", mediaType: "tv" }] } }), []);
    expect(posts("/api/quality-profiles")).toEqual([]);
  });

  it("adds the first title after the baseline and says so in the result", async () => {
    wire();
    authedFetchMock.mockImplementation(async (url: string, init?: { body?: string }) =>
      url === "/api/movies"
        ? { ok: true, status: 200, json: async () => ({ id: "movie-1", title: "Heat" }), text: async () => "" }
        : { ok: true, status: 200, json: async () => ({}), text: async () => "" });
    const created: Parameters<typeof createBaseline>[1] = [];

    const result = await createBaseline(build({ form: { firstTitle: "Heat", firstTitleType: "movies", firstTitleYear: "1995", firstTitleMonitored: true, firstTitleMetadata: null } }), created);

    const call = authedFetchMock.mock.calls.find(([url]) => url === "/api/movies")!;
    expect(JSON.parse(call[1].body)).toMatchObject({ title: "Heat", releaseYear: 1995, monitored: true });
    expect(result.message).toContain('baseline created and "Heat" was added.');
    expect(created.some((entity) => entity.kind === "movie" && entity.id === "movie-1")).toBe(true);
    expect(authedFetchMock.mock.invocationCallOrder[authedFetchMock.mock.calls.findIndex(([url]) => url === "/api/movies")]).toBeGreaterThan(fetchJsonMock.mock.invocationCallOrder[0]);
  });

  it("reports a first title that could not be added without undoing the baseline", async () => {
    wire();
    authedFetchMock.mockImplementation(async (url: string) =>
      url === "/api/movies"
        ? { ok: false, status: 409, json: async () => ({ message: "That movie is already there." }), text: async () => JSON.stringify({ message: "That movie is already there." }) }
        : { ok: true, status: 200, json: async () => ({}), text: async () => "" });
    const created: Parameters<typeof createBaseline>[1] = [];

    const result = await createBaseline(build({ form: { firstTitle: "Heat", firstTitleType: "movies", firstTitleYear: "", firstTitleMonitored: true, firstTitleMetadata: null } }), created);

    expect(result.message).toContain("baseline created.");
    expect(result.message).toContain("The first title was not added:");
    expect(result.libraries.length).toBe(2);
    expect(created.some((entity) => entity.kind === "library")).toBe(true);
    expect(created.some((entity) => entity.kind === "movie")).toBe(false);
  });
});
