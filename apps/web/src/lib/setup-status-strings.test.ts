import { describe, expect, it } from "vitest";
import type { DownloadClientItem, IndexerItem, LibraryItem, PlatformSettingsSnapshot, PolicySetItem, QualityProfileItem } from "./api";
import { buildSetupStatus, type SetupStatusInput } from "./setup-status";

function input(overrides: Partial<SetupStatusInput> = {}): SetupStatusInput {
  const { settings, ...rest } = overrides;
  return {
    libraries: [], downloadClients: [], indexers: [], intakeSources: [], policySets: [], qualityProfiles: [], ...rest,
    settings: {
      autoStartJobs: false, workflowVerified: false,
      movieFolderFormat: "{Movie Title} ({Release Year})",
      seriesFolderFormat: "{Series Title} ({Series Year})",
      episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
      ...settings
    } as PlatformSettingsSnapshot
  };
}
const indexer = (healthStatus: string, isEnabled = true) => ({ isEnabled, healthStatus }) as IndexerItem;
const client = (healthStatus: string, isEnabled = true) => ({ isEnabled, healthStatus }) as DownloadClientItem;
const step = (model: ReturnType<typeof buildSetupStatus>, id: string) => model.steps.find((item) => item.id === id)!;

describe("the Find & Download step wording, one case per connection state", () => {
  it.each([
    ["nothing connected", [], [], "Search sources and download clients still need to be connected", "not-started"],
    ["both enabled but neither healthy", [indexer("failed")], [client("failed")], "Search sources and download clients need a successful connection test", "failed"],
    ["no healthy search source", [indexer("failed")], [client("healthy")], "A healthy search source still needs to be connected", "failed"],
    ["no healthy download client", [indexer("healthy")], [client("failed")], "A healthy download client still needs to be connected", "failed"],
    ["both healthy", [indexer("healthy"), indexer("failed")], [client("healthy")], "1/2 search sources healthy - 1/1 download clients healthy", "complete"],
    ["only a disabled client", [indexer("healthy")], [client("healthy", false)], "A healthy download client still needs to be connected", "not-started"]
  ] as const)("%s", (_name, indexers, clients, status, state) => {
    const model = buildSetupStatus(input({ indexers: [...indexers], downloadClients: [...clients] }));
    expect(step(model, "connections")).toMatchObject({ status, state });
  });

  it("tells the person which half of the pair is missing in the attention item", () => {
    expect(buildSetupStatus(input({ indexers: [indexer("healthy")] })).attentionItems.find((item) => item.id === "connections")?.text)
      .toBe("Add and test at least one enabled download client so Deluno can send approved releases.");
    expect(buildSetupStatus(input({ downloadClients: [client("healthy")] })).attentionItems.find((item) => item.id === "connections")?.text)
      .toBe("Add and test at least one enabled search source so Deluno can find releases.");
    expect(buildSetupStatus(input()).attentionItems.find((item) => item.id === "connections")?.text)
      .toBe("Add and test at least one search source and one download client before Deluno can find and send releases.");
  });
});

describe("a Connection health needs review attention item", () => {
  const ready = { indexers: [indexer("healthy")], downloadClients: [client("healthy")] };

  it("appears when connections are ready but another enabled search source is not healthy", () => {
    const item = buildSetupStatus(input({ ...ready, indexers: [indexer("healthy"), indexer("failed")] })).attentionItems.find((entry) => entry.id === "connection-health");
    expect(item).toMatchObject({ title: "Connection health needs review", text: "1 search source not reporting healthy.", href: "/indexers", tone: "warn" });
  });

  it("names both kinds with their counts when a search source and two download clients are unhealthy", () => {
    const item = buildSetupStatus(input({ indexers: [indexer("healthy"), indexer("failed")], downloadClients: [client("healthy"), client("failed"), client("unknown")] }))
      .attentionItems.find((entry) => entry.id === "connection-health");
    expect(item?.text).toBe("1 search source and 2 download clients not reporting healthy.");
  });

  it("is absent when every enabled connection is healthy, and when a disabled one is the unhealthy one", () => {
    expect(buildSetupStatus(input(ready)).attentionItems.map((item) => item.id)).not.toContain("connection-health");
    expect(buildSetupStatus(input({ ...ready, indexers: [indexer("healthy"), indexer("failed", false)] })).attentionItems.map((item) => item.id)).not.toContain("connection-health");
  });

  it("is absent while connections are not ready, because the connections step already says so", () => {
    const ids = buildSetupStatus(input({ indexers: [indexer("failed")], downloadClients: [client("healthy")] })).attentionItems.map((item) => item.id);
    expect(ids).toContain("connections");
    expect(ids).not.toContain("connection-health");
  });
});

describe("the Automation & Recovery step wording", () => {
  const library = (autoSearchEnabled: boolean) => ({ mediaType: "movies", rootPath: "D:\\Media\\Movies", autoSearchEnabled }) as LibraryItem;

  it.each([
    ["paused", false, [library(true)], "Background automation is paused", false],
    ["on but no library automation", true, [library(false)], "No library automation is enabled", false],
    ["one library active", true, [library(true)], "1 library automation setting active", true],
    ["two libraries active", true, [library(true), library(true)], "2 library automation settings active", true]
  ] as const)("%s", (_name, autoStartJobs, libraries, status, complete) => {
    const model = buildSetupStatus(input({ libraries: [...libraries], settings: { autoStartJobs } as PlatformSettingsSnapshot }));
    expect(step(model, "automation")).toMatchObject({ status, complete });
  });

  it("is complete only with both the scheduler on and a library automation setting; the attention text says which is missing", () => {
    const paused = buildSetupStatus(input({ libraries: [library(true)] })).attentionItems.find((item) => item.id === "automation");
    expect(paused?.text).toBe("Turn background automation on when you want scheduled searches, upgrades, retries, and recovery checks to run.");
    const noLibrary = buildSetupStatus(input({ libraries: [library(false)], settings: { autoStartJobs: true } as PlatformSettingsSnapshot })).attentionItems.find((item) => item.id === "automation");
    expect(noLibrary?.text).toBe("Enable missing or upgrade searches on at least one library so Deluno can operate the configured acquisition plan.");
  });
});

describe("Library Profiles and Discover Media wording", () => {
  it("switches the step link between choosing and reviewing, and names active library profiles", () => {
    expect(step(buildSetupStatus(input()), "media-plans")).toMatchObject({ to: "/settings/profiles", action: "Choose a quality profile" });
    const active = buildSetupStatus(input({ policySets: [{ isEnabled: true } as PolicySetItem, { isEnabled: true } as PolicySetItem], qualityProfiles: [{} as QualityProfileItem] }));
    expect(step(active, "media-plans")).toMatchObject({ to: "/settings/policy-sets", action: "Review Library Profiles", status: "2 active library profiles - 1 quality profile" });
  });

  it("counts only enabled import lists and keeps them optional", () => {
    const model = buildSetupStatus(input({ intakeSources: [{ isEnabled: true }, { isEnabled: true }, { isEnabled: false }] as never }));
    expect(step(model, "discovery")).toMatchObject({ status: "2 import lists enabled", complete: true, optional: true, action: "Review import lists" });
    expect(model.optionalConfiguredCount).toBe(1);
    expect(step(buildSetupStatus(input()), "discovery").status).toBe("Optional - not configured");
  });
});

describe("a library is ready only when its folders and naming are set", () => {
  const ready = (libraries: LibraryItem[], over: Partial<PlatformSettingsSnapshot> = {}) =>
    step(buildSetupStatus(input({ libraries, settings: over as PlatformSettingsSnapshot })), "library").complete;

  it("a TV library needs a series folder format and an episode file format", () => {
    const tv = { mediaType: "tv", rootPath: "D:\\Media\\TV" } as LibraryItem;
    expect(ready([tv])).toBe(true);
    expect(ready([tv], { seriesFolderFormat: "" })).toBe(false);
    expect(ready([tv], { episodeFileFormat: "  " })).toBe(false);
  });

  it("a movie library needs a movie folder format, and ignores the TV formats", () => {
    const movies = { mediaType: "movies", rootPath: "D:\\Media\\Movies" } as LibraryItem;
    expect(ready([movies], { seriesFolderFormat: "", episodeFileFormat: "" })).toBe(true);
    expect(ready([movies], { movieFolderFormat: "" })).toBe(false);
  });

  it("a library with no root path, or an unknown media type, is never ready", () => {
    expect(ready([{ mediaType: "movies", rootPath: "  " } as LibraryItem])).toBe(false);
    expect(ready([{ mediaType: "music", rootPath: "D:\\Media\\Music" } as LibraryItem])).toBe(false);
  });

  it("a refine-before-import library is ready once its processor output folder is set", () => {
    const movies = { mediaType: "movies", rootPath: "D:\\Media\\Movies", importWorkflow: "refine-before-import" } as LibraryItem;
    expect(ready([movies])).toBe(false);
    expect(ready([{ ...movies, processorOutputPath: "D:\\Refined" }])).toBe(true);
  });
});
