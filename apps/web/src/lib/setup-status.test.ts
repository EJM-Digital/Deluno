import { describe, expect, it } from "vitest";
import type { DownloadClientItem, IndexerItem, LibraryItem, PlatformSettingsSnapshot, PolicySetItem, QualityProfileItem } from "./api";
import { buildSetupStatus, type SetupStatusInput } from "./setup-status";

function input(overrides: Partial<SetupStatusInput> = {}): SetupStatusInput {
  return { libraries: [], downloadClients: [], indexers: [], policySets: [], qualityProfiles: [], settings: { autoStartJobs: false } as PlatformSettingsSnapshot, ...overrides };
}

describe("setup status", () => {
  it("explains a fresh install", () => {
    const status = buildSetupStatus(input());
    expect(status).toMatchObject({ completedCount: 0, totalCount: 4, isComplete: false, summary: "Start with step 1: Library & storage." });
    expect(status.attentionItems.map((item) => item.id)).toEqual(["library", "connections", "media-plans", "automation"]);
  });

  it("recognises a fully configured setup", () => {
    const configured = input({
      libraries: [{ mediaType: "movies", autoSearchEnabled: true } as LibraryItem, { mediaType: "tv", autoSearchEnabled: true } as LibraryItem],
      indexers: [{ isEnabled: true, healthStatus: "healthy" } as IndexerItem],
      downloadClients: [{ isEnabled: true, healthStatus: "healthy" } as DownloadClientItem],
      policySets: [{ isEnabled: true } as PolicySetItem],
      qualityProfiles: [{} as QualityProfileItem],
      settings: { autoStartJobs: true } as PlatformSettingsSnapshot
    });
    const status = buildSetupStatus(configured);
    expect(status).toMatchObject({ completedCount: 4, isComplete: true, summary: "Core setup complete. No setup items need attention." });
    expect(status.attentionItems).toEqual([]);
  });
});
