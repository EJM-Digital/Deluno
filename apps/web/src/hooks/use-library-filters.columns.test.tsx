import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("../lib/library-controls", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/library-controls")>()),
  fetchLibraryControls: vi.fn(async () => ({ kind: "movies", filterFields: [], sortFields: [], posterOptions: [] }))
}));

const { useLibraryFilters } = await import("./use-library-filters");
const { listColumnStorageKey, listColumnsFor } = await import("../lib/library-list-columns");

afterEach(() => window.localStorage.clear());

describe("the list column order of a shelf (#1329)", () => {
  it("Reset puts the columns back in the default order, remembers that, and the current order is what the shelf reports", () => {
    const { result } = renderHook(() => useLibraryFilters("movies", null, "list"));
    const defaults = [...listColumnsFor("movies")];
    expect(result.current.listColumnOrder).toEqual(defaults);

    const reordered = [defaults[1]!, defaults[0]!, ...defaults.slice(2)];
    act(() => result.current.setListColumnOrder(reordered));
    expect(result.current.listColumnOrder).toEqual(reordered);
    expect(JSON.parse(window.localStorage.getItem(listColumnStorageKey("movies"))!)).toEqual(reordered);

    act(() => result.current.resetListColumnOrder());

    expect(result.current.listColumnOrder).toEqual(defaults);
    expect(JSON.parse(window.localStorage.getItem(listColumnStorageKey("movies"))!)).toEqual(defaults);
  });
});
