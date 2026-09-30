import { describe, expect, it } from "vitest";
import type { DownloadClientItem, IndexerItem, LibraryItem, PlatformSettingsSnapshot, PolicySetItem, QualityProfileItem } from "../../lib/api";
import { buildSetupStatus } from "../../lib/setup-status";
import { buildSetupState } from "./sources";

// #1082: a failing indexer has its own Needs you row ("Nyaa.si isn't
// answering", with Test again). The setup checklist's "Connection health needs
// review" said the same problem a second time on the rig; it is not a row.
describe("setup needs on the Dashboard", () => {
  const lists = {
    libraries: [{ mediaType: "movies", rootPath: "D:/Media/Movies", autoSearchEnabled: true } as LibraryItem, { mediaType: "tv", rootPath: "D:/Media/TV", autoSearchEnabled: true } as LibraryItem],
    indexers: [{ isEnabled: true, healthStatus: "healthy" } as IndexerItem, { isEnabled: true, healthStatus: "failed" } as IndexerItem],
    downloadClients: [{ isEnabled: true, healthStatus: "healthy" } as DownloadClientItem],
    intakeSources: [],
    policySets: [{ isEnabled: true } as PolicySetItem],
    qualityProfiles: [{} as QualityProfileItem],
    settings: {
      autoStartJobs: true,
      workflowVerified: true,
      movieFolderFormat: "{Movie Title} ({Release Year})",
      seriesFolderFormat: "{Series Title} ({Series Year})",
      episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}"
    } as PlatformSettingsSnapshot
  };

  it("does not repeat an unhealthy connection that already has its own row", () => {
    expect(buildSetupStatus(lists).attentionItems.map((item) => item.id)).toContain("connection-health");
    expect(buildSetupState(lists as Parameters<typeof buildSetupState>[0]).needs.map((need) => need.id)).not.toContain("connection-health");
  });
});
