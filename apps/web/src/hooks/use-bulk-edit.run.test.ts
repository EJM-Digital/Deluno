import { act, renderHook } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { toast } from "../components/shell/toaster";
import { fetchJson } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import type { MediaItem } from "../lib/media-types";
import { useBulkEdit } from "./use-bulk-edit";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), loading: vi.fn(() => "t1") } }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../lib/api")>(),
  fetchJson: vi.fn().mockResolvedValue([])
}));

/**
 * The bulk workflow run end to end in the hook (coverage register, area library): "Review and continue" never changes anything,
 * "Confirm and run" sends the one request the operation names, updates the cards at once, toasts, closes the dialog and clears the
 * selection; a missing profile is refused with a sentence; and every change can be undone and done again.
 */
const item = (id: string, over: Partial<MediaItem> = {}): MediaItem => ({
  id, title: `Title ${id}`, year: 2020, type: "movie", poster: null, backdrop: null, quality: null, monitored: true, sizeGb: null, rating: null,
  genres: [], added: "1 Jan", overview: "", libraryId: "lib-a", minimumAvailability: "released", tags: [], ...over
});

function useHarness(initial: MediaItem[], variant: "movies" | "series" = "movies") {
  const [items, setItems] = useState<MediaItem[]>(initial);
  const bulk = useBulkEdit({ libraryItems: items, setLibraryItems: setItems, variant });
  return { items, bulk };
}
type Harness = { current: ReturnType<typeof useHarness> };

const answer = (body: unknown) => ({ ok: true, json: async () => body }) as Response;
const sent = () => vi.mocked(authedFetch).mock.calls.map(([url, init]) => ({
  url: String(url), method: (init as RequestInit | undefined)?.method ?? "GET", body: (init as RequestInit | undefined)?.body ? JSON.parse(String((init as RequestInit).body)) : null
}));

async function review(result: Harness) { await act(async () => { await result.current.bulk.executeBulkToolsOperation(); }); }

function open(result: Harness, ids: string[], operation: Parameters<Harness["current"]["bulk"]["openBulkTools"]>[0], monitored = true) {
  act(() => { result.current.bulk.setSelectedIds(ids); });
  act(() => { result.current.bulk.openBulkTools(operation, monitored); });
}

beforeEach(() => {
  vi.mocked(fetchJson).mockReset();
  vi.mocked(fetchJson).mockResolvedValue([] as never);
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockResolvedValue(answer({ updated: 1 }));
  for (const fn of [toast.success, toast.error, toast.info]) vi.mocked(fn).mockClear();
});

describe("Monitor and Unmonitor (bulk)", () => {
  it("Review and continue changes nothing, Confirm and run sends PUT /api/movies/monitoring, updates the cards at once, toasts, closes the dialog and clears the selection", async () => {
    const { result } = renderHook(() => useHarness([item("a", { monitored: false }), item("b", { monitored: false }), item("c", { monitored: false })]));
    open(result, ["a", "b"], "monitoring", true);
    expect(result.current.bulk.isBulkToolsOpen).toBe(true);
    expect(result.current.bulk.bulkMonitored).toBe(true);

    await review(result);
    expect(result.current.bulk.bulkConfirming).toBe(true);
    expect(authedFetch).not.toHaveBeenCalled();
    expect(result.current.items.map((entry) => entry.monitored)).toEqual([false, false, false]);

    await review(result);

    expect(sent()).toEqual([{ url: "/api/movies/monitoring", method: "PUT", body: { movieIds: ["a", "b"], monitored: true } }]);
    expect(result.current.items.map((entry) => entry.monitored)).toEqual([true, true, false]);
    expect(toast.success).toHaveBeenCalledWith("Monitor selected titles");
    expect(result.current.bulk.isBulkToolsOpen).toBe(false);
    expect(result.current.bulk.selectedIds).toEqual([]);
  });

  it("Unmonitor runs the same flow with monitored false on the shows route, and says Unmonitor selected titles", async () => {
    const { result } = renderHook(() => useHarness([item("s1", { type: "series" }), item("s2", { type: "series" })], "series"));
    open(result, ["s1", "s2"], "monitoring", false);
    expect(result.current.bulk.bulkMonitored).toBe(false);

    await review(result);
    await review(result);

    expect(sent()).toEqual([{ url: "/api/series/monitoring", method: "PUT", body: { seriesIds: ["s1", "s2"], monitored: false } }]);
    expect(result.current.items.map((entry) => entry.monitored)).toEqual([false, false]);
    expect(toast.success).toHaveBeenCalledWith("Unmonitor selected titles");
  });

  it("a refused change says why, keeps the dialog open and the cards as they were", async () => {
    const { result } = renderHook(() => useHarness([item("a")]));
    open(result, ["a"], "monitoring", false);
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, json: async () => ({}) } as Response);

    await review(result);
    await review(result);

    expect(result.current.bulk.bulkError).toBe("bulk-monitoring-failed");
    expect(toast.error).toHaveBeenCalledWith("Bulk operation failed", { description: "bulk-monitoring-failed" });
    expect(result.current.bulk.isBulkToolsOpen).toBe(true);
    expect(result.current.items[0].monitored).toBe(true);
    expect(result.current.bulk.isBulkUpdating).toBe(false);
  });
});

describe("Two-step confirm", () => {
  it("opening the dialog again starts unconfirmed, so a leftover confirmation can never run a different operation", async () => {
    const { result } = renderHook(() => useHarness([item("a")]));
    open(result, ["a"], "monitoring");
    await review(result);
    expect(result.current.bulk.bulkConfirming).toBe(true);

    act(() => { result.current.bulk.openBulkTools("quality"); });

    expect(result.current.bulk.bulkConfirming).toBe(false);
    expect(result.current.bulk.bulkOperation).toBe("quality");
    expect(authedFetch).not.toHaveBeenCalled();
  });

  it("with nothing selected the dialog does not open, and a run says to select a title", async () => {
    const { result } = renderHook(() => useHarness([item("a")]));

    act(() => { result.current.bulk.openBulkTools("monitoring"); });
    expect(result.current.bulk.isBulkToolsOpen).toBe(false);
    expect(toast.info).toHaveBeenCalledWith("Select at least one title first.");

    await review(result);
    expect(result.current.bulk.bulkError).toBe("Select at least one title.");
    expect(authedFetch).not.toHaveBeenCalled();
  });
});

describe("Set quality profile (bulk)", () => {
  it("without a profile chosen it says Choose a quality profile first. and sends nothing", async () => {
    const { result } = renderHook(() => useHarness([item("a")]));
    open(result, ["a"], "quality");

    await review(result);
    await review(result);

    expect(result.current.bulk.bulkError).toBe("Choose a quality profile first.");
    expect(sent().filter((call) => call.method === "POST")).toEqual([]);
    expect(result.current.bulk.isBulkToolsOpen).toBe(true);
  });

  it("posts the profile for the whole selection to /api/movies/bulk/quality-profile and the cards carry its name", async () => {
    vi.mocked(fetchJson).mockImplementation(async (url: string) =>
      (url === "/api/quality-profiles" ? [{ id: "p-4k", name: "Ultra 4K", mediaType: "movies" }] : []) as never);
    vi.mocked(authedFetch).mockImplementation(async (url) =>
      String(url).startsWith("/api/movies/bulk") ? answer({ updated: 2 }) : answer({ qualityProfileId: "p-hd" }));
    const { result } = renderHook(() => useHarness([item("a"), item("b")]));
    open(result, ["a", "b"], "quality");
    await act(async () => { await Promise.resolve(); });
    act(() => { result.current.bulk.setBulkQualityProfileId("p-4k"); });

    await review(result);
    await review(result);

    expect(sent().filter((call) => call.method === "POST")).toEqual([
      { url: "/api/movies/bulk/quality-profile", method: "POST", body: { movieIds: ["a", "b"], qualityProfileId: "p-4k" } }
    ]);
    expect(result.current.items.map((entry) => entry.qualityProfile)).toEqual(["Ultra 4K", "Ultra 4K"]);
    expect(toast.success).toHaveBeenCalledWith("Quality profile updated.");

    vi.mocked(authedFetch).mockClear();
    await act(async () => { await result.current.bulk.runUndo(); });
    expect(sent().map((call) => call.body)).toEqual([{ movieIds: ["a", "b"], qualityProfileId: "p-hd" }]);
    expect(toast.success).toHaveBeenCalledWith("Quality profile change reverted");
    vi.mocked(authedFetch).mockClear();
    await act(async () => { await result.current.bulk.runRedo(); });
    expect(sent().map((call) => call.body)).toEqual([{ movieIds: ["a", "b"], qualityProfileId: "p-4k" }]);
    expect(toast.success).toHaveBeenCalledWith("Quality profile change re-applied");
  });
});

describe("Apply tags (bulk)", () => {
  it("Add merges without duplicates, ignoring case, and Undo puts each title's own earlier tags back", async () => {
    const { result } = renderHook(() => useHarness([item("a", { tags: ["Kids"] }), item("b", { tags: [] })]));
    open(result, ["a", "b"], "tags");
    act(() => { result.current.bulk.setBulkTagsInput(" Kids, 4K ;4k\nholiday "); });

    await review(result);
    await review(result);

    expect(sent()).toEqual([{ url: "/api/movies/bulk/tags", method: "POST", body: { movieIds: ["a", "b"], tags: "Kids, 4K, holiday", mode: "add" } }]);
    expect(result.current.items.map((entry) => entry.tags)).toEqual([["Kids", "4K", "holiday"], ["Kids", "4K", "holiday"]]);
    expect(toast.success).toHaveBeenCalledWith("Tags updated.");

    vi.mocked(authedFetch).mockClear();
    await act(async () => { await result.current.bulk.runUndo(); });
    expect(sent().map((call) => call.body)).toEqual([
      { movieIds: ["a"], tags: "Kids", mode: "replace" },
      { movieIds: ["b"], tags: "", mode: "replace" }
    ]);
    expect(result.current.items.map((entry) => entry.tags)).toEqual([["Kids"], []]);
    expect(toast.success).toHaveBeenCalledWith("Tags reverted");
    vi.mocked(authedFetch).mockClear();
    await act(async () => { await result.current.bulk.runRedo(); });
    expect(sent().map((call) => call.body)).toEqual([{ movieIds: ["a", "b"], tags: "Kids, 4K, holiday", mode: "add" }]);
    expect(toast.success).toHaveBeenCalledWith("Tags re-applied");
  });

  it("Remove takes only the named tags (any case) and Replace sets exactly the typed ones, on the shows route", async () => {
    const { result } = renderHook(() => useHarness([item("s1", { type: "series", tags: ["Anime", "kids"] })], "series"));
    open(result, ["s1"], "tags");
    act(() => { result.current.bulk.setBulkTagsMode("remove"); result.current.bulk.setBulkTagsInput("KIDS"); });
    await review(result);
    await review(result);
    expect(sent()).toEqual([{ url: "/api/series/bulk/tags", method: "POST", body: { seriesIds: ["s1"], tags: "KIDS", mode: "remove" } }]);
    expect(result.current.items[0].tags).toEqual(["Anime"]);

    vi.mocked(authedFetch).mockClear();
    open(result, ["s1"], "tags");
    act(() => { result.current.bulk.setBulkTagsMode("replace"); result.current.bulk.setBulkTagsInput("Docs"); });
    await review(result);
    await review(result);
    expect(sent()).toEqual([{ url: "/api/series/bulk/tags", method: "POST", body: { seriesIds: ["s1"], tags: "Docs", mode: "replace" } }]);
    expect(result.current.items[0].tags).toEqual(["Docs"]);
  });
});

describe("Undo and Redo of the other bulk changes", () => {
  it("minimum availability is sent back to each title's own earlier choice and then applied again", async () => {
    const { result } = renderHook(() => useHarness([item("a", { minimumAvailability: "announced" }), item("b", { minimumAvailability: "released" })]));
    open(result, ["a", "b"], "minimumAvailability");
    act(() => { result.current.bulk.setBulkMinimumAvailability("inCinemas"); });
    await review(result);
    await review(result);
    expect(sent().map((call) => call.body)).toEqual([{ movieIds: ["a", "b"], minimumAvailability: "inCinemas" }]);
    expect(result.current.items.map((entry) => entry.minimumAvailability)).toEqual(["inCinemas", "inCinemas"]);

    vi.mocked(authedFetch).mockClear();
    await act(async () => { await result.current.bulk.runUndo(); });
    expect(sent().map((call) => call.body)).toEqual([
      { movieIds: ["a"], minimumAvailability: "announced" },
      { movieIds: ["b"], minimumAvailability: "released" }
    ]);
    expect(result.current.items.map((entry) => entry.minimumAvailability)).toEqual(["announced", "released"]);
    vi.mocked(authedFetch).mockClear();
    await act(async () => { await result.current.bulk.runRedo(); });
    expect(sent().map((call) => call.body)).toEqual([{ movieIds: ["a", "b"], minimumAvailability: "inCinemas" }]);
  });

  it("while another update is running neither Undo nor Redo sends anything", async () => {
    const { result } = renderHook(() => useHarness([item("a")]));
    open(result, ["a"], "monitoring", false);
    await review(result);
    await review(result);
    vi.mocked(authedFetch).mockClear();

    act(() => { result.current.bulk.setIsBulkUpdating(true); });
    await act(async () => { await result.current.bulk.runUndo(); });

    expect(authedFetch).not.toHaveBeenCalled();
    expect(result.current.bulk.undoStack).toHaveLength(1);
  });
});

describe("Search now (bulk dialog)", () => {
  it("posts the selected films to /api/movies/bulk/search and toasts Manual search dispatched.", async () => {
    const { result } = renderHook(() => useHarness([item("a"), item("b")]));
    open(result, ["a", "b"], "search");

    await review(result);
    await review(result);

    expect(sent()).toEqual([{ url: "/api/movies/bulk/search", method: "POST", body: { movieIds: ["a", "b"] } }]);
    expect(toast.success).toHaveBeenCalledWith("Manual search dispatched.");
    expect(result.current.bulk.selectedIds).toEqual([]);
  });

  it("posts the selected shows to /api/series/bulk/search", async () => {
    const { result } = renderHook(() => useHarness([item("s1", { type: "series" })], "series"));
    open(result, ["s1"], "search");
    await review(result);
    await review(result);
    expect(sent()).toEqual([{ url: "/api/series/bulk/search", method: "POST", body: { seriesIds: ["s1"] } }]);
  });

  it("a refused search is explained in words and the dialog stays open", async () => {
    const { result } = renderHook(() => useHarness([item("a")]));
    open(result, ["a"], "search");
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, status: 503, json: async () => ({}), headers: new Headers(), clone() { return this; }, text: async () => "" } as unknown as Response);

    await review(result);
    await review(result);

    expect(toast.error).toHaveBeenCalledWith("Bulk operation failed", expect.objectContaining({ description: expect.any(String) }));
    expect(result.current.bulk.isBulkToolsOpen).toBe(true);
    expect(result.current.bulk.bulkError).toBeTruthy();
  });
});

describe("Bulk dialog options", () => {
  it("lists only the libraries and profiles of this shelf's kind, and says so when they cannot be read", async () => {
    vi.mocked(fetchJson).mockImplementation(async (url: string) =>
      (url === "/api/libraries"
        ? [{ id: "l1", name: "Films", mediaType: "Movies" }, { id: "l2", name: "Shows", mediaType: "tv" }]
        : [{ id: "p1", name: "HD films", mediaType: "movies" }, { id: "p2", name: "HD shows", mediaType: "tv" }]) as never);
    const { result } = renderHook(() => useHarness([item("a")]));
    open(result, ["a"], "reassignLibrary");
    await act(async () => { await Promise.resolve(); });
    expect(result.current.bulk.bulkLibraries.map((library) => library.name)).toEqual(["Films"]);
    expect(result.current.bulk.bulkQualityProfiles.map((profile) => profile.name)).toEqual(["HD films"]);
    expect(result.current.bulk.bulkTargetLibraryId).toBe("l1");
    expect(result.current.bulk.bulkOptionsLoading).toBe(false);

    vi.mocked(fetchJson).mockRejectedValue(new Error("Could not load bulk operation options."));
    const second = renderHook(() => useHarness([item("a")]));
    open(second.result, ["a"], "quality");
    await act(async () => { await Promise.resolve(); });
    expect(second.result.current.bulk.bulkError).toBe("Could not load bulk operation options.");
    expect(second.result.current.bulk.bulkOptionsLoading).toBe(false);
  });
});

describe("a 5000-title selection", () => {
  const many = Array.from({ length: 5000 }, (_, index) => item(`m${index}`));

  it("is selected across the whole library by select-all, and monitor, quality and tags each go out as one request naming all 5000", async () => {
    vi.mocked(fetchJson).mockImplementation(async (url: string) =>
      (url === "/api/quality-profiles" ? [{ id: "p-4k", name: "Ultra 4K", mediaType: "movies" }] : []) as never);
    vi.mocked(authedFetch).mockImplementation(async (url) => (String(url).endsWith("/api/movies/m0") || /\/api\/movies\/m\d+$/.test(String(url))
      ? answer({ qualityProfileId: "p-hd" }) : answer({ updated: 5000 })));
    const { result } = renderHook(() => useHarness(many));

    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    expect(result.current.bulk.selectedIds).toHaveLength(5000);
    expect(result.current.bulk.selectAllActive).toBe(true);

    act(() => { result.current.bulk.openBulkTools("monitoring", false); });
    await review(result);
    await review(result);
    const monitoring = sent().filter((call) => call.url === "/api/movies/monitoring");
    expect(monitoring).toHaveLength(1);
    expect(monitoring[0].body.movieIds).toHaveLength(5000);
    expect(result.current.items.every((entry) => entry.monitored === false)).toBe(true);

    vi.mocked(authedFetch).mockClear();
    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    act(() => { result.current.bulk.openBulkTools("tags"); });
    act(() => { result.current.bulk.setBulkTagsInput("kids"); });
    await review(result);
    await review(result);
    const tags = sent().filter((call) => call.url === "/api/movies/bulk/tags");
    expect(tags).toHaveLength(1);
    expect(tags[0].body).toMatchObject({ tags: "kids", mode: "add" });
    expect(tags[0].body.movieIds).toHaveLength(5000);
    expect(result.current.items.every((entry) => entry.tags?.includes("kids"))).toBe(true);

    vi.mocked(authedFetch).mockClear();
    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    act(() => { result.current.bulk.openBulkTools("quality"); });
    await act(async () => { await Promise.resolve(); });
    act(() => { result.current.bulk.setBulkQualityProfileId("p-4k"); });
    await review(result);
    await review(result);
    const quality = sent().filter((call) => call.url === "/api/movies/bulk/quality-profile");
    expect(quality).toHaveLength(1);
    expect(quality[0].body.qualityProfileId).toBe("p-4k");
    expect(quality[0].body.movieIds).toHaveLength(5000);
  });
});
