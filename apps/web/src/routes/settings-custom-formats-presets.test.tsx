import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot, type GuidePackage } from "../lib/api";

/**
 * The guide preset drawer on Release Preferences: what the preset is for, which rules it adds,
 * and Apply preset creating only the rules that are missing, once per media type.
 */

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api")>()), fetchJson: fetchJsonMock }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api/client")>()), fetchJson: fetchJsonMock }));

function format(trashId: string, name: string, mediaTypes: string[], originalScore: number, description: string) {
  return {
    trashId, name, category: "hdr", description, originalScore, patterns: [], bundleOnly: false, mappingStatus: "reviewed",
    mappedTraitIds: [], sourceKind: "trash-guides", mediaTypes
  };
}

const guide = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 3,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "", qualityTiers: [],
  customFormats: [
    format("hdr10", "HDR10", [], 100, "HDR10 signal."),
    format("dv", "Dolby Vision", ["movies"], 200, "Dolby Vision signal.")
  ],
  qualityProfiles: [],
  bundles: [{
    id: "picture-first", name: "Picture first", level: "Recommended", mediaType: "all", description: "Prefers the best picture.", bestFor: "Big screens",
    warnings: ["Needs a HDR screen."], includes: [{ trashId: "hdr10", score: 100 }, { trashId: "dv", score: null }]
  }],
  sourceInventory: { schemaVersion: 2, upstreamRevision: "1", customFormats: [], qualityProfiles: [], formatGroups: [] }
} as unknown as GuidePackage;

const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsCustomFormatsPage } = await import("./settings-custom-formats-page");

function load(customFormats: unknown[]) {
  Object.assign(loaderData, {
    libraries: [], policySets: [], qualityProfiles: [], customFormats, settings: emptyPlatformSettingsSnapshot, guide,
    guideUpdateCheck: { isEnabled: false, lastCheckedUtc: null, lastSeenRevision: null, status: "disabled", error: null, report: null, updatedUtc: new Date(0).toISOString() },
    guideVersions: [], ruleMigration: null
  });
}
function posts() {
  return fetchJsonMock.mock.calls.filter(([url, init]) => url === "/api/custom-formats" && init?.method === "POST").map(([, init]) => JSON.parse(init.body));
}
async function openPreset() {
  render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);
  fireEvent.click(screen.getByText("Picture first"));
  return await screen.findByRole("dialog");
}

describe("guide preset drawer", () => {
  beforeEach(() => { fetchJsonMock.mockReset(); fetchJsonMock.mockResolvedValue({ id: "made" }); });
  afterEach(() => { vi.clearAllMocks(); });

  it("says what the preset is for and lists every rule it adds, once per media type it serves", async () => {
    load([]);
    const dialog = await openPreset();

    expect(within(dialog).getByText("Prefers the best picture.")).toBeTruthy();
    expect(within(dialog).getByText("Big screens")).toBeTruthy();
    expect(within(dialog).getByText("Needs a HDR screen.")).toBeTruthy();
    expect(within(dialog).getByText("0 of 3 already here")).toBeTruthy();
    expect(within(dialog).getAllByText(/^HDR10$/).length).toBe(2);
    expect(within(dialog).getAllByText("HDR10 signal.").length).toBe(1);
    expect(within(dialog).getAllByText("· Movies").length).toBe(2);
    expect(within(dialog).getAllByText("· TV shows").length).toBe(1);
    expect(within(dialog).getAllByText("Dolby Vision signal.").length).toBe(1);
  });

  it("applies the preset by creating every missing rule with only its guide id, name, media type and score", async () => {
    load([]);
    const dialog = await openPreset();
    fireEvent.click(within(dialog).getByRole("button", { name: "Apply preset" }));

    expect(await within(dialog).findByText("Added 3 rules.")).toBeTruthy();
    expect(posts()).toEqual([
      { name: "HDR10", mediaType: "movies", score: 100, trashId: "hdr10", conditions: null, upgradeAllowed: true },
      { name: "HDR10", mediaType: "tv", score: 100, trashId: "hdr10", conditions: null, upgradeAllowed: true },
      { name: "Dolby Vision", mediaType: "movies", score: 200, trashId: "dv", conditions: null, upgradeAllowed: true }
    ]);
    expect(revalidate).toHaveBeenCalled();
  });

  it("creates only the rules that are missing and says how many were already there", async () => {
    load([{ id: "cf-1", name: "HDR10", mediaType: "movies", trashId: "hdr10", score: 100 }]);
    const dialog = await openPreset();
    expect(within(dialog).getByText("1 of 3 already here")).toBeTruthy();
    expect(within(dialog).getAllByText("Added").length).toBe(1);
    fireEvent.click(within(dialog).getByRole("button", { name: "Apply preset" }));

    expect(await within(dialog).findByText("Added 2 rules — 1 were already here.")).toBeTruthy();
    expect(posts().map((body) => `${body.trashId}:${body.mediaType}`)).toEqual(["hdr10:tv", "dv:movies"]);
  });

  it("turns Apply preset off when every rule is already here, and makes nothing", async () => {
    load([
      { id: "cf-1", name: "HDR10", mediaType: "movies", trashId: "hdr10", score: 100 },
      { id: "cf-2", name: "HDR10", mediaType: "tv", trashId: "hdr10", score: 100 },
      { id: "cf-3", name: "Dolby Vision", mediaType: "movies", trashId: "dv", score: 200 }
    ]);
    const dialog = await openPreset();

    expect(within(dialog).getByText("3 of 3 already here")).toBeTruthy();
    expect((within(dialog).getByRole("button", { name: "Apply preset" }) as HTMLButtonElement).disabled).toBe(true);
    expect(posts()).toEqual([]);
  });

  it("names the rule and media type that could not be added when one fails partway", async () => {
    load([]);
    fetchJsonMock.mockReset();
    fetchJsonMock.mockResolvedValueOnce({ id: "made" }).mockRejectedValueOnce(new Error("The database is locked."));
    const dialog = await openPreset();
    fireEvent.click(within(dialog).getByRole("button", { name: "Apply preset" }));

    expect(await within(dialog).findByText(/HDR10 \(TV shows\) could not be added: The database is locked\./)).toBeTruthy();
    await waitFor(() => expect(posts().length).toBe(2));
  });
});
