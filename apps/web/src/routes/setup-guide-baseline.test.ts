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

const guide = await import("./setup-guide-page");
const { createBaseline, detectLibraryDownloadsFolder } = guide;
type Input = Parameters<typeof createBaseline>[0];

const NO = {
  startAutomation: false, searchMissingTitles: false, rssGrabs: false, qualityUpgrades: false, renameOnImport: false, subtitleFetching: false,
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
      movieDownloadsPath: "",
      seriesDownloadsPath: "",
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
      if (url.startsWith("/api/integrations/processors/download-destinations")) return { libraries: [{ destinations: destinations(url) }] };
      return {};
    });
    authedFetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({}), text: async () => "" });
  }

  // What the connected clients report for the library asked about: the picker's completed folder with Movies and TV.
  function destinations(url: string) {
    const folder = url.includes("lib1") ? "C:\\Downloads\\Completed\\Movies" : "C:\\Downloads\\Completed\\TV";
    return [
      { saveFolder: folder, savedBy: "client-category" },
      { saveFolder: `${folder}\\`, savedBy: "client-category" },
      { saveFolder: "C:\\Downloads\\Completed", savedBy: "deluno-per-grab" }
    ];
  }

  function downloadsSaves() {
    return authedFetchMock.mock.calls
      .filter(([url, init]) => /^\/api\/libraries\/lib\d$/.test(String(url)) && init?.method === "PUT")
      .map(([url, init]) => ({ url: String(url), body: JSON.parse(init.body) }));
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

  it("sets each new library's downloads folder to the folder its clients finish in, so the folder check can tell", async () => {
    wire();

    const result = await createBaseline(input(), []);

    const saves = downloadsSaves();
    expect(saves.map((save) => save.body.downloadsPath)).toEqual(["C:\\Downloads\\Completed\\Movies", "C:\\Downloads\\Completed\\TV"]);
    expect(saves[0].url).toBe("/api/libraries/lib1");
    expect(result.message).not.toContain("could not tell where your download clients finish");
    // The destinations are read after the clients are routed to the library, not before.
    const order = [...fetchJsonMock.mock.calls.map(([url]) => String(url)), ...authedFetchMock.mock.calls.map(([url]) => String(url))];
    expect(order.some((url) => url.startsWith("/api/integrations/processors/download-destinations?libraryId=lib1"))).toBe(true);
  });

  it("uses the folder the person typed and does not look it up", async () => {
    wire();
    const base = input();

    await createBaseline(input({ form: { ...base.form, movieDownloadsPath: "E:\\Incoming\\Films" } }), []);

    const create = fetchJsonMock.mock.calls.find(([url, init]) => url === "/api/libraries" && JSON.parse(init.body).mediaType === "movies");
    expect(JSON.parse(create![1].body).downloadsPath).toBe("E:\\Incoming\\Films");
    expect(fetchJsonMock.mock.calls.some(([url]) => String(url).includes("libraryId=lib1"))).toBe(false);
    expect(downloadsSaves().map((save) => save.url)).toEqual(["/api/libraries/lib2"]);
  });

  it("says so, and sets nothing, when the clients disagree about the folder", async () => {
    wire();
    fetchJsonMock.mockImplementation(async (url: string) => {
      if (url === "/api/quality-profiles") return { id: "qp1", name: "p", mediaType: "movies" };
      if (url === "/api/libraries") return { id: "lib1", name: "Library" };
      if (url.startsWith("/api/integrations/processors/download-destinations")) {
        return { libraries: [{ destinations: [{ saveFolder: "C:\\A", savedBy: "client-category" }, { saveFolder: "C:\\B", savedBy: "client-category" }] }] };
      }
      return {};
    });

    const result = await createBaseline(input(), []);

    expect(downloadsSaves()).toEqual([]);
    expect(result.message).toContain("could not tell where your download clients finish Movies and TV");
  });
});

describe("detectLibraryDownloadsFolder", () => {
  const report = (folders: string[]) => ({
    libraries: [{ destinations: folders.map((saveFolder) => ({ saveFolder, savedBy: "client-category" })) }]
  });

  it("finds the one folder when qBittorrent writes it with / and the others with \\", async () => {
    fetchJsonMock.mockResolvedValue(report(["C:/Downloads/Completed/Movies", "C:\\Downloads\\Completed\\Movies", "c:\\downloads\\completed\\movies\\"]));
    expect(await detectLibraryDownloadsFolder("lib")).toBe("C:/Downloads/Completed/Movies");
  });

  it("stays empty when the clients really disagree", async () => {
    fetchJsonMock.mockResolvedValue(report(["C:/Downloads/Completed/Movies", "D:\\Downloads\\Movies"]));
    expect(await detectLibraryDownloadsFolder("lib")).toBeNull();
  });
});

describe("cleaning files with Weir in the guided setup", () => {
  afterEach(() => {
    fetchJsonMock.mockReset();
    authedFetchMock.mockReset();
  });

  function wireWeir() {
    let libraryCount = 0;
    fetchJsonMock.mockImplementation(async (url: string) => {
      if (url === "/api/quality-profiles") return { id: "qp1", name: "p", mediaType: "movies" };
      if (url === "/api/libraries") return { id: `lib${++libraryCount}`, name: "Library", processorTimeoutMinutes: 2880, processorFailureMode: "block" };
      if (url === "/api/settings") return {};
      if (url === "/api/integrations/processors/connections") return [{ id: "weir-1", name: "Weir", isEnabled: true }];
      return {};
    });
    authedFetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({}), text: async () => "" });
  }

  const workflowPuts = () => authedFetchMock.mock.calls
    .filter(([url, init]) => /\/workflow$/.test(String(url)) && init?.method === "PUT")
    .map(([url, init]) => ({ url: String(url), body: JSON.parse(init.body) }));

  const withAnswers = (answers: Record<string, unknown>) => {
    const base = input();
    return { ...base, form: { ...base.form, ...answers } as Input["form"] };
  };

  it("a Yes sets that library to refine before import and links it to the connected Weir; a No leaves the other alone", async () => {
    wireWeir();
    await createBaseline(withAnswers({ refineMovies: true, movieProcessedPath: "D:\\Weir\\Ready\\Movies", refineTv: false, seriesProcessedPath: "D:\\Weir\\Ready\\TV" }), []);

    const puts = workflowPuts();
    expect(puts).toHaveLength(1);
    expect(puts[0]!.url).toBe("/api/libraries/lib1/workflow");
    expect(puts[0]!.body).toMatchObject({
      importWorkflow: "refine-before-import",
      processorConnectionId: "weir-1",
      processorName: "Weir",
      processorOutputPath: "D:\\Weir\\Ready\\Movies"
    });
  });

  it("changes nothing when neither question was answered Yes", async () => {
    wireWeir();
    await createBaseline(withAnswers({ refineMovies: false, refineTv: null }), []);
    expect(workflowPuts()).toEqual([]);
  });

  it("says so, and still creates the baseline, when Weir has no connection to link to", async () => {
    wireWeir();
    fetchJsonMock.mockImplementation(async (url: string) => {
      if (url === "/api/integrations/processors/connections") return [];
      if (url === "/api/libraries") return { id: "lib1", name: "Library" };
      return { id: "qp1", name: "p", mediaType: "movies" };
    });
    const result = await createBaseline(withAnswers({ refineMovies: true, movieProcessedPath: "D:\\Weir\\Ready\\Movies" }), []);
    expect(result.message).toContain("cleaning Movies files with Weir (Weir is not connected to Deluno yet)");
    expect(workflowPuts()).toEqual([]);
  });
});

describe("the Weir questions gate Start", () => {
  const { missingWeirAnswers, suggestedProcessedFolder, describeMissingForFinish } = guide;
  const form = (over: Record<string, unknown>) => ({
    mediaIntent: "both", movieRootPath: "D:\\Media\\Movies", seriesRootPath: "D:\\Media\\TV",
    automation: NO, qualityPreset: "balanced1080p", formatGoal: "simpleClean", skipSignInLocalNetwork: false, updateAutomatically: false,
    refineMovies: null, refineTv: null, movieProcessedPath: "D:\\Weir\\Ready\\Movies", seriesProcessedPath: "D:\\Weir\\Ready\\TV", ...over
  }) as unknown as Parameters<typeof missingWeirAnswers>[0];

  it("asks nothing without Weir, and each library type in use once Weir is connected", () => {
    expect(missingWeirAnswers(form({}), false)).toEqual([]);
    expect(missingWeirAnswers(form({}), true)).toHaveLength(2);
    expect(missingWeirAnswers(form({ mediaIntent: "movies" }), true)).toHaveLength(1);
    expect(missingWeirAnswers(form({ refineMovies: false, refineTv: false }), true)).toEqual([]);
  });

  it("a Yes needs the folder Weir leaves cleaned files in", () => {
    expect(missingWeirAnswers(form({ refineMovies: true, movieProcessedPath: " ", refineTv: false }), true)).toEqual(["the folder Weir leaves cleaned Movies files in"]);
    expect(describeMissingForFinish(form({}), true)).toContain("Clean Movies files with Weir before they go into your library?");
  });

  it("suggests Weir's own folder on the library's drive", () => {
    expect(suggestedProcessedFolder("D:\\Media\\Movies", "Movies")).toBe("D:\\Weir\\Ready\\Movies");
    expect(suggestedProcessedFolder("/media/tv", "TV")).toBe("");
  });
});

describe("what the Yes/No answers write when the baseline is created (#1194, #993)", () => {
  afterEach(() => {
    fetchJsonMock.mockReset();
    authedFetchMock.mockReset();
  });

  const form = (overrides: Record<string, unknown>) => ({ ...input().form, ...overrides }) as unknown as Parameters<typeof guide.saveLocalNetworkAuth>[0];
  const rule = (kind: string, enabled: boolean) => ({ id: `rule-${kind}`, kind, enabled, strikeLimit: 9, timerMinutes: 60, actionPreset: "keep" });

  it("a Yes to skipping sign-in writes the real setting with the ticked addresses, a No writes it off, and no answer writes nothing", async () => {
    fetchJsonMock.mockResolvedValue({});

    await guide.saveLocalNetworkAuth(form({ skipSignInLocalNetwork: true, localNetworkAllowList: ["192.168.1.20"] }));
    expect(fetchJsonMock).toHaveBeenLastCalledWith("/api/security/local-network-auth", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: true, additionalLocalNetworks: [], allowList: ["192.168.1.20"], trustedProxies: [] })
    });

    await guide.saveLocalNetworkAuth(form({ skipSignInLocalNetwork: false, localNetworkAllowList: [] }));
    expect(JSON.parse(fetchJsonMock.mock.lastCall![1].body).enabled).toBe(false);

    fetchJsonMock.mockClear();
    await guide.saveLocalNetworkAuth(form({ skipSignInLocalNetwork: null }));
    expect(fetchJsonMock).not.toHaveBeenCalled();
  });

  it("a Yes to the stall question turns on both the No progress and the Too slow rule with 3 strikes, 50 KB/s and replace, whether they exist or not", async () => {
    fetchJsonMock.mockImplementation(async (url: string) => (url === "/api/download-health/rules" ? [rule("no-progress", false)] : {}));

    await guide.saveStallStrikeRules(form({ automation: { ...NO, stallStrikes: true } }));

    const writes = fetchJsonMock.mock.calls.filter(([, init]) => init?.method === "PUT").map(([url, init]) => ({ url, body: JSON.parse(init.body) }));
    expect(writes.map((write) => write.url)).toEqual(["/api/download-health/rules/rule-no-progress", "/api/download-health/rules/new"]);
    expect(writes[0].body).toMatchObject({ kind: "no-progress", enabled: true, strikeLimit: 3, actionPreset: "replace" });
    expect(writes[1].body).toMatchObject({ kind: "too-slow", enabled: true, strikeLimit: 3, actionPreset: "replace", threshold: 50 });
  });

  it("a No switches off a stall rule that was on, leaves one that is already off alone, and creates none", async () => {
    fetchJsonMock.mockImplementation(async (url: string) => (url === "/api/download-health/rules" ? [rule("no-progress", true), rule("too-slow", false)] : {}));

    await guide.saveStallStrikeRules(form({ automation: { ...NO, stallStrikes: false } }));

    const writes = fetchJsonMock.mock.calls.filter(([, init]) => init?.method === "PUT").map(([url, init]) => ({ url, body: JSON.parse(init.body) }));
    expect(writes).toHaveLength(1);
    expect(writes[0].url).toBe("/api/download-health/rules/rule-no-progress");
    expect(writes[0].body.enabled).toBe(false);
  });

  it("no answer to the stall question writes nothing", async () => {
    await guide.saveStallStrikeRules(form({ automation: { ...NO, stallStrikes: null } }));
    expect(fetchJsonMock).not.toHaveBeenCalled();
  });

  it("a sign-in write the server refuses does not stop the baseline: the message names what could not be saved", async () => {
    fetchJsonMock.mockImplementation(async (url: string) => {
      if (url === "/api/quality-profiles") return { id: "qp1", name: "p", mediaType: "movies" };
      if (url === "/api/libraries") return { id: "lib1", name: "Library" };
      if (url === "/api/security/local-network-auth") throw new Error("refused");
      return {};
    });
    authedFetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({}), text: async () => "" });
    const base = input();

    const result = await createBaseline(input({ form: { ...base.form, skipSignInLocalNetwork: true } }), []);

    expect(result.libraries.length).toBeGreaterThan(0);
    expect(result.message).toContain("These answers could not be saved, so set them in Settings: skipping sign-in on your home network");
  });
});
