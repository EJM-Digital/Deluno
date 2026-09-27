import { render, screen, within } from "@testing-library/react";
import { fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot, type GuidePackage } from "../lib/api";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

/**
 * #771 (audit F): a render test for the Custom Formats settings screen,
 * covering the guide picker's regional categories (#779) and the marker on a
 * fresh anime profile's default formats (#781) — the two things a plain
 * vitest render can check without a live backend.
 */
const guide: GuidePackage = {
  id: "guide",
  name: "Test guide",
  version: 1,
  schemaVersion: 3,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "",
  qualityTiers: [],
  customFormats: [
    {
      trashId: "hdr-hdr10",
      name: "HDR10",
      category: "hdr",
      description: "HDR10 signal.",
      originalScore: 100,
      patterns: [],
      bundleOnly: false,
      mappingStatus: "reviewed",
      mappedTraitIds: [],
      sourceKind: "trash-guides"
    },
    {
      trashId: "french-release-group",
      name: "French Release Group",
      category: "french",
      description: "A French release group.",
      originalScore: 50,
      patterns: [],
      bundleOnly: false,
      mappingStatus: "advanced",
      mappedTraitIds: [],
      sourceKind: "trash-guides"
    },
    {
      trashId: "anime-bd-tier-01",
      name: "Anime BD Tier 01",
      category: "anime",
      description: "A trusted anime Blu-ray release group.",
      originalScore: 1300,
      patterns: [],
      bundleOnly: false,
      mappingStatus: "advanced",
      mappedTraitIds: [],
      sourceKind: "trash-guides",
      sourceGroupIds: ["group-anime-release"]
    },
    {
      trashId: "anime-dual-audio",
      name: "Anime Dual Audio",
      category: "anime",
      description: "Dual audio (EN/JP) track present.",
      originalScore: 0,
      patterns: [],
      bundleOnly: false,
      mappingStatus: "advanced",
      mappedTraitIds: [],
      sourceKind: "trash-guides",
      sourceGroupIds: ["group-anime-optional"]
    }
  ],
  qualityProfiles: [],
  bundles: [],
  sourceInventory: {
    schemaVersion: 2,
    upstreamRevision: "1",
    customFormats: [],
    qualityProfiles: [],
    formatGroups: [
      { trashId: "group-anime-release", name: "[Release Groups] Anime", mediaType: "tv", sourcePath: "", sourceBlobSha: "", customFormats: [], qualityProfileIds: [] },
      { trashId: "group-anime-optional", name: "[Optional] Anime Dual Audio", mediaType: "tv", sourcePath: "", sourceBlobSha: "", customFormats: [], qualityProfileIds: [] }
    ]
  }
};

const loaderData = {
  libraries: [],
  policySets: [],
  qualityProfiles: [],
  customFormats: [],
  settings: emptyPlatformSettingsSnapshot,
  guide,
  guideUpdateCheck: {
    isEnabled: false,
    lastCheckedUtc: null,
    lastSeenRevision: null,
    status: "disabled",
    error: null,
    report: null,
    updatedUtc: new Date(0).toISOString()
  },
  guideVersions: [],
  ruleMigration: null
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsCustomFormatsPage } = await import("./settings-custom-formats-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsCustomFormatsPage />
    </MemoryRouter>
  );
}

describe("Custom Formats settings screen", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("opens the New custom rule drawer with a guide choice picker", async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New custom rule" })[0]);

    expect(await screen.findByRole("dialog")).toBeTruthy();
    expect(screen.getByRole("combobox", { name: "Guide choice· optional" })).toBeTruthy();
  });

  it("groups a regional guide format under its own named category (#779)", async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New custom rule" })[0]);
    await screen.findByRole("dialog");

    const picker = screen.getByRole("combobox", { name: "Guide choice· optional" });
    const frenchGroup = within(picker).getByRole("group", { name: "French" });
    expect(within(frenchGroup).getByRole("option", { name: "French Release Group" })).toBeTruthy();
  });

  it("marks a fresh anime profile's default format and leaves the optional extra unmarked (#781)", async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New custom rule" })[0]);
    await screen.findByRole("dialog");

    const picker = screen.getByRole("combobox", { name: "Guide choice· optional" });
    const animeGroup = within(picker).getByRole("group", { name: "Anime" });
    expect(within(animeGroup).getByRole("option", { name: /Top-tier anime Blu-ray groups.*Default for anime profiles/ })).toBeTruthy();
    expect(within(animeGroup).getByRole("option", { name: "Anime Dual Audio" })).toBeTruthy();
  });
});
