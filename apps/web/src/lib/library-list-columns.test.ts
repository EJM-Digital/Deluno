import { describe, expect, it } from "vitest";
import {
  LIST_COLUMN_SORT_FIELD,
  listColumnLabel,
  listColumnsFor,
  moveListColumn,
  listColumnStorageKey,
  parseListColumnOrder,
  shiftListColumn
} from "./library-list-columns";

const RADARR_PARITY_COLUMNS = [
  "studio",
  "originalTitle",
  "collection",
  "path",
  "releaseDate",
  "runtime",
  "releaseGroup",
  "tags",
  "qualityProfile"
] as const;

describe("library list column order", () => {
  it("keeps selection and Title outside the draggable order", () => {
    expect(listColumnsFor("movies")).toEqual([
      "quality", "status", "subtitles", "genre", "size", "rating", "added",
      "studio", "originalTitle", "collection", "path", "releaseDate", "runtime", "releaseGroup", "tags", "qualityProfile"
    ]);
    expect(listColumnsFor("shows")).toContain("episodes");
    expect(listColumnsFor("movies")).not.toContain("episodes");
  });

  // #790: Radarr parity. Studio and Collection are movie-only — a show has
  // neither — but the rest apply to both shelves, same as Radarr/Sonarr.
  it("adds the nine Radarr-parity columns the audit found missing", () => {
    for (const column of RADARR_PARITY_COLUMNS) {
      expect(listColumnsFor("movies")).toContain(column);
      expect(listColumnLabel(column)).toBeTruthy();
    }
    expect(listColumnsFor("shows")).not.toContain("studio");
    expect(listColumnsFor("shows")).not.toContain("collection");
    for (const column of ["originalTitle", "path", "runtime", "releaseGroup", "tags", "qualityProfile"] as const) {
      expect(listColumnsFor("shows")).toContain(column);
    }
  });

  it("maps the columns a served sort field exists for, and no others", () => {
    expect(LIST_COLUMN_SORT_FIELD.studio).toBe("studio");
    expect(LIST_COLUMN_SORT_FIELD.originalTitle).toBe("originalTitle");
    expect(LIST_COLUMN_SORT_FIELD.path).toBe("path");
    expect(LIST_COLUMN_SORT_FIELD.runtime).toBe("runtime");
    // Nothing on the server orders by these yet, so the header for them is
    // data-only rather than a broken sort control.
    expect(LIST_COLUMN_SORT_FIELD.collection).toBeUndefined();
    expect(LIST_COLUMN_SORT_FIELD.releaseGroup).toBeUndefined();
    expect(LIST_COLUMN_SORT_FIELD.tags).toBeUndefined();
    expect(LIST_COLUMN_SORT_FIELD.qualityProfile).toBeUndefined();
  });

  it("repairs stored orders without losing a newly supported column", () => {
    expect(parseListColumnOrder('["rating","status","rating","episodes"]', "movies")).toEqual([
      "rating", "status", "quality", "subtitles", "genre", "size", "added",
      "studio", "originalTitle", "collection", "path", "releaseDate", "runtime", "releaseGroup", "tags", "qualityProfile"
    ]);
    expect(parseListColumnOrder('["episodes","rating"]', "shows")).toEqual([
      "episodes", "rating", "quality", "status", "subtitles", "genre", "size", "added",
      "originalTitle", "path", "runtime", "releaseGroup", "tags", "qualityProfile"
    ]);
    expect(parseListColumnOrder("not json", "shows")).toEqual([...listColumnsFor("shows")]);
  });

  it("drops a movie-only column when repairing a shows order", () => {
    expect(parseListColumnOrder('["studio","collection","rating"]', "shows")).not.toContain("studio");
    expect(parseListColumnOrder('["studio","collection","rating"]', "shows")).not.toContain("collection");
  });

  it("moves a dragged column into the target column's position", () => {
    const order = [...listColumnsFor("movies")];
    expect(moveListColumn(order, "quality", "rating")).toEqual([
      "status", "subtitles", "genre", "size", "quality", "rating", "added",
      "studio", "originalTitle", "collection", "path", "releaseDate", "runtime", "releaseGroup", "tags", "qualityProfile"
    ]);
    expect(moveListColumn(order, "rating", "status")).toEqual([
      "quality", "rating", "status", "subtitles", "genre", "size", "added",
      "studio", "originalTitle", "collection", "path", "releaseDate", "runtime", "releaseGroup", "tags", "qualityProfile"
    ]);
    expect(moveListColumn(order, "quality", "quality")).toEqual(order);
  });

  it("moves a focused column one slot for the keyboard equivalent", () => {
    const order = [...listColumnsFor("movies")];
    expect(shiftListColumn(order, "quality", 1)).toEqual([
      "status", "quality", "subtitles", "genre", "size", "rating", "added",
      "studio", "originalTitle", "collection", "path", "releaseDate", "runtime", "releaseGroup", "tags", "qualityProfile"
    ]);
    expect(shiftListColumn(order, "status", -1)).toEqual([
      "status", "quality", "subtitles", "genre", "size", "rating", "added",
      "studio", "originalTitle", "collection", "path", "releaseDate", "runtime", "releaseGroup", "tags", "qualityProfile"
    ]);
    expect(shiftListColumn(order, "quality", -1)).toEqual(order);
  });
});

describe("reading saved column orders that are not an order", () => {
  it("falls back to the default columns for JSON that is not a list, is not JSON, or is empty", () => {
    const defaults = parseListColumnOrder(null, "movies");
    expect(parseListColumnOrder('{"a":1}', "movies")).toEqual(defaults);
    expect(parseListColumnOrder("not json", "movies")).toEqual(defaults);
    expect(parseListColumnOrder("", "movies")).toEqual(defaults);
  });
});

describe("where each library list keeps its column order", () => {
  it("keeps movies and shows apart", () => {
    expect(listColumnStorageKey("movies")).toBe("deluno-list-columns-movies");
    expect(listColumnStorageKey("shows")).toBe("deluno-list-columns-shows");
  });
});
