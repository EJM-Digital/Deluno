import { act, renderHook } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { useBulkEdit } from "./use-bulk-edit";
import type { MediaItem } from "../lib/media-types";

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

/**
 * The library screen loads its first page instantly and keeps appending the
 * rest behind the scenes (#312); this harness stands in for that by letting
 * the test grow `libraryItems` after the hook has already mounted, the same
 * way the background fill grows it after a click.
 */
function useHarness(initial: MediaItem[]) {
  const [items, setItems] = useState<MediaItem[]>(initial);
  const bulk = useBulkEdit({ libraryItems: items, setLibraryItems: setItems, variant: "movies" });
  return { items, setItems, bulk };
}

describe("useBulkEdit select-all (#789)", () => {
  it("selects every item matching the filter, not only the page loaded when clicked", () => {
    const page1 = [makeItem("a"), makeItem("b")];
    const { result } = renderHook(() => useHarness(page1));

    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    expect(result.current.bulk.selectedIds.sort()).toEqual(["a", "b"]);

    // The background fill appends the rest of the filtered set after the click.
    act(() => { result.current.setItems((current) => [...current, makeItem("c"), makeItem("d")]); });

    expect(result.current.bulk.selectedIds.sort()).toEqual(["a", "b", "c", "d"]);
  });

  it("keeps selecting newly-loaded rows across several fill passes", () => {
    const { result } = renderHook(() => useHarness([makeItem("a")]));

    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    act(() => { result.current.setItems((current) => [...current, makeItem("b")]); });
    act(() => { result.current.setItems((current) => [...current, makeItem("c")]); });

    expect(result.current.bulk.selectedIds.sort()).toEqual(["a", "b", "c"]);
  });

  it("stops treating the selection as 'all' the moment one row is deselected by hand", () => {
    const { result } = renderHook(() => useHarness([makeItem("a"), makeItem("b")]));

    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    act(() => { result.current.bulk.toggleSelectedId("b"); });
    expect(result.current.bulk.selectedIds).toEqual(["a"]);

    // A later page landing must not resurrect "b" just because select-all was
    // active before the hand-picked change.
    act(() => { result.current.setItems((current) => [...current, makeItem("c")]); });
    expect(result.current.bulk.selectedIds).toEqual(["a"]);
  });

  it("toggling select-all off clears the selection and stops tracking new rows", () => {
    const { result } = renderHook(() => useHarness([makeItem("a")]));

    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    expect(result.current.bulk.selectedIds).toEqual(["a"]);

    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    expect(result.current.bulk.selectedIds).toEqual([]);

    act(() => { result.current.setItems((current) => [...current, makeItem("b")]); });
    expect(result.current.bulk.selectedIds).toEqual([]);
  });

  it("clearSelection drops the standing select-all intent as well as the ids", () => {
    const { result } = renderHook(() => useHarness([makeItem("a")]));

    act(() => { result.current.bulk.toggleSelectAllVisible(); });
    act(() => { result.current.bulk.clearSelection(); });
    expect(result.current.bulk.selectedIds).toEqual([]);

    act(() => { result.current.setItems((current) => [...current, makeItem("b")]); });
    expect(result.current.bulk.selectedIds).toEqual([]);
  });
});
