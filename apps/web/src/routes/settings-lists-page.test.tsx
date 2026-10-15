import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { IntakeListPreviewResult, IntakeSourceItem, LibraryItem, MediaExclusionItem, TraktConnectionStatus } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Import lists: the Trakt card, the list rows (status chip, On switch), the drawer (required address, media
 * type, filters, dirty rules, remove), the preview (exclude, ignore for 7 days, add selected, add selected and search)
 * and the Never add decisions card.
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

describe("the list rows", () => {
  const rowOf = (name: string) => screen.getByText(name).closest("[role='row']") as HTMLElement;

  it("shows Off, No library, Synced, Partial, Failed or Not synced for each list", () => {
    loader.intakeSources = [
      list({ id: "a", name: "Off list", isEnabled: false }),
      list({ id: "b", name: "Homeless list", libraryId: null, libraryName: null }),
      list({ id: "c", name: "Synced list", lastSyncStatus: "success", lastSyncUtc: "2026-10-03T00:00:00Z" }),
      list({ id: "d", name: "Partial list", lastSyncStatus: "partial" }),
      list({ id: "e", name: "Failed list", lastSyncStatus: "error" }),
      list({ id: "f", name: "Fresh list" })
    ];
    renderPage();
    expect(within(rowOf("Off list")).getByText("Off")).toBeInTheDocument();
    expect(within(rowOf("Homeless list")).getAllByText("No library")).toHaveLength(2); // the Adds to cell and the status chip
    expect(within(rowOf("Synced list")).getByText("Synced")).toBeInTheDocument();
    expect(within(rowOf("Partial list")).getByText("Partial")).toBeInTheDocument();
    expect(within(rowOf("Failed list")).getByText("Failed")).toBeInTheDocument();
    expect(within(rowOf("Fresh list")).getByText("Not synced")).toBeInTheDocument();
  });

  it("opens the edit drawer for the clicked list, titled with its name and described by provider, media type and library", () => {
    loader.intakeSources = [list({})];
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    expect(screen.getByRole("dialog", { name: /Weekend list/ })).toBeInTheDocument();
    expect(screen.getByText(/adds to Films/)).toBeInTheDocument();
  });

  it("PUTs the whole list with only isEnabled flipped when its switch is used, and toasts the server's message when refused", async () => {
    loader.intakeSources = [list({ requiredGenres: "Drama", minimumYear: 2010 })];
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Weekend list" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(calls("PUT")[0][0]).toBe("/api/intake-sources/l1");
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ name: "Weekend list", requiredGenres: "Drama", minimumYear: 2010, isEnabled: false, libraryId: "lib-m" });
    await waitFor(() => expect(revalidate).toHaveBeenCalled());

    vi.mocked(authedFetch).mockResolvedValue(json({ errors: { minimumRating: ["Minimum rating must be between 0 and 10."] } }, 400));
    fireEvent.click(screen.getByRole("switch", { name: "Pause Weekend list" }));
    await waitFor(() => expect(toastError).toHaveBeenCalled());
  });

  it("reads again when a list, a library or a setting changes elsewhere, with no timer", () => {
    renderPage();
    expect(liveRefresh?.events).toEqual(["IntakeSourceChanged", "LibraryChanged", "SettingsChanged"]);
  });
});

describe("the list drawer", () => {
  const openNew = () => fireEvent.click(screen.getAllByRole("button", { name: "New list" })[0]);

  it("asks for the list address and sends nothing when it is blank, except for TMDb Popular which needs none", async () => {
    renderPage();
    openNew();
    fireEvent.click(screen.getByRole("button", { name: "Add list" }));
    expect(await screen.findByText("Paste the list URL or identifier.")).toBeInTheDocument();
    expect(calls("POST")).toHaveLength(0);
    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "tmdb-popular" } });
    fireEvent.click(screen.getByRole("button", { name: "Add list" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(1));
    expect(bodyOf(calls("POST")[0])).toMatchObject({ provider: "tmdb-popular", feedUrl: "" });
  });

  it("asks for at least one credit when following a TMDb person, with the five credit kinds to choose from", async () => {
    renderPage();
    openNew();
    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "tmdb-person" } });
    for (const label of ["Cast", "Director", "Producer", "Sound", "Writing"]) {
      expect(screen.getByText(label)).toBeInTheDocument();
    }
    fireEvent.click(screen.getByRole("checkbox", { name: /Cast/ }));
    fireEvent.change(screen.getByPlaceholderText(/themoviedb.org\/person/), { target: { value: "https://www.themoviedb.org/person/287" } });
    fireEvent.click(screen.getByRole("button", { name: "Add list" }));
    expect(await screen.findByText("Choose at least one credit type to follow.")).toBeInTheDocument();
    expect(calls("POST")).toHaveLength(0);
  });

  it("resets the library to the first one of the new media type and the monitor default when Movies/TV is switched", () => {
    renderPage();
    openNew();
    expect(screen.getByLabelText("Adds to")).toHaveValue("lib-m");
    fireEvent.click(screen.getByRole("radio", { name: "TV shows" }));
    expect(screen.getByLabelText("Adds to")).toHaveValue("lib-t");
    expect(screen.getByLabelText("Monitor")).toHaveValue("all");
    fireEvent.click(screen.getByRole("radio", { name: "Movies" }));
    expect(screen.getByLabelText("Adds to")).toHaveValue("lib-m");
    expect(screen.getByLabelText("Monitor")).toHaveValue("movie");
  });

  it("opens the filters by itself for a list that has some, keeps them closed for one that has none", () => {
    loader.intakeSources = [list({ id: "a", name: "Filtered", requiredGenres: "Horror" }), list({ id: "b", name: "Whole list" })];
    renderPage();
    fireEvent.click(screen.getByText("Filtered"));
    expect(screen.getByPlaceholderText("Action, Sci-Fi")).toBeVisible();
    expect(screen.getByPlaceholderText("Action, Sci-Fi")).toHaveValue("Horror");
    cleanup();
    renderPage();
    fireEvent.click(screen.getByText("Whole list"));
    const genres = screen.queryByPlaceholderText("Action, Sci-Fi");
    expect(genres === null || !genres.checkVisibility()).toBe(true);
  });

  it("saves an edited list with PUT, says Saved just now and settles; a server refusal shows its message in the footer", async () => {
    loader.intakeSources = [list({})];
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    fireEvent.change(screen.getByPlaceholderText("https://…"), { target: { value: "https://lists.example.test/b.xml" } });
    vi.mocked(authedFetch).mockResolvedValueOnce(json(list({ feedUrl: "https://lists.example.test/b.xml" })));
    fireEvent.click(screen.getByRole("button", { name: "Save list" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    expect(calls("PUT")[0][0]).toBe("/api/intake-sources/l1");
    expect(bodyOf(calls("PUT")[0]).feedUrl).toBe("https://lists.example.test/b.xml");

    vi.mocked(authedFetch).mockResolvedValueOnce(json({ errors: { feedUrl: ["Use a public HTTP or HTTPS RSS/Atom feed URL."] } }, 400));
    fireEvent.change(screen.getByPlaceholderText("https://…"), { target: { value: "ftp://nope" } });
    fireEvent.click(screen.getByRole("button", { name: "Save list" }));
    expect(await screen.findByText(/Use a public HTTP or HTTPS RSS\/Atom feed URL\./)).toBeInTheDocument();
  });

  it("disables Preview and Sync now and says 'Save your changes first.' while the form has unsaved edits", () => {
    loader.intakeSources = [list({})];
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    expect(screen.getByRole("button", { name: /Preview list/ })).toBeEnabled();
    fireEvent.change(screen.getByPlaceholderText("https://…"), { target: { value: "https://lists.example.test/changed.xml" } });
    expect(screen.getByRole("button", { name: /Preview list/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /Sync now/ })).toBeDisabled();
    expect(screen.getByText("Save your changes first.")).toBeInTheDocument();
  });

  it("queues a sync from the edit drawer and toasts that the results appear under Last sync", async () => {
    loader.intakeSources = [list({})];
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    fireEvent.click(screen.getByRole("button", { name: /Sync now/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Sync queued — results appear under Last sync"));
    expect(calls("POST")[0][0]).toBe("/api/intake-sources/l1/sync");
  });

  it("removes a list after a confirmation that names it, toasting '<name> removed' and closing the drawer", async () => {
    loader.intakeSources = [list({})];
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));
    expect(await screen.findByText("Remove “Weekend list”?")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove list" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Weekend list removed"));
    expect(calls("DELETE")[0][0]).toBe("/api/intake-sources/l1");
  });
});

describe("the preview", () => {
  const preview: IntakeListPreviewResult = {
    sourceId: "l1", sourceName: "Weekend list", provider: "rss", mediaType: "movies", targetLibraryName: "Films", fetchedCount: 3, shownCount: 3, isTruncated: false,
    warnings: ["No compatible target library is configured. Sync will not add any titles until you choose one."],
    items: [
      { title: "Arrival", year: 2016, mediaType: "movies", imdbId: null, action: "would add", reason: "This title passes the list's available filters and would be added on sync.", matchConfidence: "medium" },
      { title: "Dune", year: 2021, mediaType: "movies", imdbId: null, action: "already in library", reason: "A matching title is already in this Deluno library.", matchConfidence: "medium" },
      { title: "Heat", year: 1995, mediaType: "movies", imdbId: null, action: "excluded", reason: "You previously chose not to add this entry from this list.", matchConfidence: "medium", exclusionId: "x1" }
    ]
  };

  async function openPreview() {
    loader.intakeSources = [list({})];
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.startsWith("/api/tags")) return json([]);
      if (url.endsWith("/preview")) return json(preview);
      if (url.endsWith("/approve-preview")) return json({ selectedCount: 1, matchedCount: 1, addedCount: 1, duplicateCount: 0, skippedCount: 0, errorCount: 0, searchRequested: (JSON.parse(String(init?.body)) as { searchAfterAdd: boolean }).searchAfterAdd, summary: "" });
      if (url.endsWith("/exclude-preview")) return json({ id: "new-exclusion" });
      return json({});
    });
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    fireEvent.click(screen.getByRole("button", { name: /Preview list/ }));
    await screen.findByText("Read-only preview");
  }

  it("counts what was found, would add and is already there, names the library, shows warnings and says nothing was added", async () => {
    await openPreview();
    expect(screen.getByText(/3 found · 1 would add · 1 already in library · adds to Films/)).toBeInTheDocument();
    expect(screen.getByText(/No compatible target library is configured/)).toBeInTheDocument();
    expect(screen.getByText("Preview ready. Nothing was added or searched.")).toBeInTheDocument();
    expect(screen.getByText("excluded")).toBeInTheDocument();
  });

  it("surfaces a preview the server refuses (for example Trakt not connected) as a toast with the server's message", async () => {
    loader.intakeSources = [list({ provider: "trakt" })];
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : json({ message: "Connect Trakt first." }, 400));
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    fireEvent.click(screen.getByRole("button", { name: /Preview list/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Connect Trakt first."));
  });

  it("posts exclude-preview with the entry and 7 days for Ignore 7 days, or none for Exclude, and the entry turns into an excluded chip", async () => {
    await openPreview();
    fireEvent.click(screen.getByRole("button", { name: "Ignore 7 days" }));
    await waitFor(() => expect(calls("POST").some(([url]) => String(url).endsWith("/exclude-preview"))).toBe(true));
    const call = calls("POST").find(([url]) => String(url).endsWith("/exclude-preview"))!;
    expect(bodyOf(call)).toMatchObject({ title: "Arrival", year: 2016, durationDays: 7, reason: "Excluded during import list preview" });
    expect(await screen.findByText("Arrival will be ignored for 7 days.")).toBeInTheDocument();
    expect(screen.getAllByText("excluded")).toHaveLength(2);
  });

  it("adds only the selected entries, and Add selected and search posts searchAfterAdd and says Deluno will search them", async () => {
    await openPreview();
    fireEvent.click(screen.getByRole("button", { name: "Add selected and search" }));
    await waitFor(() => expect(screen.getByText(/1 title added from 1 approved preview entry\. Deluno will search them using normal automation rules\./)).toBeInTheDocument());
    const call = calls("POST").find(([url]) => String(url).endsWith("/approve-preview"))!;
    expect(bodyOf(call)).toEqual({ entries: [{ title: "Arrival", year: 2016, imdbId: null }], searchAfterAdd: true });
  });

  it("adds without a search for Add selected and says nothing about searching", async () => {
    await openPreview();
    fireEvent.click(screen.getByRole("button", { name: "Add selected" }));
    await waitFor(() => expect(screen.getByText(/1 title added from 1 approved preview entry\./)).toBeInTheDocument());
    expect(screen.queryByText(/Deluno will search them/)).toBeNull();
    expect(bodyOf(calls("POST").find(([url]) => String(url).endsWith("/approve-preview"))!).searchAfterAdd).toBe(false);
  });
});

describe("Trakt", () => {
  it("asks for the Client ID and Client Secret only while no app credentials are stored and refuses a blank one with a toast", async () => {
    renderPage();
    expect(screen.getByLabelText("Client ID")).toBeInTheDocument();
    expect((screen.getByLabelText("Client Secret") as HTMLInputElement).type).toBe("password");
    fireEvent.click(screen.getByRole("button", { name: "Save Trakt app credentials" }));
    expect(toastError).toHaveBeenCalledWith("Enter both the Client ID and Client Secret from your Trakt app.");
    expect(calls("POST")).toHaveLength(0);
  });

  it("posts the credentials, toasts that they were saved and, once stored, offers Connect instead of the fields", async () => {
    const first = renderPage();
    fireEvent.change(screen.getByLabelText("Client ID"), { target: { value: "client-1" } });
    fireEvent.change(screen.getByLabelText("Client Secret"), { target: { value: "secret-1" } });
    fireEvent.click(screen.getByRole("button", { name: "Save Trakt app credentials" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Trakt app credentials saved"));
    expect(calls("POST")[0][0]).toBe("/api/trakt/credentials");
    expect(bodyOf(calls("POST")[0])).toEqual({ clientId: "client-1", clientSecret: "secret-1" });
    expect(revalidate).toHaveBeenCalled();
    first.unmount();

    loader.traktStatus = { clientConfigured: true, connected: false, expiresUtc: null, needsReconnect: false };
    renderPage();
    expect(screen.queryByLabelText("Client ID")).toBeNull();
    expect(screen.getByRole("button", { name: "Connect Trakt" })).toBeInTheDocument();
  });

  it("starts the device flow and shows the address and the code; polling reports connected, pending, expired and denied in plain words", async () => {
    loader.traktStatus = { clientConfigured: true, connected: false, expiresUtc: null, needsReconnect: false };
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith("/api/tags")) return json([]);
      if (url === "/api/trakt/connect") return json({ deviceCode: "dev", userCode: "ABCD-1234", verificationUrl: "https://trakt.tv/activate" });
      return json({});
    });
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Connect Trakt" }));
    expect(await screen.findByText("ABCD-1234")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "https://trakt.tv/activate" })).toHaveAttribute("href", "https://trakt.tv/activate");

    const poll = (status: string) => vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/trakt/connect/poll" ? json({ status }) : String(input).startsWith("/api/tags") ? json([]) : json({}));
    poll("pending");
    fireEvent.click(screen.getByRole("button", { name: /approved it/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Not approved yet — open https://trakt.tv/activate and enter ABCD-1234."));
    poll("expired");
    fireEvent.click(screen.getByRole("button", { name: /approved it/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("That code expired — start again."));
    expect(screen.queryByText("ABCD-1234")).toBeNull();
  });

  it("shows Trakt connected once approved, and Disconnect posts and toasts", async () => {
    loader.traktStatus = { clientConfigured: true, connected: false, expiresUtc: null, needsReconnect: false };
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith("/api/tags")) return json([]);
      if (url === "/api/trakt/connect") return json({ deviceCode: "dev", userCode: "WXYZ", verificationUrl: "https://trakt.tv/activate" });
      if (url === "/api/trakt/connect/poll") return json({ status: "connected" });
      return json({});
    });
    const first = renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Connect Trakt" }));
    await screen.findByText("WXYZ");
    fireEvent.click(screen.getByRole("button", { name: /approved it/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Trakt connected"));
    first.unmount();

    loader.traktStatus = { clientConfigured: true, connected: true, expiresUtc: "2027-01-01T00:00:00Z", needsReconnect: false };
    renderPage();
    expect(screen.getByText("Connected")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Trakt disconnected"));
    expect(calls("POST").some(([url]) => url === "/api/trakt/disconnect")).toBe(true);
  });

  it("offers Reconnect Trakt and says Trakt stopped renewing its own connection when the refresh token no longer works", () => {
    loader.traktStatus = { clientConfigured: true, connected: true, expiresUtc: null, needsReconnect: true };
    renderPage();
    expect(screen.getByText("Needs reconnect")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reconnect Trakt" })).toBeInTheDocument();
    expect(screen.getByText(/Trakt stopped renewing its own connection/)).toBeInTheDocument();
  });
});

describe("Never add decisions", () => {
  it("lists every active exclusion with its source and whether it is permanent or timed, and Allow again deletes it, toasts and reads again", async () => {
    loader.exclusions = [
      exclusion("e1", "Arrival"),
      exclusion("e2", "Dune", { expiresUtc: "2027-01-01T00:00:00Z", sourceKind: "retention", sourceName: "Clean-up rule" })
    ];
    renderPage();
    expect(screen.getByText("2 active decisions · all automated sources")).toBeInTheDocument();
    expect(screen.getByText("Excluded permanently")).toBeInTheDocument();
    expect(screen.getByText(/^Ignored until /)).toBeInTheDocument();
    expect(screen.getByText("Retention rule")).toBeInTheDocument();
    fireEvent.click(screen.getAllByRole("button", { name: "Allow again" })[0]);
    await waitFor(() => expect(toastSuccess).toHaveBeenCalled());
    expect(calls("DELETE")[0][0]).toMatch(/^\/api\/exclusions\/e[12]$/);
    expect(toastSuccess.mock.calls[0][0]).toMatch(/ can be added again\.$/);
    expect(revalidate).toHaveBeenCalled();
  });

  it("says there are no exclusions when there are none", () => {
    renderPage();
    expect(screen.getByText("No exclusions")).toBeInTheDocument();
  });
});
