import { describe, expect, it } from "vitest";
import type { DownloadClientItem, IndexerItem, LibraryItem, PlatformSettingsSnapshot, PolicySetItem, QualityProfileItem } from "./api";
import { buildSetupStatus, type SetupStatusInput } from "./setup-status";

function input(overrides: Partial<SetupStatusInput> = {}): SetupStatusInput {
  const { settings, ...rest } = overrides;
  return {
    libraries: [],
    downloadClients: [],
    indexers: [],
    intakeSources: [],
    policySets: [],
    qualityProfiles: [],
    ...rest,
    settings: {
      autoStartJobs: false,
      workflowVerified: false,
      movieFolderFormat: "{Movie Title} ({Release Year})",
      seriesFolderFormat: "{Series Title} ({Series Year})",
      episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
      ...settings
    } as PlatformSettingsSnapshot
  };
}

describe("setup status", () => {
  it("explains a fresh install", () => {
    const status = buildSetupStatus(input());
    expect(status).toMatchObject({ completedCount: 0, totalCount: 5, isComplete: false, readiness: "not-ready", summary: "Start with step 1: Media Management." });
    expect(status.steps.map((step) => step.id)).toEqual(["library", "media-plans", "connections", "automation", "discovery", "workflow"]);
    expect(status.steps.map((step) => step.state)).toEqual(["not-started", "not-started", "not-started", "not-started", "not-started", "not-started"]);
    expect(status.attentionItems.map((item) => item.id)).toEqual(["library", "media-plans", "connections", "automation", "workflow"]);
  });

  it("requires healthy acquisition services rather than merely enabled records", () => {
    const configured = input({
      libraries: [{ mediaType: "movies", rootPath: "D:\\Media\\Movies", autoSearchEnabled: true } as LibraryItem, { mediaType: "tv", rootPath: "D:\\Media\\TV", autoSearchEnabled: true } as LibraryItem],
      indexers: [{ isEnabled: true, healthStatus: "failed" } as IndexerItem],
      downloadClients: [{ isEnabled: true, healthStatus: "healthy" } as DownloadClientItem],
      policySets: [{ isEnabled: true } as PolicySetItem],
      qualityProfiles: [{} as QualityProfileItem],
      settings: { autoStartJobs: true, workflowVerified: false } as PlatformSettingsSnapshot
    });
    const status = buildSetupStatus(configured);
    expect(status.steps.find((step) => step.id === "connections")).toMatchObject({ complete: false, state: "failed" });
    expect(status.readiness).toBe("not-ready");
    expect(status.attentionItems.map((item) => item.id)).toContain("connections");
  });

  it("does not mark media management complete when a processor workflow is missing its output folder", () => {
    const status = buildSetupStatus(input({
      libraries: [{
        mediaType: "movies",
        rootPath: "D:\\Media\\Movies",
        importWorkflow: "refine-before-import",
        processorOutputPath: ""
      } as LibraryItem]
    }));

    expect(status.steps.find((step) => step.id === "library")).toMatchObject({ complete: false, state: "failed", status: "0/1 libraries ready" });
  });

  it("accepts a quality profile attached directly to every library as the quality decision", () => {
    // Guided setup creates quality profiles and attaches them to libraries; it
    // never creates a Library Profile. Requiring one left step 2 permanently
    // incomplete for every guided-setup install (#247).
    const status = buildSetupStatus(input({
      libraries: [
        { mediaType: "movies", rootPath: "D:\\Media\\Movies", qualityProfileId: "quality-1" } as LibraryItem,
        { mediaType: "tv", rootPath: "D:\\Media\\TV", qualityProfileId: "quality-2" } as LibraryItem
      ],
      qualityProfiles: [{} as QualityProfileItem, {} as QualityProfileItem]
    }));

    expect(status.steps.find((step) => step.id === "media-plans")).toMatchObject({
      complete: true,
      state: "complete",
      status: "2 quality profiles - applied to 2 libraries"
    });
    expect(status.attentionItems.map((item) => item.id)).not.toContain("media-plans");
  });

  it("keeps the quality decision incomplete while any library has none", () => {
    const status = buildSetupStatus(input({
      libraries: [
        { mediaType: "movies", rootPath: "D:\\Media\\Movies", qualityProfileId: "quality-1" } as LibraryItem,
        { mediaType: "tv", rootPath: "D:\\Media\\TV", qualityProfileId: null } as LibraryItem
      ],
      qualityProfiles: [{} as QualityProfileItem]
    }));

    expect(status.steps.find((step) => step.id === "media-plans")).toMatchObject({
      complete: false,
      status: "No quality decision yet"
    });
  });

  it("recognises operational readiness while keeping import lists optional", () => {
    const configured = input({
      libraries: [{ mediaType: "movies", rootPath: "D:\\Media\\Movies", autoSearchEnabled: true } as LibraryItem],
      indexers: [{ isEnabled: true, healthStatus: "healthy" } as IndexerItem],
      downloadClients: [{ isEnabled: true, healthStatus: "healthy" } as DownloadClientItem],
      policySets: [{ isEnabled: true } as PolicySetItem],
      qualityProfiles: [{} as QualityProfileItem],
      settings: { autoStartJobs: true, workflowVerified: true } as PlatformSettingsSnapshot
    });
    const status = buildSetupStatus(configured);
    expect(status).toMatchObject({ completedCount: 5, totalCount: 5, isComplete: true, readiness: "operationally-ready" });
    expect(status.steps.find((step) => step.id === "discovery")).toMatchObject({ complete: false, optional: true });
    expect(status.optionalConfiguredCount).toBe(0);
    expect(status.attentionItems).toEqual([]);
  });
});
