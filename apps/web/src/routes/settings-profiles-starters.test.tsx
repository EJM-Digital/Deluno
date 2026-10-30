import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { GuidePackage, QualityModelSnapshot } from "../lib/api";

/**
 * The New quality profile drawer's starting points (#1329): the guide's balanced profile for the media type seeds the tiers, the
 * "Add the recommended formats" switch creates that profile's missing TRaSH formats on save (and stores their answers) or, switched
 * off, makes the profile with none, and changing the media type of an untouched profile re-seeds the tiers and clears the formats.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
const unsaved = vi.hoisted(() => ({ dirty: false }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: (dirty: boolean) => { unsaved.dirty = dirty; } }));
// What the saved profile's preferences compile to is read for the drawer's "Try a release" box; its own tests are elsewhere.
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  compileQualityProfilePreferences: vi.fn(async () => ({
    registryVersion: "test", profileId: "qp-1", profileName: "HD films", plan: { families: [], requiredTraitIds: [], forbiddenTraitIds: [], sources: [] },
    planHash: "hash", advancedRules: [], warnings: [], requiresReview: false, storedUtc: "2026-10-01T00:00:00Z"
  }))
}));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn() })
  };
});

const tier = (name: string, rank: number) => ({ name, rank, movieMinGb: 1, moviePreferredGb: 2.5, movieMaxGb: 4, episodeMinMb: 100, episodePreferredMb: 400, episodeMaxMb: 800, scoreCeiling: 0 });
const qualityModel: QualityModelSnapshot = {
  version: "test",
  tiers: [tier("WEB 720p", 1), tier("WEB 1080p", 2), tier("Bluray 1080p", 3)],
  upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true },
  updatedUtc: new Date(0).toISOString()
};
const guide = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 1,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "",
  qualityTiers: [{ id: "t720", label: "WEB 720p" }, { id: "t1080", label: "WEB 1080p" }, { id: "tbd", label: "Bluray 1080p" }],
  customFormats: [
    { trashId: "x1", name: "Repack Proper", category: "misc", description: "", originalScore: 5, patterns: [], mediaTypes: ["movies", "tv"], bundleOnly: false, mappingStatus: "reviewed", mappedTraitIds: [], sourceKind: "trash-guides" },
    { trashId: "x2", name: "Upscaled", category: "misc", description: "", originalScore: -10000, patterns: [], mediaTypes: ["movies", "tv"], bundleOnly: false, mappingStatus: "reviewed", mappedTraitIds: [], sourceKind: "trash-guides" }
  ],
  qualityProfiles: [
    { id: "movies-balanced", name: "Balanced", mediaType: "movies", description: "", qualityOrder: ["t1080", "t720"], cutoffQualityId: "t1080", minFormatScore: 0, cutoffFormatScore: 0, language: "Any", recommendedFormats: [{ trashId: "x1", score: 5 }, { trashId: "x2", score: -10000 }] },
    { id: "tv-balanced", name: "Balanced", mediaType: "tv", description: "", qualityOrder: ["tbd"], cutoffQualityId: "tbd", minFormatScore: 0, cutoffFormatScore: 0, language: "Any", recommendedFormats: [] }
  ],
  bundles: []
} as unknown as GuidePackage;

const profile = (overrides: Record<string, unknown> = {}) => ({
  id: "qp-1", name: "HD films", mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p, WEB 1080p", customFormatIds: "cf-1",
  upgradeUntilCutoff: true, upgradeUnknownItems: false, sizeRules: [], upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true },
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...overrides
});

let loaderData: Record<string, unknown>;
const revalidateMock = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock })
}));

const { SettingsProfilesPage } = await import("./settings-profiles-page");
const { compileQualityProfilePreferences } = await import("../lib/api");

vi.setConfig({ testTimeout: 20_000 });

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function load(profiles: unknown[] = []) {
  loaderData = {
    libraries: [], qualityProfiles: profiles, customFormats: [], settings: {}, qualityModel, guide
  };
}

function renderPage() {
  return render(<MemoryRouter><SettingsProfilesPage /></MemoryRouter>);
}

/** A switch in a labelled row: its label wraps it, so it is found through the label's own text. */
function switchOf(scope: ReturnType<typeof within>, label: string) {
  const row = scope.getAllByText(label).map((element: HTMLElement) => element.closest("label")).find((candidate: HTMLLabelElement | null) => candidate !== null);
  const found = (row?.htmlFor ? document.getElementById(row.htmlFor) : row?.querySelector("[role=switch]")) as HTMLElement | null | undefined;
  if (!found) throw new Error(`No switch labelled ${label}`);
  return found;
}

/** The tiers the drawer lists, most preferred first, read from their move buttons. */
function tierNames(scope: ReturnType<typeof within>) {
  return scope.queryAllByRole("button", { name: /^Move .* up$/ }).map((button: HTMLElement) => (button.getAttribute("aria-label") ?? "").replace(/^Move /, "").replace(/ up$/, ""));
}

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

let created: Array<{ id: string; trashId: string }>;
beforeEach(() => {
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation((path: string, init?: RequestInit) => {
    if (init?.method === "POST" && path === "/api/custom-formats") {
      const sent = JSON.parse(String(init.body));
      const made = { id: `cf-${sent.trashId}`, name: sent.name, mediaType: sent.mediaType, trashId: sent.trashId, score: sent.score };
      created.push({ id: made.id, trashId: sent.trashId });
      return reply(made);
    }
    return reply(init?.method === "POST" && path === "/api/quality-profiles" ? profile({ id: "new" }) : {});
  });
  created = [];
  load();
});

async function openNew() {
  renderPage();
  fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);
  return within(await screen.findByRole("dialog"));
}

describe("Add the recommended formats", () => {
  it("is on for a new profile and says how many of the guide's preference rules it will add", async () => {
    const drawer = await openNew();

    expect(switchOf(drawer, "Add the recommended formats")).toBeChecked();
    expect(drawer.getByText(/2 TRaSH-guide preference rules/)).toBeVisible();
  });

  it("creates the formats that do not exist yet on save, attaches them and stores the answers as the profile's own", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Profile name"), { target: { value: "With formats" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create quality profile" }));

    await waitFor(() => expect(calls("POST", "/api/quality-profiles")).toHaveLength(1));
    expect(created.map((entry) => entry.trashId).sort()).toEqual(["x1", "x2"]);
    const body = bodyOf(calls("POST", "/api/quality-profiles")[0]);
    expect(body.customFormatIds.split(", ").sort()).toEqual(["cf-x1", "cf-x2"]);
    expect(Object.keys(body.formatIntents).sort()).toEqual(["cf-x1", "cf-x2"]);
  });

  it("switched off, makes the profile with no recommended formats and creates none", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Profile name"), { target: { value: "Plain" } });
    fireEvent.click(switchOf(drawer, "Add the recommended formats"));
    expect(switchOf(drawer, "Add the recommended formats")).not.toBeChecked();

    fireEvent.click(drawer.getByRole("button", { name: "Create quality profile" }));

    await waitFor(() => expect(calls("POST", "/api/quality-profiles")).toHaveLength(1));
    expect(created).toEqual([]);
    expect(bodyOf(calls("POST", "/api/quality-profiles")[0]).customFormatIds).toBe("");
  });
});

describe("switching the media type of a new profile", () => {
  it("starts on the movie balanced profile's tiers and, untouched, re-seeds them from the TV one", async () => {
    const drawer = await openNew();
    expect(tierNames(drawer)).toEqual(["WEB 1080p", "WEB 720p"]);

    fireEvent.click(drawer.getByRole("radio", { name: "TV shows" }));

    expect(tierNames(drawer)).toEqual(["Bluray 1080p"]);
  });

  it("keeps the tiers a person chose themselves when the type is switched", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Add a tier"), { target: { value: "Bluray 1080p" } });
    expect(tierNames(drawer)).toEqual(["Bluray 1080p", "WEB 1080p", "WEB 720p"]);

    fireEvent.click(drawer.getByRole("radio", { name: "TV shows" }));

    expect(tierNames(drawer)).toEqual(["Bluray 1080p", "WEB 1080p", "WEB 720p"]);
  });
});

describe("Effective release preferences on a saved profile", () => {
  async function openSaved() {
    load([profile()]);
    renderPage();
    fireEvent.click(screen.getAllByText("HD films")[0]);
    return within(await screen.findByRole("dialog"));
  }

  it("says it is compiling while the plan is on its way", async () => {
    vi.mocked(compileQualityProfilePreferences).mockImplementationOnce(() => new Promise(() => undefined));
    const drawer = await openSaved();

    expect(await drawer.findByText("Compiling the saved profile into the typed decision plan…")).toBeVisible();
    expect(drawer.getByText("Loading…")).toBeVisible();
  });

  it("says why when the plan could not be loaded and shows no plan", async () => {
    vi.mocked(compileQualityProfilePreferences).mockRejectedValueOnce(new Error("The plan service is down."));
    const drawer = await openSaved();

    expect(await drawer.findByRole("alert")).toHaveTextContent("The plan service is down.");
    expect(drawer.queryByText(/typed families/)).toBeNull();
  });

  it("falls back to the plain sentence when the failure carries no words of its own", async () => {
    vi.mocked(compileQualityProfilePreferences).mockRejectedValueOnce("nothing");
    const drawer = await openSaved();

    expect(await drawer.findByRole("alert")).toHaveTextContent("The typed preference plan could not be loaded.");
  });

  it("shows the compiled plan's family count once it arrives", async () => {
    const drawer = await openSaved();

    expect(await drawer.findByText("0 typed families")).toBeVisible();
  });
});
