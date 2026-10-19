import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * Library Profiles (Quality & Release): a reusable profile bundles a Quality Profile, Release Preferences and a destination rule,
 * and is attached to libraries. Each control sends the request the policy-set routes are tested against (PUT /api/libraries/{id}/media-plan
 * attaches it); a refusal is said in plain words and nothing is shown as saved.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("./settings-overview-page", () => ({ settingsOverviewLoader: vi.fn() }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn() })
  };
});

const revalidateMock = vi.fn();
let loaderData: Record<string, unknown>;
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsPolicySetsPage } = await import("./settings-policy-sets-page");

vi.setConfig({ testTimeout: 20_000 });

const plan = (overrides: Record<string, unknown> = {}) => ({
  id: "plan-1", name: "4K films", mediaType: "movies", qualityProfileId: "qp-1", qualityProfileName: "UHD", destinationRuleId: null, destinationRuleName: null,
  customFormatIds: "cf-1", searchIntervalOverrideHours: null, retryDelayOverrideHours: null, upgradeUntilCutoff: true, isEnabled: true, notes: "Lounge TV",
  automationIntent: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...overrides
});

const version = (n: number) => ({ planId: "plan-1", version: n, changeKind: n === 1 ? "created" : "updated", planHash: `hash${n}`.padEnd(40, "0"), createdUtc: "2026-10-01T00:00:00Z" });

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function load(plans: unknown[] = [], extra: Record<string, unknown> = {}) {
  loaderData = {
    libraries: [
      { id: "lib-1", name: "Films", mediaType: "movies", rootPath: "D:/Films", defaultPolicySetId: plans.length ? "plan-1" : null },
      { id: "lib-2", name: "Kids films", mediaType: "movies", rootPath: "D:/Kids", defaultPolicySetId: null },
      { id: "lib-3", name: "Shows", mediaType: "tv", rootPath: "D:/Shows", defaultPolicySetId: null }
    ],
    qualityProfiles: [
      { id: "qp-1", name: "UHD", mediaType: "movies", cutoffQuality: "WEBDL 2160p" }, { id: "qp-2", name: "HD", mediaType: "movies", cutoffQuality: "Bluray 1080p" },
      { id: "qp-3", name: "TV HD", mediaType: "tv", cutoffQuality: "WEBDL 1080p" }
    ],
    customFormats: [{ id: "cf-1", name: "Prefer HDR", mediaType: "movies" }, { id: "cf-2", name: "No CAM", mediaType: "movies" }, { id: "cf-3", name: "TV only", mediaType: "tv" }],
    destinationRules: [{ id: "d1", name: "Horror drive", mediaType: "movies" }, { id: "d2", name: "Anime drive", mediaType: "tv" }],
    policySets: plans,
    mediaPlanScenarios: [],
    settings: emptyPlatformSettingsSnapshot,
    ...extra
  };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsPolicySetsPage />
    </MemoryRouter>
  );
}

/** The confirmation that opens over the drawer is the last dialog on the page. */
async function topDialog() {
  await waitFor(() => expect(screen.getAllByRole("dialog").length).toBeGreaterThan(1));
  return within(screen.getAllByRole("dialog").at(-1)!);
}

function switchOf(scope: ReturnType<typeof within>, label: string) {
  const row = scope.getAllByText(label).map((element: HTMLElement) => element.closest("label")).find((candidate: HTMLLabelElement | null) => candidate !== null);
  const found = (row?.htmlFor ? document.getElementById(row.htmlFor) : row?.querySelector("[role=switch]")) as HTMLElement | null | undefined;
  if (!found) throw new Error(`No switch labelled ${label}`);
  return found;
}

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  authedFetchMock.mockImplementation((path: string) => reply(String(path).endsWith("/versions") ? [] : {}));
  load();
});

describe("the list of library profiles", () => {
  it("says a profile is optional, and offers a first one, when there are none", () => {
    renderPage();
    expect(screen.getByText("No library profiles yet")).toBeVisible();
    expect(screen.getAllByRole("button", { name: "New library profile" }).length).toBeGreaterThan(0);
  });

  it("lists a profile with its note, what it wants, the libraries using it and whether it is active", () => {
    load([plan()]);
    renderPage();

    const row = screen.getAllByRole("row")[1];
    expect(within(row).getByText("4K films")).toBeVisible();
    expect(within(row).getByText(/Movies · Lounge TV/)).toBeVisible();
    expect(within(row).getByText("Films")).toBeVisible();
    expect(within(row).getByText("Active")).toBeVisible();
    expect(screen.getByText("1 profile · 1 enabled")).toBeVisible();
  });

  it("a profile no library uses reads Unused, and a paused one reads Off", () => {
    load([plan({ id: "plan-2", name: "Spare", isEnabled: true }), plan({ id: "plan-3", name: "Paused", isEnabled: false })]);
    loaderData.libraries = [];
    renderPage();

    expect(within(screen.getByText("Spare").closest("[role=row]") as HTMLElement).getByText("Unused")).toBeVisible();
    expect(within(screen.getByText("Paused").closest("[role=row]") as HTMLElement).getByText("Off")).toBeVisible();
  });

  it("the switch pauses a profile and turns it on again, sending the profile with only that flag changed", async () => {
    load([plan()]);
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Pause 4K films" }));

    await waitFor(() => expect(calls("PUT", "/api/policy-sets/plan-1")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/policy-sets/plan-1")[0])).toMatchObject({ name: "4K films", isEnabled: false, qualityProfileId: "qp-1", customFormatIds: "cf-1", notes: "Lounge TV" });
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("a switch the server refuses says why", async () => {
    load([plan()]);
    authedFetchMock.mockImplementation(() => reply({ message: "Not allowed." }, false, 400));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Pause 4K films" }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
  });

  it("with more than three profiles the filter narrows them by name, quality, rule or note", () => {
    load([plan({ id: "a", name: "Alpha" }), plan({ id: "b", name: "Bravo" }), plan({ id: "c", name: "Charlie", notes: "kitchen" }), plan({ id: "d", name: "Delta" })]);
    renderPage();

    fireEvent.change(screen.getByPlaceholderText("Filter library profiles"), { target: { value: "kitchen" } });

    expect(screen.getByText("Charlie")).toBeVisible();
    expect(screen.queryByText("Alpha")).not.toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText("Filter library profiles"), { target: { value: "zzz" } });
    expect(screen.getByText("No profiles match")).toBeVisible();
  });
});

describe("a new library profile", () => {
  async function openNew() {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New library profile" })[0]);
    return within(await screen.findByRole("dialog"));
  }

  it("starts empty as a movie profile, switched on, with only movie libraries, quality profiles and release preferences to pick from", async () => {
    const drawer = await openNew();

    expect(drawer.getByText("New library profile", { selector: "h2, h3, [role=heading], div, span" })).toBeVisible();
    expect(drawer.getByPlaceholderText("Name this library profile")).toHaveValue("");
    expect(switchOf(drawer, "Enabled")).toHaveAttribute("aria-checked", "true");
    const quality = drawer.getByText("Choose a Quality Profile").closest("select") as HTMLSelectElement;
    expect(Array.from(quality.options).map((option) => option.textContent)).toEqual(["Choose a Quality Profile", "UHD", "HD"]);
    expect(drawer.getByText("Films")).toBeVisible();
    expect(drawer.getByText("Kids films")).toBeVisible();
    expect(drawer.queryByText("Shows")).not.toBeInTheDocument();
    expect(drawer.getByText("None selected")).toBeVisible();
  });

  it("asks for a name, then a Quality Profile, before it sends anything", async () => {
    const drawer = await openNew();

    fireEvent.click(drawer.getByRole("button", { name: "Create library profile" }));
    expect(await drawer.findByText("Give this library profile a name.")).toBeVisible();
    fireEvent.change(drawer.getByPlaceholderText("Name this library profile"), { target: { value: "Kids" } });
    fireEvent.click(drawer.getByRole("button", { name: "Create library profile" }));

    expect(await drawer.findByText("Choose a Quality Profile first.")).toBeVisible();
    expect(calls("POST", "/api/policy-sets")).toHaveLength(0);
  });

  it("Create posts the profile with its choices and attaches it to each library switched on", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "POST" && path === "/api/policy-sets" ? plan({ id: "new", name: "Kids" }) : String(path).endsWith("/versions") ? [] : {}));
    const drawer = await openNew();
    fireEvent.change(drawer.getByPlaceholderText("Name this library profile"), { target: { value: "Kids" } });
    fireEvent.change(drawer.getByPlaceholderText("For example: 4K movies for the lounge TV"), { target: { value: "For the children" } });
    fireEvent.change(drawer.getByText("Choose a Quality Profile").closest("select")!, { target: { value: "qp-2" } });
    fireEvent.change(drawer.getByText("Choose a Release Preference").closest("select")!, { target: { value: "cf-2" } });
    fireEvent.change(drawer.getByText("Use the library folder").closest("select")!, { target: { value: "d1" } });
    fireEvent.click(drawer.getByText("Kids films").closest("div[class*='border']")!.querySelector("[role=switch]")!);
    expect(drawer.getByText("1 library")).toBeVisible();

    fireEvent.click(drawer.getByRole("button", { name: "Create library profile" }));

    await waitFor(() => expect(calls("POST", "/api/policy-sets")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/policy-sets")[0])).toMatchObject({ name: "Kids", mediaType: "movies", qualityProfileId: "qp-2", destinationRuleId: "d1", customFormatIds: "cf-2", notes: "For the children", isEnabled: true });
    await waitFor(() => expect(calls("PUT", "/api/libraries/lib-2/media-plan")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/libraries/lib-2/media-plan")[0])).toMatchObject({ policySetId: "new" });
    expect(await drawer.findByText("Library profile created")).toBeVisible();
  });

  it("a selected release preference can be taken off again", async () => {
    const drawer = await openNew();

    fireEvent.change(drawer.getByText("Choose a Release Preference").closest("select")!, { target: { value: "cf-1" } });
    const list = within(drawer.getByRole("list", { name: "Selected release preferences" }));
    expect(list.getByText("Prefer HDR")).toBeVisible();
    fireEvent.click(list.getByRole("button", { name: "Remove" }));

    expect(drawer.queryByRole("list", { name: "Selected release preferences" })).not.toBeInTheDocument();
  });

  it("choosing TV changes what can be picked and clears the quality choice and the libraries", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByText("Choose a Quality Profile").closest("select")!, { target: { value: "qp-1" } });

    fireEvent.click(drawer.getByRole("radio", { name: "TV shows" }));

    const quality = drawer.getByText("Choose a Quality Profile").closest("select") as HTMLSelectElement;
    expect(Array.from(quality.options).map((option) => option.textContent)).toEqual(["Choose a Quality Profile", "TV HD"]);
    expect(quality.value).toBe("");
    expect(drawer.getByText("Shows")).toBeVisible();
    expect(drawer.queryByText("Kids films")).not.toBeInTheDocument();
  });

  it("says the profile was saved but not attached when a library refuses it", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => {
      if (init?.method === "POST" && path === "/api/policy-sets") return reply(plan({ id: "new", name: "Kids" }));
      if (String(path).endsWith("/media-plan")) return reply({ message: "No" }, false, 409);
      return reply(String(path).endsWith("/versions") ? [] : {});
    });
    const drawer = await openNew();
    fireEvent.change(drawer.getByPlaceholderText("Name this library profile"), { target: { value: "Kids" } });
    fireEvent.change(drawer.getByText("Choose a Quality Profile").closest("select")!, { target: { value: "qp-2" } });
    fireEvent.click(drawer.getByText("Kids films").closest("div[class*='border']")!.querySelector("[role=switch]")!);

    fireEvent.click(drawer.getByRole("button", { name: "Create library profile" }));

    expect(await drawer.findByText("Profile saved, but not attached to Kids films")).toBeVisible();
  });

  it("asks before throwing away what was typed", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByPlaceholderText("Name this library profile"), { target: { value: "Half done" } });

    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));

    const confirm = await topDialog();
    expect(confirm.getByText("Discard unsaved changes?")).toBeVisible();
    fireEvent.click(confirm.getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });
});

describe("an existing library profile", () => {
  async function openIt() {
    load([plan()]);
    authedFetchMock.mockImplementation((path: string) => reply(String(path).endsWith("/versions") ? [version(3), version(2), version(1)] : {}));
    renderPage();
    fireEvent.click(screen.getByText("4K films"));
    return within(await screen.findByRole("dialog"));
  }

  it("opens with its own choices, the library using it already on, its history and a Delete section", async () => {
    const drawer = await openIt();

    expect(drawer.getByPlaceholderText("Name this library profile")).toHaveValue("4K films");
    expect(drawer.getByText("Stops at WEBDL 2160p. Size Rules are applied automatically.")).toBeVisible();
    expect(within(drawer.getByRole("list", { name: "Selected release preferences" })).getByText("Prefer HDR")).toBeVisible();
    expect(drawer.getByRole("button", { name: "Save library profile" })).toBeVisible();
    expect(await drawer.findByText("Version 3 · Updated")).toBeVisible();
    expect(drawer.getByText("Delete this Library Profile")).toBeVisible();
  });

  it("Save puts the changed profile at its own id and says Saved just now", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" && path === "/api/policy-sets/plan-1" ? plan({ name: "4K films, kept" }) : String(path).endsWith("/versions") ? [] : {}));
    const drawer = await openIt();
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" && path === "/api/policy-sets/plan-1" ? plan({ name: "4K films, kept" }) : String(path).endsWith("/versions") ? [] : {}));
    fireEvent.change(drawer.getByPlaceholderText("Name this library profile"), { target: { value: "4K films, kept" } });

    fireEvent.click(drawer.getByRole("button", { name: "Save library profile" }));

    await waitFor(() => expect(calls("PUT", "/api/policy-sets/plan-1")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/policy-sets/plan-1")[0])).toMatchObject({ name: "4K films, kept", qualityProfileId: "qp-1" });
    expect(await drawer.findByText("Saved just now")).toBeVisible();
  });

  it("previews unsaved changes, saying how many changes the next version would hold", async () => {
    const drawer = await openIt();
    authedFetchMock.mockImplementation((path: string) => reply(String(path).endsWith("/preview") ? { hasChanges: true, changes: [{ field: "name", currentValue: "4K films", proposedValue: "Renamed" }] } : String(path).endsWith("/versions") ? [] : {}));
    fireEvent.change(drawer.getByPlaceholderText("Name this library profile"), { target: { value: "Renamed" } });

    fireEvent.click(drawer.getByRole("button", { name: "Preview unsaved changes" }));

    expect(await drawer.findByText("1 change will create the next version.")).toBeVisible();
    expect(drawer.getByText(/4K films → Renamed/)).toBeVisible();
    expect(calls("POST", "/api/policy-sets/plan-1/preview")).toHaveLength(1);
  });

  it("Restore asks first, then posts the version and says it was restored", async () => {
    const drawer = await openIt();
    await drawer.findByText("Version 3 · Updated");
    authedFetchMock.mockImplementation((path: string) => reply(String(path).endsWith("/rollback") ? plan({ name: "Older name" }) : String(path).endsWith("/versions") ? [version(4), version(3)] : {}));

    fireEvent.click(drawer.getAllByRole("button", { name: "Restore" })[0]);
    const confirm = await topDialog();
    expect(confirm.getByText("Restore version 2?")).toBeVisible();
    expect(calls("POST", "/api/policy-sets/plan-1/rollback")).toHaveLength(0);
    fireEvent.click(confirm.getByRole("button", { name: "Restore version" }));

    await waitFor(() => expect(calls("POST", "/api/policy-sets/plan-1/rollback")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/policy-sets/plan-1/rollback")[0])).toEqual({ version: 2 });
    expect(toastSuccess).toHaveBeenCalledWith("Restored media-plan version 2");
  });

  it("a restore that needs review says why", async () => {
    const drawer = await openIt();
    await drawer.findByText("Version 3 · Updated");
    authedFetchMock.mockImplementation((path: string) => reply(String(path).endsWith("/rollback") ? { message: "That quality profile is gone." } : [], !String(path).endsWith("/rollback"), String(path).endsWith("/rollback") ? 409 : 200));

    fireEvent.click(drawer.getAllByRole("button", { name: "Restore" })[0]);
    fireEvent.click((await topDialog()).getByRole("button", { name: "Restore version" }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Rollback needs review: That quality profile is gone."));
  });

  it("Delete asks first, then removes the profile and says so", async () => {
    const drawer = await openIt();

    fireEvent.click(drawer.getByRole("button", { name: "Delete" }));
    const confirm = await topDialog();
    expect(confirm.getByText("Delete “4K films”?")).toBeVisible();
    expect(calls("DELETE", "/api/policy-sets/plan-1")).toHaveLength(0);
    fireEvent.click(confirm.getByRole("button", { name: "Delete Library Profile" }));

    await waitFor(() => expect(calls("DELETE", "/api/policy-sets/plan-1")).toHaveLength(1));
    expect(toastSuccess).toHaveBeenCalledWith("Library profile removed");
  });

  it("taking a library off the profile detaches it when saved", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" && path === "/api/policy-sets/plan-1" ? plan() : String(path).endsWith("/versions") ? [] : {}));
    const drawer = await openIt();
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" && path === "/api/policy-sets/plan-1" ? plan() : String(path).endsWith("/versions") ? [] : {}));

    fireEvent.click(drawer.getByText("Films").closest("div[class*='border']")!.querySelector("[role=switch]")!);
    fireEvent.click(drawer.getByRole("button", { name: "Save library profile" }));

    await waitFor(() => expect(calls("PUT", "/api/libraries/lib-1/media-plan")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/libraries/lib-1/media-plan")[0])).toMatchObject({ policySetId: null });
  });
});

describe("a scenario", () => {
  const scenario = {
    id: "family-hd", name: "Family HD", version: 2, mediaTypes: ["movies"], requirements: ["Needs an HD quality profile."],
    variants: [{
      mediaType: "movies", qualityPresetId: "hd", searchIntervalHours: 6, retryDelayHours: 12, upgradeUntilCutoff: false, sizeTierId: "t2", sizeTierName: "Medium",
      sizeDescription: "About 8 GB", summary: "Good for the family.", subtitleIntent: "English", routingIntent: "Films library", sharingIntent: "Seed 7 days", cleanupIntent: "Keep 30 days",
      notificationIntent: "Weekly digest", namingIntent: "Standard"
    }]
  };

  it("fills the profile with its readable defaults and shows what it captured, still ready to review", async () => {
    load([], { mediaPlanScenarios: [scenario], qualityProfiles: [{ id: "qp-hd", name: "HD", mediaType: "movies", presetId: "hd", cutoffQuality: "Bluray 1080p" }] });
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New library profile" })[0]);
    const drawer = within(await screen.findByRole("dialog"));

    fireEvent.change(drawer.getByText("Build a custom plan").closest("select")!, { target: { value: "family-hd" } });

    expect(drawer.getByPlaceholderText("Name this library profile")).toHaveValue("Family HD · Movies");
    expect(drawer.getByText("Family HD · Medium size tier")).toBeVisible();
    expect(drawer.getByText("Captured automation intent")).toBeVisible();
    expect(drawer.getByText("family-hd · v2")).toBeVisible();
    expect((drawer.getByText("Build a custom plan").closest("select") as HTMLSelectElement).value).toBe("family-hd");
  });
});
