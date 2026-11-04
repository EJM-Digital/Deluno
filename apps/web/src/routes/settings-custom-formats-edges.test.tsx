import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot, type GuidePackage } from "../lib/api";

/**
 * Release Preferences, the corners the other files do not reach: how a rule reads in the list and in its drawer in every
 * shape it is stored in, the criterion editor, each way a save, remove, export or import can end, the intent choices, the
 * preset and test drawers, and the safeguards. The fetch wrappers are recorders; the drawers' own props are spied on so
 * a request that no click can make is still made.
 */

vi.setConfig({ testTimeout: 30_000 });
const unsaved = vi.hoisted(() => ({ dirty: false }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: (dirty: boolean) => { unsaved.dirty = dirty; } }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));
const planApi = vi.hoisted(() => ({ compile: vi.fn(), preview: vi.fn() }));

type Reply = Response | Promise<Response>;
const calls: Array<{ path: string; method: string; body: unknown }> = [];
let replies: Record<string, (init: { method?: string; body?: string }) => Reply> = {};
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
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: fetchJsonStandIn,
  compileQualityProfilePreferences: planApi.compile,
  previewReleasePreference: planApi.preview
}));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api/client")>()), fetchJson: fetchJsonStandIn }));
vi.mock("../lib/live-refresh", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/live-refresh")>()),
  useLiveRefresh: () => undefined
}));

type Seen = { onOpenChange: (open: boolean) => void; footer?: { props: Record<string, unknown> } };
const seen = vi.hoisted(() => ({
  drawers: new Map<string, unknown>(),
  confirms: new Map<string, unknown>(),
  choice: undefined as undefined | { onValueChange: (value: string) => void }
}));
vi.mock("../components/ui/drawer", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/ui/drawer")>();
  return { ...original, Drawer: (props: Parameters<typeof original.Drawer>[0]) => { seen.drawers.set(String(props.title), props); return original.Drawer(props); } };
});
vi.mock("../components/ui/confirm-dialog", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/ui/confirm-dialog")>();
  return { ...original, ConfirmDialog: (props: Parameters<typeof original.ConfirmDialog>[0]) => { seen.confirms.set(props.title, props); return original.ConfirmDialog(props); } };
});
vi.mock("../components/ui/choice-rows", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/ui/choice-rows")>();
  return { ...original, ChoiceRows: (props: Parameters<typeof original.ChoiceRows>[0]) => { seen.choice = props as never; return original.ChoiceRows(props); } };
});
const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsCustomFormatsPage, describeMatchedCondition, parseRange, formatRange, slugForFileName } = await import("./settings-custom-formats-page");

const clause = (over: Record<string, unknown> = {}) => ({ name: "Atmos", implementation: "ReleaseTitleSpecification", negate: false, required: true, fieldsJson: JSON.stringify({ value: "atmos" }), ...over });
const guideFormat = (trashId: string, name: string, over: Record<string, unknown> = {}) => ({
  trashId, name, category: "hdr", description: `${name} description`, originalScore: 100, patterns: [], bundleOnly: false, mappingStatus: "reviewed",
  mappedTraitIds: [], sourceKind: "trash-guides", mediaTypes: [], ...over
});
const baseGuide = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 3,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "", qualityTiers: [],
  customFormats: [
    guideFormat("genuine", "Genuine HDR", { sourceMatcherClauses: [clause(), clause({ name: "Not SDR", negate: true, required: false, fieldsJson: JSON.stringify({ value: "sdr" }) })] }),
    guideFormat("plain", "Plain Audio", { category: "audio", patterns: ["dts", "truehd"] })
  ],
  qualityProfiles: [], bundles: [],
  sourceInventory: { schemaVersion: 2, upstreamRevision: "1", customFormats: [], qualityProfiles: [], formatGroups: [] }
} as unknown as GuidePackage;

const stamp = { createdUtc: "2026-01-01T00:00:00Z", updatedUtc: "2026-01-01T00:00:00Z" };
const rule = (id: string, name: string, over: Record<string, unknown> = {}) => ({
  id, name, mediaType: "movies", score: 100, upgradeAllowed: true, trashId: null,
  conditions: JSON.stringify([{ type: "releaseTitle", value: "REMUX", negate: false, required: true }]), ...stamp, ...over
});
const profile = (id: string, name: string, mediaType = "movies", over: Record<string, unknown> = {}) => ({ id, name, mediaType, allowedQualities: "WEB 1080p", cutoffQuality: "WEB 1080p", customFormatIds: "", ...over });

function load(customFormats: unknown[], over: Record<string, unknown> = {}) {
  Object.assign(loaderData, {
    libraries: [], policySets: [], qualityProfiles: [profile("qp-1", "Films")], customFormats,
    settings: { ...emptyPlatformSettingsSnapshot, searchScoringMode: "hybrid", releaseNeverGrabPatterns: "CAM\nTS" }, guide: baseGuide,
    guideUpdateCheck: { isEnabled: false, lastCheckedUtc: null, lastSeenRevision: null, status: "disabled", error: null, report: null, updatedUtc: new Date(0).toISOString() },
    guideVersions: [], ruleMigration: null, ...over
  });
}
const renderPage = () => render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);
const sent = (method: string, path: string) => calls.filter((call) => call.method === method && call.path === path);
const deferred = <T,>() => { let resolve!: (value: T) => void; const promise = new Promise<T>((a) => { resolve = a; }); return { promise, resolve }; };
const drawerProps = (title: string) => seen.drawers.get(title) as Seen;
async function openRule(name: string) {
  fireEvent.click(screen.getByText(name));
  return await screen.findByRole("dialog");
}
async function openNew() {
  fireEvent.click(screen.getAllByRole("button", { name: "New custom rule" })[0]!);
  return await screen.findByRole("dialog");
}
function openAdvanced(dialog: HTMLElement) {
  const fold = within(dialog).getByRole("button", { name: /Advanced matching/ });
  if (fold.getAttribute("aria-expanded") === "false") fireEvent.click(fold);
}

beforeEach(() => {
  calls.length = 0;
  replies = {};
  unsaved.dirty = false;
  for (const fn of [toastMock.success, toastMock.error, revalidate, planApi.compile, planApi.preview]) fn.mockReset();
  seen.drawers.clear();
  seen.confirms.clear();
  seen.choice = undefined;
  vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: () => "blob:test", revokeObjectURL: () => undefined }));
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe("how a rule reads in the list", () => {
  it("shows which libraries use it, reaching the quality profile directly, through a plan's profile, or in the plan itself", () => {
    load(
      [rule("cf-a", "Used rule"), rule("cf-b", "Unused rule", { conditions: JSON.stringify([{ type: "source", value: "WEB" }]) })],
      {
        libraries: [
          { id: "l1", name: "Direct Films", mediaType: "movies", qualityProfileId: "qp-1", defaultPolicySetId: null },
          { id: "l2", name: "Plan Films", mediaType: "movies", qualityProfileId: null, defaultPolicySetId: "ps-1" },
          { id: "l3", name: "Bare Films", mediaType: "movies", qualityProfileId: null, defaultPolicySetId: "ps-2" },
          { id: "l4", name: "Nothing Films", mediaType: "movies", qualityProfileId: null, defaultPolicySetId: null }
        ],
        policySets: [{ id: "ps-1", name: "Plan", qualityProfileId: "qp-1", customFormatIds: "" }, { id: "ps-2", name: "Bare", qualityProfileId: null, customFormatIds: "cf-a" }],
        qualityProfiles: [profile("qp-1", "Films", "movies", { customFormatIds: "cf-a" })]
      }
    );
    renderPage();

    expect(screen.getAllByText("Applied through the library's quality profile")).toHaveLength(1);
    expect(screen.getAllByText("Direct Films").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Plan Films").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Bare Films").length).toBeGreaterThan(0);
    expect(screen.queryByText("Nothing Films")).toBeNull();
    expect(screen.getByText("Not applied to any library yet")).toBeTruthy();
  });

  it("summarises the first condition in plain words: an unknown kind by its own name, a long value cut short, a negated one as without", () => {
    load([
      rule("cf-1", "Odd kind", { conditions: JSON.stringify([{ type: "mystery", value: "x".repeat(40), negate: true }]) }),
      rule("cf-2", "Bare object", { conditions: JSON.stringify([{}]) }),
      rule("cf-3", "Old shape", { conditions: "first pattern\n\n second pattern ", score: -10000 }),
      rule("cf-4", "Empty", { conditions: "", score: 0 }),
      rule("cf-5", "Imported", { conditions: JSON.stringify([{ name: "Atmos", implementation: "ReleaseTitleSpecification", negate: false, required: true, fields: { value: "atmos" } }]), score: -50 }),
      rule("cf-6", "Imported twin", { conditions: JSON.stringify([{ name: "Atmos", implementation: "ReleaseTitleSpecification", negate: false, required: true, fields: { value: "atmos" } }]), score: 700 })
    ]);
    renderPage();

    expect(screen.getByText(`mystery without “${"x".repeat(32)}…”`)).toBeTruthy();
    expect(screen.getByText("Release title with “”")).toBeTruthy();
    expect(screen.getByText("Release title with “first pattern”")).toBeTruthy();
    expect(screen.getByText("2 conditions")).toBeTruthy();
    expect(screen.getByText("No conditions")).toBeTruthy();
    expect(screen.getByText("never matches anything")).toBeTruthy();
    expect(screen.getAllByText("Imported specifications")).toHaveLength(2);
    expect(screen.getByText("Reads the same as the rule above")).toBeTruthy();
    expect(screen.getByText("Must not have")).toBeTruthy();
    expect(screen.getByText("Avoid")).toBeTruthy();
    expect(screen.getByText("I do not care")).toBeTruthy();
    expect(screen.getByText("Strongly prefer")).toBeTruthy();
  });

  it("shows a guide rule by the guide's plain name with the guide's own name under it, and a renamed one by its own name", () => {
    load([
      rule("cf-g", "Genuine HDR", { trashId: "genuine", conditions: "[]" }),
      rule("cf-p", "My audio", { trashId: "plain", conditions: "[]" })
    ]);
    renderPage();

    expect(screen.getByText("TRaSH format")).toBeTruthy();
    expect(screen.getByText("Plain Audio · Deluno guide format")).toBeTruthy();
    expect(screen.getByText("My audio")).toBeTruthy();
  });
});

describe("the rule drawer for each way a rule is stored", () => {
  it("shows an imported rule's specifications as they are, with must-not-match and required marks, and saves them untouched", async () => {
    const specs = JSON.stringify([
      { name: "Atmos", implementation: "ReleaseTitleSpecification", negate: true, required: true, fields: { value: "atmos" } },
      { implementation: "ReleaseGroupSpecification", negate: false, required: false, fields: { value: "GRP" } }
    ]);
    replies["PUT /api/custom-formats/cf-s"] = () => json(rule("cf-s", "Imported tv", { mediaType: "tv", conditions: specs }));
    load([rule("cf-s", "Imported tv", { mediaType: "tv", conditions: specs })]);
    renderPage();
    const dialog = await openRule("Imported tv");

    expect(within(dialog).getByText("TV shows · Custom rule")).toBeTruthy();
    expect(within(dialog).getByText(/Matched exactly as TRaSH defines it/)).toBeTruthy();
    const list = within(dialog).getByLabelText("Matching conditions");
    expect(within(list).getByText("must not match")).toBeTruthy();
    expect(within(list).getAllByText("required")).toHaveLength(1);
    expect(within(list).getByText("Atmos")).toBeTruthy();
    expect(within(dialog).getByText("2 conditions")).toBeTruthy();

    fireEvent.change(within(dialog).getByPlaceholderText("e.g. Dolby Vision"), { target: { value: "Imported tv 2" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save rule" }));
    await waitFor(() => expect(sent("PUT", "/api/custom-formats/cf-s")).toHaveLength(1));
    expect((sent("PUT", "/api/custom-formats/cf-s")[0]!.body as { conditions: string; mediaType: string }).conditions).toBe(specs);
    expect((sent("PUT", "/api/custom-formats/cf-s")[0]!.body as { mediaType: string }).mediaType).toBe("tv");
  });

  it("says only a title match is wanted when none of the specifications is required or of another kind", async () => {
    const specs = JSON.stringify([{ name: "", implementation: "ReleaseTitleSpecification", negate: false, required: false, fields: { value: "remux" } }]);
    load([rule("cf-s", "Title only", { conditions: specs })]);
    renderPage();
    const dialog = await openRule("Title only");
    expect(within(dialog).getByText("Matches a release title containing any one of these patterns.")).toBeTruthy();
  });

  it("opens a guide-backed rule under the guide's plain name, shows the guide's criteria in monospace, and uses its own name once renamed", async () => {
    load([
      rule("cf-p", "Plain Audio", { trashId: "plain", conditions: JSON.stringify([{ type: "releaseTitle", value: "dts" }]) }),
      rule("cf-r", "Renamed audio", { trashId: "plain", conditions: JSON.stringify([{ type: "releaseTitle", value: "dts" }]) })
    ]);
    renderPage();
    const dialog = await openRule("Plain Audio");

    expect(within(dialog).getByText("Movies · Guide-backed")).toBeTruthy();
    expect(within(dialog).getByText(/Deluno will use the guide definition above/)).toBeTruthy();
    expect(within(dialog).getByText("Guide-backed", { selector: "span, p, div" })).toBeTruthy();
    expect(within(dialog).getByText("Deluno's own format: TRaSH does not publish one.")).toBeTruthy();
    openAdvanced(dialog);
    expect(within(dialog).getByText("View or edit the guide-backed criteria")).toBeTruthy();
    expect(within(dialog).getByDisplayValue("dts").className).toContain("font-mono");
    expect(within(dialog).getByRole("heading", { name: "Plain Audio" }) ?? true).toBeTruthy();
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    const second = await openRule("Renamed audio");
    expect(within(second).getByRole("heading", { name: "Renamed audio" })).toBeTruthy();
  });

  it("reads criteria that were stored with a missing kind or value, and ones stored one per line", async () => {
    load([rule("cf-m", "Gappy", { conditions: JSON.stringify([{ value: "A" }, { type: "source" }]) }), rule("cf-o", "Lines", { conditions: "one\ntwo" })]);
    renderPage();
    const dialog = await openRule("Gappy");
    openAdvanced(dialog);
    expect(within(dialog).getByDisplayValue("A")).toBeTruthy();
    expect(within(dialog).getAllByRole("combobox", { name: /Match on/ })).toHaveLength(2);
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    const second = await openRule("Lines");
    openAdvanced(second);
    expect(within(second).getByDisplayValue("one")).toBeTruthy();
    expect(within(second).getByDisplayValue("two")).toBeTruthy();
  });

  it("starts a rule from a TRaSH format: its definition is shown, not editable, and the rule is created from the guide with no conditions of its own", async () => {
    replies["POST /api/custom-formats"] = () => json(rule("cf-new", "Genuine HDR", { trashId: "genuine", conditions: "[]" }));
    load([]);
    renderPage();
    const dialog = await openNew();

    fireEvent.change(within(dialog).getByRole("combobox", { name: /Guide choice/ }), { target: { value: "genuine" } });
    expect(within(dialog).getByText("TRaSH's own format, with TRaSH's id and conditions.")).toBeTruthy();
    expect(within(dialog).getByText(/Matched exactly as TRaSH defines it/)).toBeTruthy();
    expect(within(dialog).getByText("must not match")).toBeTruthy();

    fireEvent.click(within(dialog).getByRole("button", { name: "Create rule" }));
    await waitFor(() => expect(sent("POST", "/api/custom-formats")).toHaveLength(1));
    expect(sent("POST", "/api/custom-formats")[0]!.body).toMatchObject({ trashId: "genuine", conditions: null, name: "Genuine HDR", score: 100 });
  });

  it("shows a Deluno-written guide format's patterns as a title match, and drops back to a custom rule when the choice is cleared", async () => {
    load([]);
    renderPage();
    const dialog = await openNew();

    fireEvent.change(within(dialog).getByRole("combobox", { name: /Guide choice/ }), { target: { value: "plain" } });
    expect(within(dialog).getByText("Deluno's own format: TRaSH does not publish one.")).toBeTruthy();
    expect(within(dialog).getByText("Matches a release title containing any one of these patterns.")).toBeTruthy();
    expect(within(dialog).getByText("2 conditions")).toBeTruthy();

    fireEvent.change(within(dialog).getByRole("combobox", { name: /Guide choice/ }), { target: { value: "" } });
    expect(within(dialog).getByText("Set up below")).toBeTruthy();
    expect(within(dialog).queryByText("Deluno's own format: TRaSH does not publish one.")).toBeNull();
  });
});

describe("a rule whose guide format is gone, and answers whose body cannot be read", () => {
  const brokenBody = () => ({ ok: false, status: 500, url: "", text: () => Promise.reject(new Error("dropped")) }) as unknown as Response;

  it("opens as a custom rule when the guide no longer has the format it was made from", async () => {
    load([rule("cf-1", "Orphan", { trashId: "gone" })]);
    renderPage();
    const dialog = await openRule("Orphan");
    expect(within(dialog).getByText("Movies · Custom rule")).toBeTruthy();
  });

  it("names the address and status when a failed save sends no readable body", async () => {
    replies["PUT /api/custom-formats/cf-1"] = brokenBody;
    load([rule("cf-1", "Mine")]);
    renderPage();
    const dialog = await openRule("Mine");
    fireEvent.change(within(dialog).getByPlaceholderText("e.g. Dolby Vision"), { target: { value: "Mine 2" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save rule" }));
    expect(await within(dialog).findByText("Request failed for /api/custom-formats with status 500.")).toBeTruthy();
  });

  it("names the address and status when a failed import sends no readable body", async () => {
    replies["POST /api/custom-formats/import"] = brokenBody;
    load([]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByRole("textbox"), { target: { value: "[{}]" } });
    fireEvent.click(within(dialog).getAllByRole("button", { name: "Import" }).at(-1)!);
    expect(await within(dialog).findByText("Request failed for /api/custom-formats/import with status 500.")).toBeTruthy();
  });

  it("closes the import and preset drawers from their cancel buttons", async () => {
    load([], { guide: { ...baseGuide, bundles: [{ id: "b", name: "Bundle", level: "Basic", mediaType: "movies", description: "d", bestFor: "b", warnings: [], includes: [] }] } });
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByText("Bundle"));
    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

describe("the criterion editor", () => {
  async function newRuleWithCriterion() {
    load([]);
    renderPage();
    const dialog = await openNew();
    fireEvent.click(within(dialog).getByRole("button", { name: "Add matching criterion" }));
    return dialog;
  }
  const kind = (dialog: HTMLElement) => within(dialog).getAllByRole("combobox", { name: /Match on/ })[0] as HTMLSelectElement;

  it("takes a year range from either side and sends it as from-to", async () => {
    replies["POST /api/custom-formats"] = () => json(rule("cf-y", "Years"));
    const dialog = await newRuleWithCriterion();
    fireEvent.change(kind(dialog), { target: { value: "year" } });

    fireEvent.change(within(dialog).getByRole("spinbutton", { name: /From year/ }), { target: { value: "2020" } });
    fireEvent.change(within(dialog).getByRole("spinbutton", { name: /To year/ }), { target: { value: "2024" } });
    fireEvent.change(within(dialog).getByPlaceholderText("e.g. Dolby Vision"), { target: { value: "Years" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(sent("POST", "/api/custom-formats")).toHaveLength(1));
    expect(JSON.parse((sent("POST", "/api/custom-formats")[0]!.body as { conditions: string }).conditions)).toEqual([expect.objectContaining({ type: "year", value: "2020-2024" })]);
  });

  it("labels a size range in gigabytes", async () => {
    const dialog = await newRuleWithCriterion();
    fireEvent.change(kind(dialog), { target: { value: "size" } });

    fireEvent.change(within(dialog).getByRole("spinbutton", { name: /Minimum size \(GB\)/ }), { target: { value: "4" } });
    expect((within(dialog).getByRole("spinbutton", { name: /Maximum size \(GB\)/ }) as HTMLInputElement).value).toBe("");
    fireEvent.change(within(dialog).getByRole("spinbutton", { name: /Maximum size \(GB\)/ }), { target: { value: "20" } });
    expect((within(dialog).getByRole("spinbutton", { name: /Minimum size \(GB\)/ }) as HTMLInputElement).value).toBe("4");
  });

  it("offers Release type only to a TV rule, with its three kinds", async () => {
    const dialog = await newRuleWithCriterion();
    expect(Array.from(kind(dialog).options).map((option) => option.text)).not.toContain("Release type");

    fireEvent.click(within(dialog).getByRole("radio", { name: "TV" }));
    expect(Array.from(kind(dialog).options).map((option) => option.text)).toContain("Release type");
    fireEvent.change(kind(dialog), { target: { value: "releaseType" } });
    const picker = within(dialog).getByRole("combobox", { name: /Release type/ }) as HTMLSelectElement;
    expect(Array.from(picker.options).map((option) => option.text)).toEqual(expect.arrayContaining(["Single episode", "Multi-episode", "Season pack"]));
    fireEvent.change(picker, { target: { value: "season pack" } });
    expect(picker.value).toBe("season pack");
  });

  it("picks a known indexer flag, or any other flag by name", async () => {
    const dialog = await newRuleWithCriterion();
    fireEvent.change(kind(dialog), { target: { value: "indexerFlag" } });
    const flag = within(dialog).getByRole("combobox", { name: /Flag to match/ }) as HTMLSelectElement;

    fireEvent.change(flag, { target: { value: "Freeleech" } });
    expect(flag.value).toBe("Freeleech");
    fireEvent.change(flag, { target: { value: "__custom" } });
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Other flag" }), { target: { value: "Scene" } });
    expect((within(dialog).getByRole("textbox", { name: "Other flag" }) as HTMLInputElement).value).toBe("Scene");
  });

  it("matches on words, or on the absence of them, and a stored year with no end reads as the start", async () => {
    const dialog = await newRuleWithCriterion();
    fireEvent.change(within(dialog).getByRole("combobox", { name: /^Rule/ }), { target: { value: "not" } });
    expect((within(dialog).getByRole("combobox", { name: /^Rule/ }) as HTMLSelectElement).value).toBe("not");
    fireEvent.change(within(dialog).getByRole("combobox", { name: /^Rule/ }), { target: { value: "is" } });
    expect((within(dialog).getByRole("combobox", { name: /^Rule/ }) as HTMLSelectElement).value).toBe("is");
  });

  it("opens a stored year with no end as its start alone", async () => {
    load([rule("cf-y", "Since", { conditions: JSON.stringify([{ type: "year", value: "2020" }]) })]);
    renderPage();
    const dialog = await openRule("Since");
    openAdvanced(dialog);
    expect((within(dialog).getByRole("spinbutton", { name: /From year/ }) as HTMLInputElement).value).toBe("2020");
    expect((within(dialog).getByRole("spinbutton", { name: /To year/ }) as HTMLInputElement).value).toBe("");
  });
});

describe("saving a rule", () => {
  it("sends a score of 0 when the legacy input is cleared", async () => {
    replies["POST /api/custom-formats"] = () => json(rule("cf-z", "Zero"));
    load([]);
    renderPage();
    const dialog = await openNew();
    fireEvent.change(within(dialog).getByPlaceholderText("e.g. Dolby Vision"), { target: { value: "Zero" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Advanced legacy input/ }));
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Legacy guide input/ }), { target: { value: "__custom" } });
    fireEvent.change(within(dialog).getByRole("spinbutton", { name: "Custom legacy input" }), { target: { value: "" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(sent("POST", "/api/custom-formats")).toHaveLength(1));
    expect(sent("POST", "/api/custom-formats")[0]!.body).toMatchObject({ score: 0 });
  });

  it("ignores a second save while the first is still going, and clears the saved message once the form changes again", async () => {
    const pending = deferred<Response>();
    replies["PUT /api/custom-formats/cf-1"] = () => pending.promise;
    load([rule("cf-1", "Mine")]);
    renderPage();
    const dialog = await openRule("Mine");
    const name = within(dialog).getByPlaceholderText("e.g. Dolby Vision");
    fireEvent.change(name, { target: { value: "Mine 2" } });
    const form = dialog.querySelector("form")!;

    fireEvent.submit(form);
    fireEvent.submit(form);
    await act(async () => { pending.resolve(json(rule("cf-1", "Mine 2"))); });
    expect(await within(dialog).findByText("Saved just now")).toBeTruthy();
    expect(sent("PUT", "/api/custom-formats/cf-1")).toHaveLength(1);

    fireEvent.change(name, { target: { value: "Mine 3" } });
    await waitFor(() => expect(within(dialog).queryByText("Saved just now")).toBeNull());
  });

  it("says a plain sentence when the save fails with no reason", async () => {
    replies["PUT /api/custom-formats/cf-1"] = () => { throw "nope"; };
    load([rule("cf-1", "Mine")]);
    renderPage();
    const dialog = await openRule("Mine");
    fireEvent.change(within(dialog).getByPlaceholderText("e.g. Dolby Vision"), { target: { value: "Mine 2" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save rule" }));
    expect(await within(dialog).findByText("Could not save")).toBeTruthy();
  });
});

describe("the intent of a rule", () => {
  it.each([
    [-10000, "Must not have"], [-100, "Avoid"], [0, "I do not care"], [100, "Prefer"], [500, "Strongly prefer"]
  ])("shows a score of %i as '%s' chosen", async (score, label) => {
    load([rule("cf-1", "Mine", { score })]);
    renderPage();
    const dialog = await openRule("Mine");
    expect(within(dialog).getByRole("radio", { name: new RegExp(`^${label}`) }).getAttribute("aria-checked")).toBe("true");
  });

  it("stores each intent's own score when it is chosen", async () => {
    replies["PUT /api/custom-formats/cf-1"] = (init) => json(rule("cf-1", "Mine", { score: JSON.parse(init.body!).score }));
    load([rule("cf-1", "Mine", { score: 0 })]);
    renderPage();
    const dialog = await openRule("Mine");

    for (const [label, score] of [["Must not have", -10000], ["Avoid", -100], ["Prefer", 100], ["Strongly prefer", 500], ["I do not care", 0]] as const) {
      fireEvent.click(within(dialog).getByRole("radio", { name: new RegExp(`^${label}`) }));
      fireEvent.click(within(dialog).getByRole("button", { name: "Save rule" }));
      await waitFor(() => expect((sent("PUT", "/api/custom-formats/cf-1").at(-1)?.body as { score: number } | undefined)?.score).toBe(score));
      calls.length = 0;
      await waitFor(() => expect((within(dialog).getByRole("button", { name: /Save rule|Saved/ }) as HTMLButtonElement)).toBeTruthy());
    }
  });

  it("offers a custom intent only for a value that fits none of the choices, and choosing it changes nothing", async () => {
    load([rule("cf-1", "Mine", { score: "abc" })]);
    renderPage();
    const dialog = await openRule("Mine");
    expect(within(dialog).getByRole("radio", { name: /^Custom intent/ }).getAttribute("aria-checked")).toBe("true");

    act(() => seen.choice!.onValueChange("custom"));
    expect(within(dialog).getByRole("radio", { name: /^Custom intent/ }).getAttribute("aria-checked")).toBe("true");
  });
});

describe("removing a rule", () => {
  it("accepts a 204 as removed", async () => {
    replies["DELETE /api/custom-formats/cf-1"] = () => new Response(null, { status: 204 });
    load([rule("cf-1", "Mine")]);
    renderPage();
    const dialog = await openRule("Mine");
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Mine removed"));
    expect(revalidate).toHaveBeenCalled();
  });

  it("says why a removal failed, or a plain sentence when there is no reason", async () => {
    replies["DELETE /api/custom-formats/cf-1"] = () => json({ message: "In use." }, 409);
    load([rule("cf-1", "Mine")]);
    renderPage();
    const dialog = await openRule("Mine");
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledTimes(1));

    replies["DELETE /api/custom-formats/cf-1"] = () => { throw "nope"; };
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Rule could not be removed."));
  });
});

describe("export and import, when they go wrong", () => {
  it("says why an export failed, or a plain sentence when there is no reason", async () => {
    replies["GET /api/custom-formats/cf-1/export"] = () => json({ message: "Gone." }, 404);
    replies["GET /api/custom-formats/export"] = () => json({ message: "Busy." }, 503);
    load([rule("cf-1", "Mine")]);
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Export all" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Busy."));
    replies["GET /api/custom-formats/export"] = () => { throw "nope"; };
    fireEvent.click(screen.getByRole("button", { name: "Export all" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not export the custom formats."));

    const dialog = await openRule("Mine");
    fireEvent.click(within(dialog).getByRole("button", { name: "Export this rule" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Gone."));
    replies["GET /api/custom-formats/cf-1/export"] = () => { throw "nope"; };
    fireEvent.click(within(dialog).getByRole("button", { name: "Export this rule" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not export this rule."));
  });

  async function openImport() {
    load([]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByRole("textbox"), { target: { value: "[{}]" } });
    return dialog;
  }
  const importNow = (dialog: HTMLElement) => fireEvent.click(within(dialog).getAllByRole("button", { name: "Import" }).at(-1)!);

  it("shows the server's words, naming the address when it sent one, and a plain sentence when the call throws no reason", async () => {
    const refused = new Response(JSON.stringify({ title: "Not allowed." }), { status: 403 });
    Object.defineProperty(refused, "url", { value: "https://deluno.example/api/custom-formats/import" });
    replies["POST /api/custom-formats/import"] = () => refused;
    const dialog = await openImport();
    importNow(dialog);
    expect(await within(dialog).findByText(/Not allowed|403/)).toBeTruthy();

    replies["POST /api/custom-formats/import"] = () => { throw "nope"; };
    importNow(dialog);
    expect(await within(dialog).findByText("Could not import.")).toBeTruthy();
  });

  it("lists a rule that was not imported with its reason, an unnamed one as unnamed, and says how many came in", async () => {
    replies["POST /api/custom-formats/import"] = () => json([
      { name: null, imported: false, id: null, droppedConditions: [], error: "No name." },
      { name: "Fine", imported: true, id: "cf-9", droppedConditions: [], error: null }
    ]);
    const dialog = await openImport();
    importNow(dialog);

    expect(await within(dialog).findByText("Imported 1 of 2 rules.")).toBeTruthy();
    expect(within(dialog).queryByText(/Some conditions could not be understood/)).toBeNull();
    expect(within(dialog).getByText("(unnamed)")).toBeTruthy();
    expect(within(dialog).getByText("Not imported")).toBeTruthy();
    expect(within(dialog).getByText("No name.")).toBeTruthy();
  });

  it("lists the conditions it could not understand, and ignores a file dialog that chose no file", async () => {
    replies["POST /api/custom-formats/import"] = () => json([{ name: "Half", imported: true, id: "cf-9", droppedConditions: ["odd one", "other"], error: null }]);
    const dialog = await openImport();
    const input = dialog.querySelector("input[type='file']") as HTMLInputElement;
    fireEvent.change(input, { target: { files: [] } });
    expect((within(dialog).getByRole("textbox") as HTMLTextAreaElement).value).toBe("[{}]");

    importNow(dialog);
    expect(await within(dialog).findByText("Could not understand: odd one, other")).toBeTruthy();
  });
});

describe("the preset list and drawer", () => {
  const bundle = (id: string, name: string, over: Record<string, unknown> = {}) => ({
    id, name, level: "Basic", mediaType: "movies", description: `${name} description.`, bestFor: `${name} fans`, warnings: [], includes: [], ...over
  });
  const withBundles = (bundles: unknown[], formats: unknown[] = []) => load(formats, { guide: { ...baseGuide, bundles } });

  it("sorts by each column, and rates a preset with no rules as not applied", () => {
    withBundles([
      bundle("full", "Full", { includes: [{ trashId: "plain", score: 100 }] }),
      bundle("part", "Part", { includes: [{ trashId: "plain", score: 100 }, { trashId: "genuine", score: 50 }] }),
      bundle("none", "None", { includes: [{ trashId: "genuine", score: 100 }, { trashId: "plain", score: 100 }] }),
      bundle("empty", "Empty")
    ], [rule("cf-p", "Plain Audio", { trashId: "plain" })]);
    renderPage();

    for (const column of ["Preset", "Best for", "Rules", "Status"]) {
      fireEvent.click(screen.getByRole("button", { name: new RegExp(`^${column}`) }));
      fireEvent.click(screen.getByRole("button", { name: new RegExp(`^${column}`) }));
    }
    expect(screen.getAllByText("Not applied").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Partly applied").length).toBeGreaterThan(0);
    expect(screen.getByText("Applied")).toBeTruthy();
  });

  it("opens a preset for one media type without its media-type suffixes, or without warnings", async () => {
    withBundles([bundle("one", "One", { includes: [{ trashId: "plain", score: 100 }] })]);
    renderPage();
    fireEvent.click(screen.getByText("One"));
    const dialog = await screen.findByRole("dialog");

    expect(within(dialog).getByText("Basic · Movies")).toBeTruthy();
    expect(within(dialog).getByText("0 of 1 already here")).toBeTruthy();
    expect(within(dialog).getByText("Plain Audio")).toBeTruthy();
  });

  it("says every rule is already here when asked to apply a finished preset", async () => {
    withBundles([bundle("one", "One", { includes: [{ trashId: "plain", score: 100 }] })], [rule("cf-p", "Plain Audio", { trashId: "plain" })]);
    renderPage();
    fireEvent.click(screen.getByText("One"));
    const dialog = await screen.findByRole("dialog");

    await act(async () => { (drawerProps("One").footer!.props.onSave as () => void)(); });

    expect(await within(dialog).findByText("Every rule in this preset is already applied.")).toBeTruthy();
  });

  it("names the rule that could not be added, with its reason or none, and carries on to say so", async () => {
    withBundles([bundle("one", "One", { includes: [{ trashId: "plain", score: 100 }] })]);
    replies["POST /api/custom-formats"] = () => json({ message: "Duplicate." }, 409);
    renderPage();
    fireEvent.click(screen.getByText("One"));
    const dialog = await screen.findByRole("dialog");

    fireEvent.click(within(dialog).getByRole("button", { name: "Apply preset" }));
    expect(await within(dialog).findByText(/could not be added: Duplicate\./)).toBeTruthy();

    replies["POST /api/custom-formats"] = () => { throw "nope"; };
    fireEvent.click(within(dialog).getByRole("button", { name: "Apply preset" }));
    expect(await within(dialog).findByText(/could not be added: unknown error/)).toBeTruthy();
  });

  it("falls back to a plain sentence when the preset itself cannot say why it failed", async () => {
    withBundles([bundle("one", "One", { includes: [{ trashId: "plain", score: 100 }] })]);
    renderPage();
    fireEvent.click(screen.getByText("One"));
    const dialog = await screen.findByRole("dialog");
    revalidate.mockImplementation(() => { throw "nope"; });
    replies["POST /api/custom-formats"] = () => json(rule("cf-n", "Plain Audio"));

    fireEvent.click(within(dialog).getByRole("button", { name: "Apply preset" }));

    expect(await within(dialog).findByText("Preset could not be applied.")).toBeTruthy();
  });

  it("shows an empty drawer for a preset the guide no longer has, and cannot apply it", async () => {
    withBundles([bundle("one", "One", { includes: [{ trashId: "plain", score: 100 }] })]);
    const view = renderPage();
    fireEvent.click(screen.getByText("One"));
    await screen.findByRole("dialog");

    withBundles([]);
    view.rerender(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);
    expect(screen.getByRole("heading", { name: "Preset" })).toBeTruthy();
    await act(async () => { (drawerProps("Preset").footer!.props.onSave as () => void)(); });
    expect(sent("POST", "/api/custom-formats")).toHaveLength(0);
  });
});

describe("Test a release, the corners", () => {
  const dryRun = [{ formatId: "cf-1", formatName: "Mine", mediaType: "movies", score: 100, isMatch: true, matchedConditions: ["releaseTitle:REMUX", "NOT language:French", "mystery:odd", "plain"], missedConditions: [] }];
  const verdict = (over: Record<string, unknown> = {}) => ({
    releaseName: "x", planId: "p", planVersion: "v1", planHash: "0123456789abcdef", candidateFacts: [], currentReleaseName: null, currentFacts: null, currentEvaluation: null,
    candidateEvaluation: { status: "meetsPlan", reasons: [], families: [] }, comparison: null, ...over
  });
  async function openTest(profiles: unknown[] = [profile("qp-1", "Films"), profile("qp-2", "Shows", "tv")]) {
    load([rule("cf-1", "Mine")], { qualityProfiles: profiles });
    replies["POST /api/custom-formats/dry-run"] = () => json(dryRun);
    planApi.compile.mockResolvedValue({ plan: { id: "p", version: "v1" } });
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Test a release" }));
    return await screen.findByRole("dialog");
  }
  const runTest = async (dialog: HTMLElement, name = "Heat.1995.2160p-GRP") => {
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Release name to test" }), { target: { value: name } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Test" }));
  };

  it("starts a test on Enter, and ignores Enter on an empty name and any other key", async () => {
    planApi.preview.mockResolvedValue(verdict());
    const dialog = await openTest();
    const box = within(dialog).getByRole("textbox", { name: "Release name to test" });

    fireEvent.keyDown(box, { key: "Enter" });
    expect(sent("POST", "/api/custom-formats/dry-run")).toHaveLength(0);
    fireEvent.change(box, { target: { value: "Heat.1995" } });
    fireEvent.keyDown(box, { key: "a" });
    expect(sent("POST", "/api/custom-formats/dry-run")).toHaveLength(0);
    fireEvent.keyDown(box, { key: "Enter" });
    await waitFor(() => expect(sent("POST", "/api/custom-formats/dry-run")).toHaveLength(1));
  });

  it("lists the profiles for the scope chosen, tells Movies from TV in each, and keeps the profile chosen when reopened", async () => {
    planApi.preview.mockResolvedValue(verdict());
    const dialog = await openTest();
    const picker = within(dialog).getByRole("combobox", { name: /Release preferences to test against/ }) as HTMLSelectElement;
    expect(Array.from(picker.options).map((option) => option.text)).toEqual(expect.arrayContaining(["Films · Movies", "Shows · TV"]));

    fireEvent.change(picker, { target: { value: "qp-2" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    fireEvent.click(screen.getByRole("button", { name: "Test a release" }));
    const again = await screen.findByRole("dialog");
    expect((within(again).getByRole("combobox", { name: /Release preferences to test against/ }) as HTMLSelectElement).value).toBe("qp-2");
  });

  it("has nothing to choose, and runs only the older rules, when there are no quality profiles", async () => {
    const dialog = await openTest([]);
    expect(within(dialog).getByText("No Quality Profiles available")).toBeTruthy();

    await runTest(dialog);
    expect(await within(dialog).findByText("1 of 1 matched")).toBeTruthy();
    expect(planApi.compile).not.toHaveBeenCalled();
    expect(within(dialog).getByText("Release title: REMUX · Not language matched in the release name: French · mystery: odd · plain")).toBeTruthy();
  });

  it.each([
    ["rejected", "Rejected"], ["needsReview", "Needs review"], ["currentBetter", "Your file is better"], ["equivalent", "Equivalent"],
    ["bestMatchNow", "Best match now"], ["acceptable", "Acceptable"], ["upgrade", "Upgrade"]
  ])("reads a comparison of %s as '%s'", async (status, label) => {
    planApi.preview.mockResolvedValue(verdict({ comparison: { status, reasons: [] } }));
    const dialog = await openTest();
    await runTest(dialog);
    expect((await within(dialog).findAllByText(label)).length).toBeGreaterThan(0);
  });

  it.each([
    ["meetsPlan", "Meets plan"], ["belowGoal", "Below goal"], ["needsReview", "Needs review"], ["missingEvidence", "Missing evidence"]
  ])("reads a verdict with no comparison and a status of %s as '%s'", async (status, label) => {
    planApi.preview.mockResolvedValue(verdict({ candidateEvaluation: { status, reasons: [], families: [] } }));
    const dialog = await openTest();
    await runTest(dialog);
    expect((await within(dialog).findAllByText(label)).length).toBeGreaterThan(0);
  });

  it("says a plain sentence when the typed verdict fails with no reason", async () => {
    planApi.compile.mockRejectedValue("nope");
    const dialog = await openTest();
    planApi.compile.mockRejectedValue("nope");
    await runTest(dialog);
    expect(await within(dialog).findByText("The typed release preview could not be run.")).toBeTruthy();
    expect(within(dialog).getByText("Nothing to report — the test could not be run.")).toBeTruthy();
  });

  it("warns that nothing can match when there are no rules", async () => {
    load([], { qualityProfiles: [] });
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Test a release" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("You have no rules yet, so nothing can match. Apply a preset first.")).toBeTruthy();
  });
});

describe("the safeguards, the corners", () => {
  it("ignores a second save while the first is going, and says a plain sentence when the save fails with no reason", async () => {
    const pending = deferred<Response>();
    replies["PATCH /api/settings"] = () => pending.promise;
    load([]);
    renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "Rules only" }));
    const form = screen.getByRole("button", { name: "Save safeguards" }).closest("form")!;

    fireEvent.submit(form);
    fireEvent.submit(form);
    await act(async () => { pending.resolve(json({})); });
    await waitFor(() => expect(sent("PATCH", "/api/settings")).toHaveLength(1));

    replies["PATCH /api/settings"] = () => { throw "nope"; };
    fireEvent.click(screen.getByRole("radio", { name: "Ranking only" }));
    fireEvent.submit(form);
    expect(await screen.findByText("Could not save")).toBeTruthy();
  });

  it("shows no words when all are removed, and adds on Enter alone", () => {
    load([], { settings: { ...emptyPlatformSettingsSnapshot, searchScoringMode: "hybrid", releaseNeverGrabPatterns: "CAM" } });
    renderPage();
    const box = screen.getByRole("textbox", { name: "Add a never-grab word" });

    fireEvent.change(box, { target: { value: "WORD" } });
    fireEvent.keyDown(box, { key: "a" });
    expect(screen.queryByText("WORD")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Remove CAM" }));
    expect(screen.queryByRole("button", { name: /^Remove / })).toBeNull();
    fireEvent.keyDown(box, { key: "Enter" });
    expect(screen.getByText("WORD")).toBeTruthy();
    fireEvent.change(box, { target: { value: "  " } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(screen.getAllByRole("button", { name: /^Remove / })).toHaveLength(1);
  });
});

describe("a drawer asked to open when it is already open", () => {
  it("stays open for every drawer, and closes when asked to close", async () => {
    load([rule("cf-1", "Mine")], { guide: { ...baseGuide, bundles: [{ id: "b", name: "Bundle", level: "Basic", mediaType: "all", description: "d", bestFor: "b", warnings: ["Careful."], includes: [{ trashId: "plain", score: 100 }] }] } });
    renderPage();

    fireEvent.click(screen.getByText("Bundle"));
    await screen.findByRole("dialog");
    expect(screen.getByText("Careful.")).toBeTruthy();
    expect(screen.getByText(/· Movies$/)).toBeTruthy();
    act(() => drawerProps("Bundle").onOpenChange(true));
    expect(screen.queryByRole("dialog")).toBeTruthy();
    act(() => drawerProps("Bundle").onOpenChange(false));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByRole("button", { name: "Test a release" }));
    await screen.findByRole("dialog");
    act(() => drawerProps("Test a release").onOpenChange(true));
    expect(screen.queryByRole("dialog")).toBeTruthy();
    act(() => drawerProps("Test a release").onOpenChange(false));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    await screen.findByRole("dialog");
    act(() => drawerProps("Import custom formats").onOpenChange(true));
    expect(screen.queryByRole("dialog")).toBeTruthy();
    act(() => drawerProps("Import custom formats").onOpenChange(false));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    const rules = await openRule("Mine");
    act(() => drawerProps("Mine").onOpenChange(true));
    expect(rules.isConnected).toBe(true);
    act(() => drawerProps("Mine").onOpenChange(false));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("keeps the discard question open when asked to open, and closes it when asked to close", async () => {
    load([rule("cf-1", "Mine")]);
    renderPage();
    const dialog = await openRule("Mine");
    fireEvent.change(within(dialog).getByPlaceholderText("e.g. Dolby Vision"), { target: { value: "Changed" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await screen.findByText("Discard unsaved changes?");

    const confirm = seen.confirms.get("Discard unsaved changes?") as unknown as { onOpenChange: (open: boolean) => void };
    act(() => confirm.onOpenChange(true));
    expect(screen.queryByText("Discard unsaved changes?")).toBeTruthy();
    act(() => confirm.onOpenChange(false));
    await waitFor(() => expect(screen.queryByText("Discard unsaved changes?")).toBeNull());
  });
});

describe("the small helpers", () => {
  it("describes a matched condition in plain words", () => {
    expect(describeMatchedCondition("NOT hdr:HDR10")).toBe("Not hDR format: HDR10");
    expect(describeMatchedCondition("language:French")).toBe("Language matched in the release name: French");
    expect(describeMatchedCondition("mystery:odd")).toBe("mystery: odd");
    expect(describeMatchedCondition("no separator")).toBe("no separator");
  });

  it("reads and writes a range, and names a file from a rule", () => {
    expect(parseRange("2020-2024")).toEqual({ min: "2020", max: "2024" });
    expect(parseRange("2020")).toEqual({ min: "2020", max: "" });
    expect(formatRange(" 4 ", " 20 ")).toBe("4-20");
    expect(slugForFileName("  My Rule!! ")).toBe("my-rule");
    expect(slugForFileName("***")).toBe("custom-format");
  });
});
