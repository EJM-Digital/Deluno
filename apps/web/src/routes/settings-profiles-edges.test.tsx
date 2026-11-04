import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GuidePackage, QualityModelSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Quality Profiles, the corners the other files do not reach: the list as it sorts and groups, closing and discarding,
 * every template's answers, each way a save or a delete can end, the compiled preference plan in all its shapes, and the
 * rules that create the template's recommended formats. The fetch wrapper is a recorder; the plan compiler is a spy.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));
const compile = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  compileQualityProfilePreferences: compile,
  judgeDraftProfile: vi.fn().mockResolvedValue({ releaseName: "x", candidateEvaluation: { status: "meetsPlan", reasons: [] } })
}));
vi.mock("../lib/live-refresh", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/live-refresh")>()),
  useLiveRefresh: () => undefined
}));
// The props the page hands the drawer and its confirmations, so a close request that no click can make is still made.
const seen = vi.hoisted(() => ({ drawer: undefined as undefined | { onOpenChange: (open: boolean) => void }, confirms: new Map<string, { onOpenChange: (open: boolean) => void }>() }));
vi.mock("../components/ui/drawer", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/ui/drawer")>();
  return { ...original, Drawer: (props: Parameters<typeof original.Drawer>[0]) => { seen.drawer = props; return original.Drawer(props); } };
});
vi.mock("../components/ui/confirm-dialog", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/ui/confirm-dialog")>();
  return { ...original, ConfirmDialog: (props: Parameters<typeof original.ConfirmDialog>[0]) => { seen.confirms.set(props.title, props); return original.ConfirmDialog(props); } };
});
const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsProfilesPage } = await import("./settings-profiles-page");

const tier = (name: string, rank: number) => ({ name, rank, movieMinGb: 1, moviePreferredGb: 3, movieMaxGb: 8, episodeMinMb: 100, episodePreferredMb: 400, episodeMaxMb: 800, scoreCeiling: 0 });
const qualityModel: QualityModelSnapshot = {
  version: "test",
  tiers: [tier("WEB 720p", 1), tier("WEB 1080p", 2), tier("Bluray 2160p", 3)],
  upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true },
  updatedUtc: new Date(0).toISOString()
};
const guideFormat = (trashId: string, name: string, mediaTypes?: string[]) => ({ trashId, name, category: "misc", description: name, originalScore: 100, patterns: [], bundleOnly: false, mappingStatus: "reviewed", mappedTraitIds: [], sourceKind: "trash-guides", mediaTypes });
const starter = (id: string, name: string, mediaType: string, over: Record<string, unknown> = {}) => ({
  id, name, tagline: "", description: `About ${name}.`, highlights: [], mediaType, qualityOrder: ["web-1080p", "web-720p"], cutoffQualityId: "web-1080p",
  upgradeAllowed: true, minFormatScore: 0, cutoffFormatScore: 6000, recommendedFormats: [], ...over
});
const guide = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 1,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "",
  qualityTiers: [
    { id: "web-720p", label: "WEB 720p", source: "web", resolution: "720p", minMbPerMin: 1, maxMbPerMin: 4, rank: 1 },
    { id: "web-1080p", label: "WEB 1080p", source: "web", resolution: "1080p", minMbPerMin: 2, maxMbPerMin: 8, rank: 2 },
    { id: "odd", label: "Odd tier", source: "web", resolution: "?", minMbPerMin: 1, maxMbPerMin: 2, rank: 9 }
  ],
  customFormats: [guideFormat("hdr10", "HDR10"), guideFormat("dts", "DTS"), guideFormat("tvonly", "TV only", ["tv"])],
  qualityProfiles: [
    starter("balanced-movies", "Balanced", "movies", {
      qualityOrder: ["web-1080p", "odd", "missing-id"], minFormatScore: 1500, minUpgradeFormatScore: 100, language: null,
      recommendedFormats: [{ trashId: "hdr10", score: 500 }, { trashId: "dts", score: 300 }, { trashId: "ghost", score: 1 }, { trashId: "tvonly", score: 5 }]
    }),
    starter("original-movies", "Original", "movies", { qualityOrder: ["web-720p"], cutoffQualityId: "unknown-cutoff", language: "Original" }),
    starter("any-movies", "Anything", "movies", { language: "Any" }),
    starter("french-movies", "French", "movies", { language: " fRench " }),
    starter("klingon-movies", "Klingon", "movies", { language: "Klingon" }),
    starter("balanced-tv", "Balanced TV", "tv", { qualityOrder: ["web-720p"], cutoffQualityId: "web-720p", recommendedFormats: [{ trashId: "hdr10", score: 500 }] })
  ],
  bundles: []
} as unknown as GuidePackage;

const existingFormats = [
  { id: "cf-hdr", name: "HDR10", mediaType: "movies", trashId: "hdr10", score: 500 },
  { id: "cf-dts-tv", name: "DTS", mediaType: "tv", trashId: "dts", score: 300 },
  { id: "cf-mine", name: "Mine", mediaType: "movies", trashId: null, score: 0 }
];
const profile = (over: Record<string, unknown> = {}) => ({
  id: "qp-1", name: "Films", mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p, WEB 1080p", customFormatIds: "cf-hdr",
  upgradeUntilCutoff: true, upgradeUnknownItems: false, sizeRules: [], upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true },
  formatIntents: { "cf-hdr": "prefer" }, acquisition: null, scoreLimits: null, languagePreference: null, releasePreferencePlan: null, ...over
});

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const emptyPlan = () => ({
  registryVersion: "t", profileId: "qp-1", profileName: "Films", planHash: "h", advancedRules: [], warnings: [], requiresReview: false, storedUtc: "2026-10-01T00:00:00Z",
  plan: { id: "p", version: "1", mediaType: "movies", families: [], requiredTraitIds: [], forbiddenTraitIds: [], sources: [] }
});
function reset(profiles: unknown[] = [profile()], libraries: unknown[] = []) {
  Object.assign(loaderData, { libraries, qualityProfiles: profiles, customFormats: existingFormats, settings: {}, qualityModel, guide });
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockResolvedValue(json(profile()));
  compile.mockReset();
  compile.mockResolvedValue(emptyPlan());
  toastMock.success.mockReset();
  toastMock.error.mockReset();
  revalidate.mockReset();
  seen.confirms.clear();
}
const renderPage = () => render(<MemoryRouter><SettingsProfilesPage /></MemoryRouter>);
const writes = () => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method && (init as RequestInit).method !== "GET");
const lastBody = () => JSON.parse(String((writes().at(-1)![1] as RequestInit).body));
async function openProfile(name = "Films") {
  renderPage();
  fireEvent.click(screen.getByText(name));
  return await screen.findByRole("dialog");
}
async function openNew() {
  renderPage();
  fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);
  return await screen.findByRole("dialog");
}
const template = (dialog: HTMLElement, id: string) => fireEvent.change(within(dialog).getByRole("combobox", { name: /Choose a template/ }), { target: { value: id } });
const save = (dialog: HTMLElement, label = "Save quality profile") => fireEvent.click(within(dialog).getByRole("button", { name: label }));
const touch = (dialog: HTMLElement) => fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Touched" } });
const languageOf = (dialog: HTMLElement) => (within(dialog).getByRole("combobox", { name: /Preferred language/ }) as HTMLSelectElement).value;

describe("the list of profiles", () => {
  afterEach(() => vi.clearAllMocks());

  it("sorts by media type then name, groups movies and TV, and says what each row holds", () => {
    reset([
      profile({ id: "a", name: "Zed", cutoffQuality: "WEB 720p", allowedQualities: "WEB 720p", customFormatIds: "" }),
      profile({ id: "b", name: "Alpha", allowedQualities: "", upgradeUntilCutoff: false }),
      profile({ id: "c", name: "Series", mediaType: "tv", allowedQualities: "WEB 720p, Mystery 4K" })
    ], [{ id: "l1", name: "Orphan", mediaType: "movies", qualityProfileId: null }, { id: "l2", name: "Shelf", mediaType: "movies", qualityProfileId: "b" }]);
    renderPage();

    const rows = screen.getAllByRole("row").map((row) => row.textContent ?? "");
    const order = ["Alpha", "Zed", "Series"].map((name) => rows.findIndex((text) => text.includes(name)));
    expect(order).toEqual([...order].sort((a, b) => a - b));
    expect(rows[order[0]]).toContain("None");
    expect(rows[order[0]]).toContain("No upgrades");
    expect(rows[order[0]]).toContain("Shelf");
    expect(rows[order[1]]).toContain("No formats");
    expect(rows[order[1]]).toContain("Not assigned");
    expect(rows[order[2]]).toContain("TV shows");
    expect(rows[order[2]]).toContain("Check tiers");
    expect(screen.getAllByText("TV shows").length).toBeGreaterThanOrEqual(2);
    expect(screen.queryByText("Orphan")).toBeNull();
  });
});

describe("closing the drawer", () => {
  beforeEach(() => reset());
  afterEach(() => vi.clearAllMocks());

  it("Cancel on an untouched profile closes it without asking", async () => {
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.queryByText("Discard unsaved changes?")).toBeNull();
  });

  it("a close request from the drawer asks first once something changed, and a request to open it again does nothing", async () => {
    const dialog = await openProfile();
    act(() => seen.drawer!.onOpenChange(true));
    expect(screen.queryByText("Discard unsaved changes?")).toBeNull();

    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Changed" } });
    act(() => seen.drawer!.onOpenChange(false));
    expect(await screen.findByText("Discard unsaved changes?")).toBeTruthy();

    act(() => seen.confirms.get("Discard unsaved changes?")!.onOpenChange(true));
    expect(screen.getByText("Discard unsaved changes?")).toBeTruthy();
    act(() => seen.confirms.get("Discard unsaved changes?")!.onOpenChange(false));
    await waitFor(() => expect(screen.queryByText("Discard unsaved changes?")).toBeNull());
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("a plan that arrives after the drawer moved to another profile is ignored, and so is one that fails", async () => {
    reset([profile(), profile({ id: "qp-2", name: "Shows" })]);
    let resolveFirst: (value: unknown) => void = () => undefined;
    let rejectFirst: (reason: unknown) => void = () => undefined;
    compile.mockImplementationOnce(() => new Promise((resolve) => { resolveFirst = resolve; }));
    const first = await openProfile("Films");
    fireEvent.click(within(first).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    compile.mockImplementationOnce(() => new Promise(() => undefined));
    fireEvent.click(screen.getByText("Shows"));
    const second = await screen.findByRole("dialog");
    await act(async () => { resolveFirst({ ...emptyPlan(), plan: { ...emptyPlan().plan, families: [{ id: "f", dimension: "late", order: 1, intent: "ranked", levels: [], targetLevelId: null, upgradeDriving: true, transient: false }] } }); });
    expect(within(second).getByText(/Compiling the saved profile/)).toBeTruthy();
    expect(within(second).queryByText("Late")).toBeNull();

    fireEvent.click(within(second).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    compile.mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectFirst = reject; }));
    fireEvent.click(screen.getByText("Films"));
    const third = await screen.findByRole("dialog");
    fireEvent.click(within(third).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    compile.mockImplementationOnce(() => new Promise(() => undefined));
    fireEvent.click(screen.getByText("Shows"));
    const fourth = await screen.findByRole("dialog");
    await act(async () => { rejectFirst(new Error("Too late.")); });
    expect(within(fourth).queryByText("Too late.")).toBeNull();
    expect(within(fourth).getByText(/Compiling the saved profile/)).toBeTruthy();
  });
});

describe("templates and the media type", () => {
  beforeEach(() => reset([], []));
  afterEach(() => vi.clearAllMocks());

  it("a template fills the tiers the quality model knows, the guide's numbers and plain choices, and choosing none again empties the form", async () => {
    const dialog = await openNew();
    template(dialog, "balanced-movies");
    expect(within(dialog).queryByRole("button", { name: /Odd tier/ })).toBeNull();
    expect(within(dialog).getByRole("button", { name: "Remove WEB 1080p" })).toBeTruthy();
    expect(within(dialog).getByRole("switch", { name: /Never grab copies/ }).getAttribute("aria-checked")).toBe("true");
    fireEvent.click(within(dialog).getByText("Show the numbers"));
    expect((within(dialog).getByLabelText("Minimum custom-format score to grab") as HTMLInputElement).value).toBe("1500");
    expect((within(dialog).getByLabelText("Cutoff custom-format score to stop upgrading") as HTMLInputElement).value).toBe("6000");
    expect((within(dialog).getByLabelText("Minimum custom-format score gain required for an upgrade") as HTMLInputElement).value).toBe("100");
    expect(languageOf(dialog)).toBe("");

    template(dialog, "");
    expect(within(dialog).queryByRole("button", { name: "Remove WEB 1080p" })).toBeNull();
    expect((within(dialog).getByPlaceholderText("Name this quality profile") as HTMLInputElement).value).toBe("");
  });

  it("a template with no minimum score leaves 'never grab bad copies' off, and TV templates are named for TV", async () => {
    const dialog = await openNew();
    template(dialog, "original-movies");
    expect(within(dialog).getByRole("switch", { name: /Never grab copies/ }).getAttribute("aria-checked")).toBe("false");
    fireEvent.click(within(dialog).getByText("Show the numbers"));
    expect((within(dialog).getByLabelText("Minimum custom-format score to grab") as HTMLInputElement).value).toBe("");
    expect((within(dialog).getByLabelText("Minimum custom-format score gain required for an upgrade") as HTMLInputElement).value).toBe("1");

    template(dialog, "balanced-tv");
    expect((within(dialog).getByPlaceholderText("Name this quality profile") as HTMLInputElement).value).toBe("Movies / Original");
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "" } });
    template(dialog, "balanced-tv");
    expect((within(dialog).getByPlaceholderText("Name this quality profile") as HTMLInputElement).value).toBe("TV Shows / Balanced TV");
  });

  it("reads the guide's language answer: a language by name, the original language, none for Any, none for one it does not know", async () => {
    const dialog = await openNew();
    template(dialog, "french-movies");
    expect(languageOf(dialog)).toBe("fr");
    template(dialog, "original-movies");
    expect(languageOf(dialog)).toBe("original");
    template(dialog, "any-movies");
    expect(languageOf(dialog)).toBe("");
    template(dialog, "klingon-movies");
    expect(languageOf(dialog)).toBe("");
    template(dialog, "balanced-movies");
    expect(languageOf(dialog)).toBe("");
  });

  it("an unknown guide tier or cutoff keeps its own name rather than inventing a quality", async () => {
    const dialog = await openNew();
    template(dialog, "original-movies");
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Odd" } });
    save(dialog, "Create quality profile");
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(lastBody()).toMatchObject({ cutoffQuality: "unknown-cutoff", allowedQualities: "WEB 720p" });
  });

  it("explains the baseline a custom profile starts from, for movies and for TV, and names the template once one is chosen", async () => {
    const dialog = await openNew();
    expect(within(dialog).getByText(/the baseline every custom movie profile starts with \(from "Balanced"\)/)).toBeTruthy();

    fireEvent.click(within(dialog).getByRole("radio", { name: "TV shows" }));
    expect(within(dialog).getByText(/the baseline every custom TV profile starts with \(from "Balanced TV"\)/)).toBeTruthy();
    expect(within(dialog).getByText("1 TRaSH-guide preference rule", { exact: false })).toBeTruthy();

    template(dialog, "balanced-tv");
    expect(within(dialog).queryByText(/the baseline every custom/)).toBeNull();
    expect(within(dialog).getByText("About Balanced TV.")).toBeTruthy();
  });

  it("choosing the media type it already has changes nothing", async () => {
    reset([profile()]);
    const dialog = await openProfile();
    touch(dialog);
    fireEvent.click(within(dialog).getByRole("radio", { name: "Movies" }));
    save(dialog);
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(lastBody().customFormatIds).toBe("cf-hdr");
  });
});

describe("the plain controls and numbers", () => {
  beforeEach(() => reset());
  afterEach(() => vi.clearAllMocks());

  it("a typed score is kept, and clearing the box goes back to the plain choice", async () => {
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByText("Show the numbers"));
    for (const label of ["Minimum custom-format score to grab", "Cutoff custom-format score to stop upgrading", "Minimum custom-format score gain required for an upgrade"]) {
      const box = within(dialog).getByLabelText(label) as HTMLInputElement;
      fireEvent.change(box, { target: { value: "250" } });
      expect(box.value).toBe("250");
      fireEvent.change(box, { target: { value: "" } });
      expect(box.value).toBe("");
    }
  });

  it("choosing 'No preference' after a language clears the language, and the waits fall back to zero when emptied", async () => {
    const dialog = await openProfile();
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Preferred language/ }), { target: { value: "fr" } });
    expect(languageOf(dialog)).toBe("fr");
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Preferred language/ }), { target: { value: "" } });
    expect(languageOf(dialog)).toBe("");

    fireEvent.click(within(dialog).getByRole("button", { name: /Who do you want it from/ }));
    for (const name of [/Wait before taking a usenet release/, /Wait before taking a torrent/]) {
      const box = within(dialog).getByRole("spinbutton", { name }) as HTMLInputElement;
      fireEvent.change(box, { target: { value: "15" } });
      expect(box.value).toBe("15");
      fireEvent.change(box, { target: { value: "" } });
      expect(box.value).toBe("0");
    }
  });

  it("opens a saved profile on the plain choices its saved scores are nearest to, and a TV profile as TV", async () => {
    reset([profile({ mediaType: "tv", scoreLimits: { minFormatScore: 1500, cutoffFormatScore: 6000, minUpgradeFormatScore: 100 } })]);
    const dialog = await openProfile();
    expect(within(dialog).getByText(/Quality profile · TV ·/)).toBeTruthy();
    expect(within(dialog).getByRole("switch", { name: /Never grab copies/ }).getAttribute("aria-checked")).toBe("true");
    fireEvent.click(within(dialog).getByText("Show the numbers"));
    expect((within(dialog).getByLabelText("Minimum custom-format score to grab") as HTMLInputElement).value).toBe("1500");
  });

  it("adds a tier in rank order even when a tier the quality model lacks is already allowed", async () => {
    reset([profile({ allowedQualities: "Mystery 4K, WEB 1080p" })]);
    const dialog = await openProfile();
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Add a tier/ }), { target: { value: "Bluray 2160p" } });
    expect(within(dialog).getByRole("button", { name: "Move Bluray 2160p up" })).toBeTruthy();
    expect((within(dialog).getByRole("button", { name: "Move Bluray 2160p up" }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe("saving and its endings", () => {
  beforeEach(() => reset());
  afterEach(() => vi.clearAllMocks());

  it("a profile with no stop tier saves its best allowed tier as the stop", async () => {
    reset([profile({ cutoffQuality: "" })]);
    const dialog = await openProfile();
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Films 2" } });
    save(dialog);
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(lastBody().cutoffQuality).toBe("WEB 1080p");
  });

  it("shows the server's words about the stop tier in the footer and next to that field", async () => {
    vi.mocked(authedFetch).mockResolvedValue(json({ errors: { cutoffQuality: ["That stop tier is not allowed."] } }, 400));
    const dialog = await openProfile();
    touch(dialog);
    save(dialog);
    expect((await within(dialog).findAllByText("That stop tier is not allowed.")).length).toBeGreaterThanOrEqual(2);
  });

  it("says the profile could not be saved when the server refuses an edit without a reason it can place", async () => {
    vi.mocked(authedFetch).mockResolvedValue(json({ title: "bad" }, 500));
    const dialog = await openProfile();
    touch(dialog);
    save(dialog);
    expect(await within(dialog).findByText("Profile could not be saved.")).toBeTruthy();
  });

  it("says 'Could not save' when the request fails with nothing to read", async () => {
    vi.mocked(authedFetch).mockRejectedValue("offline");
    const dialog = await openProfile();
    touch(dialog);
    save(dialog);
    expect(await within(dialog).findByText("Could not save")).toBeTruthy();
  });

  it("an answer with no profile in it still counts as saved, and editing again takes the badge away", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response("", { status: 200 }));
    const dialog = await openProfile();
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Renamed" } });
    save(dialog);
    expect(await within(dialog).findByText("Saved just now")).toBeTruthy();
    expect(revalidate).toHaveBeenCalled();

    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Renamed again" } });
    await waitFor(() => expect(within(dialog).queryByText("Saved just now")).toBeNull());
  });

  it("a new profile whose answer carries no profile says it was created", async () => {
    vi.mocked(authedFetch).mockImplementation(async (path) => (path === "/api/quality-profiles" ? new Response("", { status: 200 }) : json(existingFormats[0])));
    const dialog = await openNew();
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Fresh" } });
    save(dialog, "Create quality profile");
    expect(await within(dialog).findByText("Profile created")).toBeTruthy();
  });

  it("sends nothing a second time while the first save is still on its way", async () => {
    vi.mocked(authedFetch).mockReturnValue(new Promise(() => undefined));
    const dialog = await openProfile();
    const form = dialog.querySelector("form")!;
    fireEvent.submit(form);
    await waitFor(() => expect(writes()).toHaveLength(1));
    fireEvent.submit(form);
    expect(writes()).toHaveLength(1);
  });
});

describe("deleting, and its failures", () => {
  beforeEach(() => reset([profile()], [{ id: "lib-1", name: "Shelf", mediaType: "movies", qualityProfileId: "qp-1", defaultPolicySetName: "Archive plan" }]));
  afterEach(() => vi.clearAllMocks());

  it("lists the library using the profile and how it got it", async () => {
    const dialog = await openProfile();
    expect(within(dialog).getByText("via Archive plan")).toBeTruthy();
  });

  it("says why a delete failed when the failure carries no words", async () => {
    vi.mocked(authedFetch).mockRejectedValue("offline");
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete Quality Profile" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Profile could not be removed."));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("names the profile 'Profile' when it has already gone from the list by the time the delete lands", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(null, { status: 204 }));
    const view = renderPage();
    fireEvent.click(screen.getByText("Films"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    await screen.findByRole("button", { name: "Delete Quality Profile" });
    loaderData.qualityProfiles = [];
    view.rerender(<MemoryRouter><SettingsProfilesPage /></MemoryRouter>);
    fireEvent.click(screen.getByRole("button", { name: "Delete Quality Profile" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Profile removed"));
  });
});

describe("the recommended formats a template creates", () => {
  beforeEach(() => {
    reset([], []);
    vi.mocked(authedFetch).mockImplementation(async (path, init) => {
      if ((init as RequestInit | undefined)?.method === "POST" && path === "/api/custom-formats") {
        const sent = JSON.parse(String((init as RequestInit).body));
        return json({ id: `cf-new-${sent.trashId}`, name: sent.name, mediaType: sent.mediaType, trashId: sent.trashId, score: sent.score });
      }
      return json(profile({ id: "qp-new" }));
    });
  });
  afterEach(() => vi.clearAllMocks());

  it("reuses a format that exists for this media type, creates one that only exists for the other, and skips ones the guide cannot supply", async () => {
    const dialog = await openNew();
    template(dialog, "balanced-movies");
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "With formats" } });
    save(dialog, "Create quality profile");

    await waitFor(() => expect(writes().filter(([path]) => path === "/api/quality-profiles")).toHaveLength(1));
    const created = writes().filter(([path]) => path === "/api/custom-formats").map(([, init]) => JSON.parse(String((init as RequestInit).body)).trashId);
    expect(created).toEqual(["dts"]);
    expect(lastBody().customFormatIds.split(", ").sort()).toEqual(["cf-hdr", "cf-new-dts"]);
  });

  it("says which format could not be created and why", async () => {
    vi.mocked(authedFetch).mockImplementation(async (path, init) => {
      if ((init as RequestInit | undefined)?.method === "POST" && path === "/api/custom-formats") return json({ title: "Nope." }, 500);
      return json(profile());
    });
    const dialog = await openNew();
    template(dialog, "balanced-movies");
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Broken" } });
    save(dialog, "Create quality profile");
    expect(await within(dialog).findByText(/Could not create the “DTS” format: /)).toBeTruthy();
  });

  it("falls back to 'unknown error' when the failure carries no words", async () => {
    vi.mocked(authedFetch).mockImplementation(async (path, init) => {
      if ((init as RequestInit | undefined)?.method === "POST" && path === "/api/custom-formats") throw "offline";
      return json(profile());
    });
    const dialog = await openNew();
    template(dialog, "balanced-movies");
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Broken" } });
    save(dialog, "Create quality profile");
    expect(await within(dialog).findByText("Could not create the “DTS” format: unknown error")).toBeTruthy();
  });
});

const levels = (count: number) => Array.from({ length: count }, (_, index) => ({ id: `l${index}`, rank: index, traitIds: [`video.trait${index}`] }));
const family = (over: Record<string, unknown>) => ({ id: "f", dimension: "video.quality-tier", order: 1, intent: "ranked", levels: [], targetLevelId: null, upgradeDriving: false, transient: false, ...over });
const source = (over: Record<string, unknown>) => ({
  sourceKind: "format", sourceId: "s", sourceVersion: "v1", originalScore: null, assignedScore: null, mappingId: null, mappingVersion: null, layer: null,
  matcherDefinition: null, mappedTraitIds: null, matcherAny: false, ruleId: null, ruleName: null, profileIntent: null, ...over
});
const rule = (over: Record<string, unknown>) => ({
  ruleId: "r", name: "Rule", trashId: null, mediaType: "movies", originalScore: 10, upgradeAllowed: true, conditions: "", kind: "exactTyped",
  proposedIntent: null, requiresReview: false, explanation: "Because.", ...over
});

describe("the effective release preferences", () => {
  beforeEach(() => reset());
  afterEach(() => vi.clearAllMocks());

  it("describes each family's intent, levels and target, shortening a long ladder", async () => {
    compile.mockResolvedValue({
      ...emptyPlan(),
      plan: {
        ...emptyPlan().plan,
        requiredTraitIds: null,
        forbiddenTraitIds: null,
        families: [
          family({ id: "f1", dimension: "video.quality-tier", intent: "ranked", levels: levels(8), targetLevelId: "l1", upgradeDriving: true }),
          family({ id: "f2", dimension: "audio", intent: "required", levels: [] }),
          family({ id: "f3", dimension: "unwanted", intent: "forbidden", levels: [{ id: "a", rank: 1, traitIds: ["x.one", "x.two"] }], targetLevelId: "gone" }),
          family({ id: "f4", dimension: "tie", intent: "tieBreak", levels: levels(6) }),
          family({ id: "f5", dimension: "other", intent: "mystery", levels: levels(1) })
        ]
      }
    });
    const dialog = await openProfile();
    const families = await within(dialog).findByLabelText("Typed preference families");
    const text = families.textContent ?? "";
    expect(text).toContain("Video Quality Tier");
    expect(text).toContain("Prefer · upgrade-driving");
    expect(text).toContain("Trait0 → Trait1 → Trait2 → Trait3 → Trait4 → Trait5 → 2 more · stops at Trait1");
    expect(text).toContain("No explicit levels");
    expect(text).toContain("Required · tie-break");
    expect(text).toContain("One or Two · stops at gone");
    expect(text).toContain("Forbidden");
    expect(text).toContain("Tie-break");
    const tieBreak = [...families.children].find((child) => child.textContent?.startsWith("Tie"))!.textContent ?? "";
    expect(tieBreak).toContain("Trait0 → Trait1 → Trait2 → Trait3 → Trait4 → Trait5");
    expect(tieBreak).not.toContain("more");
    expect(text).toContain("Neutral");
    expect(within(dialog).getAllByText("0").length).toBeGreaterThanOrEqual(2);
    expect(within(dialog).getByText("5 typed families")).toBeTruthy();
  });

  it("counts the hard gates and blocked traits, and says when a review is needed", async () => {
    compile.mockResolvedValue({
      ...emptyPlan(), requiresReview: true, warnings: ["Two rules overlap."],
      advancedRules: [rule({ ruleId: "r1" })],
      plan: { ...emptyPlan().plan, requiredTraitIds: ["a", "b"], forbiddenTraitIds: ["c"] }
    });
    const dialog = await openProfile();
    expect(await within(dialog).findByText("needs attention")).toBeTruthy();
    expect(within(dialog).getByText("required traits").parentElement?.textContent).toContain("2");
    expect(within(dialog).getByText("blocked traits").parentElement?.textContent).toContain("1");
    expect(within(dialog).getByLabelText("Preference plan warnings").textContent).toContain("Two rules overlap.");
    expect(within(dialog).getByText("No typed preference families are active yet. Add formats or start from a guide template.")).toBeTruthy();
  });

  it("says when the saved profile is judged by the release preferences it was set up from, and when the preview is behind the edits", async () => {
    reset([profile({ releasePreferencePlan: { planId: "plan-9", version: "v7", planHash: "abc123" } })]);
    const dialog = await openProfile();
    expect(await within(dialog).findByText("Release preferences in use")).toBeTruthy();
    expect(within(dialog).getByText("v7")).toBeTruthy();
    expect(within(dialog).getByText("plan-9 · abc123")).toBeTruthy();
    expect(within(dialog).queryByText(/This preview reflects the last saved profile/)).toBeNull();

    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Edited" } });
    expect(within(dialog).getByText(/This preview reflects the last saved profile/)).toBeTruthy();
  });

  it("traces each compatibility rule to its value, rule, matcher and classification", async () => {
    const kinds: Array<[string, string]> = [
      ["exactTyped", "Translated exactly"], ["guideMapped", "Mapped from the guide"],
      ["orderedFamilyCandidate", "Could become an ordered preference — needs your confirmation"],
      ["hardGateCandidate", "Looks like a must-have or must-not-have — needs your confirmation"],
      ["tieBreakCandidate", "Preference that should not drive upgrades — needs your confirmation"],
      ["ambiguousOverlap", "Overlaps another rule and may count twice"], ["conflicting", "Conflicts with another rule"],
      ["unmappedAdvanced", "Kept as-is; no safe typed translation yet"],
      ["invalid", "Cannot affect decisions — the rule or its reference is broken"], ["somethingNew", "somethingNew"]
    ];
    compile.mockResolvedValue({
      ...emptyPlan(),
      advancedRules: kinds.map(([kind], index) => rule({
        ruleId: `rule-${index}`, name: `Name ${index}`, kind, trashId: index === 0 ? "trash-0" : null, conditions: index === 1 ? "x264" : "", requiresReview: index === 2, explanation: `Why ${index}.`
      }))
    });
    const dialog = await openProfile();
    fireEvent.click(await within(dialog).findByText("Advanced review"));
    for (const [, label] of kinds) expect(within(dialog).getAllByText(label).length).toBeGreaterThan(0);
    expect(within(dialog).getByText("trash-0")).toBeTruthy();
    expect(within(dialog).getByText("rule-3")).toBeTruthy();
    expect(within(dialog).getByText("x264")).toBeTruthy();
    expect(within(dialog).getAllByText("Matcher")).toHaveLength(1);
    expect(within(dialog).getAllByText("Review")).toHaveLength(2);
    expect(within(dialog).getAllByText("Mapped")).toHaveLength(9);
  });

  it("traces each preference to the source that produced it", async () => {
    const intents = ["prefer", "blocked", "avoid", "neutral", "strong-prefer", "custom-intent"];
    compile.mockResolvedValue({
      ...emptyPlan(),
      plan: {
        ...emptyPlan().plan,
        sources: [
          source({
            sourceId: "full", ruleName: "Full rule", originalScore: "500", assignedScore: "100", profileIntent: "prefer", ruleId: "saved-1", mappingId: "guide-1",
            mappedTraitIds: ["video.hdr", "video.dv"], matcherDefinition: "hdr|dv", matcherAny: true, mappingVersion: "m1", layer: "base"
          }),
          source({ sourceId: "bare" }),
          source({ sourceId: "same", originalScore: "300", assignedScore: "300", matcherDefinition: "dts", mappedTraitIds: [] }),
          ...intents.slice(1).map((intent) => source({ sourceId: `i-${intent}`, profileIntent: intent }))
        ]
      }
    });
    const dialog = await openProfile();
    fireEvent.click(await within(dialog).findByText("Where these came from"));
    const provenance = (await within(dialog).findByLabelText("Preference provenance")).textContent ?? "";
    expect(provenance).toContain("Full rule");
    expect(provenance).toContain("500 → 100 in this profile");
    expect(provenance).toContain("Prefer");
    expect(provenance).toContain("saved-1");
    expect(provenance).toContain("guide-1");
    expect(provenance).toContain("Hdr, Dv");
    expect(provenance).toContain("hdr|dv (any)");
    expect(provenance).toContain("m1");
    expect(provenance).toContain("none");
    expect(provenance).toContain("bare");
    expect(provenance).not.toContain("300 → 300");
    for (const label of ["Must not have", "Avoid", "Off", "Strongly prefer", "custom-intent"]) expect(provenance).toContain(label);
    expect(within(dialog).getAllByText("Matcher")).toHaveLength(2);
  });

  it("leaves the sources and rules sections out when the plan has none", async () => {
    compile.mockResolvedValue({ ...emptyPlan(), plan: { ...emptyPlan().plan, sources: null } });
    const dialog = await openProfile();
    expect(await within(dialog).findByText("0 typed families")).toBeTruthy();
    expect(within(dialog).queryByText("Where these came from")).toBeNull();
    expect(within(dialog).queryByText("Advanced review")).toBeNull();
  });
});

describe("duplicating, moving tiers, the plain stop and gain choices, and the baseline", () => {
  afterEach(() => vi.clearAllMocks());

  it("numbers a copy when 'Copy of' the profile is already taken", async () => {
    reset([profile(), profile({ id: "qp-2", name: "Copy of Films" })]);
    const dialog = await openProfile("Films");
    fireEvent.click(within(dialog).getByRole("button", { name: "Duplicate" }));
    expect((within(await screen.findByRole("dialog")).getByPlaceholderText("Name this quality profile") as HTMLInputElement).value).toBe("Copy of Films (2)");
  });

  it("moves a tier up as well as down", async () => {
    reset();
    const dialog = await openProfile();
    const names = () => within(dialog).queryAllByRole("button", { name: /^Move .* up$/ }).map((button) => button.getAttribute("aria-label"));
    expect(names()).toEqual(["Move WEB 1080p up", "Move WEB 720p up"]);
    fireEvent.click(within(dialog).getByRole("button", { name: "Move WEB 720p up" }));
    expect(names()).toEqual(["Move WEB 720p up", "Move WEB 1080p up"]);
  });

  it("choosing a plain stop or gain choice drops the hand-typed number behind it", async () => {
    reset([profile({ scoreLimits: { minFormatScore: 1500, cutoffFormatScore: 6000, minUpgradeFormatScore: 100 } })]);
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByText("Show the numbers"));
    const cutoff = within(dialog).getByLabelText("Cutoff custom-format score to stop upgrading") as HTMLInputElement;
    const gain = within(dialog).getByLabelText("Minimum custom-format score gain required for an upgrade") as HTMLInputElement;
    expect([cutoff.value, gain.value]).toEqual(["6000", "100"]);

    const [cutoffChoice, gainChoice] = [/Stop upgrading once a copy is/, /Only replace a file when the new copy is/].map((name) => within(dialog).getByRole("combobox", { name }) as HTMLSelectElement);
    fireEvent.change(cutoffChoice, { target: { value: cutoffChoice.options[0].value } });
    fireEvent.change(gainChoice, { target: { value: gainChoice.options[0].value } });
    expect([cutoff.value, gain.value]).toEqual(["", ""]);
  });

  it("a guide without a 'balanced' template starts a custom profile from its first template for that media type", async () => {
    reset([], []);
    loaderData.guide = { ...guide, qualityProfiles: [starter("original-movies", "Original", "movies", { recommendedFormats: [{ trashId: "hdr10", score: 500 }] })] };
    const dialog = await openNew();
    expect(within(dialog).getByText(/the baseline every custom movie profile starts with \(from "Original"\)/)).toBeTruthy();
    loaderData.guide = guide;
  });
});
