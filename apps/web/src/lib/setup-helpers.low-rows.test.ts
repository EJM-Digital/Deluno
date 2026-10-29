import { describe, expect, it } from "vitest";
import type { DownloadClientItem, IndexerItem, LibraryItem, PlatformSettingsSnapshot, PolicySetItem, QualityProfileItem } from "./api";
import { friendlyRuleName, hasFriendlyRuleName } from "./guide-names";
import { guideSpecifications, readSpecifications, specificationKind } from "./guide-formats";
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
const step = (model: ReturnType<typeof buildSetupStatus>, id: string) => model.steps.find((item) => item.id === id)!;
const movies = { mediaType: "movies", rootPath: "D:\\Media\\Movies" } as LibraryItem;
const tv = { mediaType: "tv", rootPath: "D:\\Media\\TV" } as LibraryItem;
const broken = { mediaType: "movies", rootPath: "" } as LibraryItem;

describe("the setup status wording, low rows (#1329)", () => {
  it("the Media Management step reads Not configured, n/m libraries ready, or the count of movie and TV libraries, with its link and action", () => {
    expect(step(buildSetupStatus(input()), "library")).toMatchObject({ status: "Not configured", state: "not-started", to: "/settings/libraries", action: "Configure media management" });

    const partly = step(buildSetupStatus(input({ libraries: [movies, broken, tv] })), "library");
    expect(partly).toMatchObject({ status: "2/3 libraries ready", state: "failed", action: "Finish media management", complete: false });

    const one = step(buildSetupStatus(input({ libraries: [movies, tv] })), "library");
    expect(one).toMatchObject({ status: "1 movie library - 1 TV library", state: "complete", action: "Review media management", complete: true });
  });

  it("the Library Profiles step counts active profiles and quality profiles, and its target moves from the profiles page to the policy sets page", () => {
    const none = step(buildSetupStatus(input()), "media-plans");
    expect(none).toMatchObject({ status: "No quality decision yet", to: "/settings/profiles" });

    const one = step(buildSetupStatus(input({ policySets: [{ isEnabled: true } as PolicySetItem], qualityProfiles: [{} as QualityProfileItem, {} as QualityProfileItem] })), "media-plans");
    expect(one).toMatchObject({ status: "1 active library profile - 2 quality profiles", to: "/settings/policy-sets" });
  });

  it("the readiness is acquisition-ready when the basics and the connections are done and the automation is not", () => {
    const healthy = { isEnabled: true, healthStatus: "healthy" };
    const basics = { libraries: [{ ...movies, autoSearchEnabled: false } as LibraryItem], qualityProfiles: [{} as QualityProfileItem], policySets: [{ isEnabled: true } as PolicySetItem], indexers: [healthy as IndexerItem], downloadClients: [healthy as DownloadClientItem] };

    expect(buildSetupStatus(input(basics)).readiness).toBe("acquisition-ready");
    expect(buildSetupStatus(input({ ...basics, downloadClients: [] })).readiness).toBe("not-ready");
    expect(buildSetupStatus(input({ ...basics, settings: { autoStartJobs: true } as PlatformSettingsSnapshot, libraries: [{ ...basics.libraries[0], autoSearchEnabled: true } as LibraryItem] })).readiness).toBe("operationally-ready");
  });

  it("with the required steps done and import lists enabled the summary says the import lists are also configured", () => {
    const healthy = { isEnabled: true, healthStatus: "healthy" };
    const done = {
      libraries: [{ ...movies, autoSearchEnabled: true } as LibraryItem], qualityProfiles: [{} as QualityProfileItem], policySets: [{ isEnabled: true } as PolicySetItem],
      indexers: [healthy as IndexerItem], downloadClients: [healthy as DownloadClientItem], settings: { autoStartJobs: true } as PlatformSettingsSnapshot
    };

    expect(buildSetupStatus(input({ ...done, intakeSources: [{ isEnabled: true }] as never })).summary).toBe("Operational setup complete. Your import lists are also configured.");
    expect(buildSetupStatus(input({ ...done, intakeSources: [{ isEnabled: false }] as never })).summary).toBe("Operational setup complete. Import lists are optional and can be added later.");
  });
});

describe("the guide's rule names and specifications, low rows (#1329)", () => {
  it("friendlyRuleName gives the plain name for each wiki identifier and the original for any other, and hasFriendlyRuleName says which were renamed", () => {
    expect(friendlyRuleName("WEB Tier 01")).toBe("Top-tier WEB groups");
    expect(friendlyRuleName("  BR-DISK ")).toBe("Full disc image, not a video file");
    expect(friendlyRuleName("Repack / Proper")).toBe("Corrected re-release");
    expect(friendlyRuleName("NF")).toBe("Netflix");
    expect(friendlyRuleName("HDR10")).toBe("HDR10");
    expect(friendlyRuleName(null)).toBe("");
    expect(friendlyRuleName(undefined)).toBe("");
    expect(hasFriendlyRuleName("WEB Tier 01")).toBe(true);
    expect(hasFriendlyRuleName(" NF ")).toBe(true);
    expect(hasFriendlyRuleName("HDR10")).toBe(false);
    expect(hasFriendlyRuleName("")).toBe(false);
    expect(hasFriendlyRuleName(null)).toBe(false);
  });

  it("readSpecifications reads the stored specifications and refuses anything that is not an array of them", () => {
    const stored = JSON.stringify([
      { name: "AMZN", implementation: "ReleaseTitleSpecification", negate: false, required: true, fields: { value: "\\bAMZN\\b" } },
      { implementation: "SourceSpecification", negate: true, fields: [{ name: "value", value: 7 }] }
    ]);

    expect(readSpecifications(stored)).toEqual([
      { name: "AMZN", implementation: "ReleaseTitleSpecification", negate: false, required: true, value: "\\bAMZN\\b" },
      { name: "", implementation: "SourceSpecification", negate: true, required: false, value: "7" }
    ]);
    expect(readSpecifications("{\"a\":1}")).toBeNull();
    expect(readSpecifications("[]")).toBeNull();
    expect(readSpecifications("[1,2]")).toBeNull();
    expect(readSpecifications("[{\"name\":\"x\"}]")).toBeNull();
    expect(readSpecifications("[{ not json")).toBeNull();
    expect(readSpecifications("")).toBeNull();
    expect(readSpecifications(null)).toBeNull();
  });

  it("guideSpecifications reads a guide format's clauses, with a clause whose fields cannot be read giving an empty value", () => {
    const format = { sourceMatcherClauses: [
      { name: "Amazon", implementation: "ReleaseTitleSpecification", negate: false, required: true, fieldsJson: "{\"value\":\"AMZN\"}" },
      { name: "Broken", implementation: "ReleaseGroupSpecification", negate: true, required: false, fieldsJson: "{ nope" }
    ] } as never;

    expect(guideSpecifications(format)).toEqual([
      { name: "Amazon", implementation: "ReleaseTitleSpecification", negate: false, required: true, value: "AMZN" },
      { name: "Broken", implementation: "ReleaseGroupSpecification", negate: true, required: false, value: "" }
    ]);
    expect(guideSpecifications({} as never)).toEqual([]);
  });

  it("specificationKind names the kind of thing a specification looks at, dropping the Specification suffix for one it does not know", () => {
    expect(specificationKind("ReleaseTitleSpecification")).toBe("Release title");
    expect(specificationKind("ReleaseGroupSpecification")).toBe("Release group");
    expect(specificationKind("SizeSpecification")).toBe("Size");
    expect(specificationKind("AudioChannelsSpecification")).toBe("AudioChannels");
  });
});
