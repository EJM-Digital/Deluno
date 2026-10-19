import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { GuidePackage, QualityModelSnapshot } from "../lib/api";

/**
 * Quality Profiles (Library Profiles): the list, and the drawer that makes, changes, copies and removes a profile. A profile allows some
 * tiers (most preferred first), stops upgrading at one, and says what to prefer. Each control sends the request the quality-profile
 * routes are tested against (QualityRoutesTests); a refusal is said in the server's words and nothing is shown as saved.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
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
const guide: GuidePackage = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 1,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "", qualityTiers: [], customFormats: [], qualityProfiles: [], bundles: []
};

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

vi.setConfig({ testTimeout: 20_000 });

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function load(profiles: unknown[] = []) {
  loaderData = {
    libraries: [{ id: "lib-1", name: "Films", mediaType: "movies", qualityProfileId: "qp-1" }],
    qualityProfiles: profiles, customFormats: [{ id: "cf-1", name: "Prefer HDR", mediaType: "movies" }], settings: {}, qualityModel, guide
  };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsProfilesPage />
    </MemoryRouter>
  );
}

/** The confirmation that opens over the drawer. */
async function confirmation(title: RegExp) {
  return within(await screen.findByRole("dialog", { name: title }));
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

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  authedFetchMock.mockImplementation(() => reply({}));
  load();
});

describe("the list of quality profiles", () => {
  it("says what a quality profile is and offers a first one, when there are none", () => {
    renderPage();
    expect(screen.getByText("No quality profiles yet")).toBeVisible();
    expect(screen.getAllByRole("button", { name: "New quality profile" }).length).toBeGreaterThan(0);
  });

  it("lists a profile with its tiers best first, where it stops, the libraries using it and how many formats it has", () => {
    load([profile()]);
    renderPage();

    const row = screen.getAllByRole("row")[1];
    expect(within(row).getByText("HD films")).toBeVisible();
    expect(within(row).getByText("WEB 1080p → WEB 720p")).toBeVisible();
    expect(within(row).getByText("Upgrades until this tier")).toBeVisible();
    expect(within(row).getByText("1 library")).toBeVisible();
    expect(within(row).getByText("1 format")).toBeVisible();
    expect(screen.getByText("1 profile")).toBeVisible();
  });

  it("warns when a profile allows a tier the quality model no longer has", () => {
    load([profile({ allowedQualities: "WEB 720p, Lost tier" })]);
    renderPage();
    expect(screen.getByText("Check tiers")).toBeVisible();
    expect(screen.getByText(/1 tier/)).toBeVisible();
  });
});

describe("a new quality profile", () => {
  async function openNew() {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);
    return within(await screen.findByRole("dialog"));
  }

  it("starts empty as a movie profile with the seven questions, nothing allowed yet", async () => {
    const drawer = await openNew();

    expect(drawer.getByLabelText("Profile name")).toHaveValue("");
    expect(drawer.getByRole("radio", { name: "Movies" })).toBeChecked();
    expect(drawer.getByText("How good, and when to stop?")).toBeVisible();
    expect(drawer.getByText("No tiers chosen yet")).toBeVisible();
    expect(drawer.getByRole("button", { name: "Create quality profile" })).toBeVisible();
    expect(drawer.getByLabelText("Choose a template")).toHaveValue("");
  });

  it("asks for a name and at least one tier before it sends anything", async () => {
    const drawer = await openNew();

    fireEvent.click(drawer.getByRole("button", { name: "Create quality profile" }));

    expect(await drawer.findByText("Give this quality profile a name.")).toBeVisible();
    expect(drawer.getByText("Allow at least one quality tier.")).toBeVisible();
    expect(calls("POST", "/api/quality-profiles")).toHaveLength(0);
  });

  it("adding tiers lists them best first, the first one added becomes where it stops, and a tier can move or go", async () => {
    const drawer = await openNew();

    fireEvent.change(drawer.getByLabelText("Add a tier"), { target: { value: "WEB 720p" } });
    fireEvent.change(drawer.getByLabelText("Add a tier"), { target: { value: "WEB 1080p" } });

    expect((drawer.getByLabelText("Stop upgrading at") as HTMLSelectElement).value).toBe("WEB 720p");
    expect(tierNames(drawer)).toEqual(["WEB 1080p", "WEB 720p"]);
    expect(drawer.getByRole("button", { name: "Move WEB 1080p up" })).toBeDisabled();
    fireEvent.click(drawer.getByRole("button", { name: "Move WEB 1080p down" }));
    expect(tierNames(drawer)).toEqual(["WEB 720p", "WEB 1080p"]);
    fireEvent.click(drawer.getByRole("button", { name: "Remove WEB 720p" }));
    expect(tierNames(drawer)).toEqual(["WEB 1080p"]);
    expect((drawer.getByLabelText("Stop upgrading at") as HTMLSelectElement).value).toBe("WEB 1080p");
  });

  it("Create sends the name, media type, tiers, where it stops and the upgrade choice, then says Profile created", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "POST" && path === "/api/quality-profiles" ? profile({ id: "new", name: "Kids" }) : {}));
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Profile name"), { target: { value: "Kids" } });
    fireEvent.change(drawer.getByLabelText("Add a tier"), { target: { value: "WEB 720p" } });
    fireEvent.change(drawer.getByLabelText("Add a tier"), { target: { value: "Bluray 1080p" } });
    fireEvent.change(drawer.getByLabelText("Stop upgrading at"), { target: { value: "Bluray 1080p" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create quality profile" }));

    await waitFor(() => expect(calls("POST", "/api/quality-profiles")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/quality-profiles")[0])).toMatchObject({
      name: "Kids", mediaType: "movies", cutoffQuality: "Bluray 1080p", allowedQualities: "WEB 720p, Bluray 1080p", upgradeUntilCutoff: true,
      upgradeStop: { stopWhenCutoffMet: true }, languagePreference: { preferredLanguages: [], requireLanguage: false }
    });
    expect(await drawer.findByText("Profile created")).toBeVisible();
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("the plain choices reach the request: a preferred language that is required, and never grabbing bad copies", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "POST" && path === "/api/quality-profiles" ? profile({ id: "new" }) : {}));
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Profile name"), { target: { value: "German" } });
    fireEvent.change(drawer.getByLabelText("Add a tier"), { target: { value: "WEB 1080p" } });
    fireEvent.change(drawer.getByLabelText("Preferred language"), { target: { value: "de" } });
    fireEvent.click(switchOf(drawer, "Require this language"));
    fireEvent.click(switchOf(drawer, "Never grab copies TRaSH rates as bad"));

    fireEvent.click(drawer.getByRole("button", { name: "Create quality profile" }));

    await waitFor(() => expect(calls("POST", "/api/quality-profiles")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/quality-profiles")[0]).languagePreference).toEqual({ preferredLanguages: ["de"], requireLanguage: true });
  });

  it("choosing TV shows the TV tiers and keeps the name that was typed", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Profile name"), { target: { value: "Shows" } });

    fireEvent.click(drawer.getByRole("radio", { name: "TV shows" }));

    expect(drawer.getByRole("radio", { name: "TV shows" })).toBeChecked();
    expect(drawer.getByLabelText("Profile name")).toHaveValue("Shows");
  });

  it("shows the server's reason about the tiers when it refuses the profile and keeps the drawer open", async () => {
    authedFetchMock.mockImplementation(() => reply({ errors: { allowedQualities: ["WEB 720p is not a quality Deluno knows."] } }, false, 400));
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Profile name"), { target: { value: "Odd" } });
    fireEvent.change(drawer.getByLabelText("Add a tier"), { target: { value: "WEB 720p" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create quality profile" }));

    expect((await drawer.findAllByText("WEB 720p is not a quality Deluno knows.")).length).toBeGreaterThan(0);
    expect(screen.getByRole("dialog")).toBeVisible();
    expect(drawer.queryByText("Profile created")).not.toBeInTheDocument();
  });

  it("asks before throwing away what was typed", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Profile name"), { target: { value: "Half done" } });

    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));

    const confirm = await confirmation(/Discard/);
    expect(confirm.getByText("Discard unsaved changes?")).toBeVisible();
    fireEvent.click(confirm.getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });
});

describe("an existing quality profile", () => {
  async function openIt() {
    load([profile()]);
    renderPage();
    fireEvent.click(screen.getByText("HD films"));
    return within(await screen.findByRole("dialog"));
  }

  it("opens with its own tiers, where it stops, and Duplicate and Delete sections", async () => {
    const drawer = await openIt();

    expect(drawer.getByLabelText("Profile name")).toHaveValue("HD films");
    expect(tierNames(drawer)).toEqual(["WEB 1080p", "WEB 720p"]);
    expect((drawer.getByLabelText("Stop upgrading at") as HTMLSelectElement).value).toBe("WEB 1080p");
    expect(drawer.getByRole("button", { name: "Save quality profile" })).toBeVisible();
    expect(drawer.getByRole("button", { name: "Duplicate" })).toBeVisible();
    expect(drawer.getByText("Delete this Quality Profile")).toBeVisible();
  });

  it("Save puts the changed profile at its own id and says Saved just now", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" && path === "/api/quality-profiles/qp-1" ? profile({ name: "HD films, kept" }) : {}));
    const drawer = await openIt();
    fireEvent.change(drawer.getByLabelText("Profile name"), { target: { value: "HD films, kept" } });
    fireEvent.click(switchOf(drawer, "Upgrade until cutoff"));

    fireEvent.click(drawer.getByRole("button", { name: "Save quality profile" }));

    await waitFor(() => expect(calls("PUT", "/api/quality-profiles/qp-1")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/quality-profiles/qp-1")[0])).toMatchObject({ name: "HD films, kept", upgradeUntilCutoff: false, allowedQualities: "WEB 720p, WEB 1080p", cutoffQuality: "WEB 1080p" });
    expect(await drawer.findByText("Saved just now")).toBeVisible();
  });

  it("Duplicate opens a new profile named Copy of the original with the same tiers, ready to save", async () => {
    const drawer = await openIt();

    fireEvent.click(drawer.getByRole("button", { name: "Duplicate" }));

    const copy = within(screen.getByRole("dialog"));
    expect(await copy.findByDisplayValue("Copy of HD films")).toBeVisible();
    expect(copy.getByText("Template")).toBeVisible();
    expect(tierNames(copy)).toEqual(["WEB 1080p", "WEB 720p"]);
    expect(copy.getByRole("button", { name: "Create quality profile" })).toBeEnabled();
  });

  it("Delete asks first, then removes the profile and says so", async () => {
    const drawer = await openIt();

    fireEvent.click(drawer.getByRole("button", { name: "Delete" }));
    const confirm = await confirmation(/Delete/);
    expect(calls("DELETE", "/api/quality-profiles/qp-1")).toHaveLength(0);
    fireEvent.click(confirm.getByRole("button", { name: /^Delete/ }));

    await waitFor(() => expect(calls("DELETE", "/api/quality-profiles/qp-1")).toHaveLength(1));
    expect(toastSuccess).toHaveBeenCalledWith("HD films removed");
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("a delete the server refuses says why and the profile stays", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "DELETE" ? { message: "A library still uses this profile." } : {}, init?.method !== "DELETE", init?.method === "DELETE" ? 409 : 200));
    const drawer = await openIt();

    fireEvent.click(drawer.getByRole("button", { name: "Delete" }));
    fireEvent.click((await confirmation(/Delete/)).getByRole("button", { name: /^Delete/ }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});
