import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GuidePackage, QualityModelSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Quality Profiles: the template, the seven questions, the tier list, sizes, extras, the live judgement, saving and deleting.
 * The fetch wrapper is replaced with a recorder; the draft judge and the typed plan are replaced by spies.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));
const judge = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  compileQualityProfilePreferences: vi.fn().mockRejectedValue(new Error("not needed")),
  judgeDraftProfile: judge
}));

const tier = (name: string, rank: number) => ({ name, rank, movieMinGb: 1, moviePreferredGb: 3, movieMaxGb: 8, episodeMinMb: 100, episodePreferredMb: 400, episodeMaxMb: 800, scoreCeiling: 0 });
const qualityModel: QualityModelSnapshot = {
  version: "test",
  tiers: [tier("WEB 720p", 1), tier("WEB 1080p", 2), tier("Bluray 2160p", 3)],
  upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true },
  updatedUtc: new Date(0).toISOString()
};
function guideFormat(trashId: string, name: string, category: string, originalScore: number) {
  return { trashId, name, category, description: name, originalScore, patterns: [], bundleOnly: false, mappingStatus: "reviewed", mappedTraitIds: [], sourceKind: "trash-guides" };
}
const guide = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 1,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "",
  qualityTiers: [
    { id: "web-720p", label: "WEB 720p", source: "web", resolution: "720p", minMbPerMin: 1, maxMbPerMin: 4, rank: 1 },
    { id: "web-1080p", label: "WEB 1080p", source: "web", resolution: "1080p", minMbPerMin: 2, maxMbPerMin: 8, rank: 2 }
  ],
  customFormats: [guideFormat("hdr10", "HDR10", "hdr", 500), guideFormat("dts", "DTS", "audio", 300), guideFormat("bad", "Bad Groups", "unwanted", -10000)],
  qualityProfiles: [{
    id: "balanced-movies", name: "Balanced", tagline: "", description: "A balanced starting point.", highlights: [], mediaType: "movies",
    qualityOrder: ["web-1080p", "web-720p"], cutoffQualityId: "web-1080p", upgradeAllowed: true, minFormatScore: 1500, cutoffFormatScore: 6000,
    minUpgradeFormatScore: 100, recommendedFormats: []
  }],
  bundles: []
} as unknown as GuidePackage;

const formats = [
  { id: "cf-hdr", name: "HDR10", mediaType: "movies", trashId: "hdr10", score: 500 },
  { id: "cf-dts", name: "DTS", mediaType: "movies", trashId: "dts", score: 300 },
  { id: "cf-bad", name: "Bad Groups", mediaType: "movies", trashId: "bad", score: -10000 }
];
const profile = {
  id: "qp-1", name: "Films", mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p, WEB 1080p, Mystery 4K", customFormatIds: "cf-hdr",
  upgradeUntilCutoff: true, upgradeUnknownItems: false, sizeRules: [], upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true },
  formatIntents: { "cf-hdr": "preferred" }, acquisition: null, scoreLimits: null, languagePreference: null, releasePreferencePlan: null
};

const liveSpec = vi.hoisted(() => ({ current: undefined as { events?: readonly string[]; areas?: readonly string[] } | undefined }));
vi.mock("../lib/live-refresh", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/live-refresh")>()),
  useLiveRefresh: (spec: { events?: readonly string[]; areas?: readonly string[] }) => { liveSpec.current = spec; }
}));
const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsProfilesPage } = await import("./settings-profiles-page");

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
function reset(profiles: unknown[] = [profile], libraries: unknown[] = []) {
  Object.assign(loaderData, { libraries, qualityProfiles: profiles, customFormats: formats, settings: {}, qualityModel, guide });
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockResolvedValue(json({ ...profile }));
  judge.mockReset();
  judge.mockResolvedValue({ releaseName: "x", candidateEvaluation: { status: "meetsPlan", reasons: ["It is the best tier you allow."] } });
  toastMock.success.mockReset();
  toastMock.error.mockReset();
}
const renderPage = () => render(<MemoryRouter><SettingsProfilesPage /></MemoryRouter>);
const writes = () => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method && (init as RequestInit).method !== "GET");
const lastBody = () => JSON.parse(String((writes().at(-1)![1] as RequestInit).body));
async function openProfile() {
  renderPage();
  fireEvent.click(screen.getByText("Films"));
  return await screen.findByRole("dialog");
}
async function openNew() {
  renderPage();
  fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);
  return await screen.findByRole("dialog");
}
const step = (dialog: HTMLElement, question: RegExp | string) => within(dialog).getByRole("button", { name: question });

describe("Quality profile template and validation", () => {
  beforeEach(() => reset());
  afterEach(() => vi.clearAllMocks());

  it("choosing a template fills the name when empty, the tiers, the cutoff and the guide's own score guardrails, and saves them", async () => {
    vi.mocked(authedFetch).mockResolvedValue(json({ ...profile, id: "qp-new", name: "Movies / Balanced" }));
    const dialog = await openNew();
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Choose a template/ }), { target: { value: "balanced-movies" } });

    expect((within(dialog).getByPlaceholderText("Name this quality profile") as HTMLInputElement).value).toBe("Movies / Balanced");
    fireEvent.click(within(dialog).getByRole("button", { name: "Create quality profile" }));

    await waitFor(() => expect(writes().length).toBeGreaterThan(0));
    const body = lastBody();
    expect(body).toMatchObject({ name: "Movies / Balanced", mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p, WEB 1080p" });
    expect(body.scoreLimits).toEqual({ minFormatScore: 1500, cutoffFormatScore: 6000, minUpgradeFormatScore: 100 });
    expect(await within(dialog).findByText("Profile created")).toBeTruthy();
  });

  it("keeps a name the person already typed when a template is chosen", async () => {
    const dialog = await openNew();
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "My own" } });
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Choose a template/ }), { target: { value: "balanced-movies" } });
    expect((within(dialog).getByPlaceholderText("Name this quality profile") as HTMLInputElement).value).toBe("My own");
  });

  it("asks for a name and for at least one tier before sending anything", async () => {
    const dialog = await openNew();
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove WEB 1080p" }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove WEB 720p" }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Create quality profile" }));

    expect(await within(dialog).findByText("Give this quality profile a name.")).toBeTruthy();
    expect(within(dialog).getByText("Allow at least one quality tier.")).toBeTruthy();
    expect(writes()).toHaveLength(0);
  });

  it("shows the server's words in the footer, and next to the field, when it refuses the tiers or the cutoff", async () => {
    vi.mocked(authedFetch).mockResolvedValue(json({ errors: { allowedQualities: ["Mystery 4K is not a quality Deluno knows."] } }, 400));
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove WEB 720p" }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Save quality profile" }));

    expect((await within(dialog).findAllByText("Mystery 4K is not a quality Deluno knows.")).length).toBeGreaterThanOrEqual(2);
    expect(within(dialog).queryByText("Saved just now")).toBeNull();
  });

  it("says the profile could not be created when the server refuses without a reason it can place", async () => {
    vi.mocked(authedFetch).mockResolvedValue(json({ title: "bad" }, 500));
    const dialog = await openNew();
    fireEvent.change(within(dialog).getByPlaceholderText("Name this quality profile"), { target: { value: "Fine" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create quality profile" }));
    expect(await within(dialog).findByText("Profile could not be created.")).toBeTruthy();
  });
});

describe("the seven questions and the tier list", () => {
  beforeEach(() => reset());
  afterEach(() => vi.clearAllMocks());

  it("lists the seven questions, each with its current answer, and opens one at a time", async () => {
    const dialog = await openProfile();
    const questions = ["How good, and when to stop?", "How big should a file of that quality be?", "What should the picture be?", "What should the sound be?", "Who do you want it from?", "Which language?", "What do you never want?"];
    for (const question of questions) expect(within(dialog).getByText(question)).toBeTruthy();
    expect(within(dialog).getAllByRole("listitem").length).toBeGreaterThanOrEqual(7);
    expect(step(dialog, /How good, and when to stop/).getAttribute("aria-expanded")).toBe("true");

    fireEvent.click(step(dialog, /What should the picture be/));

    expect(step(dialog, /What should the picture be/).getAttribute("aria-expanded")).toBe("true");
    expect(step(dialog, /How good, and when to stop/).getAttribute("aria-expanded")).toBe("false");
    expect(within(dialog).getByRole("group", { name: "What should the picture be?" })).toBeTruthy();
  });

  it("shows each allowed tier with its rank, warns about one the quality model does not have, and moves and removes tiers", async () => {
    const dialog = await openProfile();
    expect(within(dialog).getByText("rank 2")).toBeTruthy();
    expect(within(dialog).getByText("not in the quality model")).toBeTruthy();
    const order = () => within(dialog).getAllByRole("listitem").filter((item) => item.tagName === "LI").map((item) => item.textContent ?? "");
    expect(order()[0]).toContain("Mystery 4K");
    expect((within(dialog).getByRole("button", { name: "Move Mystery 4K up" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(within(dialog).getByRole("button", { name: "Move Mystery 4K down" }));
    expect(order()[0]).toContain("WEB 1080p");
    expect(order()[1]).toContain("Mystery 4K");

    fireEvent.click(within(dialog).getByRole("button", { name: "Remove Mystery 4K" }));
    expect(order().some((text) => text.includes("Mystery 4K"))).toBe(false);
  });

  it("offers only tiers not yet allowed under 'Add a tier', disables it when all are allowed, and adds in rank order", async () => {
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove Mystery 4K" }));
    const add = () => within(dialog).getByRole("combobox", { name: /Add a tier/ }) as HTMLSelectElement;
    expect(Array.from(add().options).map((option) => option.text).filter(Boolean)).toEqual(expect.arrayContaining(["Bluray 2160p · rank 3"]));
    expect(Array.from(add().options).map((option) => option.text)).not.toContain("WEB 720p · rank 1");

    fireEvent.change(add(), { target: { value: "Bluray 2160p" } });

    expect(add().disabled).toBe(true);
    expect(Array.from(add().options).map((option) => option.text)).toContain("All tiers allowed");
    expect(within(dialog).getByRole("button", { name: "Move Bluray 2160p up" })).toBeTruthy();
  });

  it("offers only allowed tiers under 'Stop upgrading at', and removing the cutoff tier moves the cutoff to the next best allowed one", async () => {
    const dialog = await openProfile();
    const cutoff = () => within(dialog).getByRole("combobox", { name: /Stop upgrading at/ }) as HTMLSelectElement;
    expect(Array.from(cutoff().options).map((option) => option.value).filter(Boolean).sort()).toEqual(["Mystery 4K", "WEB 1080p", "WEB 720p"]);
    expect(cutoff().value).toBe("WEB 1080p");

    fireEvent.click(within(dialog).getByRole("button", { name: "Remove WEB 1080p" }));

    expect(cutoff().value).toBe("Mystery 4K");
    fireEvent.click(within(dialog).getByRole("button", { name: "Save quality profile" }));
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(lastBody()).toMatchObject({ cutoffQuality: "Mystery 4K", allowedQualities: "WEB 720p, Mystery 4K" });
  });

  it("'Upgrade until cutoff' off sends 'take what is found' (no stop at the cutoff) and turns the same-quality gain choice off", async () => {
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByText("Fine-tune"));
    const gain = () => within(dialog).getByRole("switch", { name: /Only replace a same-quality file that is genuinely better/ }) as HTMLButtonElement;
    expect(gain().disabled).toBe(false);

    fireEvent.click(within(dialog).getByRole("switch", { name: /Upgrade until cutoff/ }));

    expect(gain().disabled).toBe(true);
    fireEvent.click(within(dialog).getByRole("button", { name: "Save quality profile" }));
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(lastBody()).toMatchObject({ upgradeUntilCutoff: false, upgradeStop: { stopWhenCutoffMet: false } });
  });

  it("fine-tune: 'Upgrade files of unknown quality' and 'Only replace a same-quality file that is genuinely better' are saved as chosen", async () => {
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByText("Fine-tune"));
    fireEvent.click(within(dialog).getByRole("switch", { name: /Upgrade files of unknown quality/ }));
    fireEvent.click(within(dialog).getByRole("switch", { name: /Only replace a same-quality file that is genuinely better/ }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Save quality profile" }));

    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(lastBody()).toMatchObject({ upgradeUnknownItems: true, upgradeStop: { requireCustomFormatGainForSameQuality: false } });
  });
});

describe("sizes, chips and extras", () => {
  beforeEach(() => reset());
  afterEach(() => vi.clearAllMocks());

  it("shows one slider pair per allowed tier in GB for films with a sentence about what it refuses, and moving one makes the profile changed and saves the size", async () => {
    const dialog = await openProfile();
    fireEvent.click(step(dialog, /How big should a file/));
    for (const name of ["WEB 720p", "WEB 1080p", "Mystery 4K"]) {
      expect(within(dialog).getByRole("slider", { name: `${name} smallest file this profile accepts` })).toBeTruthy();
      expect(within(dialog).getByRole("slider", { name: `${name} largest file this profile accepts` })).toBeTruthy();
    }
    expect(within(dialog).getAllByText(/GB/).length).toBeGreaterThan(0);

    fireEvent.keyDown(within(dialog).getByRole("slider", { name: "WEB 1080p largest file this profile accepts" }), { key: "ArrowRight" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save quality profile" }));

    await waitFor(() => expect(writes()).toHaveLength(1));
    const rule = (lastBody().sizeRules as Array<{ quality: string; maxGb: number }>).find((item) => item.quality === "WEB 1080p");
    expect(rule?.maxGb).toBeCloseTo(8.1, 5);
  });

  it("a format chip toggles a guide rule on and off for the profile, and ticking stores the guide's suggestion as the answer", async () => {
    const dialog = await openProfile();
    fireEvent.click(step(dialog, /What should the sound be/));
    const dts = within(dialog).getByRole("checkbox", { name: "DTS" });
    expect(dts.getAttribute("aria-checked")).toBe("false");

    fireEvent.click(dts);
    expect(dts.getAttribute("aria-checked")).toBe("true");
    fireEvent.click(dts);
    expect(dts.getAttribute("aria-checked")).toBe("false");
    fireEvent.click(dts);
    expect(dts.getAttribute("aria-checked")).toBe("true");
    fireEvent.click(within(dialog).getByRole("button", { name: "Save quality profile" }));
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(String(lastBody().customFormatIds).split(", ")).toEqual(expect.arrayContaining(["cf-hdr", "cf-dts"]));
    expect(lastBody().formatIntents["cf-dts"]).toBeTruthy();
    expect(lastBody().formatIntents["cf-dts"]).not.toBe("blocked");
  });

  it("ticking a rule in the 'never' step stores 'Never' as its answer", async () => {
    const dialog = await openProfile();
    fireEvent.click(step(dialog, /What do you never want/));
    fireEvent.click(within(dialog).getByRole("checkbox", { name: /Bad Groups/ }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Save quality profile" }));
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(lastBody().formatIntents["cf-bad"]).toBe("blocked");
  });

  it("step 5 extras and step 7 are saved with the profile: only take, protocol, waits, never take", async () => {
    const dialog = await openProfile();
    fireEvent.click(step(dialog, /Who do you want it from/));
    fireEvent.change(within(dialog).getByPlaceholderText("e.g. NTb, FLUX"), { target: { value: "NTb, /FLUX|CMRG/i" } });
    fireEvent.click(within(dialog).getByRole("radio", { name: "Usenet" }));
    fireEvent.change(within(dialog).getByRole("spinbutton", { name: /Wait before taking a usenet release/ }), { target: { value: "30" } });
    fireEvent.change(within(dialog).getByRole("spinbutton", { name: /Wait before taking a torrent/ }), { target: { value: "90" } });
    fireEvent.click(step(dialog, /What do you never want/));
    fireEvent.change(within(dialog).getByPlaceholderText("e.g. HDTV, CAM"), { target: { value: "HDTV, CAM" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save quality profile" }));

    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(lastBody().acquisition).toMatchObject({
      mustContain: "NTb, /FLUX|CMRG/i", preferredProtocol: "usenet", usenetDelayMinutes: 30, torrentDelayMinutes: 90, mustNotContain: "HDTV, CAM"
    });
  });
});

describe("live judgement inside every step", () => {
  beforeEach(() => reset());
  afterEach(() => vi.clearAllMocks());

  it("posts the draft answers with the release typed and says what Deluno would do and why", async () => {
    judge.mockResolvedValue({ releaseName: "x", candidateEvaluation: { status: "meetsPlan", reasons: ["It is the best tier you allow."] } });
    const dialog = await openProfile();
    const box = within(dialog).getByRole("textbox", { name: "Release name to judge" });
    fireEvent.change(box, { target: { value: "Heat.1995.1080p.WEB-DL.x264-GRP" } });

    expect(await within(dialog).findByText("It is the best tier you allow.", {}, { timeout: 3000 })).toBeTruthy();
    const sentDraft = judge.mock.calls.at(-1)![0];
    expect(sentDraft).toMatchObject({
      name: "Films", mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: ["WEB 720p", "WEB 1080p", "Mystery 4K"],
      customFormatIds: ["cf-hdr"], upgradeUntilCutoff: true, releaseName: "Heat.1995.1080p.WEB-DL.x264-GRP"
    });
  });

  it("shows a refusal with its reason, whatever the preference evaluation says", async () => {
    judge.mockResolvedValue({ releaseName: "x", refusal: "2160p is above the tiers you allow.", candidateEvaluation: { status: "meetsPlan", reasons: ["fine"] } });
    const dialog = await openProfile();
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Release name to judge" }), { target: { value: "Heat.1995.2160p.WEB-DL-GRP" } });
    expect(await within(dialog).findByText("Deluno would not take this", {}, { timeout: 3000 })).toBeTruthy();
    expect(within(dialog).getByText("2160p is above the tiers you allow.")).toBeTruthy();
  });

  it("says it could not judge when the server fails, and judges again after an answer changes", async () => {
    judge.mockRejectedValueOnce(new Error("down"));
    const dialog = await openProfile();
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Release name to judge" }), { target: { value: "Heat.1995.1080p.WEB-DL-GRP" } });
    expect(await within(dialog).findByText(/could not judge that just now/, {}, { timeout: 3000 })).toBeTruthy();

    judge.mockResolvedValue({ releaseName: "x", candidateEvaluation: { status: "meetsPlan", reasons: ["Back again."] } });
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Release name to judge" }), { target: { value: "Heat.1995.1080p.WEB-DL-GRP2" } });
    expect(await within(dialog).findByText("Back again.", {}, { timeout: 3000 })).toBeTruthy();
  });

  it("lets the newest answer win when two are in flight", async () => {
    let releaseFirst: (value: unknown) => void = () => undefined;
    judge.mockImplementationOnce(() => new Promise((resolve) => { releaseFirst = resolve; }));
    judge.mockResolvedValue({ releaseName: "x", candidateEvaluation: { status: "meetsPlan", reasons: ["The newer answer."] } });
    const dialog = await openProfile();
    const box = within(dialog).getByRole("textbox", { name: "Release name to judge" });
    fireEvent.change(box, { target: { value: "First.Release.1080p-GRP" } });
    await waitFor(() => expect(judge).toHaveBeenCalledTimes(1), { timeout: 3000 });
    fireEvent.change(box, { target: { value: "Second.Release.1080p-GRP" } });
    expect(await within(dialog).findByText("The newer answer.", {}, { timeout: 3000 })).toBeTruthy();

    releaseFirst({ releaseName: "x", candidateEvaluation: { status: "meetsPlan", reasons: ["The older answer."] } });
    await Promise.resolve();
    expect(within(dialog).queryByText("The older answer.")).toBeNull();
    expect(within(dialog).getByText("The newer answer.")).toBeTruthy();
  });
});

describe("deleting a profile", () => {
  beforeEach(() => reset([profile], [{ id: "lib-1", name: "Shelf", mediaType: "movies", qualityProfileId: "qp-1" }]));
  afterEach(() => vi.clearAllMocks());

  it("asks first, names the libraries that use the profile, deletes with DELETE and toasts the name", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(null, { status: 204 }));
    const dialog = await openProfile();
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    expect(writes()).toHaveLength(0);
    expect(await screen.findByText("1 library uses this Quality Profile and will need another one.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Delete Quality Profile" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Films removed"));
    expect(vi.mocked(authedFetch)).toHaveBeenCalledWith("/api/quality-profiles/qp-1", { method: "DELETE" });
  });
});

describe("Quality Profiles: a change made elsewhere", () => {
  it("is followed with no reload: the screen reads again for QualityProfileChanged, LibraryChanged", () => {
    reset();
    renderPage();
    expect(liveSpec.current?.events).toEqual(expect.arrayContaining(["QualityProfileChanged","LibraryChanged"]));
    
  });
});
