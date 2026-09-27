import { act, renderHook } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useBulkEdit } from "./use-bulk-edit";
import { authedFetch } from "../lib/use-auth";
import type { MediaItem } from "../lib/media-types";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../lib/api")>(),
  fetchJson: vi.fn().mockResolvedValue([])
}));

function makeItem(id: string): MediaItem {
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
    overview: ""
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

/**
 * #788: bulk rename gains an apply action alongside the existing preview.
 * These cover the full preview-then-apply flow, using the same selection
 * without re-running the operation from scratch, including the case where
 * some titles rename and others fail.
 */
describe("useBulkEdit bulk rename preview-then-apply (#788)", () => {
  beforeEach(() => {
    vi.mocked(authedFetch).mockReset();
  });

  it("applies the previewed renames for the same selection without re-selecting anything", async () => {
    vi.mocked(authedFetch).mockImplementation(async (url) => {
      if (url === "/api/movies/bulk/rename-preview") {
        return jsonResponse({
          previews: [
            { movieId: "a", title: "Title a", releaseYear: 2020, template: "{Movie Title}", proposedName: "Title a Renamed" },
            { movieId: "b", title: "Title b", releaseYear: 2020, template: "{Movie Title}", proposedName: "Title b Renamed" }
          ]
        });
      }
      if (url === "/api/movies/bulk/rename-apply") {
        return jsonResponse({ renamedCount: 2, renamed: [], failures: [] });
      }
      throw new Error(`unexpected fetch: ${url}`);
    });

    const { result } = renderHook(() => useHarness([makeItem("a"), makeItem("b")]));

    act(() => { result.current.bulk.setSelectedIds(["a", "b"]); });
    act(() => { result.current.bulk.openBulkTools("renamePreview"); });

    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });
    expect(result.current.bulk.bulkRenamePreview).toHaveLength(2);

    await act(async () => { await result.current.bulk.applyBulkRename(); });

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/bulk/rename-apply",
      expect.objectContaining({
        body: JSON.stringify({ movieIds: ["a", "b"], template: undefined })
      })
    );
    // A clean apply closes the dialog and clears the preview and selection -
    // there is nothing left needing review.
    expect(result.current.bulk.bulkRenamePreview).toEqual([]);
    expect(result.current.bulk.isBulkToolsOpen).toBe(false);
    expect(result.current.bulk.selectedIds).toEqual([]);
  });

  it("keeps the dialog open and the preview visible when some renames fail", async () => {
    vi.mocked(authedFetch).mockImplementation(async (url) => {
      if (url === "/api/movies/bulk/rename-preview") {
        return jsonResponse({
          previews: [
            { movieId: "a", title: "Title a", releaseYear: 2020, template: "{Movie Title}", proposedName: "Title a Renamed" },
            { movieId: "b", title: "Title b", releaseYear: 2020, template: "{Movie Title}", proposedName: "Title b Renamed" }
          ]
        });
      }
      if (url === "/api/movies/bulk/rename-apply") {
        return jsonResponse({
          renamedCount: 1,
          renamed: [{ movieId: "a", title: "Title a" }],
          failures: [{ movieId: "b", title: "Title b", reason: "A folder with that name already exists." }]
        });
      }
      throw new Error(`unexpected fetch: ${url}`);
    });

    const { result } = renderHook(() => useHarness([makeItem("a"), makeItem("b")]));

    act(() => { result.current.bulk.setSelectedIds(["a", "b"]); });
    act(() => { result.current.bulk.openBulkTools("renamePreview"); });
    await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });

    await act(async () => { await result.current.bulk.applyBulkRename(); });

    // The dialog stays open on a partial failure so the user can see which
    // title needs attention, rather than losing that information the moment
    // it closes.
    expect(result.current.bulk.isBulkToolsOpen).toBe(true);
    expect(result.current.bulk.bulkRenamePreview).toEqual([]);
  });

  it("does nothing when there is no preview to apply", async () => {
    const { result } = renderHook(() => useHarness([makeItem("a")]));

    await act(async () => { await result.current.bulk.applyBulkRename(); });

    expect(authedFetch).not.toHaveBeenCalled();
  });
});
