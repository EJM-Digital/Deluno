import { describe, expect, it } from "vitest";
import type { GuideCustomFormat, GuidePackage } from "./api/types";
import { isAnimeDefaultFormat } from "./guide-formats";

const baseFormat: GuideCustomFormat = {
  trashId: "placeholder",
  name: "placeholder",
  category: "anime",
  description: "",
  originalScore: 0,
  patterns: [],
  bundleOnly: false,
  mappingStatus: "reviewed",
  mappedTraitIds: [],
  sourceKind: "trash-guides"
};

const guide: GuidePackage = {
  id: "guide",
  name: "Test guide",
  version: 1,
  schemaVersion: 1,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "",
  qualityTiers: [],
  customFormats: [],
  qualityProfiles: [],
  bundles: [],
  sourceInventory: {
    schemaVersion: 2,
    upstreamRevision: "1",
    customFormats: [],
    qualityProfiles: [],
    formatGroups: [
      { trashId: "group-anime-release", name: "[Release Groups] Anime", mediaType: "tv", sourcePath: "", sourceBlobSha: "", customFormats: [], qualityProfileIds: [] },
      { trashId: "group-anime-versions", name: "[Required] Anime Versions", mediaType: "tv", sourcePath: "", sourceBlobSha: "", customFormats: [], qualityProfileIds: [] },
      { trashId: "group-anime-optional", name: "[Optional] Anime Dual Audio", mediaType: "tv", sourcePath: "", sourceBlobSha: "", customFormats: [], qualityProfileIds: [] }
    ]
  }
};

describe("isAnimeDefaultFormat", () => {
  it("marks a format from the anime release-group tier group", () => {
    const format: GuideCustomFormat = { ...baseFormat, trashId: "bd-tier-01", name: "Anime BD Tier 01", sourceGroupIds: ["group-anime-release"] };
    expect(isAnimeDefaultFormat(format, guide)).toBe(true);
  });

  it("marks a format from the Anime Versions (v0-v4) group", () => {
    const format: GuideCustomFormat = { ...baseFormat, trashId: "v1", name: "v1", sourceGroupIds: ["group-anime-versions"] };
    expect(isAnimeDefaultFormat(format, guide)).toBe(true);
  });

  it("leaves an optional anime extra (Dual Audio) unmarked", () => {
    const format: GuideCustomFormat = { ...baseFormat, trashId: "dual-audio", name: "Anime Dual Audio", sourceGroupIds: ["group-anime-optional"] };
    expect(isAnimeDefaultFormat(format, guide)).toBe(false);
  });

  it("never marks a format outside the anime category, even in a matching group", () => {
    const format: GuideCustomFormat = { ...baseFormat, category: "groups", trashId: "not-anime", sourceGroupIds: ["group-anime-release"] };
    expect(isAnimeDefaultFormat(format, guide)).toBe(false);
  });

  it("returns false when the format carries no source group", () => {
    const format: GuideCustomFormat = { ...baseFormat, trashId: "no-group" };
    expect(isAnimeDefaultFormat(format, guide)).toBe(false);
  });
});
