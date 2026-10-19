import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Library Profiles (policy sets): the list sentence, the switch, creating and attaching to libraries,
 * the scenario start, history, preview, delete and the library deep link. Every call goes through the
 * page's own fetch wrapper, replaced here with a recorder that answers each address.
 */

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
const authedFetchMock = vi.fn(async (path: string, init: { method?: string; body?: string } = {}) => {
  calls.push({ path, method: init.method ?? "GET", body: init.body ? JSON.parse(init.body) : undefined });
  const key = `${init.method ?? "GET"} ${path}`;
  const reply = replies[key] ?? Object.entries(replies).find(([pattern]) => pattern.endsWith("*") && key.startsWith(pattern.slice(0, -1)))?.[1];
  return reply ? reply(init) : json([]);
});
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => (authedFetchMock as (...a: unknown[]) => unknown)(...args) }));

const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsPolicySetsPage } = await import("./settings-policy-sets-page");

const profile = { id: "qp-1", name: "Films 4K", mediaType: "movies", allowedQualities: "WEB 2160p, WEB 1080p", cutoffQuality: "WEB 1080p", upgradeUntilCutoff: true };
const keepProfile = { ...profile, id: "qp-2", name: "Keep as is", upgradeUntilCutoff: false };
const tvProfile = { ...profile, id: "qp-3", name: "Shows", mediaType: "tv", presetId: "preset-tv" };
const movieProfile = { ...profile, presetId: "preset-films" };
const format = { id: "cf-1", name: "HDR10", mediaType: "movies", score: 50 };
const planA = {
  id: "plan-a", name: "Lounge", mediaType: "movies", qualityProfileId: "qp-1", qualityProfileName: "Films 4K", destinationRuleId: null, destinationRuleName: null,
  customFormatIds: "cf-1", searchIntervalOverrideHours: null, retryDelayOverrideHours: null, upgradeUntilCutoff: true, isEnabled: true, notes: "For the lounge TV",
  createdUtc: "2026-01-01T00:00:00Z", updatedUtc: "2026-01-01T00:00:00Z", automationIntent: null, releasePreferencePlan: null
};
const planB = { ...planA, id: "plan-b", name: "Archive", qualityProfileId: "qp-2", customFormatIds: "", notes: null, isEnabled: false };
const libMovies = { id: "lib-1", name: "Movies", mediaType: "movies", rootPath: "/media/movies", defaultPolicySetId: "plan-a" };
const libFilms2 = { id: "lib-2", name: "Kids films", mediaType: "movies", rootPath: "/media/kids", defaultPolicySetId: "plan-b" };
const libFree = { id: "lib-3", name: "Spare films", mediaType: "movies", rootPath: "/media/spare", defaultPolicySetId: null };
const scenario = {
  id: "family-night", name: "Family night", version: 3, mediaTypes: ["movies"], requirements: ["Needs a kids library."],
  variants: [{
    mediaType: "movies", qualityPresetId: "PRESET-FILMS", searchIntervalHours: 6, retryDelayHours: 12, upgradeUntilCutoff: false,
    sizeTierId: "small", sizeTierName: "Small", sizeDescription: "Up to 8 GB", subtitleIntent: "English", routingIntent: "Kids folder",
    sharingIntent: "", cleanupIntent: "", notificationIntent: "", namingIntent: "", summary: "Light and quick."
  }]
};

function reset() {
  calls.length = 0;
  replies = {};
  Object.assign(loaderData, {
    libraries: [libMovies, libFilms2, libFree], qualityProfiles: [movieProfile, keepProfile, tvProfile], customFormats: [format],
    destinationRules: [], policySets: [planA, planB], mediaPlanScenarios: [scenario]
  });
}
function renderPage(url = "/settings/library-profiles") {
  return render(<MemoryRouter initialEntries={[url]}><SettingsPolicySetsPage /></MemoryRouter>);
}
function sent(method: string, path: string) {
  return calls.filter((call) => call.method === method && call.path === path);
}

describe("Library Profiles list", () => {
  beforeEach(reset);
  afterEach(() => { vi.clearAllMocks(); });

  it("reads each row as name, media type and note plus one sentence about what the profile wants", () => {
    renderPage();
    expect(screen.getByText("Lounge")).toBeTruthy();
    expect(screen.getByText("Movies · For the lounge TV")).toBeTruthy();
    expect(screen.getByText("Up to WEB 2160p, and keeps looking until WEB 1080p")).toBeTruthy();
    expect(screen.getByText(/^Prefers .*\.$/)).toBeTruthy();
    expect(screen.getByText("Up to WEB 2160p, and keeps whatever it already has")).toBeTruthy();
    expect(screen.getByText("No release preferences beyond the quality it accepts.")).toBeTruthy();
    expect(screen.getByText("2 profiles · 1 enabled")).toBeTruthy();
  });

  it("shows Active for an attached profile, Off for a paused one, and names the libraries that use it", () => {
    renderPage();
    expect(screen.getByText("Active")).toBeTruthy();
    expect(screen.getByText("Off")).toBeTruthy();
    expect(screen.getByText("Kids films")).toBeTruthy();
    expect(screen.getAllByText("1 library").length).toBe(2);
  });

  it("pauses a profile with PUT carrying the whole profile and the new switch value, and refreshes", async () => {
    replies["PUT /api/policy-sets/plan-a"] = () => json({ ...planA, isEnabled: false });
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Lounge" }));

    await waitFor(() => expect(sent("PUT", "/api/policy-sets/plan-a")).toHaveLength(1));
    expect(sent("PUT", "/api/policy-sets/plan-a")[0].body).toMatchObject({
      name: "Lounge", mediaType: "movies", qualityProfileId: "qp-1", customFormatIds: "cf-1", isEnabled: false, notes: "For the lounge TV"
    });
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it("enables a paused profile from its row switch", async () => {
    replies["PUT /api/policy-sets/plan-b"] = () => json({ ...planB, isEnabled: true });
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Enable Archive" }));
    await waitFor(() => expect(sent("PUT", "/api/policy-sets/plan-b")[0]?.body).toMatchObject({ isEnabled: true }));
  });

  it("keeps the open drawer's switch in step when the same profile is switched from its row", async () => {
    replies["PUT /api/policy-sets/plan-a"] = () => json({ ...planA, isEnabled: false });
    renderPage();
    fireEvent.click(screen.getByText("Lounge"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("switch", { name: /^Enabled/ }).getAttribute("aria-checked")).toBe("true");
    fireEvent.click(screen.getByRole("switch", { name: "Pause Lounge", hidden: true }));
    await waitFor(() => expect(within(dialog).getByRole("switch", { name: /^Enabled/ }).getAttribute("aria-checked")).toBe("false"));
  });

  it("says why when the switch cannot be saved", async () => {
    replies["PUT /api/policy-sets/plan-a"] = () => json({ message: "Nope" }, 500);
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Lounge" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
  });
});

describe("Library Profile drawer", () => {
  beforeEach(reset);
  afterEach(() => { vi.clearAllMocks(); });

  it("refuses to create without a Quality Profile, saying so, and sends nothing", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New library profile" }));
    await screen.findByRole("dialog");
    fireEvent.change(screen.getByPlaceholderText("Name this library profile"), { target: { value: "Fresh" } });
    fireEvent.click(screen.getByRole("button", { name: "Create library profile" }));

    expect(await screen.findByText("Choose a Quality Profile first.")).toBeTruthy();
    expect(calls.filter((call) => call.method !== "GET")).toHaveLength(0);
  });

  it("refuses to create without a name", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New library profile" }));
    await screen.findByRole("dialog");
    fireEvent.click(screen.getByRole("button", { name: "Create library profile" }));
    expect(await screen.findByText("Give this library profile a name.")).toBeTruthy();
  });

  it("creates with POST, then attaches each switched-on library with PUT media-plan, and says it was created", async () => {
    replies["POST /api/policy-sets"] = () => json({ ...planA, id: "plan-new", name: "Fresh", qualityProfileId: "qp-1" });
    replies["PUT /api/libraries/*"] = () => json({});
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New library profile" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(screen.getByPlaceholderText("Name this library profile"), { target: { value: "Fresh" } });
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Quality Profile/ }), { target: { value: "qp-1" } });
    fireEvent.click(within(dialog).getByRole("switch", { name: /Spare films/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create library profile" }));

    expect(await screen.findByText("Library profile created")).toBeTruthy();
    const created = sent("POST", "/api/policy-sets");
    expect(created).toHaveLength(1);
    expect(created[0].body).toMatchObject({ name: "Fresh", mediaType: "movies", qualityProfileId: "qp-1" });
    expect(sent("PUT", "/api/libraries/lib-3/media-plan")[0].body).toEqual({ policySetId: "plan-new" });
    expect(calls.filter((call) => call.path.endsWith("/media-plan"))).toHaveLength(1);
    expect(screen.getByRole("button", { name: "Save library profile" })).toBeTruthy();
  });

  it("saves the profile but names the library it could not be attached to when that part fails", async () => {
    replies["POST /api/policy-sets"] = () => json({ ...planA, id: "plan-new", name: "Fresh" });
    replies["PUT /api/libraries/*"] = () => json({ message: "no" }, 500);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New library profile" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(screen.getByPlaceholderText("Name this library profile"), { target: { value: "Fresh" } });
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Quality Profile/ }), { target: { value: "qp-1" } });
    fireEvent.click(within(dialog).getByRole("switch", { name: /Spare films/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create library profile" }));

    expect(await screen.findByText("Profile saved, but not attached to Spare films")).toBeTruthy();
  });

  it("edits with PUT and detaches a library that was switched off", async () => {
    replies["PUT /api/policy-sets/plan-a"] = () => json(planA);
    replies["PUT /api/libraries/*"] = () => json({});
    renderPage();
    fireEvent.click(screen.getByText("Lounge"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("switch", { name: /^Movies/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save library profile" }));

    expect(await screen.findByText("Saved just now")).toBeTruthy();
    expect(sent("PUT", "/api/libraries/lib-1/media-plan")[0].body).toEqual({ policySetId: null });
  });

  it("lists one switch per library of the profile's media type, and says which other profile a library currently uses", async () => {
    renderPage();
    fireEvent.click(screen.getByText("Lounge"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("switch", { name: /^Movies/ }).getAttribute("aria-checked")).toBe("true");
    expect(within(dialog).getByRole("switch", { name: /Kids films/ }).getAttribute("aria-checked")).toBe("false");
    expect(within(dialog).getByText(/\/media\/kids · currently uses Archive/)).toBeTruthy();
    expect(within(dialog).getByRole("switch", { name: /Spare films/ })).toBeTruthy();
  });

  it("starts from a scenario: fills the name, the Quality Profile by its preset, the hours and the upgrade choice", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New library profile" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Start from a scenario/ }), { target: { value: "family-night" } });

    expect((screen.getByPlaceholderText("Name this library profile") as HTMLInputElement).value).toBe("Family night · Movies");
    expect((within(dialog).getByRole("combobox", { name: /Quality Profile/ }) as HTMLSelectElement).value).toBe("qp-1");
    expect(within(dialog).getByText("Captured automation intent")).toBeTruthy();
    expect(within(dialog).getByText("Light and quick.")).toBeTruthy();

    replies["POST /api/policy-sets"] = () => json({ ...planA, id: "plan-new" });
    fireEvent.click(screen.getByRole("button", { name: "Create library profile" }));
    await waitFor(() => expect(sent("POST", "/api/policy-sets")).toHaveLength(1));
    expect(sent("POST", "/api/policy-sets")[0].body).toMatchObject({ searchIntervalOverrideHours: 6, retryDelayOverrideHours: 12, upgradeUntilCutoff: false });
  });

  it("does not offer 'start from a scenario' when editing an existing profile", async () => {
    renderPage();
    fireEvent.click(screen.getByText("Lounge"));
    await screen.findByRole("dialog");
    expect(screen.queryByRole("combobox", { name: /Start from a scenario/ })).toBeNull();
  });

  it("picks Release Preferences from the media type's rules into a removable list, and disables the pick when there are none", async () => {
    renderPage();
    fireEvent.click(screen.getByText("Archive"));
    const dialog = await screen.findByRole("dialog");
    const pick = within(dialog).getByRole("combobox", { name: /Release Preferences/ });
    expect(Array.from((pick as HTMLSelectElement).options).map((option) => option.text)).toContain("HDR10");
    fireEvent.change(pick, { target: { value: "cf-1" } });
    const list = within(dialog).getByRole("list", { name: "Selected release preferences" });
    expect(within(list).getByText("HDR10")).toBeTruthy();
    fireEvent.click(within(list).getByRole("button", { name: "Remove" }));
    expect(within(dialog).queryByRole("list", { name: "Selected release preferences" })).toBeNull();

    fireEvent.click(within(dialog).getByRole("radio", { name: "TV shows" }));
    expect((within(dialog).getByRole("combobox", { name: /Release Preferences/ }) as HTMLSelectElement).disabled).toBe(true);
  });

  it("shows up to 8 immutable versions with change kind and hash prefix, marks the current one, and offers Restore on the older ones", async () => {
    const versions = Array.from({ length: 10 }, (_, index) => ({
      planId: "plan-a", version: 10 - index, changeKind: index === 9 ? "created" : "updated-fields", planHash: `abcdef0123456789${index}`, createdUtc: "2026-01-01T00:00:00Z"
    }));
    replies["GET /api/policy-sets/plan-a/versions"] = () => json(versions);
    renderPage();
    fireEvent.click(screen.getByText("Lounge"));
    const dialog = await screen.findByRole("dialog");

    expect(await within(dialog).findByText("10 immutable versions")).toBeTruthy();
    expect(within(dialog).getByText(/Version 10 · Updated Fields/)).toBeTruthy();
    expect(within(dialog).getByText("· current")).toBeTruthy();
    expect(within(dialog).getAllByText("abcdef012345", { selector: "p" })).toHaveLength(8);
    expect(within(dialog).queryByText(/Version 2 ·/)).toBeNull();
    expect(within(dialog).getAllByRole("button", { name: "Restore" })).toHaveLength(7);
  });

  it("restores an older version through a confirmation with POST rollback and says which version came back", async () => {
    const versions = [2, 1].map((version) => ({ planId: "plan-a", version, changeKind: "updated", planHash: `hash${version}000000000000`, createdUtc: "2026-01-01T00:00:00Z" }));
    replies["GET /api/policy-sets/plan-a/versions"] = () => json(versions);
    replies["POST /api/policy-sets/plan-a/rollback"] = () => json(planA);
    renderPage();
    fireEvent.click(screen.getByText("Lounge"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(await within(dialog).findByRole("button", { name: "Restore" }));
    fireEvent.click(await screen.findByRole("button", { name: "Restore version" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Restored media-plan version 1"));
    expect(sent("POST", "/api/policy-sets/plan-a/rollback")[0].body).toEqual({ version: 1 });
  });

  it("offers 'Preview unsaved changes' only once the form is changed, and reports how many changes the next version would carry", async () => {
    replies["POST /api/policy-sets/plan-a/preview"] = () => json({ hasChanges: true, changes: [{ field: "searchIntervalOverrideHours", currentValue: null, proposedValue: "4" }] });
    renderPage();
    fireEvent.click(screen.getByText("Lounge"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByRole("button", { name: "Preview unsaved changes" })).toBeNull();

    fireEvent.change(within(dialog).getByPlaceholderText("For example: 4K movies for the lounge TV"), { target: { value: "Changed note" } });
    fireEvent.click(await within(dialog).findByRole("button", { name: "Preview unsaved changes" }));

    expect(await within(dialog).findByText("1 change will create the next version.")).toBeTruthy();
    expect(within(dialog).getByText("Search Interval Override Hours: none → 4")).toBeTruthy();
    expect(sent("POST", "/api/policy-sets/plan-a/preview")[0].body).toMatchObject({ notes: "Changed note" });
  });

  it("deletes through the danger zone after a confirmation that counts the libraries using it", async () => {
    replies["DELETE /api/policy-sets/plan-a"] = () => new Response(null, { status: 204 });
    renderPage();
    fireEvent.click(screen.getByText("Lounge"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    expect(sent("DELETE", "/api/policy-sets/plan-a")).toHaveLength(0);
    expect(await screen.findByText(/1 library uses this Library Profile\. They will need another profile/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Delete Library Profile" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Library profile removed"));
    expect(sent("DELETE", "/api/policy-sets/plan-a")).toHaveLength(1);
  });
});

describe("Library Profiles opened from a library", () => {
  beforeEach(reset);
  afterEach(() => { vi.clearAllMocks(); });

  it("opens the library's current profile when it has one", async () => {
    renderPage("/settings/library-profiles?libraryId=lib-1&returnTo=library");
    const dialog = await screen.findByRole("dialog");
    expect((within(dialog).getByPlaceholderText("Name this library profile") as HTMLInputElement).value).toBe("Lounge");
    expect(within(dialog).getByRole("button", { name: "Save library profile" })).toBeTruthy();
  });

  it("opens a new profile with that library already switched on when it has none", async () => {
    renderPage("/settings/library-profiles?libraryId=lib-3&returnTo=library");
    const dialog = await screen.findByRole("dialog");
    expect((within(dialog).getByPlaceholderText("Name this library profile") as HTMLInputElement).value).toBe("");
    expect(within(dialog).getByRole("button", { name: "Create library profile" })).toBeTruthy();
    expect(within(dialog).getByRole("switch", { name: /Spare films/ }).getAttribute("aria-checked")).toBe("true");
  });
});
