import { describe, expect, it } from "vitest";
import type { DownloadClientItem, IndexerItem, LibraryItem, PlatformSettingsSnapshot, PolicySetItem, QualityProfileItem } from "./api";
import { buildSetupStatus, type SetupStatusInput } from "./setup-status";

function input(overrides: Partial<SetupStatusInput> = {}): SetupStatusInput {
  return { libraries: [], downloadClients: [], indexers: [], intakeSources: [], policySets: [], qualityProfiles: [], settings: { autoStartJobs: false, workflowVerified: false } as PlatformSettingsSnapshot, ...overrides };
}

describe("setup status", () => {
  it("explains a fresh install", () => {
    const status = buildSetupStatus(input());
    expect(status).toMatchObject({ completedCount: 0, totalCount: 5, isComplete: false, readiness: "not-ready", summary: "Start with step 1: Library & storage." });
    expect(status.steps.map((step) => step.id)).toEqual(["library", "media-plans", "connections", "automation", "workflow", "discovery"]);
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
    expect(status.steps.find((step) => step.id === "connections")).toMatchObject({ complete: false });
    expect(status.readiness).toBe("not-ready");
    expect(status.attentionItems.map((item) => item.id)).toContain("connections");
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
