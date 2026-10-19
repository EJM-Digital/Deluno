import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot, type GuidePackage } from "../lib/api";

/**
 * Custom Formats (Quality & Release): the rules that score a release. Each control (the safeguards, a rule's drawer, Export, Import and
 * Test a release) sends the request the custom-format routes are tested against (RuleRoutesTests, QualityRoutesTests); a refusal is said in
 * the server's words and nothing is shown as saved.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn() })
  };
});

const guide: GuidePackage = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 3,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "", qualityTiers: [], customFormats: [], qualityProfiles: [], bundles: []
};

const rule = (overrides: Record<string, unknown> = {}) => ({
  id: "cf-1", name: "Good groups", mediaType: "movies", score: 100, trashId: null, upgradeAllowed: true,
  conditions: JSON.stringify([{ type: "releaseTitle", value: "NTb", negate: false, required: true }, { type: "releaseTitle", value: "FLUX", negate: false, required: true }]),
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...overrides
});

let loaderData: Record<string, unknown>;
const revalidateMock = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock })
}));

const { SettingsCustomFormatsPage } = await import("./settings-custom-formats-page");

vi.setConfig({ testTimeout: 20_000 });

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, url: "", json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(typeof body === "string" ? body : JSON.stringify(body)) } as unknown as Response);
}

function load(customFormats: unknown[] = [], overrides: Record<string, unknown> = {}) {
  loaderData = {
    libraries: [{ id: "lib-1", name: "Films", mediaType: "movies", defaultPolicySetId: "plan-1" }],
    policySets: [{ id: "plan-1", name: "Plan", customFormatIds: "cf-1" }],
    qualityProfiles: [{ id: "qp-1", name: "HD", mediaType: "movies" }],
    customFormats,
    settings: { ...emptyPlatformSettingsSnapshot, releaseNeverGrabPatterns: "CAM\nTELESYNC" },
    guide,
    guideUpdateCheck: { isEnabled: false, lastCheckedUtc: null, lastSeenRevision: null, status: "disabled", error: null, report: null, updatedUtc: new Date(0).toISOString() },
    guideVersions: [],
    ruleMigration: null,
    ...overrides
  };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsCustomFormatsPage />
    </MemoryRouter>
  );
}

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

/** The matching criteria sit in a fold that is open on a new custom rule and shut on a saved one. */
function openAdvanced(drawer: ReturnType<typeof within>) {
  const fold = drawer.getByRole("button", { name: /Advanced matching/ });
  if (fold.getAttribute("aria-expanded") === "false") fireEvent.click(fold);
}

function switchOf(scope: ReturnType<typeof within>, label: string) {
  const row = scope.getAllByText(label).map((element: HTMLElement) => element.closest("label")).find((candidate: HTMLLabelElement | null) => candidate !== null);
  const found = (row?.htmlFor ? document.getElementById(row.htmlFor) : row?.querySelector("[role=switch]")) as HTMLElement | null | undefined;
  if (!found) throw new Error(`No switch labelled ${label}`);
  return found;
}

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  authedFetchMock.mockImplementation(() => reply([]));
  Object.assign(URL, { createObjectURL: vi.fn(() => "blob:test"), revokeObjectURL: vi.fn() });
  load();
});

describe("the safeguards", () => {
  it("lists the words and groups that are never grabbed, adds one, restores the defaults and saves them with the scoring mode", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PATCH" ? { ...emptyPlatformSettingsSnapshot } : []));
    renderPage();
    expect(screen.getByText("CAM")).toBeVisible();
    expect(screen.getByText("TELESYNC")).toBeVisible();

    fireEvent.change(screen.getByLabelText("Add a never-grab word"), { target: { value: "HDTS" } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(screen.getByText("HDTS")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Remove TELESYNC" }));
    expect(screen.queryByText("TELESYNC")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Save safeguards" }));

    await waitFor(() => expect(calls("PATCH", "/api/settings")).toHaveLength(1));
    expect(bodyOf(calls("PATCH", "/api/settings")[0]).releaseNeverGrabPatterns.split("\n")).toEqual(["CAM", "HDTS"]);
    expect(await screen.findByText("Saved just now")).toBeVisible();
  });

  it("a word already on the list is not added twice, and Restore defaults brings back the shipped list", async () => {
    renderPage();

    fireEvent.change(screen.getByLabelText("Add a never-grab word"), { target: { value: "cam" } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(screen.getAllByText(/^CAM$/i)).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Remove CAM" }));
    expect(screen.queryByText("CAM")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Restore defaults" }));

    expect(screen.getByText("cam")).toBeVisible();
    expect(screen.getByText("camrip")).toBeVisible();
  });
});

describe("the list of rules", () => {
  it("lists a rule with what it looks for, its intent and the libraries whose profile uses it", () => {
    load([rule()]);
    renderPage();

    const row = screen.getByText("Good groups").closest("[role=row]") as HTMLElement;
    expect(within(row).getByText(/NTb/)).toBeVisible();
    expect(within(row).getByText(/Films/)).toBeVisible();
  });
});

describe("a new custom rule", () => {
  async function openNew() {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New custom rule" })[0]);
    return within(await screen.findByRole("dialog"));
  }

  it("starts as a movie rule with the Custom rule choice, and asks for a name before it sends anything", async () => {
    const drawer = await openNew();

    expect(drawer.getByText("New custom release rule")).toBeVisible();
    expect(drawer.getByRole("radio", { name: "Movies" })).toBeChecked();
    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    expect(await drawer.findByText("Give this rule a name.")).toBeVisible();
    expect(calls("POST", "/api/custom-formats")).toHaveLength(0);
  });

  it("Create sends the name, media type, intent score and the matching words that were filled in, leaving blank criteria out", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "POST" && path === "/api/custom-formats" ? rule({ id: "new", name: "Dolby Vision" }) : []));
    const drawer = await openNew();
    fireEvent.change(drawer.getByPlaceholderText("e.g. Dolby Vision"), { target: { value: "Dolby Vision" } });
    openAdvanced(drawer);
    fireEvent.click(drawer.getByRole("button", { name: "Add matching criterion" }));
    fireEvent.change(drawer.getByPlaceholderText("e.g. WEB-DL, HDR, or GROUP"), { target: { value: "DV" } });
    fireEvent.click(drawer.getByRole("button", { name: "Add matching criterion" }));

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(calls("POST", "/api/custom-formats")).toHaveLength(1));
    const body = bodyOf(calls("POST", "/api/custom-formats")[0]);
    expect(body).toMatchObject({ name: "Dolby Vision", mediaType: "movies", upgradeAllowed: true });
    expect(JSON.parse(body.conditions)).toEqual([expect.objectContaining({ type: "releaseTitle", value: "DV", negate: false })]);
    expect(await drawer.findByText("Saved just now")).toBeVisible();
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("a criterion can be taken away again", async () => {
    const drawer = await openNew();
    openAdvanced(drawer);
    fireEvent.click(drawer.getByRole("button", { name: "Add matching criterion" }));
    expect(drawer.getByPlaceholderText("e.g. WEB-DL, HDR, or GROUP")).toBeVisible();

    fireEvent.click(drawer.getByRole("button", { name: "Remove criterion" }));

    expect(drawer.queryByPlaceholderText("e.g. WEB-DL, HDR, or GROUP")).not.toBeInTheDocument();
    expect(drawer.getByText("Add at least one matching criterion before saving this custom rule.")).toBeVisible();
  });

  it("shows the server's reason when it refuses the rule and does not say it saved", async () => {
    authedFetchMock.mockImplementation(() => reply({ errors: { name: ["A rule with that name already exists."] }, title: "A rule with that name already exists." }, false, 400));
    const drawer = await openNew();
    fireEvent.change(drawer.getByPlaceholderText("e.g. Dolby Vision"), { target: { value: "Dup" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(calls("POST", "/api/custom-formats")).toHaveLength(1));
    await waitFor(() => expect(drawer.queryByText("Saved just now")).not.toBeInTheDocument());
    expect(screen.getByRole("dialog")).toBeVisible();
  });
});

describe("an existing custom rule", () => {
  async function openIt() {
    load([rule()]);
    renderPage();
    fireEvent.click(screen.getByText("Good groups"));
    return within(await screen.findByRole("dialog"));
  }

  it("opens with its own name, its criteria and a library impact, an Export button and a Remove section", async () => {
    const drawer = await openIt();

    expect(drawer.getByDisplayValue("Good groups")).toBeVisible();
    expect(drawer.getByText("Library impact")).toBeVisible();
    expect(drawer.getByRole("button", { name: "Export this rule" })).toBeVisible();
    expect(drawer.getByText("Remove this rule")).toBeVisible();
    expect(drawer.getByRole("button", { name: "Save rule" })).toBeVisible();
  });

  it("Save puts the changed rule at its own id with the criteria that remain", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" ? rule({ name: "Better groups" }) : []));
    const drawer = await openIt();
    fireEvent.change(drawer.getByDisplayValue("Good groups"), { target: { value: "Better groups" } });
    openAdvanced(drawer);
    fireEvent.click(drawer.getAllByRole("button", { name: "Remove criterion" })[1]);

    fireEvent.click(drawer.getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(calls("PUT", "/api/custom-formats/cf-1")).toHaveLength(1));
    const body = bodyOf(calls("PUT", "/api/custom-formats/cf-1")[0]);
    expect(body.name).toBe("Better groups");
    expect(JSON.parse(body.conditions).map((condition: { value: string }) => condition.value)).toEqual(["NTb"]);
    expect(await drawer.findByText("Saved just now")).toBeVisible();
  });

  it("Export this rule asks for that rule's file and hands it over as a download", async () => {
    authedFetchMock.mockImplementation((path: string) => reply(String(path).endsWith("/export") ? { name: "Good groups", mediaType: "movies", conditions: [] } : []));
    const drawer = await openIt();

    fireEvent.click(drawer.getByRole("button", { name: "Export this rule" }));

    await waitFor(() => expect(calls("GET", "/api/custom-formats/cf-1/export")).toHaveLength(1));
    await waitFor(() => expect(URL.createObjectURL).toHaveBeenCalled());
  });

  it("an export the server refuses says so", async () => {
    authedFetchMock.mockImplementation(() => reply({ message: "Nope." }, false, 500));
    const drawer = await openIt();

    fireEvent.click(drawer.getByRole("button", { name: "Export this rule" }));

    await waitFor(() => expect(toastError).toHaveBeenCalled(), { timeout: 8000 });
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });

  it("Remove takes the rule away and says so", async () => {
    const drawer = await openIt();

    fireEvent.click(drawer.getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(calls("DELETE", "/api/custom-formats/cf-1")).toHaveLength(1));
    expect(toastSuccess).toHaveBeenCalledWith("Good groups removed");
    expect(revalidateMock).toHaveBeenCalled();
  });
});

describe("Export all, Import and Test a release", () => {
  it("Export all is off while there are no rules, and asks for every rule when there are some", async () => {
    renderPage();
    expect(screen.getByRole("button", { name: "Export all" })).toBeDisabled();

    load([rule()]);
    authedFetchMock.mockImplementation((path: string) => reply(path === "/api/custom-formats/export" ? [{ name: "Good groups" }] : []));
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "Export all" }).at(-1)!);

    await waitFor(() => expect(calls("GET", "/api/custom-formats/export")).toHaveLength(1));
  });

  it("Import takes pasted JSON, sends it and lists each rule as imported or not, with what could not be understood", async () => {
    authedFetchMock.mockImplementation((path: string) => reply(path === "/api/custom-formats/import" ? [
      { name: "Good groups", imported: true, error: null, droppedConditions: [] },
      { name: "Odd rule", imported: false, error: "Needs a name.", droppedConditions: ["fuzzy"] }
    ] : []));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByRole("button", { name: "Import" })).toBeDisabled();

    fireEvent.change(drawer.getByPlaceholderText(/"mediaType": "movies"/), { target: { value: '[{"name":"Good groups"},{"name":"Odd rule"}]' } });
    fireEvent.click(drawer.getByRole("button", { name: "Import" }));

    await waitFor(() => expect(calls("POST", "/api/custom-formats/import")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/custom-formats/import")[0])).toEqual([{ name: "Good groups" }, { name: "Odd rule" }]);
    expect(await drawer.findByText("Imported 1 of 2 rules. Some conditions could not be understood — see below.")).toBeVisible();
    expect(drawer.getByText("Not imported")).toBeVisible();
    expect(drawer.getByText("Needs a name.")).toBeVisible();
  });

  it("Import says plainly that the text is not JSON and sends nothing", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    const drawer = within(await screen.findByRole("dialog"));

    fireEvent.change(drawer.getByPlaceholderText(/"mediaType": "movies"/), { target: { value: "not json at all" } });
    fireEvent.click(drawer.getByRole("button", { name: "Import" }));

    expect(await drawer.findByText("That is not valid JSON. Paste what Export produced, unchanged.")).toBeVisible();
    expect(calls("POST", "/api/custom-formats/import")).toHaveLength(0);
  });

  it("Test a release sends the name and the rules to test, and lists the rules that matched", async () => {
    load([rule()], { qualityProfiles: [] });
    authedFetchMock.mockImplementation((path: string) => reply(path === "/api/custom-formats/dry-run" ? [{ formatId: "cf-1", formatName: "Good groups", mediaType: "movies", score: 100, isMatch: true, matchedConditions: [] }] : []));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Test a release" }));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByRole("button", { name: "Test" })).toBeDisabled();

    fireEvent.change(drawer.getByLabelText("Release name to test"), { target: { value: "Movie.2024.1080p.WEB-DL.x264-NTb" } });
    fireEvent.click(drawer.getByRole("radio", { name: "Movies" }));
    fireEvent.click(drawer.getByRole("button", { name: "Test" }));

    await waitFor(() => expect(calls("POST", "/api/custom-formats/dry-run")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/custom-formats/dry-run")[0])).toEqual({ releaseName: "Movie.2024.1080p.WEB-DL.x264-NTb", mediaType: "movies" });
    expect(await drawer.findByText("Good groups")).toBeVisible();
  });
});
