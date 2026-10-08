import { describe, expect, it } from "vitest";
import type { ActivityEventItem } from "./api";
import { emptyActivityFilters, type ActivityFilters } from "./activity-history";
import { activityEventMatches, activityItemFromPush, addToHistory, isNewestFirst, prependOnce } from "./activity-live-patch";
import type { ActivityEventAddedEvent } from "./use-signalr";

const NOW = new Date(2026, 9, 8, 15, 0);

const pushed: ActivityEventAddedEvent = {
  id: "e1",
  message: "Imported Dune",
  category: "download.import.completed",
  label: "Imported",
  severity: "success",
  createdUtc: new Date(2026, 9, 8, 14, 59).toISOString(),
  relatedEntityType: "movie",
  relatedEntityId: "m1",
  relatedJobId: "j1",
  title: "Dune",
  filePath: "/films/Dune (2021)/Dune.mkv",
  sourceName: "qBittorrent",
  type: "imported",
  source: "client",
  outcome: "good",
  detailsJson: "{\"title\":\"Dune\"}"
};

const filters = (patch: Partial<ActivityFilters>): ActivityFilters => ({ ...emptyActivityFilters, ...patch });

describe("a pushed Activity event is a history row (#1179)", () => {
  it("carries the entity, title, job and details the list endpoint would give", () => {
    const item = activityItemFromPush(pushed);
    expect(item).toMatchObject({ id: "e1", relatedEntityType: "movie", relatedEntityId: "m1", relatedJobId: "j1", title: "Dune", type: "imported", outcome: "good" });
    expect(item.detailsJson).toBe("{\"title\":\"Dune\"}");
  });

  it("fills what an older push left out with nothing, not undefined", () => {
    const item = activityItemFromPush({ id: "e2", message: "Searched", category: "search", severity: "info", createdUtc: pushed.createdUtc });
    expect(item.relatedEntityId).toBeNull();
    expect(item.title).toBeNull();
  });
});

describe("which cached history view a pushed event belongs in", () => {
  const item = activityItemFromPush(pushed);

  it("belongs in the unfiltered view", () => {
    expect(activityEventMatches(filters({}), item, NOW)).toBe("yes");
  });

  it("respects the kind, source, outcome and reason it can read from the push", () => {
    expect(activityEventMatches(filters({ type: "imported" }), item, NOW)).toBe("yes");
    expect(activityEventMatches(filters({ type: "failed" }), item, NOW)).toBe("no");
    expect(activityEventMatches(filters({ source: "indexer" }), item, NOW)).toBe("no");
    expect(activityEventMatches(filters({ outcome: "problem" }), item, NOW)).toBe("no");
    expect(activityEventMatches(filters({ reason: "language" }), item, NOW)).toBe("no");
  });

  it("matches a title or file by what the event names, and says it cannot tell when the event names none", () => {
    expect(activityEventMatches(filters({ title: "dun" }), item, NOW)).toBe("yes");
    expect(activityEventMatches(filters({ title: "Arrival" }), item, NOW)).toBe("no");
    expect(activityEventMatches(filters({ file: "Dune.mkv" }), item, NOW)).toBe("yes");
    const unnamed = activityItemFromPush({ ...pushed, title: null, filePath: null });
    expect(activityEventMatches(filters({ title: "Arrival" }), unnamed, NOW)).toBe("unknown");
    expect(activityEventMatches(filters({ file: "x.mkv" }), unnamed, NOW)).toBe("unknown");
  });

  it("matches one title by its id, and cannot tell for another's", () => {
    expect(activityEventMatches(filters({ item: "m1", itemType: "movie" }), item, NOW)).toBe("yes");
    expect(activityEventMatches(filters({ item: "m2", itemType: "movie" }), item, NOW)).toBe("unknown");
    expect(activityEventMatches(filters({ item: "s1", itemType: "series" }), item, NOW)).toBe("no");
  });

  it("keeps a day's view to that day", () => {
    expect(activityEventMatches(filters({ since: "today" }), item, NOW)).toBe("yes");
    expect(activityEventMatches(filters({ since: "2026-10-09" }), item, NOW)).toBe("no");
    expect(activityEventMatches(filters({ until: "2026-10-07" }), item, NOW)).toBe("no");
  });
});

describe("putting the event into what is on screen", () => {
  const first: ActivityEventItem = activityItemFromPush({ ...pushed, id: "old" });
  const data = { pages: [{ items: [first] }, { items: [] }], pageParams: [undefined, "t"] };

  it("goes to the head of the first page, once", () => {
    const next = addToHistory(data, activityItemFromPush(pushed));
    expect(next.pages[0].items.map((row) => row.id)).toEqual(["e1", "old"]);
    expect(addToHistory(next, activityItemFromPush(pushed))).toBe(next);
  });

  it("only a newest-first view takes it at the head", () => {
    expect(isNewestFirst("when", "desc")).toBe(true);
    expect(isNewestFirst("title", "asc")).toBe(false);
  });

  it("joins the bell's Recent list once and keeps it to its length", () => {
    const list = [first];
    const next = prependOnce(list, activityItemFromPush(pushed), 2);
    expect(next.map((row) => row.id)).toEqual(["e1", "old"]);
    expect(prependOnce(next, activityItemFromPush(pushed), 2)).toBe(next);
    expect(prependOnce(next, activityItemFromPush({ ...pushed, id: "e3" }), 2).map((row) => row.id)).toEqual(["e3", "e1"]);
  });
});
