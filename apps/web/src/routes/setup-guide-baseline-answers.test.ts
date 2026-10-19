import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());
const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api")>()), fetchJson: fetchJsonMock }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api/client")>()), fetchJson: fetchJsonMock }));
vi.mock("../lib/use-auth", () => ({ authedFetch: authedFetchMock, useAuth: () => ({ user: null }) }));

const page = await import("./setup-guide-page");
const { createBaseline, buildAutomationSettingsPayload, buildLibraryAutomationFields, buildIndexerAutomationFields } = page;
type Input = Parameters<typeof createBaseline>[0];
type Choices = Parameters<typeof buildAutomationSettingsPayload>[0];

const KEYS = [
  "startAutomation", "searchMissingTitles", "rssGrabs", "qualityUpgrades", "renameOnImport", "subtitleFetching", "removeBadDownloads",
  "deleteBadDownloadFiles", "stallStrikes", "leftoverCleanup", "leftoverDownloadCleanup", "forceImport", "pauseSearchWhenQueueLong", "dangerousFileCheck"
] as const;
const choices = (over: Partial<Record<(typeof KEYS)[number], boolean | null>> = {}, fill: boolean | null = false) =>
  ({ ...Object.fromEntries(KEYS.map((key) => [key, fill])), ...over }) as Choices;
const NO = choices();

const guide = {
  bundles: [{ id: "balanced-1080p", mediaType: "all", includes: [{ trashId: "t-web", score: 100 }] }],
  customFormats: [{ trashId: "t-web", name: "WEB Tier 1", mediaTypes: [], patterns: ["x"], originalScore: 50 }]
};

function build(overrides: { data?: Record<string, unknown>; form?: Record<string, unknown> } & Omit<Partial<Input>, "data" | "form"> = {}): Input {
  const { data, form, ...rest } = overrides;
  return {
    data: {
      settings: { movieRootPath: "", seriesRootPath: "", hostBindAddress: "127.0.0.1", hostPort: 5099, uiLanguage: "en-US", timezone: "Europe/London" },
      libraries: [], qualityProfiles: [], customFormats: [], indexers: [], clients: [], guide,
      updatePreferences: { mode: "ask", autoCheck: true, channel: "stable" },
      contentBlocklist: { enabled: false, mode: "block", patterns: [] },
      allowedHosts: { enabled: false, allowedHosts: [] },
      localNetworkAuth: { enabled: false, additionalLocalNetworks: [], trustedProxies: [], allowList: [] },
      ...data
    } as unknown as Input["data"],
    form: {
      mediaIntent: "both", movieRootPath: "D:\\Media\\Movies", seriesRootPath: "D:\\Media\\TV", movieDownloadsPath: "", seriesDownloadsPath: "",
      qualityPreset: "balanced1080p", formatGoal: "balanced", indexerUrl: "", indexerApiKey: "", indexerProtocol: "torznab",
      clientName: "qBittorrent", clientProtocol: "qbittorrent", clientHost: "", clientPort: "8080", clientUsername: "", clientPassword: "",
      automation: NO, restrictAllowedHosts: false, skipSignInLocalNetwork: false, localNetworkAllowList: [], allowedHostsSelection: [], updateAutomatically: false,
      uiLanguage: "en-AU", calendarFirstDayOfWeek: "monday", shortDateFormat: "dmy", longDateFormat: "full", timeFormat: "12", timezone: "UTC",
      firstTitle: "", firstTitleType: "movies", firstTitleYear: "", firstTitleMonitored: true,
      ...form
    } as unknown as Input["form"],
    liveClients: [] as unknown as Input["liveClients"],
    indexerTested: false, clientTested: false, hasMovies: true, hasTv: true, canCreateMovies: true, canCreateTv: true,
    ...rest
  };
}

type Called = { method?: string; body?: string };
function wire(answer: (url: string, init?: Called) => unknown = () => undefined, authed: (url: string, init?: Called) => unknown = () => undefined) {
  let counter = 0;
  fetchJsonMock.mockImplementation(async (url: string, init?: Called) => {
    const custom = answer(url, init);
    if (custom !== undefined) return custom;
    const id = `${url.replace(/\W+/g, "-")}-${++counter}`;
    const body = init?.body ? JSON.parse(init.body) : {};
    if (init?.method === "POST" && ["/api/custom-formats", "/api/quality-profiles", "/api/libraries", "/api/indexers", "/api/download-clients"].includes(url)) return { id, ...body };
    if (url.endsWith("/test")) return { healthStatus: "healthy", message: "ok" };
    if (url === "/api/download-health/rules") return [];
    return {};
  });
  authedFetchMock.mockImplementation(async (url: string, init?: Called) => {
    const custom = authed(url, init);
    if (custom !== undefined) return custom;
    return { ok: true, status: 200, json: async () => ({ id: "title-1", title: "A Title" }), text: async () => "" };
  });
}
const calls = (method: string, url: string) => fetchJsonMock.mock.calls.filter(([called, init]) => called === url && init?.method === method).map(([, init]) => JSON.parse(init.body));
const authedCalls = (method: string, urlStart: string) => authedFetchMock.mock.calls.filter(([url, init]) => String(url).startsWith(urlStart) && init?.method === method);

afterEach(() => { fetchJsonMock.mockReset(); authedFetchMock.mockReset(); });

describe("each of the fourteen Automation questions, answered Yes, sets exactly its own settings", () => {
  const flat = (automation: Choices) => {
    const out: string[] = [];
    for (const [group, values] of [["settings", buildAutomationSettingsPayload(automation)], ["library", buildLibraryAutomationFields(automation)], ["indexer", buildIndexerAutomationFields(automation)]] as const) {
      for (const [name, value] of Object.entries(values)) if (value === true && !name.startsWith("downloadHealthMinSpeedKbps")) out.push(`${group}.${name}`);
    }
    return out.sort();
  };

  it("all No turns nothing on", () => {
    expect(flat(NO)).toEqual([]);
  });

  it.each([
    ["startAutomation", ["settings.autoStartJobs"]],
    ["searchMissingTitles", ["indexer.automaticSearchEnabled", "library.autoSearchEnabled", "library.missingSearchEnabled"]],
    ["rssGrabs", ["indexer.rssEnabled"]],
    ["qualityUpgrades", ["indexer.automaticSearchEnabled", "library.autoSearchEnabled", "library.upgradeSearchEnabled"]],
    ["renameOnImport", ["settings.renameOnImport"]],
    ["subtitleFetching", ["settings.subtitleFetchAutomaticEnabled"]],
    ["removeBadDownloads", ["settings.cleanupRemoveClientEntryAfterThreshold"]],
    ["deleteBadDownloadFiles", ["settings.cleanupPurgePayloadAfterThreshold"]],
    ["stallStrikes", ["settings.cleanupBlockReleaseAfterThreshold", "settings.cleanupQueueReplacementAfterThreshold", "settings.cleanupRemoveClientEntryAfterThreshold", "settings.downloadHealthMinSpeedEnabled", "settings.downloadHealthNoProgressEnabled"]],
    ["leftoverCleanup", ["library.removeEmptySourceFolders"]],
    ["leftoverDownloadCleanup", ["settings.leftoverDownloadCleanupEnabled"]],
    ["forceImport", ["settings.forceImportEnabled"]],
    ["pauseSearchWhenQueueLong", ["settings.pauseSearchWhenQueueLongEnabled"]],
    ["dangerousFileCheck", []]
  ] as const)("%s", (key, expected) => {
    expect(flat(choices({ [key]: true }))).toEqual([...expected].sort());
  });

  it("an unanswered question turns on nothing, like a No", () => {
    expect(flat(choices({}, null))).toEqual([]);
  });

  it("the dangerous-file check Yes is written to its own endpoint with the other fields kept, and a No switches it off again", async () => {
    wire();
    const blocklist = { enabled: true, mode: "block", patterns: ["*.exe"] };
    await createBaseline(build({ data: { contentBlocklist: blocklist }, form: { automation: choices({ dangerousFileCheck: false }) } }), []);
    expect(calls("PUT", "/api/download-health/content-blocklist")).toEqual([{ enabled: false, mode: "block", patterns: ["*.exe"] }]);
    fetchJsonMock.mockClear();
    await createBaseline(build({ data: { contentBlocklist: { ...blocklist, enabled: false } }, form: { automation: choices({ dangerousFileCheck: true }) } }), []);
    expect(calls("PUT", "/api/download-health/content-blocklist")).toEqual([{ enabled: true, mode: "block", patterns: ["*.exe"] }]);
  });
});

describe("the answers a baseline applies through the API", () => {
  it("all Yes: the settings, both new libraries and the search source carry their own switches, with the stall numbers the question showed", async () => {
    wire();
    await createBaseline(build({ form: { automation: choices({}, true), indexerUrl: "https://indexer.example/api", indexerApiKey: "k" }, indexerTested: true }), []);

    const settings = calls("PUT", "/api/settings")[0];
    expect(settings).toMatchObject({
      autoStartJobs: true, renameOnImport: true, subtitleFetchAutomaticEnabled: true, cleanupPurgePayloadAfterThreshold: true,
      cleanupRemoveClientEntryAfterThreshold: true, leftoverDownloadCleanupEnabled: true, forceImportEnabled: true, pauseSearchWhenQueueLongEnabled: true,
      downloadHealthNoProgressEnabled: true, downloadHealthMinSpeedEnabled: true, downloadHealthMinSpeedKbps: 50, downloadHealthStrikeThreshold: 3
    });
    for (const library of calls("POST", "/api/libraries")) {
      expect(library).toMatchObject({ autoSearchEnabled: true, missingSearchEnabled: true, upgradeSearchEnabled: true, removeEmptySourceFolders: true });
    }
    expect(calls("POST", "/api/libraries")).toHaveLength(2);
    expect(calls("POST", "/api/indexers")[0]).toMatchObject({ rssEnabled: true, automaticSearchEnabled: true });
  });

  it("all No: the libraries and the search source are made with every switch off, and the scheduler stays off", async () => {
    wire();
    await createBaseline(build({ form: { indexerUrl: "https://indexer.example/api", indexerApiKey: "k" }, indexerTested: true }), []);
    expect(calls("PUT", "/api/settings")[0]).toMatchObject({ autoStartJobs: false, renameOnImport: false, cleanupPurgePayloadAfterThreshold: false, forceImportEnabled: false });
    for (const library of calls("POST", "/api/libraries")) {
      expect(library).toMatchObject({ autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false, removeEmptySourceFolders: false });
    }
    expect(calls("POST", "/api/indexers")[0]).toMatchObject({ rssEnabled: false, automaticSearchEnabled: false });
  });

  it("on a re-run a library that is already there is left alone, so the answers do not reach it", async () => {
    wire();
    const existing = [{ id: "m1", name: "Movies", mediaType: "movies" }, { id: "t1", name: "TV Shows", mediaType: "tv" }];
    await createBaseline(build({ data: { libraries: existing }, form: { automation: choices({}, true) } }), []);
    expect(calls("POST", "/api/libraries")).toEqual([]);
    expect(fetchJsonMock.mock.calls.filter(([url]) => String(url).startsWith("/api/libraries/"))).toEqual([]);
  });

  it("on a re-run an existing search source is updated with the same payload, answers included", async () => {
    wire();
    await createBaseline(build({
      data: { indexers: [{ id: "i1", name: "Mine" }] },
      form: { automation: choices({ rssGrabs: true }), indexerUrl: "https://indexer.example/api", indexerApiKey: "k" },
      indexerTested: true
    }), []);
    expect(calls("PUT", "/api/indexers/i1")[0]).toMatchObject({ rssEnabled: true, automaticSearchEnabled: false });
    expect(calls("POST", "/api/indexers")).toEqual([]);
  });
});

describe("time and region", () => {
  it("writes the six values the person confirmed beside the automation settings", async () => {
    wire();
    await createBaseline(build({ form: { uiLanguage: "en-US", calendarFirstDayOfWeek: "sunday", shortDateFormat: "mdy", longDateFormat: "short", timeFormat: "24", timezone: "America/Chicago", automation: choices({ startAutomation: true }) } }), []);
    expect(calls("PUT", "/api/settings")[0]).toMatchObject({
      uiLanguage: "en-US", calendarFirstDayOfWeek: "sunday", shortDateFormat: "mdy", longDateFormat: "short", timeFormat: "24", timezone: "America/Chicago", autoStartJobs: true
    });
  });

  it("falls back to the loaded setting for a value left blank", async () => {
    wire();
    await createBaseline(build({ form: { uiLanguage: "", timezone: "" } }), []);
    expect(calls("PUT", "/api/settings")[0]).toMatchObject({ uiLanguage: "en-US", timezone: "Europe/London" });
  });

  it("sends a timezone the person typed as it is and still completes, leaving the server to normalise it", async () => {
    wire();
    const result = await createBaseline(build({ form: { timezone: "Not/AZone" } }), []);
    expect(calls("PUT", "/api/settings")[0].timezone).toBe("Not/AZone");
    expect(result.message).toContain("baseline created");
  });
});

describe("re-running the guide with the sign-in and address answers", () => {
  it("re-running the guide keeps the Security page's extra local networks and trusted proxies", async () => {
    wire();
    const loaded = { enabled: true, additionalLocalNetworks: ["10.9.0.0/16"], trustedProxies: ["192.168.1.5"] };
    await createBaseline(build({ data: { localNetworkAuth: loaded }, form: { skipSignInLocalNetwork: true } }), []);
    const body = calls("PUT", "/api/security/local-network-auth")[0];
    expect(body.additionalLocalNetworks).toEqual(["10.9.0.0/16"]);
    expect(body.trustedProxies).toEqual(["192.168.1.5"]);
  });

  it("a No to Only answer on the addresses you use switches a current Yes off", async () => {
    wire();
    await createBaseline(build({ data: { allowedHosts: { enabled: true, allowedHosts: ["192.168.1.10"] } }, form: { restrictAllowedHosts: false } }), []);
    expect(calls("PUT", "/api/security/allowed-hosts")).toEqual([{ enabled: false, allowedHosts: [] }]);
  });

  it("a Yes writes enabled with the ticked addresses, and a No to skipping sign-in writes that setting off", async () => {
    wire();
    await createBaseline(build({ form: { restrictAllowedHosts: true, allowedHostsSelection: ["192.168.1.10"], skipSignInLocalNetwork: false } }), []);
    expect(calls("PUT", "/api/security/allowed-hosts")).toEqual([{ enabled: true, allowedHosts: ["192.168.1.10"] }]);
    expect(calls("PUT", "/api/security/local-network-auth")[0].enabled).toBe(false);
  });
});

describe("routing each new library", () => {
  it("sends the search source first and every connected client in order, for Movies and for TV", async () => {
    wire();
    const clients = [
      { id: "c1", name: "qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8081, isEnabled: true, healthStatus: "healthy" },
      { id: "c2", name: "SABnzbd", protocol: "sabnzbd", host: "localhost", port: 8085, isEnabled: true, healthStatus: "healthy" }
    ];
    await createBaseline(build({
      form: { indexerUrl: "https://indexer.example/api", indexerApiKey: "k" }, indexerTested: true,
      liveClients: clients as unknown as Input["liveClients"]
    }), []);
    const routes = authedFetchMock.mock.calls.filter(([url, init]) => /\/routing$/.test(String(url)) && init?.method === "PUT");
    expect(routes).toHaveLength(2);
    for (const [, init] of routes) {
      const body = JSON.parse(init.body);
      expect(body.sources).toHaveLength(1);
      expect(body.sources[0]).toMatchObject({ priority: 1 });
      expect(body.downloadClients).toEqual([{ downloadClientId: "c1", priority: 1 }, { downloadClientId: "c2", priority: 2 }]);
    }
  });
});

describe("the first title", () => {
  it("a Movie is posted to /api/movies with its year and the Monitor tick", async () => {
    wire();
    const result = await createBaseline(build({ form: { firstTitle: "  Heat ", firstTitleType: "movies", firstTitleYear: "1995", firstTitleMonitored: false } }), []);
    const [, init] = authedFetchMock.mock.calls.find(([url]) => url === "/api/movies")!;
    expect(JSON.parse(init.body)).toMatchObject({ title: "Heat", releaseYear: 1995, monitored: false, imdbId: null });
    expect(result).toMatchObject({ firstTitle: "A Title", firstTitlePath: "/movies" });
    expect(result.message).toContain('and "A Title" was added');
  });

  it("a TV show is posted to /api/series with its start year and the metadata the person picked", async () => {
    wire();
    const metadata = { provider: "tmdb", providerId: "1", imdbId: "tt0000001", title: "Show", originalTitle: "Show", overview: "o", posterUrl: "p", backdropUrl: null, rating: 8, genres: ["Drama"], externalUrl: null, libraryEntryId: "x" };
    await createBaseline(build({ form: { firstTitle: "Show", firstTitleType: "tv", firstTitleYear: "2020", firstTitleMonitored: true, firstTitleMetadata: metadata } }), []);
    const [, init] = authedFetchMock.mock.calls.find(([url]) => url === "/api/series")!;
    const body = JSON.parse(init.body);
    expect(body).toMatchObject({ title: "Show", startYear: 2020, imdbId: "tt0000001", monitored: true, metadataProvider: "tmdb", metadataProviderId: "1" });
    expect(body).not.toHaveProperty("libraryEntryId");
  });

  it("when the title cannot be added the baseline stands and the message says why", async () => {
    wire(undefined, (url) => (url === "/api/movies" ? { ok: false, status: 409, json: async () => ({ message: "Heat is already in your library." }), text: async () => JSON.stringify({ message: "Heat is already in your library." }) } : undefined));
    const result = await createBaseline(build({ form: { firstTitle: "Heat" } }), []);
    expect(result.libraries).toEqual(["Movies", "TV Shows"]);
    expect(result.firstTitle).toBeNull();
    expect(result.message).toContain("baseline created.");
    expect(result.message).toContain("The first title was not added:");
  });

  it("no title typed adds nothing", async () => {
    wire();
    await createBaseline(build(), []);
    expect(authedFetchMock.mock.calls.some(([url]) => url === "/api/movies" || url === "/api/series")).toBe(false);
  });
});

describe("the completion message", () => {
  it("lists every answer that could not be saved, with the first-title problem, in one message", async () => {
    wire(
      (url) => {
        if (url === "/api/updates/preferences" || url === "/api/download-health/content-blocklist") throw new Error("refused");
        return undefined;
      },
      (url) => (url === "/api/series" ? { ok: false, status: 500, json: async () => ({}), text: async () => "" } : undefined)
    );
    const result = await createBaseline(build({ form: { firstTitle: "Show", firstTitleType: "tv", updateAutomatically: true, automation: choices({ dangerousFileCheck: true }) } }), []);
    expect(result.message).toContain("The first title was not added:");
    expect(result.message).toContain("These answers could not be saved, so set them in Settings: how Deluno updates, the dangerous-file check.");
  });

  it("says Deluno could not tell where the clients finish when a connected client gives no folder", async () => {
    wire();
    const clients = [{ id: "c1", name: "qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8081, isEnabled: true, healthStatus: "healthy" }];
    const result = await createBaseline(build({ liveClients: clients as unknown as Input["liveClients"] }), []);
    expect(result.message).toContain("Deluno could not tell where your download clients finish Movies and TV, so no downloads folder is set yet.");
  });

  it("names what was made and records it in the audit event, then removes the saved draft", async () => {
    wire();
    const result = await createBaseline(build(), []);
    expect(result.message.startsWith("Movies and TV Shows baseline created.")).toBe(true);
    expect(authedFetchMock.mock.calls.some(([url, init]) => url === "/api/setup/completed" && init?.method === "POST")).toBe(true);
    expect(authedCalls("DELETE", "/api/setup/draft")).toHaveLength(1);
  });
});

describe("re-running the guide changes only what the person answers (#1357)", () => {
  const ALL_ON = {
    autoStartJobs: true, renameOnImport: true, subtitleFetchAutomaticEnabled: true, cleanupRemoveClientEntryAfterThreshold: true, cleanupPurgePayloadAfterThreshold: true,
    downloadHealthNoProgressEnabled: true, downloadHealthMinSpeedEnabled: true, leftoverDownloadCleanupEnabled: true, forceImportEnabled: true, pauseSearchWhenQueueLongEnabled: true,
    downloadHealthMinSpeedKbps: 120, downloadHealthStrikeThreshold: 7, downloadHealthDefaultStrikeWindowMinutes: 45
  };
  const library = { id: "lib-m", name: "Movies", mediaType: "movies", rootPath: "D:\Media\Movies", missingSearchEnabled: true, upgradeSearchEnabled: true, removeEmptySourceFolders: true };
  const indexer = { id: "ix1", name: "Source", priority: 3, categories: "2000", tags: "mine", rssEnabled: true, isEnabled: true };
  const client = { id: "c1", name: "qB", protocol: "qbittorrent", host: "localhost", port: 8081, username: "old", isEnabled: true, healthStatus: "healthy", moviesCategory: "my-films", tvCategory: "my-tv", categoryTemplate: "t", endpointUrl: "http://x", priority: 4 };
  const live = {
    settings: { movieRootPath: "D:\Media\Movies", seriesRootPath: "D:\Media\TV", hostBindAddress: "127.0.0.1", hostPort: 5099, uiLanguage: "en-US", timezone: "UTC", ...ALL_ON },
    libraries: [library], indexers: [indexer], clients: [client],
    contentBlocklist: { enabled: true, mode: "block", patterns: ["*.exe"] },
    updatePreferences: { mode: "download-apply-on-restart", autoCheck: true, channel: "beta" },
    allowedHosts: { enabled: false, allowedHosts: [] },
    localNetworkAuth: { enabled: true, additionalLocalNetworks: ["10.9.0.0/16"], trustedProxies: ["192.168.1.5"], allowList: ["192.168.1.20"] }
  };

  it("reads each of the fourteen answers from the live settings", () => {
    expect(page.liveAutomationAnswers(live as never)).toEqual(choices({}, true));
    const off = page.liveAutomationAnswers({ settings: {}, libraries: [], indexers: [], contentBlocklist: { enabled: false } } as never);
    expect(off).toEqual(choices({}, false));
  });

  it("every step that saves writes back what it loaded when the answers are the ones already in force", async () => {
    wire((url) => (url === "/api/download-health/rules"
      ? [{ id: "r1", kind: "no-progress", enabled: true, strikeLimit: 7 }, { id: "r2", kind: "too-slow", enabled: true, strikeLimit: 7, threshold: 120 }]
      : undefined));
    await createBaseline(build({
      data: live,
      liveClients: [client] as unknown as Input["liveClients"],
      indexerTested: true, clientTested: true,
      form: {
        automation: page.liveAutomationAnswers(live as never), restrictAllowedHosts: false, skipSignInLocalNetwork: true, localNetworkAllowList: ["192.168.1.20"],
        updateAutomatically: true, indexerUrl: "https://indexer.example/api", clientHost: "localhost", clientPort: "8081", clientUsername: "new", clientPassword: "pw"
      }
    }), []);
    const settings = calls("PUT", "/api/settings")[0];
    expect(settings).toMatchObject(ALL_ON);
    expect(calls("PUT", "/api/updates/preferences")).toEqual([{ mode: "download-apply-on-restart", channel: "beta", autoCheck: true }]);
    expect(calls("PUT", "/api/security/allowed-hosts")).toEqual([]);
    expect(calls("PUT", "/api/security/local-network-auth")[0]).toMatchObject({ enabled: true, additionalLocalNetworks: ["10.9.0.0/16"], trustedProxies: ["192.168.1.5"], allowList: ["192.168.1.20"] });
    expect(calls("PUT", "/api/download-health/content-blocklist")).toEqual([{ ...live.contentBlocklist, enabled: true }]);
    expect(calls("PUT", "/api/download-health/rules/r1")).toEqual([]);
    expect(calls("PUT", "/api/download-health/rules/r2")).toEqual([]);
    expect(calls("PUT", "/api/indexers/ix1")[0]).toMatchObject({ priority: 3, categories: "2000", tags: "mine", rssEnabled: true });
    expect(calls("PUT", "/api/download-clients/c1")[0]).toMatchObject({ moviesCategory: "my-films", tvCategory: "my-tv", categoryTemplate: "t", endpointUrl: "http://x", priority: 4 });
  });
});
