import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: fetchJsonMock
}));

const {
  AUTOMATION_QUESTIONS,
  answeredAutomation,
  describeMissingForFinish,
  restoreDraftAutomation,
  SETUP_STAGE_LABELS,
  STEPS,
  buildAutomationSettingsPayload,
  buildIndexerAutomationFields,
  buildLibraryAutomationFields,
  connectedDownloadClients,
  waitingForUsenetProvider,
  describeDownloadRoute,
  getEffectiveFormatGoal,
  hasAnsweredAllAutomationQuestions,
  preflightDownloadClient,
  resolveDraftFormatGoal,
  saveAllowedHosts,
  saveDangerousFileCheck,
  saveUpdatePreferences
} = await import("./setup-guide-page");
type AutomationChoices = Parameters<typeof buildAutomationSettingsPayload>[0];
type SecurityForm = Parameters<typeof saveAllowedHosts>[0];
type UpdatesForm = Parameters<typeof saveUpdatePreferences>[0];

const UNANSWERED: AutomationChoices = {
  startAutomation: null,
  searchMissingTitles: null,
  rssGrabs: null,
  qualityUpgrades: null,
  renameOnImport: null,
  subtitleFetching: null,
  removeBadDownloads: null,
  deleteBadDownloadFiles: null,
  stallStrikes: null,
  leftoverCleanup: null,
  leftoverDownloadCleanup: null,
  forceImport: null,
  pauseSearchWhenQueueLong: null,
  dangerousFileCheck: null
};

const ALL_NO: AutomationChoices = {
  startAutomation: false,
  searchMissingTitles: false,
  rssGrabs: false,
  qualityUpgrades: false,
  renameOnImport: false,
  subtitleFetching: false,
  removeBadDownloads: false,
  deleteBadDownloadFiles: false,
  stallStrikes: false,
  leftoverCleanup: false,
  leftoverDownloadCleanup: false,
  forceImport: false,
  pauseSearchWhenQueueLong: false,
  dangerousFileCheck: false
};

const ALL_YES: AutomationChoices = {
  startAutomation: true,
  searchMissingTitles: true,
  rssGrabs: true,
  qualityUpgrades: true,
  renameOnImport: true,
  subtitleFetching: true,
  removeBadDownloads: true,
  deleteBadDownloadFiles: true,
  stallStrikes: true,
  leftoverCleanup: true,
  leftoverDownloadCleanup: true,
  forceImport: true,
  pauseSearchWhenQueueLong: true,
  dangerousFileCheck: true
};

describe("setup guide draft format goal (#731)", () => {
  it("keeps a saved anime draft instead of silently rewriting it to balanced", () => {
    expect(resolveDraftFormatGoal("anime")).toBe("anime");
  });

  it("round-trips every other goal the guide still offers", () => {
    expect(resolveDraftFormatGoal("simpleClean")).toBe("simpleClean");
    expect(resolveDraftFormatGoal("balanced")).toBe("balanced");
    expect(resolveDraftFormatGoal("homeTheater")).toBe("homeTheater");
    expect(resolveDraftFormatGoal("storageSaver")).toBe("storageSaver");
    expect(resolveDraftFormatGoal("archive")).toBe("archive");
  });

  it("falls back to not-chosen for an empty or unrecognised draft value", () => {
    expect(resolveDraftFormatGoal("")).toBe("");
    expect(resolveDraftFormatGoal("some-retired-goal")).toBe("");
  });
});

describe("setup guide effective format goal per media type", () => {
  type Form = Parameters<typeof getEffectiveFormatGoal>[1];
  it("gives TV the anime goal and films Balanced, because the anime bundle is TV-only", () => {
    const form = { formatGoal: "anime" } as Form;
    expect(getEffectiveFormatGoal("tv", form)).toBe("anime");
    expect(getEffectiveFormatGoal("movies", form)).toBe("balanced");
  });

  it("uses the chosen goal for both media types otherwise", () => {
    const form = { formatGoal: "homeTheater" } as Form;
    expect(getEffectiveFormatGoal("tv", form)).toBe("homeTheater");
    expect(getEffectiveFormatGoal("movies", form)).toBe("homeTheater");
  });

  it("gives films the archive goal and TV Balanced, because archive-4k-remux is movies-only (B006)", () => {
    const form = { formatGoal: "archive" } as Form;
    expect(getEffectiveFormatGoal("movies", form)).toBe("archive");
    expect(getEffectiveFormatGoal("tv", form)).toBe("balanced");
  });
});

describe("setup guide Automation step (#936)", () => {
  it("has nothing pre-selected: an unanswered set of choices is not complete", () => {
    expect(hasAnsweredAllAutomationQuestions(UNANSWERED)).toBe(false);
  });

  it("is not complete until every single question has an answer, even if only one is missing", () => {
    for (const item of AUTOMATION_QUESTIONS) {
      const almostAllAnswered: AutomationChoices = { ...ALL_NO, [item.key]: null };
      expect(hasAnsweredAllAutomationQuestions(almostAllAnswered)).toBe(false);
    }
  });

  it("is complete once every question has an explicit Yes or No, even if every answer is No", () => {
    expect(hasAnsweredAllAutomationQuestions(ALL_NO)).toBe(true);
    expect(hasAnsweredAllAutomationQuestions(ALL_YES)).toBe(true);
  });

  it("asks exactly the eight behaviours #936 lists plus #932's leftover-download cleanup and #944's dangerous-file check, each with its own key", () => {
    const keys = AUTOMATION_QUESTIONS.map((item) => item.key).sort();
    expect(keys).toEqual([
      "dangerousFileCheck",
      "deleteBadDownloadFiles",
      "forceImport",
      "leftoverCleanup",
      "leftoverDownloadCleanup",
      "pauseSearchWhenQueueLong",
      "qualityUpgrades",
      "removeBadDownloads",
      "renameOnImport",
      "rssGrabs",
      "searchMissingTitles",
      "stallStrikes",
      "startAutomation",
      "subtitleFetching"
    ]);
  });

  it("never hints that removing or deleting a bad download is recommended", () => {
    const removeQuestion = AUTOMATION_QUESTIONS.find((item) => item.key === "removeBadDownloads")!;
    const deleteQuestion = AUTOMATION_QUESTIONS.find((item) => item.key === "deleteBadDownloadFiles")!;
    for (const question of [removeQuestion, deleteQuestion]) {
      expect(question.question.toLowerCase()).not.toContain("recommended");
      expect(question.help.toLowerCase()).not.toContain("recommended");
    }
  });

  it("maps every 'No' answer to every automatic setting reading off", () => {
    expect(buildAutomationSettingsPayload(ALL_NO)).toEqual({
      autoStartJobs: false,
      renameOnImport: false,
      cleanupRemoveClientEntryAfterThreshold: false,
      cleanupBlockReleaseAfterThreshold: false,
      cleanupQueueReplacementAfterThreshold: false,
      downloadHealthNoProgressEnabled: false,
      downloadHealthMinSpeedEnabled: false,
      downloadHealthMinSpeedKbps: 50,
      downloadHealthStrikeThreshold: 3,
      downloadHealthDefaultStrikeWindowMinutes: 5,
      cleanupPurgePayloadAfterThreshold: false,
      subtitleFetchAutomaticEnabled: false,
      leftoverDownloadCleanupEnabled: false,
      forceImportEnabled: false,
      pauseSearchWhenQueueLongEnabled: false
    });
    expect(buildLibraryAutomationFields(ALL_NO)).toEqual({
      autoSearchEnabled: false,
      missingSearchEnabled: false,
      upgradeSearchEnabled: false,
      removeEmptySourceFolders: false
    });
    expect(buildIndexerAutomationFields(ALL_NO)).toEqual({
      rssEnabled: false,
      automaticSearchEnabled: false
    });
  });

  it("maps a 'Yes' answer to its own setting turning on, and only that setting", () => {
    const onlyRenameYes: AutomationChoices = { ...ALL_NO, renameOnImport: true };
    expect(buildAutomationSettingsPayload(onlyRenameYes)).toMatchObject({
      renameOnImport: true,
      cleanupRemoveClientEntryAfterThreshold: false,
      cleanupPurgePayloadAfterThreshold: false,
      subtitleFetchAutomaticEnabled: false
    });

    const onlySubtitleFetchingYes: AutomationChoices = { ...ALL_NO, subtitleFetching: true };
    expect(buildAutomationSettingsPayload(onlySubtitleFetchingYes)).toMatchObject({
      renameOnImport: false,
      subtitleFetchAutomaticEnabled: true
    });

    const onlyMissingSearchYes: AutomationChoices = { ...ALL_NO, searchMissingTitles: true };
    expect(buildLibraryAutomationFields(onlyMissingSearchYes)).toEqual({
      autoSearchEnabled: true,
      missingSearchEnabled: true,
      upgradeSearchEnabled: false,
      removeEmptySourceFolders: false
    });

    const onlyLeftoverCleanupYes: AutomationChoices = { ...ALL_NO, leftoverCleanup: true };
    expect(buildLibraryAutomationFields(onlyLeftoverCleanupYes)).toEqual({
      autoSearchEnabled: false,
      missingSearchEnabled: false,
      upgradeSearchEnabled: false,
      removeEmptySourceFolders: true
    });

    const onlyRssYes: AutomationChoices = { ...ALL_NO, rssGrabs: true };
    expect(buildIndexerAutomationFields(onlyRssYes)).toEqual({
      rssEnabled: true,
      automaticSearchEnabled: false
    });

    const onlyLeftoverDownloadCleanupYes: AutomationChoices = { ...ALL_NO, leftoverDownloadCleanup: true };
    expect(buildAutomationSettingsPayload(onlyLeftoverDownloadCleanupYes)).toMatchObject({
      renameOnImport: false,
      subtitleFetchAutomaticEnabled: false,
      leftoverDownloadCleanupEnabled: true
    });
  });

  it("#932/#936: the leftover-download cleanup question defaults off and is not one of the per-library or per-indexer fields", () => {
    expect(buildAutomationSettingsPayload(ALL_NO).leftoverDownloadCleanupEnabled).toBe(false);
    expect(buildLibraryAutomationFields(ALL_YES)).not.toHaveProperty("leftoverDownloadCleanupEnabled");
    expect(buildIndexerAutomationFields(ALL_YES)).not.toHaveProperty("leftoverDownloadCleanupEnabled");
  });

  it("never writes the dead global CleanupEmptyFolders flag that #736 already removed from the UI", () => {
    expect(buildAutomationSettingsPayload(ALL_YES)).not.toHaveProperty("cleanupEmptyFolders");
  });

  it("starts the background scheduler exactly when the plain start-now question is answered Yes", () => {
    expect(buildAutomationSettingsPayload({ ...ALL_NO, startAutomation: true }).autoStartJobs).toBe(true);
    expect(buildAutomationSettingsPayload({ ...ALL_YES, startAutomation: false }).autoStartJobs).toBe(false);
    expect(buildAutomationSettingsPayload(ALL_NO).autoStartJobs).toBe(false);
  });

  it("asks the start-now question first, in plain words", () => {
    const first = AUTOMATION_QUESTIONS[0];
    expect(first.key).toBe("startAutomation");
    expect(first.question).toBe("Should Deluno start searching and downloading on its own now?");
  });
});

describe("setup guide Security step (#939)", () => {
  afterEach(() => {
    fetchJsonMock.mockReset();
  });

  it("answering No saves nothing -- a fresh install behaves exactly like before this feature (#936)", async () => {
    await saveAllowedHosts({ restrictAllowedHosts: false, allowedHostsSelection: [] } as unknown as SecurityForm, { enabled: false });

    expect(fetchJsonMock).not.toHaveBeenCalled();
  });

  it("answering Yes saves the ticked addresses turned on", async () => {
    await saveAllowedHosts({ restrictAllowedHosts: true, allowedHostsSelection: ["localhost", "my-pc"] } as unknown as SecurityForm, { enabled: false });

    expect(fetchJsonMock).toHaveBeenCalledWith("/api/security/allowed-hosts", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: true, allowedHosts: ["localhost", "my-pc"] })
    });
  });
});

describe("setup guide dangerous-file check question (#944)", () => {
  afterEach(() => {
    fetchJsonMock.mockReset();
  });

  const loaded = {
    enabled: false,
    mode: "blacklist",
    useBuiltInPatterns: true,
    customPatterns: [],
    remoteUrl: null,
    remotePatterns: [],
    lastRefreshedUtc: null,
    syncToQbittorrentEnabled: false,
    updatedUtc: "1970-01-01T00:00:00Z"
  };

  it("leaving the question unanswered saves nothing", async () => {
    await saveDangerousFileCheck({ automation: { ...ALL_NO, dangerousFileCheck: null } } as unknown as Parameters<typeof saveDangerousFileCheck>[0], loaded);

    expect(fetchJsonMock).not.toHaveBeenCalled();
  });

  it("answering No writes an explicit off, honoring the choice even over an existing install's migrated-on default", async () => {
    await saveDangerousFileCheck({ automation: { ...ALL_NO, dangerousFileCheck: false } } as unknown as Parameters<typeof saveDangerousFileCheck>[0], { ...loaded, enabled: true });

    expect(fetchJsonMock).toHaveBeenCalledWith("/api/download-health/content-blocklist", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...loaded, enabled: false })
    });
  });

  it("answering Yes turns the check on, keeping every other loaded field", async () => {
    const loadedWithPatterns = { ...loaded, customPatterns: ["*.iso"] };
    await saveDangerousFileCheck({ automation: { ...ALL_NO, dangerousFileCheck: true } } as unknown as Parameters<typeof saveDangerousFileCheck>[0], loadedWithPatterns);

    expect(fetchJsonMock).toHaveBeenCalledWith("/api/download-health/content-blocklist", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...loadedWithPatterns, enabled: true })
    });
  });
});

describe("setup guide two-stage journey (#1034)", () => {
  it("walks Stage 1 (this computer and you) before Stage 2 (Deluno itself)", () => {
    const stages = STEPS.map((step) => step.stage);
    const firstStage2 = stages.indexOf(2);
    expect(firstStage2).toBeGreaterThan(0);
    expect(stages.slice(0, firstStage2).every((stage) => stage === 1)).toBe(true);
    expect(stages.slice(firstStage2).every((stage) => stage === 2)).toBe(true);
  });

  it("asks every Stage 1 question #1034 lists, in order, before anything about media", () => {
    const stage1Ids = STEPS.filter((step) => step.stage === 1).map((step) => step.id);
    expect(stage1Ids).toEqual(["account", "timeRegion", "signIn", "addresses", "network", "updates"]);
  });

  it("asks Stage 2's media questions in the guide's existing order", () => {
    const stage2Ids = STEPS.filter((step) => step.stage === 2).map((step) => step.id);
    expect(stage2Ids).toEqual(["mode", "folders", "quality", "services", "connectApps", "automation", "finish"]);
  });

  it("names both stages for the progress rail", () => {
    expect(SETUP_STAGE_LABELS[1]).toBe("This computer and you");
    expect(SETUP_STAGE_LABELS[2]).toBe("Deluno itself");
  });
});

describe("setup guide Updates step (#1034, #936)", () => {
  afterEach(() => {
    fetchJsonMock.mockReset();
  });

  it("leaving the question unanswered saves nothing", async () => {
    await saveUpdatePreferences({ updateAutomatically: null } as unknown as UpdatesForm, { mode: "notify-only", channel: "stable", autoCheck: true });

    expect(fetchJsonMock).not.toHaveBeenCalled();
  });

  it("answering Yes turns automatic updates on and keeps checking, on the existing channel", async () => {
    await saveUpdatePreferences({ updateAutomatically: true } as unknown as UpdatesForm, { mode: "notify-only", channel: "beta", autoCheck: false });

    expect(fetchJsonMock).toHaveBeenCalledWith("/api/updates/preferences", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ mode: "download-apply-on-restart", channel: "beta", autoCheck: true })
    });
  });

  it("answering No asks first instead of applying automatically, but still checks", async () => {
    await saveUpdatePreferences({ updateAutomatically: false } as unknown as UpdatesForm, { mode: "download-apply-on-restart", channel: "stable", autoCheck: true });

    expect(fetchJsonMock).toHaveBeenCalledWith("/api/updates/preferences", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ mode: "notify-only", channel: "stable", autoCheck: true })
    });
  });
});

describe("Force import setup question (#1013)", () => {
  it("defaults off and only Yes turns it on, leaving every other setting alone", () => {
    expect(buildAutomationSettingsPayload(ALL_NO).forceImportEnabled).toBe(false);
    expect(buildAutomationSettingsPayload(UNANSWERED).forceImportEnabled).toBe(false);
    expect(buildAutomationSettingsPayload({ ...ALL_NO, forceImport: true })).toMatchObject({
      forceImportEnabled: true,
      leftoverDownloadCleanupEnabled: false,
      renameOnImport: false
    });
    expect(buildLibraryAutomationFields(ALL_YES)).not.toHaveProperty("forceImportEnabled");
  });
});

describe("setup guide uses the download clients that are already connected (#1132)", () => {
  const qbit = { id: "c1", name: "qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8081, isEnabled: true, healthStatus: "healthy" } as never;
  const deluge = { id: "c2", name: "Deluge", protocol: "deluge", host: "localhost", port: 8112, isEnabled: true, healthStatus: "healthy" } as never;
  const broken = { id: "c3", name: "SABnzbd", protocol: "sabnzbd", host: "localhost", port: 8080, isEnabled: true, healthStatus: "unreachable" } as never;
  const blankForm = { clientHost: "localhost", clientName: "qBittorrent", clientProtocol: "qbittorrent" } as never;

  it("counts only clients that are on and healthy as connected", () => {
    expect(connectedDownloadClients([qbit, broken, { ...(deluge as object), isEnabled: false } as never])).toEqual([qbit]);
  });

  it("routes a Usenet client that is connected but has no provider yet, like the torrent clients", () => {
    const nzbget = { id: "c4", name: "NZBGet", protocol: "nzbget", host: "localhost", port: 6789, isEnabled: true, healthStatus: "degraded", lastHealthMessage: "No Usenet provider set up. Add yours in Deluno, under Find & Download, Download clients, and it is written to every Usenet client." } as never;
    const sabBroken = { id: "c5", name: "SABnzbd", protocol: "sabnzbd", host: "localhost", port: 8080, isEnabled: true, healthStatus: "degraded", lastHealthMessage: "SABnzbd answered 500." } as never;

    expect(connectedDownloadClients([qbit, nzbget, sabBroken])).toEqual([qbit, nzbget]);
    expect(waitingForUsenetProvider(nzbget)).toBe(true);
    expect(waitingForUsenetProvider(sabBroken)).toBe(false);
  });

  it("asks for no test of the form when a client is connected, so the baseline can be created", () => {
    expect(preflightDownloadClient([qbit], blankForm, false)).toBeNull();
  });

  it("refuses an untested form only when nothing is connected, before anything is written", () => {
    expect(preflightDownloadClient([broken], blankForm, false)).toMatch(/Test the download client you entered/);
    expect(preflightDownloadClient([], { clientHost: "" } as never, false)).toBeNull();
    expect(preflightDownloadClient([broken], blankForm, true)).toBeNull();
  });

  it("names the clients it will really route to, never the form's default", () => {
    const route = describeDownloadRoute([deluge, qbit], blankForm, false);
    expect(route.names).toEqual(["Deluge", "qBittorrent"]);
    expect(route.copy).toContain("Deluge and qBittorrent");
    expect(route.copy).not.toMatch(/qbittorrent with deluno-movies/);
  });

  it("says nothing is routed yet when no client is connected or tested", () => {
    expect(describeDownloadRoute([broken], blankForm, false).names).toEqual([]);
  });
});

describe("setup guide keeps every Yes/No answer in the draft (#1132)", () => {
  type FinishForm = Parameters<typeof describeMissingForFinish>[0];

  it("saves only the questions that were answered and restores them after a reload", () => {
    const partly: AutomationChoices = { ...UNANSWERED, rssGrabs: true, forceImport: false };
    const saved = answeredAutomation(partly);
    expect(saved).toEqual({ rssGrabs: true, forceImport: false });
    expect(restoreDraftAutomation(saved)).toEqual(partly);
  });

  it("restores nothing when the draft has no answers, and ignores retired or non-boolean keys", () => {
    expect(restoreDraftAutomation(undefined)).toEqual(UNANSWERED);
    expect(restoreDraftAutomation({ retiredQuestion: true, rssGrabs: "yes" as unknown as boolean })).toEqual(UNANSWERED);
  });

  it("names exactly what is missing on the Start page", () => {
    const form = {
      mediaIntent: "both", movieRootPath: "D:/Movies", seriesRootPath: "D:/TV",
      qualityPreset: "balanced1080p", formatGoal: "balanced",
      skipSignInLocalNetwork: null, updateAutomatically: true, automation: ALL_NO
    } as unknown as FinishForm;
    const said = describeMissingForFinish(form);
    expect(said).toContain("Skip sign-in");
    expect(said).not.toContain("library root folder");
    expect(said).not.toContain("picture quality");
    expect(said).not.toContain("Update automatically");
    expect(describeMissingForFinish({ ...form, skipSignInLocalNetwork: false })).toBe("Everything is chosen.");
  });
});

describe("the stall and slow-speed question (#1194)", () => {
  it("is asked with the strike count, speed limit and check interval it will use", () => {
    const question = AUTOMATION_QUESTIONS.find((item) => item.key === "stallStrikes")!;
    expect(question.question).toBe("Remove downloads that stall or crawl, after 3 strikes?");
    expect(question.help).toContain("50 KB/s");
    expect(question.help).toContain("Every 5 minutes");
  });

  it("switches the stall and slow-speed rules on with a Yes, and leaves everything off with a No", () => {
    expect(buildAutomationSettingsPayload({ ...ALL_NO, stallStrikes: true })).toMatchObject({
      downloadHealthNoProgressEnabled: true,
      downloadHealthMinSpeedEnabled: true,
      cleanupBlockReleaseAfterThreshold: true,
      cleanupQueueReplacementAfterThreshold: true,
      cleanupRemoveClientEntryAfterThreshold: true,
      cleanupPurgePayloadAfterThreshold: false
    });
    expect(buildAutomationSettingsPayload(ALL_NO)).toMatchObject({
      downloadHealthNoProgressEnabled: false,
      downloadHealthMinSpeedEnabled: false,
      cleanupBlockReleaseAfterThreshold: false
    });
  });

  it("holds Start until the stall question has an answer", () => {
    expect(hasAnsweredAllAutomationQuestions({ ...ALL_NO, stallStrikes: null })).toBe(false);
    expect(hasAnsweredAllAutomationQuestions(ALL_NO)).toBe(true);
  });
});
