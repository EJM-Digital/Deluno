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
