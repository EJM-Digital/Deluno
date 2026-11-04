import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useColumnVisibility, type ColumnDefinition } from "./use-column-visibility";

const columns: ColumnDefinition[] = [
  { key: "size", label: "Size", defaultVisible: true },
  { key: "added", label: "Added", defaultVisible: false },
  { key: "codec", label: "Codec", defaultVisible: true }
];
const KEY = "deluno.test.columns";

beforeEach(() => localStorage.clear());
afterEach(() => vi.restoreAllMocks());

describe("which optional columns a table shows", () => {
  it("starts from the columns that are on for a first visit", () => {
    const { result } = renderHook(() => useColumnVisibility(KEY, columns));
    expect([...result.current.visible]).toEqual(["size", "codec"]);
    expect(result.current.isVisible("size")).toBe(true);
    expect(result.current.isVisible("added")).toBe(false);
    expect(result.current.columns).toBe(columns);
  });

  it("remembers a toggle for the next visit, and toggles back", () => {
    const { result } = renderHook(() => useColumnVisibility(KEY, columns));
    act(() => result.current.toggle("added"));
    expect(result.current.isVisible("added")).toBe(true);
    expect(JSON.parse(localStorage.getItem(KEY)!)).toEqual(["size", "codec", "added"]);
    act(() => result.current.toggle("size"));
    expect(result.current.isVisible("size")).toBe(false);
    expect(JSON.parse(localStorage.getItem(KEY)!)).toEqual(["codec", "added"]);
    const again = renderHook(() => useColumnVisibility(KEY, columns));
    expect([...again.result.current.visible]).toEqual(["codec", "added"]);
  });

  it("keeps each table's choice under its own name", () => {
    const first = renderHook(() => useColumnVisibility("a", columns));
    act(() => first.result.current.toggle("added"));
    const second = renderHook(() => useColumnVisibility("b", columns));
    expect(second.result.current.isVisible("added")).toBe(false);
  });

  it("drops a remembered column that no longer exists, and never switches on one added since", () => {
    localStorage.setItem(KEY, JSON.stringify(["added", "gone", 7, null]));
    const { result } = renderHook(() => useColumnVisibility(KEY, columns));
    expect([...result.current.visible]).toEqual(["added"]);
    expect(result.current.isVisible("size")).toBe(false);
  });

  it("falls back to the defaults when what is remembered is empty, not a list, or not readable", () => {
    localStorage.setItem(KEY, "");
    expect([...renderHook(() => useColumnVisibility(KEY, columns)).result.current.visible]).toEqual(["size", "codec"]);
    localStorage.setItem(KEY, JSON.stringify({ size: true }));
    expect([...renderHook(() => useColumnVisibility(KEY, columns)).result.current.visible]).toEqual(["size", "codec"]);
    localStorage.setItem(KEY, "{not json");
    expect([...renderHook(() => useColumnVisibility(KEY, columns)).result.current.visible]).toEqual(["size", "codec"]);
  });

  it("keeps a remembered empty list as nothing shown", () => {
    localStorage.setItem(KEY, "[]");
    expect([...renderHook(() => useColumnVisibility(KEY, columns)).result.current.visible]).toEqual([]);
  });

  it("falls back to the defaults when the browser will not be read, and still toggles when it will not be written", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("full"); });
    const { result } = renderHook(() => useColumnVisibility(KEY, columns));
    expect([...result.current.visible]).toEqual(["size", "codec"]);
    act(() => result.current.toggle("added"));
    expect(result.current.isVisible("added")).toBe(true);
  });

  it("keeps the same toggle for the same table, and a new answer once something is toggled", () => {
    const { result, rerender } = renderHook(() => useColumnVisibility(KEY, columns));
    const first = result.current;
    rerender();
    expect(result.current).toBe(first);
    act(() => result.current.toggle("added"));
    expect(result.current.toggle).toBe(first.toggle);
    expect(result.current.isVisible).not.toBe(first.isVisible);
  });
});
