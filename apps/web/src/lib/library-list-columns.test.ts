import { describe, expect, it } from "vitest";
import {
  listColumnsFor,
  moveListColumn,
  parseListColumnOrder,
  shiftListColumn
} from "./library-list-columns";

describe("library list column order", () => {
  it("keeps selection and Title outside the draggable order", () => {
    expect(listColumnsFor("movies")).toEqual([
      "quality", "status", "subtitles", "genre", "size", "rating", "added"
    ]);
    expect(listColumnsFor("shows")).toContain("episodes");
    expect(listColumnsFor("movies")).not.toContain("episodes");
  });

  it("repairs stored orders without losing a newly supported column", () => {
    expect(parseListColumnOrder('["rating","status","rating","episodes"]', "movies")).toEqual([
      "rating", "status", "quality", "subtitles", "genre", "size", "added"
    ]);
    expect(parseListColumnOrder('["episodes","rating"]', "shows")).toEqual([
      "episodes", "rating", "quality", "status", "subtitles", "genre", "size", "added"
    ]);
    expect(parseListColumnOrder("not json", "shows")).toEqual([...listColumnsFor("shows")]);
  });

  it("moves a dragged column into the target column's position", () => {
    const order = [...listColumnsFor("movies")];
    expect(moveListColumn(order, "quality", "rating")).toEqual([
      "status", "subtitles", "genre", "size", "quality", "rating", "added"
    ]);
    expect(moveListColumn(order, "rating", "status")).toEqual([
      "quality", "rating", "status", "subtitles", "genre", "size", "added"
    ]);
    expect(moveListColumn(order, "quality", "quality")).toEqual(order);
  });

  it("moves a focused column one slot for the keyboard equivalent", () => {
    const order = [...listColumnsFor("movies")];
    expect(shiftListColumn(order, "quality", 1)).toEqual([
      "status", "quality", "subtitles", "genre", "size", "rating", "added"
    ]);
    expect(shiftListColumn(order, "status", -1)).toEqual([
      "status", "quality", "subtitles", "genre", "size", "rating", "added"
    ]);
    expect(shiftListColumn(order, "quality", -1)).toEqual(order);
  });
});
