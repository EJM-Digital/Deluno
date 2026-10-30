import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot, type GuidePackage } from "../lib/api";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

/**
 * The rule rows of Settings > Release preferences > Custom formats (#1329): the friendly name, the source line (TRaSH format, Written
 * here, Imported specifications), what the rule matches with its condition count or "never matches anything", the sentence that
 * stands in for a description the rule above already gave, the libraries that use it, and the intent chip.
 */
const guide = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 3,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "", qualityTiers: [],
  customFormats: [{
    trashId: "web-tier-01", name: "WEB Tier 01", category: "release-group", description: "The most trusted web release groups.", originalScore: 1700,
    patterns: [], sourceMatcherClauses: [{ field: "releaseTitle", pattern: "x" }], bundleOnly: false, mappingStatus: "reviewed", mappedTraitIds: [], sourceKind: "trash-guides"
  }],
  qualityProfiles: [], bundles: [],
  sourceInventory: { schemaVersion: 2, upstreamRevision: "1", customFormats: [], qualityProfiles: [], formatGroups: [] }
} as unknown as GuidePackage;

const rule = (id: string, name: string, score: number, conditions: string, extra: Record<string, unknown> = {}) => ({
  id, name, mediaType: "movies", score, trashId: null, conditions, upgradeAllowed: true, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...extra
});
const remux = '[{"type":"releaseTitle","value":"remux","negate":false,"required":true}]';

const loaderData = {
  libraries: [{ id: "l1", name: "Films HD", mediaType: "movies", qualityProfileId: "p1", rootPath: "/films" }],
  policySets: [],
  qualityProfiles: [{ id: "p1", name: "HD", mediaType: "movies", customFormatIds: "r-guide" }],
  customFormats: [] as unknown[],
  settings: emptyPlatformSettingsSnapshot,
  guide,
  guideUpdateCheck: { isEnabled: false, lastCheckedUtc: null, lastSeenRevision: null, status: "disabled", error: null, report: null, updatedUtc: new Date(0).toISOString() },
  guideVersions: [],
  ruleMigration: null
};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsCustomFormatsPage } = await import("./settings-custom-formats-page");
const row = (name: string) => screen.getAllByRole("row").find((candidate) => candidate.textContent?.includes(name))!;

afterEach(() => cleanup());

describe("a rule's row", () => {
  it("leads with the friendly name, says where the rule came from, what it matches and how many conditions, who uses it, and the intent", () => {
    loaderData.customFormats = [
      rule("r-guide", "WEB Tier 01", 1700, remux, { trashId: "web-tier-01" }),
      rule("r-written", "No remuxes", -10000, remux),
      rule("r-same", "Also no remuxes", -50, remux),
      rule("r-imported", "From Radarr", 25, '[{"implementation":"ReleaseTitleSpecification","negate":false,"required":true,"fields":{"value":"x264"}}]'),
      rule("r-empty", "Matches nothing", 0, "[]")
    ];
    render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);

    const guideRow = within(row("WEB Tier 01"));
    expect(guideRow.getByText(/TRaSH format/)).toBeInTheDocument();
    expect(guideRow.getByText("The most trusted web release groups.")).toBeInTheDocument();
    expect(guideRow.getByText("1 condition")).toBeInTheDocument();
    expect(guideRow.getByText("Films HD")).toBeInTheDocument();
    expect(guideRow.getByText("Applied through the library's quality profile")).toBeInTheDocument();
    expect(guideRow.getByText("Strongly prefer")).toBeInTheDocument();

    const written = within(row("No remuxes"));
    expect(written.getByText("Written here")).toBeInTheDocument();
    expect(written.getByText(/with “remux”/)).toBeInTheDocument();
    expect(written.getByText("Not applied to any library yet")).toBeInTheDocument();
    expect(written.getByText("Must not have")).toBeInTheDocument();

    const repeated = within(row("Also no remuxes"));
    expect(repeated.getByText("Reads the same as the rule above")).toBeInTheDocument();
    expect(repeated.getByText("Avoid")).toBeInTheDocument();

    const imported = within(row("From Radarr"));
    expect(imported.getByText("Imported specifications")).toBeInTheDocument();
    expect(imported.getByText("1 condition")).toBeInTheDocument();
    expect(imported.getByText("Prefer")).toBeInTheDocument();

    const empty = within(row("Matches nothing"));
    expect(empty.getByText("No conditions")).toBeInTheDocument();
    expect(empty.getByText("never matches anything")).toBeInTheDocument();
    expect(empty.getByText("I do not care")).toBeInTheDocument();
  });

  it("opens the rule's drawer when the row is pressed", () => {
    loaderData.customFormats = [rule("r-written", "No remuxes", -10000, remux)];
    render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);

    fireEvent.click(row("No remuxes"));

    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });
});
