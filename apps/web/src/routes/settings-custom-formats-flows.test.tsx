import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot, type GuidePackage } from "../lib/api";

/**
 * Release Preferences: the presets list, the safeguards, export and import of rules, a rule's matching criteria, and the
 * "Test a release" drawer with its typed verdict. The page's fetch wrappers are replaced with recorders.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));

const calls: Array<{ path: string; method: string; body: unknown }> = [];
let replies: Record<string, (init: { method?: string; body?: string }) => Response> = {};
function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}
async function answer(path: string, init: { method?: string; body?: string } = {}) {
  calls.push({ path, method: init.method ?? "GET", body: init.body ? JSON.parse(init.body) : undefined });
  const reply = replies[`${init.method ?? "GET"} ${path}`];
  return reply ? reply(init) : json({});
}
vi.mock("../lib/use-auth", () => ({ authedFetch: (path: string, init?: { method?: string; body?: string }) => answer(path, init) }));
async function fetchJsonStandIn(path: string, init?: { method?: string; body?: string }) {
  const response = await answer(path, init);
  if (!response.ok) throw new Error((await response.json()).message ?? "failed");
  return response.json();
}
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api")>()), fetchJson: fetchJsonStandIn }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api/client")>()), fetchJson: fetchJsonStandIn }));

function guideFormat(trashId: string, name: string, originalScore: number) {
  return { trashId, name, category: "hdr", description: name, originalScore, patterns: [], bundleOnly: false, mappingStatus: "reviewed", mappedTraitIds: [], sourceKind: "trash-guides", mediaTypes: [] };
}
const guide = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 3,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "", qualityTiers: [],
  customFormats: [guideFormat("hdr10", "HDR10", 100), guideFormat("dv", "Dolby Vision", 200)],
  qualityProfiles: [],
  bundles: [
    { id: "all-in", name: "All in", level: "Recommended", mediaType: "movies", description: "Both signals.", bestFor: "Big screens", warnings: [], includes: [{ trashId: "hdr10", score: 100 }, { trashId: "dv", score: 200 }] },
    { id: "one-only", name: "One only", level: "Basic", mediaType: "movies", description: "Just HDR.", bestFor: "Small screens", warnings: [], includes: [{ trashId: "hdr10", score: 100 }] },
    { id: "none-yet", name: "None yet", level: "Basic", mediaType: "movies", description: "Dolby only.", bestFor: "Dolby fans", warnings: [], includes: [{ trashId: "dv", score: 200 }] }
  ],
  sourceInventory: { schemaVersion: 2, upstreamRevision: "1", customFormats: [], qualityProfiles: [], formatGroups: [] }
} as unknown as GuidePackage;

const customRule = {
  id: "cf-mine", name: "My remux rule", mediaType: "movies", score: 150, upgradeAllowed: true, trashId: null,
  conditions: JSON.stringify([{ type: "releaseTitle", value: "REMUX", negate: false, required: true }]),
  createdUtc: "2026-01-01T00:00:00Z", updatedUtc: "2026-01-01T00:00:00Z"
};
const hdrRule = { id: "cf-hdr", name: "HDR10", mediaType: "movies", score: 100, upgradeAllowed: true, trashId: "hdr10", conditions: "[]", createdUtc: "2026-01-01T00:00:00Z", updatedUtc: "2026-01-01T00:00:00Z" };
const profile = { id: "qp-1", name: "Films", mediaType: "movies", allowedQualities: "WEB 1080p", cutoffQuality: "WEB 1080p" };

const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsCustomFormatsPage } = await import("./settings-custom-formats-page");

function load(customFormats: unknown[], settings: Record<string, unknown> = {}) {
  Object.assign(loaderData, {
    libraries: [], policySets: [], qualityProfiles: [profile], customFormats, settings: { ...emptyPlatformSettingsSnapshot, searchScoringMode: "hybrid", releaseNeverGrabPatterns: "CAM\nTS", ...settings }, guide,
    guideUpdateCheck: { isEnabled: false, lastCheckedUtc: null, lastSeenRevision: null, status: "disabled", error: null, report: null, updatedUtc: new Date(0).toISOString() },
    guideVersions: [], ruleMigration: null
  });
}
const renderPage = () => render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);
const sent = (method: string, path: string) => calls.filter((call) => call.method === method && call.path === path);

let downloads: Array<{ name: string; text: Promise<string> }> = [];
beforeEach(() => {
  calls.length = 0;
  replies = {};
  downloads = [];
  toastMock.success.mockReset();
  toastMock.error.mockReset();
  revalidate.mockReset();
  let lastBlob: Blob | null = null;
  vi.stubGlobal("URL", Object.assign(URL, {
    createObjectURL: (blob: Blob) => { lastBlob = blob; return "blob:test"; },
    revokeObjectURL: () => undefined
  }));
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    const blob = lastBlob!;
    downloads.push({ name: this.download, text: blob.text() });
  });
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe("presets list", () => {
  it("lists each preset with who it is best for, how many of its rules are applied, and whether it is Applied, Partly applied or Not applied", () => {
    load([hdrRule, { ...hdrRule, id: "cf-dv", name: "Dolby Vision", trashId: "dv" }]);
    renderPage();
    const rowOf = (name: string) => screen.getByText(name).closest("[role='row'], tr, li, div[class*='grid']") as HTMLElement;

    expect(screen.getByText("Big screens")).toBeTruthy();
    expect(screen.getByText("Small screens")).toBeTruthy();
    expect(within(rowOf("All in")).getByText("2 of 2")).toBeTruthy();
    expect(within(rowOf("All in")).getByText("Applied")).toBeTruthy();
    expect(within(rowOf("None yet")).getByText("Applied")).toBeTruthy();
  });

  it("calls a preset Partly applied when some of its rules exist, and Not applied when none do", () => {
    load([hdrRule]);
    renderPage();
    const rowOf = (name: string) => screen.getByText(name).closest("[role='row'], tr, li, div[class*='grid']") as HTMLElement;

    expect(within(rowOf("All in")).getByText("Partly applied")).toBeTruthy();
    expect(within(rowOf("All in")).getByText("1 of 2")).toBeTruthy();
    expect(within(rowOf("One only")).getByText("Applied")).toBeTruthy();
    expect(within(rowOf("None yet")).getByText("Not applied")).toBeTruthy();
    expect(within(rowOf("None yet")).getByText("0 of 1")).toBeTruthy();
  });
});

describe("safeguards", () => {
  it("offers the three ways of picking a release and saves the choice with the never-grab words", async () => {
    replies["PATCH /api/settings"] = (init) => { loaderData.settings = { ...(loaderData.settings as object), ...JSON.parse(init.body!) }; return json(loaderData.settings); };
    load([]);
    renderPage();
    const picker = screen.getByRole("radiogroup", { name: "How Deluno picks a release" });
    expect(within(picker).getAllByRole("radio").map((radio) => radio.textContent)).toEqual(["Rules and ranking together", "Rules only", "Ranking only"]);

    fireEvent.click(within(picker).getByRole("radio", { name: "Rules only" }));
    fireEvent.click(screen.getByRole("button", { name: "Save safeguards" }));

    await waitFor(() => expect(sent("PATCH", "/api/settings")).toHaveLength(1));
    expect(sent("PATCH", "/api/settings")[0].body).toEqual({ searchScoringMode: "rules-only", releaseNeverGrabPatterns: "CAM\nTS" });
    expect(await screen.findByText("Saved just now")).toBeTruthy();
  });

  it("adds a never-grab word with Enter or Add, ignores a repeat in any letter case, removes a word and restores the defaults", async () => {
    replies["PATCH /api/settings"] = (init) => { loaderData.settings = { ...(loaderData.settings as object), ...JSON.parse(init.body!) }; return json(loaderData.settings); };
    load([]);
    renderPage();
    const box = screen.getByRole("textbox", { name: "Add a never-grab word" });

    fireEvent.change(box, { target: { value: "HDTV" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(screen.getByText("HDTV")).toBeTruthy();
    expect((box as HTMLInputElement).value).toBe("");

    fireEvent.change(box, { target: { value: "hdtv" } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(screen.getAllByText(/^HDTV$/i)).toHaveLength(1);

    fireEvent.change(box, { target: { value: "NOGROUP" } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove TS" }));
    fireEvent.click(screen.getByRole("button", { name: "Save safeguards" }));
    await waitFor(() => expect(sent("PATCH", "/api/settings")).toHaveLength(1));
    expect(sent("PATCH", "/api/settings")[0].body).toMatchObject({ releaseNeverGrabPatterns: "CAM\nHDTV\nNOGROUP" });

    fireEvent.click(screen.getByRole("button", { name: "Restore defaults" }));
    expect(screen.getByRole("button", { name: "Remove camrip" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Remove NOGROUP" })).toBeNull();
  });

  it("shows the server's words when the safeguards cannot be saved", async () => {
    replies["PATCH /api/settings"] = () => json({ title: "Too long." }, 400);
    load([]);
    renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "Ranking only" }));
    fireEvent.click(screen.getByRole("button", { name: "Save safeguards" }));
    await waitFor(() => expect(screen.queryByText("Saved just now")).toBeNull());
    expect(sent("PATCH", "/api/settings")).toHaveLength(1);
  });
});

describe("export and import of rules", () => {
  it("Export all is off with no rules, and with rules downloads deluno-custom-formats.json with what the server sent", async () => {
    load([]);
    const view = renderPage();
    expect((screen.getByRole("button", { name: "Export all" }) as HTMLButtonElement).disabled).toBe(true);
    view.unmount();

    replies["GET /api/custom-formats/export"] = () => json({ formats: [{ name: "My remux rule" }] });
    load([customRule]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Export all" }));

    await waitFor(() => expect(downloads).toHaveLength(1));
    expect(downloads[0].name).toBe("deluno-custom-formats.json");
    expect(JSON.parse(await downloads[0].text)).toEqual({ formats: [{ name: "My remux rule" }] });
  });

  it("'Export this rule' in a rule's drawer downloads <rule-name>.json from that rule's own export", async () => {
    replies["GET /api/custom-formats/cf-mine/export"] = () => json({ name: "My remux rule" });
    load([customRule]);
    renderPage();
    fireEvent.click(screen.getByText("My remux rule"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Share or back up this rule")).toBeTruthy();
    fireEvent.click(within(dialog).getByRole("button", { name: "Export this rule" }));

    await waitFor(() => expect(downloads).toHaveLength(1));
    expect(downloads[0].name).toBe("my-remux-rule.json");
    expect(sent("GET", "/api/custom-formats/cf-mine/export")).toHaveLength(1);
  });

  it("the Import drawer takes pasted JSON, imports it, and says how many rules came in and which conditions were dropped", async () => {
    replies["POST /api/custom-formats/import"] = () => json([
      { name: "Good", imported: true, id: "cf-1", droppedConditions: [], error: null },
      { name: "Half", imported: true, id: "cf-2", droppedConditions: ["A condition nobody understands"], error: null }
    ]);
    load([]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    const dialog = await screen.findByRole("dialog");
    const importButton = within(dialog).getAllByRole("button", { name: "Import" }).at(-1) as HTMLButtonElement;
    expect(importButton.disabled).toBe(true);

    fireEvent.change(within(dialog).getByRole("textbox"), { target: { value: JSON.stringify([{ name: "Good" }, { name: "Half" }]) } });
    expect(importButton.disabled).toBe(false);
    fireEvent.click(importButton);

    expect(await within(dialog).findByText(/Imported 2 of 2 rules\./)).toBeTruthy();
    expect(within(dialog).getByText(/Some conditions could not be understood/)).toBeTruthy();
    expect(sent("POST", "/api/custom-formats/import")[0].body).toEqual([{ name: "Good" }, { name: "Half" }]);
    expect(revalidate).toHaveBeenCalled();
  });

  it("says it is not valid JSON, and sends nothing, when the pasted text is not JSON", async () => {
    load([]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByRole("textbox"), { target: { value: "{ not json" } });
    fireEvent.click(within(dialog).getAllByRole("button", { name: "Import" }).at(-1)!);

    expect(await within(dialog).findByText("That is not valid JSON. Paste what Export produced, unchanged.")).toBeTruthy();
    expect(sent("POST", "/api/custom-formats/import")).toHaveLength(0);
  });

  it("reads an uploaded file into the box", async () => {
    load([]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    const dialog = await screen.findByRole("dialog");
    const file = new File(['[{"name":"From file"}]'], "rules.json", { type: "application/json" });
    const input = dialog.querySelector("input[type='file']") as HTMLInputElement;
    expect(input).toBeTruthy();
    fireEvent.change(input, { target: { files: [file] } });
    await waitFor(() => expect((within(dialog).getByRole("textbox") as HTMLTextAreaElement).value).toBe('[{"name":"From file"}]'));
  });
});

describe("a rule's matching criteria", () => {
  it("lists the kinds of criterion to match on, adds a criterion and removes it, and saving sends the criteria that are left", async () => {
    replies["PUT /api/custom-formats/cf-mine"] = () => json(customRule);
    load([customRule]);
    renderPage();
    fireEvent.click(screen.getByText("My remux rule"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByText("Advanced matching"));

    const matchOn = () => within(dialog).getAllByRole("combobox", { name: /Match on/ }) as HTMLSelectElement[];
    expect(matchOn()).toHaveLength(1);
    expect(Array.from(matchOn()[0].options).map((option) => option.text)).toEqual(expect.arrayContaining(["Release title", "Source", "Resolution", "HDR format", "Video codec", "Release group", "Language", "Indexer flag", "Year", "Size"]));
    expect(Array.from(matchOn()[0].options).map((option) => option.text)).not.toContain("Release type");

    fireEvent.click(within(dialog).getByRole("button", { name: "Add matching criterion" }));
    expect(matchOn()).toHaveLength(2);
    fireEvent.change(matchOn()[1], { target: { value: "resolution" } });
    fireEvent.click(within(dialog).getAllByRole("button", { name: "Remove criterion" })[1]);
    expect(matchOn()).toHaveLength(1);

    fireEvent.change(within(dialog).getByDisplayValue("REMUX"), { target: { value: "REMUX2" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save rule" }));
    await waitFor(() => expect(sent("PUT", "/api/custom-formats/cf-mine")).toHaveLength(1));
    const body = sent("PUT", "/api/custom-formats/cf-mine")[0].body as { conditions: string };
    expect(JSON.parse(body.conditions)).toEqual([expect.objectContaining({ type: "releaseTitle", value: "REMUX2" })]);
  });

  it("asks for a criterion when a custom rule has none", async () => {
    load([]);
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New custom rule" })[0]);
    const dialog = await screen.findByRole("dialog");
    const toggle = within(dialog).getByText("Advanced matching").closest("button")!;
    if (toggle.getAttribute("aria-expanded") !== "true") fireEvent.click(toggle);
    for (const remove of within(dialog).queryAllByRole("button", { name: "Remove criterion" })) fireEvent.click(remove);
    expect(await within(dialog).findByText("Add at least one matching criterion before saving this custom rule.")).toBeTruthy();
  });
});

describe("Test a release", () => {
  const dryRun = [
    { formatId: "cf-hdr", formatName: "HDR10", mediaType: "movies", score: 100, isMatch: true, matchedConditions: ["releaseTitle:HDR10"], missedConditions: [] },
    { formatId: "cf-mine", formatName: "My remux rule", mediaType: "movies", score: 150, isMatch: false, matchedConditions: [], missedConditions: ["releaseTitle:REMUX"] }
  ];

  async function openTest() {
    load([hdrRule, customRule]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Test a release" }));
    return await screen.findByRole("dialog");
  }

  it("asks the server which rules match the release for the scope chosen, and lists the matches first with the others as No match", async () => {
    replies["POST /api/custom-formats/dry-run"] = () => json(dryRun);
    replies["GET /api/v1/release-preferences/plans/quality-profile/qp-1"] = () => json({ plan: { id: "plan-1", version: "v1" } });
    replies["POST /api/v1/release-preferences/preview"] = () => json({ planId: "plan-1", planVersion: "v1", planHash: "abcdef0123456789", candidateEvaluation: { status: "meetsPlan", reasons: [], families: [] }, candidateFacts: [] });
    const dialog = await openTest();
    fireEvent.click(within(dialog).getByRole("radio", { name: "Movies" }));
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Release name to test" }), { target: { value: "Heat.1995.2160p.HDR10-GRP" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Test" }));

    expect(await within(dialog).findByText("1 of 2 matched")).toBeTruthy();
    expect(sent("POST", "/api/custom-formats/dry-run")[0].body).toEqual({ releaseName: "Heat.1995.2160p.HDR10-GRP", mediaType: "movies" });
    expect(within(dialog).getByText("Matched")).toBeTruthy();
    expect(within(dialog).getByText("No match")).toBeTruthy();
  });

  it("shows the typed verdict: status, the plan version, the reasons and each family's outcome, and compares with an installed release", async () => {
    replies["POST /api/custom-formats/dry-run"] = () => json(dryRun);
    replies["GET /api/v1/release-preferences/plans/quality-profile/qp-1"] = () => json({ plan: { id: "plan-1", version: "v7" } });
    replies["POST /api/v1/release-preferences/preview"] = () => json({
      planId: "plan-1", planVersion: "v7", planHash: "abcdef0123456789", candidateFacts: [{}, {}],
      candidateEvaluation: { status: "belowGoal", reasons: ["Stops short of your cutoff."], families: [{ familyId: "video.hdr", explanation: "HDR10 found." }] },
      comparison: { status: "upgrade", reasons: ["Better than the file you have."] }
    });
    const dialog = await openTest();
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Release name to test" }), { target: { value: "Heat.1995.2160p.HDR10-GRP" } });
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Current installed release name" }), { target: { value: "Heat.1995.1080p-GRP" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Test" }));

    expect(await within(dialog).findByText("How this release is judged")).toBeTruthy();
    expect(within(dialog).getAllByText("Upgrade").length).toBeGreaterThan(0);
    expect(within(dialog).getByText("Plan v7")).toBeTruthy();
    expect(within(dialog).getByText("Better than the file you have.")).toBeTruthy();
    expect(within(dialog).getByText("Stops short of your cutoff.")).toBeTruthy();
    expect(within(dialog).getByText("Video Hdr")).toBeTruthy();
    expect(within(dialog).getByText("HDR10 found.")).toBeTruthy();
    expect(within(dialog).getByText(/Detected 2 typed evidence items/)).toBeTruthy();
    expect(sent("POST", "/api/v1/release-preferences/preview")[0].body).toMatchObject({ planId: "plan-1", planVersion: "v7", releaseName: "Heat.1995.2160p.HDR10-GRP", currentReleaseName: "Heat.1995.1080p-GRP" });
  });

  it("says the typed verdict could not be run when the plan cannot be read, and still shows the rule matches", async () => {
    replies["POST /api/custom-formats/dry-run"] = () => json(dryRun);
    replies["GET /api/v1/release-preferences/plans/quality-profile/qp-1"] = () => json({ message: "No plan." }, 500);
    const dialog = await openTest();
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Release name to test" }), { target: { value: "Heat.1995.2160p.HDR10-GRP" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Test" }));

    expect(await within(dialog).findByRole("alert")).toBeTruthy();
  });

  it("will not test an empty release name", async () => {
    const dialog = await openTest();
    expect((within(dialog).getByRole("button", { name: "Test" }) as HTMLButtonElement).disabled).toBe(true);
    expect(sent("POST", "/api/custom-formats/dry-run")).toHaveLength(0);
  });
});
