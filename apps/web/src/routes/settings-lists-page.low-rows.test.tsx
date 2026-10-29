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

describe("Import lists (low rows)", () => {
  it.each([
    [{ clientConfigured: false, connected: false, expiresUtc: null, needsReconnect: false }, "Not connected"],
    [{ clientConfigured: true, connected: true, expiresUtc: "2027-01-01T00:00:00Z", needsReconnect: false }, "Connected"],
    [{ clientConfigured: true, connected: true, expiresUtc: null, needsReconnect: true }, "Needs reconnect"]
  ])("the Trakt card heading reads the connection %j as %s", (status, words) => {
    loader.traktStatus = status as TraktConnectionStatus;
    renderPage();

    expect(screen.getByText(words)).toBeInTheDocument();
    for (const other of ["Not connected", "Connected", "Needs reconnect"].filter((value) => value !== words)) expect(screen.queryByText(other)).toBeNull();
  });

  it("'Allow again' on an excluded preview entry deletes its exclusion and the entry returns to would add", async () => {
    loader.intakeSources = [list({})];
    const preview: IntakeListPreviewResult = {
      sourceId: "l1", sourceName: "Weekend list", provider: "rss", mediaType: "movies", targetLibraryName: "Films", fetchedCount: 1, shownCount: 1, isTruncated: false, warnings: [],
      items: [{ title: "Heat", year: 1995, mediaType: "movies", imdbId: null, action: "excluded", reason: "You previously chose not to add this entry from this list.", matchConfidence: "medium", exclusionId: "x1" }]
    };
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.startsWith("/api/tags")) return json([]);
      if (url.endsWith("/preview")) return json(preview);
      if (url.endsWith("/exclusions/x1") && init?.method === "DELETE") return new Response(null, { status: 204 });
      return json({});
    });
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    fireEvent.click(screen.getByRole("button", { name: /Preview list/ }));
    await screen.findByText("Read-only preview");

    fireEvent.click(within(screen.getByText("Read-only preview").closest("div")!.parentElement as HTMLElement).getByRole("button", { name: "Allow again" }));

    await waitFor(() => expect(calls("DELETE").map(([url]) => String(url))).toEqual(["/api/intake-sources/l1/exclusions/x1"]));
    expect(await screen.findByText(/Eligible again\. Choose it when you are ready to add it\./)).toBeInTheDocument();
    expect(screen.getByText("Heat is eligible for this list again.")).toBeInTheDocument();
    expect(screen.getByText("would add")).toBeInTheDocument();
  });

  it("the Off-list card toasts 'Saved' when the setting is saved and the server's sentence when it is refused", async () => {
    loader.settings = { tagTitlesOffEveryList: false, offListTag: "" };
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).startsWith("/api/tags")) return json([{ id: "t1", name: "off-list", color: "slate", description: "" }]);
      if (String(input) === "/api/settings" && init?.method === "PATCH") return refuse ? json({ message: "Settings are locked." }, 400) : json({});
      return json({});
    });
    let refuse = false;
    renderPage();
    const toggle = screen.getByRole("switch", { name: /Tag titles that are no longer on any list/ });

    fireEvent.click(toggle);
    const tag = screen.getByLabelText("Tag for titles that left every list");
    fireEvent.change(tag, { target: { value: "off" } });
    fireEvent.click(await screen.findByRole("button", { name: "Add off-list to Tag for titles that left every list" }));
    fireEvent.click(await screen.findByRole("button", { name: "Save list tag settings" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Saved"));

    toastSuccess.mockReset();
    refuse = true;
    fireEvent.click(screen.getByRole("button", { name: "Save list tag settings" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Settings are locked."));
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});
