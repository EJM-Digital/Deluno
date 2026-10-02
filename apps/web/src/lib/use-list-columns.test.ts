import { describe, expect, it } from "vitest";
import { compareSortValues, fullColumnOrder, reconcileMovableOrder, sortListItems } from "./use-list-columns";

describe("list column sort helpers (#1119)", () => {
  it("compares text with numbers inside it the way a person reads them", () => {
    expect(["file 10", "file 2", "File 1"].sort(compareSortValues)).toEqual(["File 1", "file 2", "file 10"]);
  });

  it("sorts dates by time, and rows with no value last in both directions", () => {
    const rows = [
      { id: "a", when: "2026-02-01" },
      { id: "none", when: null as string | null },
      { id: "b", when: "2026-01-01" }
    ];
    const column = { label: "When", sort: (row: (typeof rows)[number]) => (row.when ? new Date(row.when) : null) };
    expect(sortListItems(rows, column, "asc").map((row) => row.id)).toEqual(["b", "a", "none"]);
    expect(sortListItems(rows, column, "desc").map((row) => row.id)).toEqual(["a", "b", "none"]);
  });

  it("keeps equal rows in the list's own order", () => {
    const rows = [
      { id: "1", n: 1 },
      { id: "2", n: 1 },
      { id: "3", n: 0 }
    ];
    const column = { label: "N", sort: (row: (typeof rows)[number]) => row.n };
    expect(sortListItems(rows, column, "asc").map((row) => row.id)).toEqual(["3", "1", "2"]);
    expect(sortListItems(rows, column, "desc").map((row) => row.id)).toEqual(["1", "2", "3"]);
  });

  it("uses a comparator when a column has one", () => {
    const column = { label: "Len", compare: (a: string, b: string) => a.length - b.length };
    expect(sortListItems(["ccc", "a", "bb"], column, "asc")).toEqual(["a", "bb", "ccc"]);
  });

  it("leaves rows alone for a column the server sorts", () => {
    const rows = ["b", "a"];
    expect(sortListItems(rows, { label: "Name", sort: "name" }, "asc")).toBe(rows);
  });

  it("forgets saved columns that no longer exist and keeps new ones in their place", () => {
    expect(reconcileMovableOrder(["a", "b", "c"], ["c", "gone", "a"])).toEqual(["c", "a", "b"]);
  });

  it("lays movable columns into the slots between pinned ones", () => {
    expect(fullColumnOrder(["name", "select", "type", "status"], [true, false, true, true], ["status", "name", "type"])).toEqual(["status", "select", "name", "type"]);
  });
});
