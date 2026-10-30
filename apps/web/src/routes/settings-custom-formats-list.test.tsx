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


afterEach(() => cleanup());

const imported = '[{"implementation":"ReleaseTitleSpecification","negate":false,"required":true,"fields":{"value":"x264"}}]';

describe("the Advanced release rules list", () => {
  it("counts its rules beside the title and says the full guide and custom controls live here", () => {
    loaderData.customFormats = [
      rule("r-guide", "WEB Tier 01", 1700, remux, { trashId: "web-tier-01" }),
      rule("r-written", "No remuxes", -10000, remux),
      rule("r-imported", "From Radarr", 25, imported)
    ];
    render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);

    expect(screen.getByText(/^advanced release rules$/i)).toBeInTheDocument();
    expect(screen.getByText("3 rules · full TRaSH and custom controls")).toBeInTheDocument();
    const names = screen.getAllByRole("row").map((candidate) => candidate.textContent ?? "");
    for (const name of ["WEB Tier 01", "No remuxes", "From Radarr"]) expect(names.some((text) => text.includes(name))).toBe(true);
  });

  it("filters by Movies or TV with a count on each, and shows only that kind", () => {
    loaderData.customFormats = [
      rule("r-film", "Film rule", 10, remux),
      rule("r-show", "Show rule", 20, remux, { mediaType: "tv" })
    ];
    render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);
    const filter = screen.getByRole("radiogroup", { name: "Filter by media type" });
    const options = within(filter).getAllByRole("radio").map((radio) => radio.textContent ?? "");
    expect(options[0]).toMatch(/All.*2/);
    expect(options[1]).toMatch(/Movies.*1/);
    expect(options[2]).toMatch(/TV.*1/);
    const rowText = () => screen.getAllByRole("row").map((candidate) => candidate.textContent ?? "").join("|");
    expect(rowText()).toContain("Film rule");
    expect(rowText()).toContain("Show rule");
    const headers = Array.from(document.querySelectorAll('[class*="list-group-height"]')).map((header) => header.textContent ?? "");
    expect(headers).toHaveLength(2);
    expect(headers[0]).toMatch(/Movies/);
    expect(headers[1]).toMatch(/TV/);

    fireEvent.click(within(filter).getByRole("radio", { name: /^TV/ }));
    expect(rowText()).toContain("Show rule");
    expect(rowText()).not.toContain("Film rule");

    fireEvent.click(within(filter).getByRole("radio", { name: /^Movies/ }));
    expect(rowText()).toContain("Film rule");
    expect(rowText()).not.toContain("Show rule");
  });

  it("says there are no advanced rules yet, with a New custom rule button, when there are none", () => {
    loaderData.customFormats = [];
    render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);

    expect(screen.getByText("No advanced release rules yet")).toBeInTheDocument();
    expect(screen.queryByText(/full TRaSH and custom controls/)).toBeNull();
    fireEvent.click(screen.getAllByRole("button", { name: "New custom rule" }).at(-1)!);
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });
});
