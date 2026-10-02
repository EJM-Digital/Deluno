import { describe, expect, it } from "vitest";
import {
  activityStatusLink,
  activityLink,
  activityLinkForStatus,
  activityStatusFromParams,
  paramsWithStatus,
  activityLinkForEvent,
  activityLinkForFile,
  activityLinkForTitle,
  activityQueryString,
  emptyActivityFilters,
  filtersFromParams,
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
    expect(activityLink({ type: "turned-down", since: "today", reason: "tooBig" })).toBe("/activity?type=turned-down&since=today&reason=tooBig");
    expect(activityLink({})).toBe("/activity");
  });

  it("links from a title and from a file", () => {
    expect(activityLinkForTitle("Sintel", "movie", "m1")).toBe("/activity?title=Sintel&item=m1&itemType=movie");
    expect(activityLinkForFile("/movies/Sintel (2010).mkv")).toBe("/activity?file=%2Fmovies%2FSintel+%282010%29.mkv");
    const parsed = filtersFromParams(new URLSearchParams(activityLinkForFile("/movies/Sintel (2010).mkv").split("?")[1]));
    expect(parsed.file).toBe("/movies/Sintel (2010).mkv");
  });

  it("links to one event on its own day", () => {
    const event = { id: "evt-1", createdUtc: new Date(2026, 8, 30, 23, 59).toISOString() };

    expect(activityLinkForEvent(event)).toBe("/activity?event=evt-1&since=2026-09-30&until=2026-09-30");
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
    expect(activityStatusLink()).toBe("/activity");
    expect(activityStatusLink(null)).toBe("/activity");
    expect(activityStatusLink("in-progress")).toBe("/activity?status=in-progress");
    expect(activityStatusLink("failed", { type: "turned-down", since: "today" })).toBe("/activity?type=turned-down&since=today&status=failed");
    expect(activityStatusLink("needs-decision")).toBe("/activity?status=needs-decision");
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

describe("Activity's status chips live in the address (#1114)", () => {
  it("links to a chip, with the history filters beside it", () => {
    expect(activityLinkForStatus("failed")).toBe("/activity?status=failed");
    expect(activityLinkForStatus("in-progress", { type: "search", since: "today" })).toBe("/activity?status=in-progress&type=search&since=today");
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
