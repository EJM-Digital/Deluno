import { describe, expect, it } from "vitest";
import {
  ACTIVITY_DEFAULT_SORT,
  activityLink,
  activitySortFrom,
  sortActivityRows,
  activityLinkForStatus,
  activityStatusFromParams,
  paramsWithStatus,
  activityLinkForEvent,
  activityLinkForFile,
  activityLinkForTitle,
  activityQueryString,
  emptyActivityFilters,
  filtersFromParams,
  hasActivityFilterParams,
  hasActivityFilters,
  isQuickSince,
  paramsFromFilters,
  resolveSince,
  resolveUntil,
  type ActivityFilters
} from "./activity-history";

const NOW = new Date(2026, 9, 1, 15, 30);

describe("the Activity history's filters live in the address (#1100)", () => {
  it("round-trips every filter through the address", () => {
    const filters: ActivityFilters = {
      type: "turned-down",
      since: "today",
      until: "2026-10-02",
      reason: "language",
      title: "Sintel 2010",
      file: "/movies/Sintel (2010)/Sintel.mkv",
      source: "indexer",
      outcome: "problem",
      item: "movie-1",
      itemType: "movie"
    };

    const address = paramsFromFilters(filters).toString();

    expect(filtersFromParams(new URLSearchParams(address))).toEqual(filters);
    expect(hasActivityFilters(filters)).toBe(true);
  });

  it("leaves out what is empty and keeps parameters that are not filters", () => {
    const base = new URLSearchParams("section=events&event=abc");

    const params = paramsFromFilters({ ...emptyActivityFilters, type: "search" }, base);

    expect(params.toString()).toBe("section=events&event=abc&type=search");
    expect(paramsFromFilters(emptyActivityFilters, params).toString()).toBe("section=events&event=abc");
    expect(hasActivityFilters(emptyActivityFilters)).toBe(false);
  });

  it("reads a link the way Live writes it", () => {
    expect(filtersFromParams(new URLSearchParams("type=turned-down&since=today&reason=tooBig"))).toEqual({
      ...emptyActivityFilters,
      type: "turned-down",
      since: "today",
      reason: "tooBig"
    });
    expect(activityLink({ type: "turned-down", since: "today", reason: "tooBig" })).toBe("/queue?view=history&type=turned-down&since=today&reason=tooBig");
    expect(activityLink({})).toBe("/queue?view=history");
  });

  it("links from a title and from a file", () => {
    expect(activityLinkForTitle("Sintel", "movie", "m1")).toBe("/queue?view=history&title=Sintel&item=m1&itemType=movie");
    expect(activityLinkForFile("/movies/Sintel (2010).mkv")).toBe("/queue?view=history&file=%2Fmovies%2FSintel+%282010%29.mkv");
    const parsed = filtersFromParams(new URLSearchParams(activityLinkForFile("/movies/Sintel (2010).mkv").split("?")[1]));
    expect(parsed.file).toBe("/movies/Sintel (2010).mkv");
  });

  it("links to one event on its own day", () => {
    const event = { id: "evt-1", createdUtc: new Date(2026, 8, 30, 23, 59).toISOString() };

    expect(activityLinkForEvent(event)).toBe("/queue?view=history&event=evt-1&since=2026-09-30&until=2026-09-30");
  });

  it("turns the day words into the person's own midnight, and an until day includes the whole day", () => {
    expect(resolveSince("today", NOW)).toEqual(new Date(2026, 9, 1));
    expect(resolveSince("yesterday", NOW)).toEqual(new Date(2026, 8, 30));
    expect(resolveSince("7d", NOW)).toEqual(new Date(2026, 8, 25));
    expect(resolveSince("30d", NOW)).toEqual(new Date(2026, 8, 2));
    expect(resolveSince("2026-09-10", NOW)).toEqual(new Date(2026, 8, 10));
    expect(resolveSince("", NOW)).toBeNull();
    expect(resolveSince("not a day", NOW)).toBeNull();
    expect(resolveUntil("2026-09-10", NOW)).toEqual(new Date(2026, 8, 11));
    expect(resolveUntil("today", NOW)).toEqual(new Date(2026, 9, 2));
    expect(resolveUntil("garbage", NOW)).toBeNull();
    expect(isQuickSince("7d")).toBe(true);
    expect(isQuickSince("2026-09-10")).toBe(false);
  });

  it("builds the link for a status chip, with the history filters beside it, and All when there is no status (#1114)", () => {
    expect(activityLinkForStatus()).toBe("/queue?view=history");
    expect(activityLinkForStatus(null)).toBe("/queue?view=history");
    expect(activityLinkForStatus("in-progress")).toBe("/queue?view=history&status=in-progress");
    expect(activityLinkForStatus("failed", { type: "turned-down", since: "today" })).toBe("/queue?view=history&status=failed&type=turned-down&since=today");
    expect(activityLinkForStatus("needs-decision")).toBe("/queue?view=history&status=needs-decision");
  });

  it("asks the server for exactly the filters, with a page size and the page token", () => {
    const query = new URLSearchParams(
      activityQueryString(
        { ...emptyActivityFilters, type: "turned-down", since: "today", reason: "language", title: "Sintel", file: "a.mkv", source: "client", outcome: "good", item: "m1", itemType: "movie" },
        50,
        "token-1",
        NOW
      )
    );

    expect(Object.fromEntries(query)).toEqual({
      pageSize: "50",
      type: "turned-down",
      title: "Sintel",
      file: "a.mkv",
      source: "client",
      outcome: "good",
      reason: "language",
      relatedEntityId: "m1",
      relatedEntityType: "movie",
      since: new Date(2026, 9, 1).toISOString(),
      pageToken: "token-1"
    });
    expect(activityQueryString(emptyActivityFilters, 25, undefined, NOW)).toBe("pageSize=25");
  });
});

describe("Activity's sort (#1119)", () => {
  it("asks the server for a sort only when it is not newest first", () => {
    expect(activityQueryString(emptyActivityFilters, 25, undefined, NOW, ACTIVITY_DEFAULT_SORT)).toBe("pageSize=25");
    expect(activityQueryString(emptyActivityFilters, 25, "t", NOW, { id: "what", direction: "desc" })).toBe("pageSize=25&sortBy=what&sortDir=desc&pageToken=t");
    expect(activityQueryString(emptyActivityFilters, 25, undefined, NOW, { id: "when", direction: "asc" })).toBe("pageSize=25&sortBy=when&sortDir=asc");
  });

  it("falls back to newest first for a column it does not sort by", () => {
    expect(activitySortFrom(null)).toEqual(ACTIVITY_DEFAULT_SORT);
    expect(activitySortFrom({ id: "nope", direction: "asc" })).toEqual(ACTIVITY_DEFAULT_SORT);
    expect(activitySortFrom({ id: "title", direction: "asc" })).toEqual({ id: "title", direction: "asc" });
  });

  it("sorts the rows built in the browser the way the server sorts the history", () => {
    const rows = [
      { title: "sintel", detail: "b", whenUtc: "2026-10-01T10:00:00Z" },
      { title: "Arrival", detail: "A", whenUtc: "2026-10-01T11:00:00Z" },
      { title: "Sintel", detail: "c", whenUtc: "2026-10-01T12:00:00Z" }
    ];
    expect(sortActivityRows(rows, { id: "title", direction: "asc" }).map((row) => row.detail)).toEqual(["A", "c", "b"]);
    expect(sortActivityRows(rows, { id: "title", direction: "desc" }).map((row) => row.detail)).toEqual(["c", "b", "A"]);
    expect(sortActivityRows(rows, { id: "what", direction: "asc" }).map((row) => row.detail)).toEqual(["A", "b", "c"]);
    expect(sortActivityRows(rows, ACTIVITY_DEFAULT_SORT).map((row) => row.detail)).toEqual(["c", "A", "b"]);
    expect(sortActivityRows(rows, { id: "when", direction: "asc" }).map((row) => row.detail)).toEqual(["b", "A", "c"]);
  });
});

describe("Activity's status chips live in the address (#1114)", () => {
  it("links to a chip, with the history filters beside it", () => {
    expect(activityLinkForStatus("failed")).toBe("/queue?view=history&status=failed");
    expect(activityLinkForStatus("in-progress", { type: "search", since: "today" })).toBe("/queue?view=history&status=in-progress&type=search&since=today");
  });

  it("reads the chip, and an old section link opens the matching chip", () => {
    expect(activityStatusFromParams(new URLSearchParams("status=needs-decision"))).toBe("needs-decision");
    expect(activityStatusFromParams(new URLSearchParams("section=jobs"))).toBe("in-progress");
    expect(activityStatusFromParams(new URLSearchParams("section=imports"))).toBe("needs-decision");
    expect(activityStatusFromParams(new URLSearchParams("section=events"))).toBe("all");
    expect(activityStatusFromParams(new URLSearchParams("status=nonsense"))).toBe("all");
    expect(activityStatusFromParams(new URLSearchParams(""))).toBe("all");
    expect(activityStatusFromParams(new URLSearchParams("status=done&section=jobs"))).toBe("done");
  });

  it("writes a chip into the address, keeping the filters and dropping the old section", () => {
    expect(paramsWithStatus("failed", new URLSearchParams("section=jobs&since=today")).toString()).toBe("since=today&status=failed");
    expect(paramsWithStatus("all", new URLSearchParams("status=failed&since=today")).toString()).toBe("since=today");
  });
});

describe("the Activity filters read from an address, and the days they name", () => {
  it("says whether an address carries any filter, ignoring a blank one", () => {
    expect(hasActivityFilterParams(new URLSearchParams("view=history&status=failed"))).toBe(false);
    expect(hasActivityFilterParams(new URLSearchParams("type=failed&title=%20"))).toBe(true);
    expect(hasActivityFilterParams(new URLSearchParams("title=%20%20"))).toBe(false);
  });

  it("ends an until day just after that day: today and yesterday by word, a date by its digits, anything else not at all", () => {
    expect(resolveUntil("today", NOW)).toEqual(new Date(2026, 9, 2));
    expect(resolveUntil("yesterday", NOW)).toEqual(new Date(2026, 9, 1));
    expect(resolveUntil("2026-03-04", NOW)).toEqual(new Date(2026, 2, 5));
    expect(resolveUntil("sometime", NOW)).toBeNull();
    expect(resolveUntil("", NOW)).toBeNull();
  });

  it("asks only for the filters that are set, and names the kind of a related item only when it has one", () => {
    expect(activityQueryString({ ...emptyActivityFilters, item: "abc" }, 10, undefined, NOW)).toBe("pageSize=10&relatedEntityId=abc");
    expect(activityQueryString({ ...emptyActivityFilters, item: "abc", itemType: "movie" }, 10, undefined, NOW)).toBe("pageSize=10&relatedEntityId=abc&relatedEntityType=movie");
    const query = new URLSearchParams(activityQueryString({ ...emptyActivityFilters, since: "2026-09-30", until: "2026-10-01", source: "indexer", outcome: "failed", reason: "size", file: "a.mkv" }, 10, undefined, NOW));
    expect(query.get("since")).toBe(new Date(2026, 8, 30).toISOString());
    expect(query.get("until")).toBe(new Date(2026, 9, 2).toISOString());
    expect([query.get("source"), query.get("outcome"), query.get("reason"), query.get("file")]).toEqual(["indexer", "failed", "size", "a.mkv"]);
  });

  it("sorts rows without a detail as empty text, and equal text newest first", () => {
    const rows = [
      { title: "A", whenUtc: "2026-10-01T10:00:00Z" },
      { title: "B", detail: "x", whenUtc: "2026-10-01T12:00:00Z" },
      { title: "C", whenUtc: "2026-10-01T11:00:00Z" }
    ];
    expect(sortActivityRows(rows, { id: "what", direction: "asc" }).map((row) => row.title)).toEqual(["C", "A", "B"]);
    expect(sortActivityRows(rows, { id: "what", direction: "desc" }).map((row) => row.title)).toEqual(["B", "C", "A"]);
  });
});
