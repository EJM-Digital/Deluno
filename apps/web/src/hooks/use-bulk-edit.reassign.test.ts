import { act, render, renderHook, screen } from "@testing-library/react";
import { toast } from "../components/shell/toaster";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useBulkEdit } from "./use-bulk-edit";
import { authedFetch } from "../lib/use-auth";
import type { MediaItem } from "../lib/media-types";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../lib/api")>(),
  // The dialog's own options loader (libraries, quality profiles) - not what
  // these tests are about, and without this it would fire a real fetch every
  // time openBulkTools opens the dialog.
  fetchJson: vi.fn().mockResolvedValue([])
}));

function makeItem(id: string, extra: Partial<MediaItem> = {}): MediaItem {
  return {
    id,
    title: `Title ${id}`,
    year: 2020,
    type: "movie",
    poster: null,
    backdrop: null,
    quality: null,
    monitored: true,
    sizeGb: null,
    rating: null,
    genres: [],
    added: "1 Jan",
    overview: "",
    libraryId: "lib-a",
    minimumAvailability: "released",
    ...extra
  };
}

function useHarness(initial: MediaItem[]) {
  const [items, setItems] = useState<MediaItem[]>(initial);
  const bulk = useBulkEdit({ libraryItems: items, setLibraryItems: setItems, variant: "movies" });
  return { items, setItems, bulk };
}

function jsonResponse(body: unknown, ok = true) {
  return { ok, json: async () => body } as Response;
}

describe("useBulkEdit bulk minimum availability (#787)", () => {
  beforeEach(() => {
    vi.mocked(authedFetch).mockReset();
    vi.mocked(authedFetch).mockResolvedValue(jsonResponse({ updated: 1 }));
  });

  it("posts the chosen availability for every selected title", async () => {
    const { result } = renderHook(() => useHarness([makeItem("a"), makeItem("b")]));

    act(() => { result.current.bulk.setSelectedIds(["a", "b"]); });
    act(() => { result.current.bulk.openBulkTools("minimumAvailability"); });
    act(() => { result.current.bulk.setBulkMinimumAvailability("announced"); });

    // First call only asks for confirmation, matching every other bulk op.
    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });
    expect(authedFetch).not.toHaveBeenCalled();

    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/bulk/minimum-availability",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({ movieIds: ["a", "b"], minimumAvailability: "announced" })
      })
    );
    expect(result.current.items.map((item) => item.minimumAvailability)).toEqual(["announced", "announced"]);
  });
});

describe("useBulkEdit bulk reassign-library move-files (#787)", () => {
  beforeEach(() => {
    vi.mocked(authedFetch).mockReset();
    vi.mocked(authedFetch).mockResolvedValue(jsonResponse({ reassigned: 1, filesMoved: 1, moveFailures: [] }));
  });

  it("defaults to record-only and says so plainly", async () => {
    const { result } = renderHook(() => useHarness([makeItem("a", { libraryId: "lib-a" })]));

    act(() => { result.current.bulk.setSelectedIds(["a"]); });
    act(() => { result.current.bulk.openBulkTools("reassignLibrary"); });
    act(() => { result.current.bulk.setBulkTargetLibraryId("lib-b"); });

    expect(result.current.bulk.bulkMoveFiles).toBe(false);

    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });
    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/bulk/reassign-library",
      expect.objectContaining({
        body: JSON.stringify({ movieIds: ["a"], fromLibraryId: "lib-a", toLibraryId: "lib-b", moveFiles: false })
      })
    );
  });

  it("passes moveFiles through when the user opts in", async () => {
    const { result } = renderHook(() => useHarness([makeItem("a", { libraryId: "lib-a" })]));

    act(() => { result.current.bulk.setSelectedIds(["a"]); });
    act(() => { result.current.bulk.openBulkTools("reassignLibrary"); });
    act(() => { result.current.bulk.setBulkTargetLibraryId("lib-b"); });
    act(() => { result.current.bulk.setBulkMoveFiles(true); });

    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });
    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/bulk/reassign-library",
      expect.objectContaining({
        body: JSON.stringify({ movieIds: ["a"], fromLibraryId: "lib-a", toLibraryId: "lib-b", moveFiles: true })
      })
    );
  });

  it("offers the same choice for shows and posts it to the series route", async () => {
    const { result } = renderHook(() => {
      const [items, setItems] = useState<MediaItem[]>([makeItem("s1", { libraryId: "lib-a", type: "show" })]);
      const bulk = useBulkEdit({ libraryItems: items, setLibraryItems: setItems, variant: "shows" });
      return { items, bulk };
    });

    act(() => { result.current.bulk.setSelectedIds(["s1"]); });
    act(() => { result.current.bulk.openBulkTools("reassignLibrary"); });
    act(() => { result.current.bulk.setBulkTargetLibraryId("lib-b"); });
    expect(result.current.bulk.bulkMoveFiles).toBe(false);
    act(() => { result.current.bulk.setBulkMoveFiles(true); });

    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });
    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/series/bulk/reassign-library",
      expect.objectContaining({
        body: JSON.stringify({ seriesIds: ["s1"], fromLibraryId: "lib-a", toLibraryId: "lib-b", moveFiles: true })
      })
    );
  });

  it("resets moveFiles to off every time the dialog is reopened", () => {
    const { result } = renderHook(() => useHarness([makeItem("a")]));

    act(() => { result.current.bulk.setSelectedIds(["a"]); });
    act(() => { result.current.bulk.openBulkTools("reassignLibrary"); });
    act(() => { result.current.bulk.setBulkMoveFiles(true); });
    act(() => { result.current.bulk.setIsBulkToolsOpen(false); });
    act(() => { result.current.bulk.openBulkTools("reassignLibrary"); });

    expect(result.current.bulk.bulkMoveFiles).toBe(false);
  });
});

describe("useBulkEdit refused library moves (#1078)", () => {
  beforeEach(() => {
    vi.mocked(authedFetch).mockReset();
    vi.mocked(toast.error).mockClear();
  });

  it("lists each refused title with its own reason and keeps it in its old library", async () => {
    const reason = 'Heat: the folder "Action" holds other films, so Deluno moved nothing.';
    vi.mocked(authedFetch).mockResolvedValue(jsonResponse({
      reassigned: 1, filesMoved: 1, moveFailures: ["Title b"], moveFailureReasons: [reason]
    }));
    const { result } = renderHook(() => useHarness([makeItem("a"), makeItem("b")]));

    act(() => { result.current.bulk.setSelectedIds(["a", "b"]); });
    act(() => { result.current.bulk.openBulkTools("reassignLibrary"); });
    act(() => { result.current.bulk.setBulkTargetLibraryId("lib-b"); });
    act(() => { result.current.bulk.setBulkMoveFiles(true); });
    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });
    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });

    expect(result.current.items.find((item) => item.id === "a")?.libraryId).toBe("lib-b");
    expect(result.current.items.find((item) => item.id === "b")?.libraryId).toBe("lib-a");
    expect(toast.error).toHaveBeenCalledWith(
      "1 title not moved",
      expect.objectContaining({ duration: Infinity })
    );
    const { description } = vi.mocked(toast.error).mock.calls[0][1] as { description: React.ReactElement<{ children: React.ReactElement[] }> };
    render(description);
    expect(screen.getByRole("list", { name: "Titles Deluno did not move and why" })).toHaveTextContent(reason);
  });
});
