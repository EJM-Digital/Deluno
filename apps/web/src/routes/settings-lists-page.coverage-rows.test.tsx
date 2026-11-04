import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { IntakeListPreviewResult, IntakeSourceItem, LibraryItem, MediaExclusionItem, TraktConnectionStatus } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
/**
 * Settings > Import lists, coverage rows (#1329): the Trakt failure paths, the list drawer and the preview.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", () => ({ toast: { success: (...a: unknown[]) => toastSuccess(...a), error: (...a: unknown[]) => toastError(...a) } }));
let liveRefresh: { events: string[] } | undefined;
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: (spec: { events: string[] }) => { liveRefresh = spec; } }));
const prefs = vi.hoisted(() => ({ relative: true }));
vi.mock("../lib/display-preferences", async (importOriginal) => {
  const original = await importOriginal<typeof import("../lib/display-preferences")>();
  return {
    ...original,
    useDisplayPreferences: () => ({ preferences: { ...original.DEFAULT_DISPLAY_PREFERENCES, showRelativeDates: prefs.relative }, replaceFromSettings: () => undefined, certificationCountry: "US" })
  };
});

const library = (id: string, name: string, mediaType: string) => ({ id, name, mediaType }) as unknown as LibraryItem;
const list = (over: Partial<IntakeSourceItem>): IntakeSourceItem => ({
  id: "l1", name: "Weekend list", provider: "rss", feedUrl: "https://lists.example.test/a.xml", mediaType: "movies", libraryId: "lib-m", libraryName: "Films",
  qualityProfileId: null, qualityProfileName: null, requiredGenres: "", minimumRating: null, minimumYear: null, maximumAgeDays: null, allowedCertifications: "",
  audience: "any", syncIntervalHours: 12, lastSyncUtc: null, lastSyncStatus: "never", lastSyncSummary: null, searchOnAdd: false, isEnabled: true,
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", tags: "", ...over
});
const exclusion = (id: string, title: string, over: Partial<MediaExclusionItem> = {}): MediaExclusionItem => ({
  id, mediaType: "movies", sourceKind: "list", sourceId: "l1", sourceName: "Weekend list", provider: "rss", entryKey: `k-${id}`, title, year: 2016, imdbId: null,
  reason: "Not for us", expiresUtc: null, createdUtc: "2026-10-02T00:00:00Z", updatedUtc: "2026-10-02T00:00:00Z", ...over
});

const loader = {
  intakeSources: [] as IntakeSourceItem[],
  libraries: [library("lib-m", "Films", "movies"), library("lib-t", "Shows", "tv")],
  exclusions: [] as MediaExclusionItem[],
  traktStatus: { clientConfigured: false, connected: false, expiresUtc: null, needsReconnect: false } as TraktConnectionStatus,
  settings: { tagTitlesOffEveryList: false, offListTag: "" } as Record<string, unknown>
};
const revalidate = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loader,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsListsPage } = await import("./settings-lists-page");
const renderPage = () => render(<MemoryRouter><SettingsListsPage /></MemoryRouter>);
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const calls = (method: string) => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === method);
const bodyOf = (call: unknown[]) => JSON.parse((call[1] as RequestInit).body as string);

beforeEach(() => {
  loader.intakeSources = [];
  loader.exclusions = [];
  loader.traktStatus = { clientConfigured: false, connected: false, expiresUtc: null, needsReconnect: false };
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : json({}));
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidate.mockReset();
  localStorage.clear();
  prefs.relative = true;
});

describe("the Trakt card when a call fails", () => {
  const configured = { clientConfigured: true, connected: false, expiresUtc: null, needsReconnect: false };
  const emptyFail = () => new Response(null, { status: 500 });

  it("saving the app credentials says the server's fallback words when it refuses with no message, and a thrown non-error", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : emptyFail());
    renderPage();
    fireEvent.change(screen.getByLabelText("Client ID"), { target: { value: "c" } });
    fireEvent.change(screen.getByLabelText("Client Secret"), { target: { value: "s" } });
    fireEvent.click(screen.getByRole("button", { name: "Save Trakt app credentials" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not save the Trakt app credentials."));
    toastError.mockReset();
    vi.mocked(authedFetch).mockImplementation(async () => { throw "offline"; });
    fireEvent.click(screen.getByRole("button", { name: "Save Trakt app credentials" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not save the Trakt app credentials."));
  });

  it("starting to connect says why it could not, whether the server refused or the call threw", async () => {
    loader.traktStatus = configured;
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : emptyFail());
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Connect Trakt" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not start connecting Trakt."));
    toastError.mockReset();
    vi.mocked(authedFetch).mockImplementation(async () => { throw "offline"; });
    fireEvent.click(screen.getByRole("button", { name: "Connect Trakt" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not start connecting Trakt."));
  });

  it("checking the approval says a denied code was declined, and says why a failed check failed", async () => {
    loader.traktStatus = configured;
    const device = { deviceCode: "dev", userCode: "ABCD-1234", verificationUrl: "https://trakt.tv/activate" };
    const reply = (poll: () => Response | Promise<Response>) => vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith("/api/tags")) return json([]);
      if (url === "/api/trakt/connect") return json(device);
      return url === "/api/trakt/connect/poll" ? poll() : json({});
    });
    reply(() => json({ status: "denied" }));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Connect Trakt" }));
    fireEvent.click(await screen.findByRole("button", { name: /approved it/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Trakt declined the request — start again."));
    expect(screen.queryByText("ABCD-1234")).toBeNull();

    reply(() => emptyFail());
    fireEvent.click(screen.getByRole("button", { name: "Connect Trakt" }));
    fireEvent.click(await screen.findByRole("button", { name: /approved it/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not check Trakt."));
    toastError.mockReset();
    reply(() => { throw "offline"; });
    fireEvent.click(screen.getByRole("button", { name: /approved it/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not check Trakt."));
  });

  it("disconnecting says why it could not, whether the server refused or the call threw", async () => {
    loader.traktStatus = { clientConfigured: true, connected: true, expiresUtc: "2027-01-01T00:00:00Z", needsReconnect: false };
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : emptyFail());
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not disconnect Trakt."));
    toastError.mockReset();
    vi.mocked(authedFetch).mockImplementation(async () => { throw "offline"; });
    fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not disconnect Trakt."));
  });
});

const reply = (handler: (url: string, init?: RequestInit) => Response | Promise<Response>) =>
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    return url.startsWith("/api/tags") ? json([]) : handler(url, init);
  });
const empty = (status = 500) => new Response(null, { status });
const open = (name = "Weekend list") => fireEvent.click(screen.getByText(name));
const touch = () => fireEvent.click(screen.getByRole("switch", { name: /Search when a title/ }));
const deferred = () => {
  let release: (value: Response) => void = () => undefined;
  const promise = new Promise<Response>((resolve) => { release = resolve; });
  return { promise, release };
};

describe("closing and saving the list drawer", () => {
  it("Cancel closes a clean drawer at once, and an edited one asks first: Discard closes it, dismissing the question keeps it", async () => {
    loader.intakeSources = [list({})];
    renderPage();
    open();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    open();
    touch();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByText("Discard unsaved changes?")).toBeInTheDocument();
    fireEvent.keyDown(screen.getByText("Discard unsaved changes?"), { key: "Escape" });
    await waitFor(() => expect(screen.queryByText("Discard unsaved changes?")).toBeNull());

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    fireEvent.click(await screen.findByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("a list the server refuses to add says so in the drawer, in its fallback words or a plain Could not save", async () => {
    reply(() => empty());
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New list" })[0]);
    fireEvent.change(screen.getByPlaceholderText(/themoviedb|https/i), { target: { value: "https://lists.example.test/b.xml" } });
    fireEvent.click(screen.getByRole("button", { name: "Add list" }));
    expect(await screen.findByText("Import list could not be added.")).toBeInTheDocument();
    reply(async () => { throw "offline"; });
    fireEvent.click(screen.getByRole("button", { name: "Add list" }));
    expect(await screen.findByText("Could not save")).toBeInTheDocument();
  });

  it("a second press on Save while the first is still working sends nothing more", async () => {
    loader.intakeSources = [list({})];
    const slow = deferred();
    reply(() => slow.promise);
    renderPage();
    open();
    touch();
    const form = screen.getByRole("button", { name: "Save list" }).closest("form")!;
    fireEvent.submit(form);
    fireEvent.submit(form);
    expect(calls("PUT")).toHaveLength(1);
    slow.release(json(list({ name: "Renamed" })));
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it("a save whose reply has no list in it keeps what was sent", async () => {
    loader.intakeSources = [list({})];
    reply(() => new Response("", { status: 200 }));
    renderPage();
    open();
    touch();
    fireEvent.click(screen.getByRole("button", { name: "Save list" }));
    expect(await screen.findByText("Saved just now")).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: /Search when a title/ })).toHaveAttribute("aria-checked", "true");
  });
});

describe("removing, pausing, syncing and previewing when the server refuses", () => {
  it("a refused removal says why and leaves the drawer open; a thrown one says the action failed", async () => {
    loader.intakeSources = [list({})];
    reply(() => empty());
    renderPage();
    open();
    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove list" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Import list could not be removed."));
    expect(screen.getByRole("dialog", { name: "Weekend list" })).toBeInTheDocument();
    reply(async () => { throw "offline"; });
    fireEvent.click(screen.getByRole("button", { name: "Remove list" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Action failed."));
  });

  it("a list that vanished while the question was open is still reported as removed under a plain name", async () => {
    loader.intakeSources = [list({})];
    const view = renderPage();
    open();
    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));
    await screen.findByRole("button", { name: "Remove list" });
    loader.intakeSources = [];
    view.rerender(<MemoryRouter><SettingsListsPage /></MemoryRouter>);
    fireEvent.click(screen.getByRole("button", { name: "Remove list" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("List removed"));
  });

  it("a refused pause says the fallback words, a thrown one says the list could not be updated, and the open drawer follows only its own untouched list", async () => {
    loader.intakeSources = [list({}), list({ id: "l2", name: "Other list" })];
    reply(() => empty());
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Other list" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not pause Other list."));
    reply(async () => { throw "offline"; });
    fireEvent.click(screen.getByRole("switch", { name: "Pause Other list" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Import list could not be updated."));

    reply(() => json({}));
    vi.mocked(authedFetch).mockClear();
    open();
    const row = (name: string) => screen.getByRole("switch", { name, hidden: true });
    fireEvent.click(row("Pause Other list"));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    fireEvent.click(row("Pause Weekend list"));
    await waitFor(() => expect(calls("PUT")).toHaveLength(2));
    touch();
    fireEvent.click(row("Pause Weekend list"));
    await waitFor(() => expect(calls("PUT")).toHaveLength(3));
  });

  it("a refused sync says why, and a refused preview says what it can", async () => {
    loader.intakeSources = [list({})];
    reply(() => empty());
    renderPage();
    open();
    fireEvent.click(screen.getByRole("button", { name: /Sync now/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Sync could not be queued."));
    fireEvent.click(screen.getByRole("button", { name: /Preview list/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Preview could not be loaded."));
  });
});

describe("the preview when an entry cannot be acted on", () => {
  const shown: IntakeListPreviewResult = {
    sourceId: "l1", sourceName: "Weekend list", provider: "rss", mediaType: "movies", targetLibraryName: null, fetchedCount: 2, shownCount: 2, isTruncated: false, warnings: [],
    items: [
      { title: "Arrival", year: null, mediaType: "movies", imdbId: "tt2543164", action: "would add", reason: "Would be added.", matchConfidence: "high" },
      { title: "Heat", year: 1995, mediaType: "movies", imdbId: null, action: "excluded", reason: "Excluded.", matchConfidence: "medium", exclusionId: "x1" }
    ]
  };
  const added = { selectedCount: 1, matchedCount: 1, addedCount: 1, duplicateCount: 0, skippedCount: 0, errorCount: 0, searchRequested: false };
  async function openShown(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
    loader.intakeSources = [list({})];
    reply((url, init) => (url.endsWith("/preview") ? json(shown) : handler(url, init)));
    renderPage();
    open();
    fireEvent.click(screen.getByRole("button", { name: /Preview list/ }));
    await screen.findByText("Read-only preview");
  }

  it("says no library was chosen, and a refused add says the server's words or its own", async () => {
    await openShown(() => json({ message: "The library is full." }, 400));
    expect(screen.getByText(/no library chosen/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add selected" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("The library is full."));
    reply((url) => (url.endsWith("/approve-preview") ? new Response("not json", { status: 500 }) : json(shown)));
    fireEvent.click(screen.getByRole("button", { name: "Add selected" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Selected entries could not be added."));
  });

  it("Exclude says the title will not be added again", async () => {
    await openShown(() => json({ id: "e1" }));
    fireEvent.click(screen.getByRole("button", { name: "Exclude" }));
    expect(await screen.findByText("Arrival will not be added from this list again.")).toBeInTheDocument();
  });

  it("a refused exclusion or restoration says why", async () => {
    await openShown(() => empty());
    fireEvent.click(screen.getByRole("button", { name: "Exclude" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("This entry could not be excluded."));
    fireEvent.click(screen.getByRole("button", { name: "Allow again" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("This entry could not be restored."));
  });

  it("an exclusion that finishes after the entries were added changes nothing on screen but the note", async () => {
    const slow = deferred();
    await openShown((url) => (url.endsWith("/approve-preview") ? json(added) : slow.promise));
    fireEvent.click(screen.getByRole("button", { name: "Exclude" }));
    fireEvent.click(screen.getByRole("button", { name: "Add selected" }));
    await waitFor(() => expect(screen.queryByText("Read-only preview")).toBeNull());
    slow.release(json({ id: "e1" }));
    expect(await screen.findByText("Arrival will not be added from this list again.")).toBeInTheDocument();
    expect(screen.queryByText("Read-only preview")).toBeNull();
  });

  it("a restoration that finishes after the entries were added changes nothing on screen but the note", async () => {
    const slow = deferred();
    await openShown((url) => (url.endsWith("/approve-preview") ? json(added) : slow.promise));
    fireEvent.click(screen.getByRole("button", { name: "Allow again" }));
    fireEvent.click(screen.getByRole("button", { name: "Add selected" }));
    await waitFor(() => expect(screen.queryByText("Read-only preview")).toBeNull());
    slow.release(new Response(null, { status: 204 }));
    expect(await screen.findByText("Heat is eligible for this list again.")).toBeInTheDocument();
    expect(screen.queryByText("Read-only preview")).toBeNull();
  });
});

const press = (label: string) => fireEvent.click(within(screen.getByRole("columnheader", { name: new RegExp(`^${label}`) })).getByRole("button"));
const order = (names: string[]) => screen.getAllByRole("row").map((row) => names.find((name) => (row.textContent ?? "").includes(name))).filter(Boolean);

describe("the lists table", () => {
  const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();
  const lists = () => [
    list({ id: "l1", name: "Alpha", feedUrl: "https://lists.example.test/b.xml", libraryName: "Films", lastSyncUtc: ago(300), lastSyncStatus: "success", isEnabled: true, searchOnAdd: true }),
    list({ id: "l2", name: "Bravo", feedUrl: "https://lists.example.test/a.xml", libraryName: null, lastSyncUtc: null, lastSyncStatus: "error", isEnabled: false })
  ];

  it("sorts by every column, and says how long ago in minutes, hours or days, or the exact time when relative dates are off", () => {
    loader.intakeSources = lists();
    renderPage();
    expect(order(["Alpha", "Bravo"])).toEqual(["Alpha", "Bravo"]);
    press("List");
    expect(order(["Alpha", "Bravo"])).toEqual(["Bravo", "Alpha"]);
    press("Adds to");
    expect(order(["Alpha", "Bravo"])).toEqual(["Bravo", "Alpha"]);
    press("Last sync");
    expect(order(["Alpha", "Bravo"])).toEqual(["Alpha", "Bravo"]);
    press("Status");
    expect(order(["Alpha", "Bravo"])).toHaveLength(2);
    press("On");
    expect(order(["Alpha", "Bravo"])).toEqual(["Bravo", "Alpha"]);
    press("Name");
    expect(order(["Alpha", "Bravo"])).toEqual(["Alpha", "Bravo"]);
    expect(screen.getByText("Adds and searches")).toBeInTheDocument();
    expect(screen.getByText("Adds only")).toBeInTheDocument();
    expect(screen.getAllByText("Off").length).toBeGreaterThan(0);
  });

  it.each([
    [0, "just now"],
    [20, "20 min ago"],
    [300, "5 h ago"],
    [60 * 72, "3 d ago"]
  ])("a list synced %i minutes ago reads %s", (minutes, words) => {
    loader.intakeSources = [list({ lastSyncUtc: ago(minutes), lastSyncStatus: "partial" })];
    renderPage();
    expect(screen.getByText(words)).toBeInTheDocument();
    expect(screen.getByText("Partial")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Weekend list"));
    expect(screen.getAllByText(new RegExp(`last sync ${words}`)).length).toBeGreaterThan(0);
  });

  it("shows the exact time of the last sync when relative dates are off", () => {
    prefs.relative = false;
    loader.intakeSources = [list({ lastSyncUtc: ago(300), lastSyncStatus: "success" })];
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    expect(screen.queryByText(/h ago/)).toBeNull();
    expect(screen.getAllByText(/last sync/).length).toBeGreaterThan(0);
  });
});

describe("the Never add decisions table", () => {
  const rows = () => [
    exclusion("x1", "Heat", { sourceName: "Weekend list", year: 1995, reason: "", expiresUtc: "2027-01-01T00:00:00Z", createdUtc: "2026-10-03T00:00:00Z" }),
    exclusion("x2", "Arrival", { sourceName: "", sourceId: "l1", sourceKind: "list", year: null, imdbId: "tt2543164", createdUtc: "2026-10-02T00:00:00Z" }),
    exclusion("x3", "Dune", { sourceName: "", sourceId: "gone", sourceKind: "collection", year: null, imdbId: null, createdUtc: "2026-10-01T00:00:00Z" }),
    exclusion("x4", "Alien", { sourceName: "", sourceId: "gone", sourceKind: "retention", year: null, imdbId: null, createdUtc: "2026-09-30T00:00:00Z" })
  ];

  it("names the source, the decision and its reason in plain words, and sorts by every column", () => {
    loader.intakeSources = [list({})];
    loader.exclusions = rows();
    renderPage();
    expect(screen.getByText("1995")).toBeInTheDocument();
    expect(screen.getByText("tt2543164")).toBeInTheDocument();
    expect(screen.getAllByText("No year or IMDb id")).toHaveLength(2);
    expect(screen.getByText("Excluded from import list by user")).toBeInTheDocument();
    expect(screen.getByText(/Ignored until/)).toBeInTheDocument();
    expect(screen.getByText("Collection")).toBeInTheDocument();
    expect(screen.getByText("Retention rule")).toBeInTheDocument();
    const names = ["Heat", "Arrival", "Dune", "Alien"];
    expect(order(names)).toEqual(["Heat", "Arrival", "Dune", "Alien"]);
    press("Entry");
    expect(order(names)).toEqual(["Alien", "Arrival", "Dune", "Heat"]);
    press("Source");
    expect(order(names)).toHaveLength(4);
    press("Decision");
    expect(order(names)).toHaveLength(4);
    press("Created");
    expect(order(names)).toEqual(["Alien", "Dune", "Arrival", "Heat"]);
  });

  it("allowing an entry again says it can be added again, and a refusal or a thrown call says why", async () => {
    loader.exclusions = [exclusion("x1", "Heat")];
    renderPage();
    reply(() => empty());
    fireEvent.click(screen.getByRole("button", { name: "Allow again" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("This exclusion could not be restored."));
    reply(async () => { throw "offline"; });
    fireEvent.click(screen.getByRole("button", { name: "Allow again" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(2));
    reply(() => new Response(null, { status: 204 }));
    fireEvent.click(screen.getByRole("button", { name: "Allow again" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Heat can be added again."));
  });
});

describe("the status of a list", () => {
  it.each([
    [{ lastSyncStatus: "error" }, "Failed"],
    [{ lastSyncStatus: "never" }, "Not synced"],
    [{ libraryId: null, libraryName: null }, "No library"]
  ])("%j reads %s", (over, words) => {
    loader.intakeSources = [list(over as Partial<IntakeSourceItem>)];
    renderPage();
    expect(screen.getAllByText(words)[0]).toBeInTheDocument();
  });
});

describe("what the list drawer says for each kind of list", () => {
  it.each([
    ["tmdb", "Paste a TMDb list URL or list ID."],
    ["tmdb-discover", "Enter one or more TMDb keyword IDs, comma-separated."],
    ["tmdb-collection", "Paste a TMDb collection URL or numeric collection ID. Every movie in the collection is added."],
    ["tmdb-company-credits", "Paste a TMDb company URL or numeric company ID."],
    ["mdblist", "For a public MDbList list, choose Custom list URL and paste https://mdblist.com/lists/owner/list-name."],
    ["letterboxd", "Paste a public Letterboxd list URL or its RSS feed."],
    ["stevenlu", "Paste a StevenLu-format JSON feed URL — a plain list of { title, imdb_id } entries, movies only."]
  ])("a %s list asks for its address in the right words", (provider, words) => {
    loader.intakeSources = [list({ provider })];
    renderPage();
    open();
    expect(screen.getByText(words)).toBeInTheDocument();
  });
});

describe("saving a new list with every field filled", () => {
  it("sends the filters as numbers and the chosen library, interval, audience and switches", async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New list" })[0]);
    fireEvent.change(screen.getByPlaceholderText(/themoviedb|https/i), { target: { value: " https://lists.example.test/c.xml " } });
    fireEvent.click(screen.getByRole("radio", { name: "TV shows" }));
    fireEvent.click(screen.getByRole("radio", { name: "Movies" }));
    fireEvent.change(screen.getByLabelText("Adds to"), { target: { value: "lib-m" } });
    fireEvent.click(screen.getByRole("button", { name: /Fine-tune/ }));
    fireEvent.change(screen.getByLabelText(/Required genres/), { target: { value: "Drama" } });
    fireEvent.change(screen.getByLabelText(/Allowed certifications/), { target: { value: "PG" } });
    fireEvent.change(screen.getByLabelText(/Minimum rating/), { target: { value: "7.5" } });
    fireEvent.change(screen.getByLabelText(/Minimum year/), { target: { value: "2015" } });
    fireEvent.change(screen.getByLabelText(/Maximum age/), { target: { value: "365" } });
    fireEvent.change(screen.getByLabelText("Audience"), { target: { value: "kids" } });
    fireEvent.click(screen.getByRole("switch", { name: /Enabled/ }));
    fireEvent.click(screen.getByRole("button", { name: "Add list" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(1));
    expect(bodyOf(calls("POST")[0])).toMatchObject({
      feedUrl: "https://lists.example.test/c.xml", mediaType: "movies", libraryId: "lib-m", requiredGenres: "Drama", allowedCertifications: "PG",
      minimumRating: 7.5, minimumYear: 2015, maximumAgeDays: 365, audience: "kids", isEnabled: false
    });
  });

  it("a cleared check interval goes back to every 24 hours, and a list with no library of its kind is sent with none", async () => {
    const keep = loader.libraries;
    loader.libraries = [];
    loader.intakeSources = [list({ syncIntervalHours: 99, libraryId: null, libraryName: null })];
    reply(() => json({}));
    renderPage();
    open();
    expect(screen.getByText(/No movie libraries yet/)).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText("Hours"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "Save list" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ syncIntervalHours: 24, libraryId: null });
    loader.libraries = keep;
  });
});

describe("opening a saved list", () => {
  it("a TV list with no monitor setting starts on all episodes, and a season-folder choice is sent as true, false or left on default", async () => {
    loader.intakeSources = [list({ mediaType: "tv", libraryId: "lib-t", libraryName: "Shows", monitorMode: undefined, seasonFolder: true })];
    reply(() => json({}));
    renderPage();
    open();
    expect(screen.getByLabelText("Monitor")).toHaveValue("all");
    touch();
    fireEvent.click(screen.getByRole("button", { name: "Save list" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ monitorMode: "all", seasonFolder: true });
    cleanup();
    vi.mocked(authedFetch).mockClear();

    loader.intakeSources = [list({ mediaType: "tv", libraryId: "lib-t", libraryName: "Shows", seasonFolder: false })];
    renderPage();
    open();
    touch();
    fireEvent.click(screen.getByRole("button", { name: "Save list" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ seasonFolder: false });
  });

  it.each([
    ["https://www.themoviedb.org/person/287?credits=cast,director", ["Cast", "Director"], "https://www.themoviedb.org/person/287?credits=cast,director"],
    ["287", ["Cast"], "https://www.themoviedb.org/person/287?credits=cast"],
    ["https://www.themoviedb.org/person/287?credits=bogus", ["Cast"], "https://www.themoviedb.org/person/287?credits=cast"],
    ["https://www.themoviedb.org/person/287", ["Cast"], "https://www.themoviedb.org/person/287?credits=cast"],
    ["not a person", ["Cast"], "not a person"],
    ["https://www.themoviedb.org/movie/287", ["Cast"], "https://www.themoviedb.org/movie/287"],
    ["https://www.themoviedb.org/person/abc", ["Cast"], "https://www.themoviedb.org/person/abc"],
    ["https://www.themoviedb.org/person", ["Cast"], "https://www.themoviedb.org/person"]
  ])("a followed person %j opens with %j ticked and is saved as %j", async (feedUrl, ticked, saved) => {
    loader.intakeSources = [list({ provider: "tmdb-person", feedUrl })];
    reply(() => json({}));
    renderPage();
    open();
    for (const label of ["Cast", "Director", "Producer", "Sound", "Writing"]) {
      const box = screen.getByRole("checkbox", { name: new RegExp(label) });
      if (ticked.includes(label)) expect(box).toBeChecked(); else expect(box).not.toBeChecked();
    }
    touch();
    fireEvent.click(screen.getByRole("button", { name: "Save list" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(bodyOf(calls("PUT")[0]).feedUrl).toBe(saved);
  });

  it("ticking and unticking credits changes what is followed", () => {
    loader.intakeSources = [list({ provider: "tmdb-person", feedUrl: "https://www.themoviedb.org/person/287" })];
    renderPage();
    open();
    fireEvent.click(screen.getByRole("checkbox", { name: /Director/ }));
    expect(screen.getByRole("checkbox", { name: /Director/ })).toBeChecked();
    expect(screen.getByText("2 selected")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("checkbox", { name: /Cast/ }));
    expect(screen.getByText("1 selected")).toBeInTheDocument();
  });
});

describe("a followed person with no address yet", () => {
  it("opens with Cast ticked and asks for the address when saved", async () => {
    loader.intakeSources = [list({ provider: "tmdb-person", feedUrl: "" })];
    renderPage();
    open();
    expect(screen.getByRole("checkbox", { name: /Cast/ })).toBeChecked();
    touch();
    fireEvent.click(screen.getByRole("button", { name: "Save list" }));
    expect(await screen.findByText("Paste the list URL or identifier.")).toBeInTheDocument();
  });
});

describe("the last small gaps on the Import lists page", () => {
  it("a refused resume says Could not enable, and Escape closes a clean drawer", async () => {
    loader.intakeSources = [list({ isEnabled: false })];
    reply(() => empty());
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Enable Weekend list" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not enable Weekend list."));
    open();
    fireEvent.keyDown(screen.getByRole("dialog", { name: "Weekend list" }), { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("allowing one entry again leaves the other entries of the preview as they were", async () => {
    loader.intakeSources = [list({})];
    const two: IntakeListPreviewResult = {
      sourceId: "l1", sourceName: "Weekend list", provider: "rss", mediaType: "movies", targetLibraryName: "Films", fetchedCount: 2, shownCount: 2, isTruncated: false, warnings: [],
      items: [
        { title: "Arrival", year: 2016, mediaType: "movies", imdbId: null, action: "would add", reason: "Would be added.", matchConfidence: "high" },
        { title: "Heat", year: 1995, mediaType: "movies", imdbId: null, action: "excluded", reason: "Excluded.", matchConfidence: "medium", exclusionId: "x1" }
      ]
    };
    reply((url) => (url.endsWith("/preview") ? json(two) : new Response(null, { status: 204 })));
    renderPage();
    open();
    fireEvent.click(screen.getByRole("button", { name: /Preview list/ }));
    await screen.findByText("Read-only preview");
    fireEvent.click(screen.getByRole("button", { name: "Allow again" }));
    expect(await screen.findByText("Heat is eligible for this list again.")).toBeInTheDocument();
    expect(screen.getAllByText("would add")).toHaveLength(2);
  });

  it("Reconnect Trakt starts the device flow again when the connection needs it", async () => {
    loader.traktStatus = { clientConfigured: true, connected: true, expiresUtc: null, needsReconnect: true };
    reply((url) => json(url === "/api/trakt/connect" ? { deviceCode: "dev", userCode: "WXYZ-9999", verificationUrl: "https://trakt.tv/activate" } : {}));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Reconnect Trakt" }));
    expect(await screen.findByText("WXYZ-9999")).toBeInTheDocument();
  });

  it("a page whose settings carry no list-tag fields shows the card off and empty, and a thrown save says it could not be saved", async () => {
    loader.settings = {};
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => json(String(input).startsWith("/api/tags") ? [{ id: "t1", name: "off-list", color: "slate", description: "" }] : {}));
    renderPage();
    expect(screen.getByText("Off")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("switch", { name: /Tag titles that are no longer on any list/ }));
    fireEvent.change(screen.getByLabelText("Tag for titles that left every list"), { target: { value: "off" } });
    fireEvent.click(await screen.findByRole("button", { name: "Add off-list to Tag for titles that left every list" }));
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      if (String(input).startsWith("/api/tags")) return json([{ id: "t1", name: "off-list", color: "slate", description: "" }]);
      throw "offline";
    });
    fireEvent.click(await screen.findByRole("button", { name: "Save list tag settings" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("This setting could not be saved."));
  });

  it("with the list-tag setting on, the card says On", () => {
    loader.settings = { tagTitlesOffEveryList: true, offListTag: "off-list" };
    renderPage();
    expect(screen.getByText("On")).toBeInTheDocument();
    loader.settings = { tagTitlesOffEveryList: false, offListTag: "" };
  });

  it("with no library of the other kind, switching Movies/TV leaves the library empty and says none exist", () => {
    const keep = loader.libraries;
    loader.libraries = keep.filter((library) => (library as { mediaType: string }).mediaType === "movies");
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New list" })[0]);
    fireEvent.click(screen.getByRole("radio", { name: "TV shows" }));
    expect(screen.getByLabelText("Adds to")).toHaveValue("");
    expect(screen.getByText(/No TV libraries yet/)).toBeInTheDocument();
    loader.libraries = keep;
  });
});
