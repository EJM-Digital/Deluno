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

const tvList = (over: Partial<IntakeSourceItem> = {}) => list({ mediaType: "tv", libraryId: "lib-t", libraryName: "Shows", monitorMode: "all", seriesType: "standard", seasonFolder: null, ...over } as Partial<IntakeSourceItem>);

describe("Import lists (drawer and preview rows)", () => {
  it("offers the five Monitor choices, three series types and Default/On/Off for the season folder on a TV list", () => {
    loader.intakeSources = [tvList()];
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    expect(screen.getByLabelText("Monitor")).toHaveValue("all");
    expect(Array.from((screen.getByLabelText("Monitor") as HTMLSelectElement).options).map((option) => option.value)).toEqual(["all", "future", "missing", "existing", "recent"]);
    expect(Array.from((screen.getByLabelText("Series type") as HTMLSelectElement).options).map((option) => option.value)).toEqual(["standard", "daily", "anime"]);
    expect(Array.from(screen.getByDisplayValue("Default").closest("select")!.options).map((option) => option.text)).toEqual(["Default", "On", "Off"]);
  });

  it("saves a TV list's Monitor choice, Series type and a Season folder of On, Off or Default as true, false or null", async () => {
    for (const [choice, sent, was] of [["true", true, null], ["false", false, null], ["", null, true]] as const) {
      cleanup();
      vi.mocked(authedFetch).mockClear();
      loader.intakeSources = [tvList({ seasonFolder: was } as Partial<IntakeSourceItem>)];
      renderPage();
      fireEvent.click(screen.getByText("Weekend list"));
      fireEvent.change(screen.getByLabelText("Monitor"), { target: { value: "recent" } });
      fireEvent.change(screen.getByLabelText("Series type"), { target: { value: "anime" } });
      const folder = (was === true ? screen.getByDisplayValue("On") : screen.getByDisplayValue("Default")).closest("select")!;
      fireEvent.change(folder, { target: { value: choice } });
      vi.mocked(authedFetch).mockResolvedValueOnce(json(list({})));
      fireEvent.click(screen.getByRole("button", { name: "Save list" }));
      await waitFor(() => expect(calls("PUT")).toHaveLength(1));
      expect(bodyOf(calls("PUT")[0])).toMatchObject({ monitorMode: "recent", seriesType: "anime", seasonFolder: sent });
    }
  });

  it("disables both Add buttons once nothing is ticked, and while an add is in flight", async () => {
    loader.intakeSources = [list({})];
    const preview: IntakeListPreviewResult = {
      itemCount: 1, wouldAddCount: 1, alreadyInLibraryCount: 0, unmatchedCount: 0, excludedCount: 0, targetLibraryName: "Films", warnings: [],
      items: [{ title: "Arrival", year: 2016, mediaType: "movies", imdbId: null, action: "would add", reason: "New.", matchConfidence: "high", exclusionId: null }]
    } as unknown as IntakeListPreviewResult;
    let release: (value: Response) => void = () => {};
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith("/api/tags")) return json([]);
      if (url.endsWith("/preview")) return json(preview);
      if (url.endsWith("/approve-preview")) return new Promise<Response>((resolve) => { release = resolve; });
      return json({});
    });
    renderPage();
    fireEvent.click(screen.getByText("Weekend list"));
    fireEvent.click(screen.getByRole("button", { name: /Preview list/ }));
    await screen.findByText("Read-only preview");
    const add = screen.getByRole("button", { name: "Add selected" });
    const addSearch = screen.getByRole("button", { name: "Add selected and search" });
    expect(add).toBeEnabled();
    expect(addSearch).toBeEnabled();

    const tick = screen.getAllByRole("checkbox").find((box) => (box as HTMLInputElement).checked)!;
    fireEvent.click(tick);
    expect(add).toBeDisabled();
    expect(addSearch).toBeDisabled();
    fireEvent.click(add);
    expect(calls("POST").filter(([url]) => String(url).endsWith("/approve-preview"))).toHaveLength(0);

    fireEvent.click(tick);
    expect(add).toBeEnabled();
    fireEvent.click(add);
    await waitFor(() => expect(add).toBeDisabled());
    expect(addSearch).toBeDisabled();
    release(json({ selectedCount: 1, matchedCount: 1, addedCount: 1, duplicateCount: 0, skippedCount: 0, errorCount: 0, searchRequested: false }));
    await waitFor(() => expect(screen.getByText(/1 title added from 1 approved preview entry\./)).toBeInTheDocument());
  });
});
