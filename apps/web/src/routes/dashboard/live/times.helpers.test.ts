import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadDispatchItem, ProcessorHandoffItem, SeriesUpcomingEpisodeItem } from "../../../lib/api";
import type { JourneyCard } from "./journey";

const rows = vi.hoisted(() => ({ current: [] as unknown[] }));
vi.mock("./countdowns", () => ({ buildCountdownRows: () => rows.current }));

const { allInBy, buildNext, clockTime, doneAbout, isMediaJob, journeyHeading, MEDIA_TASKS, MIN_JOURNEYS, usualTimes, whenText } = await import("./times");

const local = (h: number, m = 0, day = 4) => new Date(2026, 10, day, h, m, 0).getTime();
const NOW = local(12, 0);
const card = (overrides: Partial<JourneyCard>) => ({ key: "k", stage: "downloading", title: "t", state: { tone: "info", label: "x" }, ...overrides }) as JourneyCard;

beforeEach(() => { rows.current = []; });

describe("the clock time as Australians read it", () => {
  it.each([[0, 5, "12:05 am"], [9, 0, "9:00 am"], [11, 59, "11:59 am"], [12, 0, "12:00 pm"], [14, 28, "2:28 pm"], [23, 59, "11:59 pm"]])("%i:%i reads %s", (h, m, text) => {
    expect(clockTime(local(h, m))).toBe(text);
  });
});

describe("when every title on its way should be in", () => {
  it("is the latest finish time any client gives", () => {
    expect(allInBy([card({ finishAtMs: 100 }), card({ finishAtMs: 300 }), card({ finishAtMs: 200 })])).toBe(300);
    expect(allInBy([card({ finishAtMs: 300 }), card({ finishAtMs: 100 })])).toBe(300);
  });

  it("leaves out the imported, the stuck, the unread and the unreadable", () => {
    expect(allInBy([card({ stage: "imported", finishAtMs: 900 }), card({ stuck: true, finishAtMs: 800 }), card({}), card({ finishAtMs: Number.NaN }), card({ finishAtMs: 50 })])).toBe(50);
    expect(allInBy([])).toBeUndefined();
    expect(allInBy([card({ finishAtMs: Number.POSITIVE_INFINITY })])).toBeUndefined();
  });
});

describe("the heading over the journeys", () => {
  it("says nothing is in progress when none is", () => {
    expect(journeyHeading(0, local(14, 28))).toBe("nothing in progress right now");
    expect(journeyHeading(-1, undefined)).toBe("nothing in progress right now");
  });

  it("counts them, with when they will all be done when a client says", () => {
    expect(journeyHeading(8, local(14, 28))).toBe("8 in progress · all done by about 2:28 pm");
    expect(journeyHeading(1200, undefined)).toBe("1,200 in progress");
  });
});

describe("when one download should be done", () => {
  it("says the clock time, or nothing", () => {
    expect(doneAbout(local(14, 28))).toBe("done about 2:28 pm");
    expect(doneAbout(undefined)).toBeNull();
    expect(doneAbout(Number.NaN)).toBeNull();
  });
});

describe("what most journeys took today", () => {
  const dispatch = (n: number, overrides: Partial<DownloadDispatchItem> = {}) => {
    const t = (min: number) => new Date(local(10, 0) + min * 60_000).toISOString();
    return { createdUtc: t(0), detectedUtc: t(2 + n), importDetectedUtc: t(12 + n), importCompletedUtc: t(15 + n), ...overrides } as unknown as DownloadDispatchItem;
  };
  const handoff = (min: number, overrides: Partial<ProcessorHandoffItem> = {}) =>
    ({ status: "Completed", createdUtc: new Date(local(10, 0)).toISOString(), updatedUtc: new Date(local(10, 0) + min * 60_000).toISOString(), ...overrides }) as unknown as ProcessorHandoffItem;

  it("needs three finished journeys to say anything", () => {
    expect(MIN_JOURNEYS).toBe(3);
    expect(usualTimes([dispatch(0), dispatch(0)], [], NOW)).toEqual({});
    expect(usualTimes([], [handoff(4), handoff(4)], NOW)).toEqual({});
  });

  it("averages the wait, the download and the import of today's finished journeys", () => {
    const result = usualTimes([dispatch(0), dispatch(2), dispatch(4)], [], NOW);
    expect(result).toEqual({ grabbed: "usually 4 min", downloading: "usually 10 min", importing: "usually 3 min" });
  });

  it("averages the time the processor kept each hand-off, counting only completed ones from today", () => {
    const result = usualTimes([], [handoff(4), handoff(6), handoff(8), handoff(100, { status: "failed" }), handoff(100, { updatedUtc: "garbage" }), handoff(100, { updatedUtc: new Date(local(10, 0, 3)).toISOString() })], NOW);
    expect(result).toEqual({ processor: "usually 6 min" });
  });

  it("counts a hand-off whatever the case of its status", () => {
    expect(usualTimes([], [handoff(4, { status: "COMPLETED" }), handoff(4, { status: "completed" }), handoff(4)], NOW)).toEqual({ processor: "usually 4 min" });
  });

  it("leaves out journeys that did not finish today or whose times cannot be read, and spans that are not forward", () => {
    const yesterday = (n: number) => dispatch(n, { importCompletedUtc: new Date(local(10, 0, 3)).toISOString() });
    expect(usualTimes([yesterday(0), yesterday(1), yesterday(2)], [], NOW)).toEqual({});
    expect(usualTimes([dispatch(0, { importCompletedUtc: null }), dispatch(0, { importCompletedUtc: "garbage" }), dispatch(0, { importCompletedUtc: undefined })], [], NOW)).toEqual({});
    const noTimes = [dispatch(0, { detectedUtc: null, importDetectedUtc: null }), dispatch(0, { detectedUtc: null, importDetectedUtc: null }), dispatch(0, { detectedUtc: null, importDetectedUtc: null })];
    expect(usualTimes(noTimes, [], NOW)).toEqual({});
    const backwards = [0, 1, 2].map(() => dispatch(0, { createdUtc: new Date(local(10, 30)).toISOString(), detectedUtc: new Date(local(10, 0)).toISOString() }));
    expect(usualTimes(backwards, [], NOW).grabbed).toBeUndefined();
    const same = [0, 1, 2].map(() => dispatch(0, { createdUtc: new Date(local(10, 0)).toISOString(), detectedUtc: new Date(local(10, 0)).toISOString() }));
    expect(usualTimes(same, [], NOW).grabbed).toBeUndefined();
  });

  it("leaves out a span when only one end of it can be read", () => {
    const half = [0, 1, 2].map(() => dispatch(0, { importDetectedUtc: null }));
    expect(usualTimes(half, [], NOW)).toEqual({ grabbed: "usually 2 min" });
  });

  it("leaves out hand-offs finished on another day and hand-offs that ran backwards", () => {
    const old = (min: number) => handoff(min, { createdUtc: new Date(local(10, 0, 3)).toISOString(), updatedUtc: new Date(local(10, 0, 3) + min * 60_000).toISOString() });
    const backwards = handoff(-5);
    expect(usualTimes([], [handoff(4), handoff(4), handoff(4), old(100), backwards], NOW)).toEqual({ processor: "usually 4 min" });
  });

  it("says at least a second however fast the journeys were", () => {
    const fast = [0, 1, 2].map(() => dispatch(0, { createdUtc: new Date(local(10, 0)).toISOString(), detectedUtc: new Date(local(10, 0) + 1).toISOString() }));
    expect(usualTimes(fast, [], NOW).grabbed).toBe("usually under 1 min");
  });

  it("does not count journeys finished on the same day number of another month or year", () => {
    const other = (month: number, year: number) => dispatch(0, { importCompletedUtc: new Date(year, month, 4, 10, 0).toISOString() });
    expect(usualTimes([other(9, 2026), other(9, 2026), other(9, 2026)], [], NOW)).toEqual({});
    expect(usualTimes([other(10, 2025), other(10, 2025), other(10, 2025)], [], NOW)).toEqual({});
  });
});

describe("what Deluno does next for your media", () => {
  const row = (overrides: Record<string, unknown> = {}) => ({ key: "search:lib1", label: "Movies search", hint: "", to: "/movies", next: new Date(NOW + 5 * 60_000).toISOString(), last: new Date(NOW - 10 * 60_000).toISOString(), running: false, rank: 0, ...overrides });
  const episode = (overrides: Partial<SeriesUpcomingEpisodeItem> = {}) => ({ seriesId: "s1", title: "Lioness", startYear: null, posterUrl: null, episodeId: "e1", seasonNumber: 2, episodeNumber: 3, episodeTitle: null, airDateUtc: new Date(NOW + 3600_000).toISOString(), ...overrides }) as SeriesUpcomingEpisodeItem;

  it("knows which tasks are about your media", () => {
    expect(MEDIA_TASKS).toEqual(["intake.automation", "rss.sync"]);
    expect(isMediaJob({ key: "search:lib1" })).toBe(true);
    expect(isMediaJob({ key: "task:rss.sync" })).toBe(true);
    expect(isMediaJob({ key: "task:intake.automation" })).toBe(true);
    expect(isMediaJob({ key: "task:backup" })).toBe(false);
    expect(isMediaJob({ key: "rss.sync" })).toBe(false);
    expect(isMediaJob({ key: "other:search:1" })).toBe(false);
    expect(isMediaJob({ key: "abcderss.sync" })).toBe(false);
  });

  it("lists a media job with its time and when it last ran", () => {
    rows.current = [row()];
    expect(buildNext(undefined, [], NOW)).toEqual([{ key: "search:lib1", label: "Movies search", at: NOW + 5 * 60_000, last: NOW - 10 * 60_000, running: false, to: "/movies", kind: "job" }]);
  });

  it("leaves out housekeeping, and a job with no readable next time unless it is running", () => {
    rows.current = [row({ key: "task:backup" }), row({ key: "search:b", next: "garbage" }), row({ key: "search:c", next: "garbage", running: true })];
    const next = buildNext(undefined, [], NOW);
    expect(next.map((n) => n.key)).toEqual(["search:c"]);
    expect(next[0].at).toBe(NOW);
  });

  it("puts a running job at now, whatever time it was due", () => {
    rows.current = [row({ running: true, next: new Date(NOW + 99 * 60_000).toISOString() })];
    expect(buildNext(undefined, [], NOW)[0]).toMatchObject({ at: NOW, running: true });
  });

  it("has no last run when the time cannot be read or there is none", () => {
    rows.current = [row({ last: "garbage" }), row({ key: "search:b", last: null })];
    expect(buildNext(undefined, [], NOW).map((n) => n.last)).toEqual([null, null]);
  });

  it("lists an episode that is about to air, with its code and its show, but not one that aired long ago or has no readable time", () => {
    const next = buildNext(undefined, [episode(), episode({ episodeId: "e2", airDateUtc: new Date(NOW - 61_000).toISOString() }), episode({ episodeId: "e3", airDateUtc: "garbage" }), episode({ episodeId: "e4", airDateUtc: new Date(NOW - 60_000).toISOString() })], NOW);
    expect(next.map((n) => n.key)).toEqual(["airs:e4", "airs:e1"]);
    expect(next[1]).toEqual({ key: "airs:e1", label: "Lioness S02E03 airs", at: NOW + 3600_000, last: null, running: false, to: "/tv/s1", kind: "airing" });
  });

  it("pads single-digit season and episode numbers", () => {
    expect(buildNext(undefined, [episode({ seasonNumber: 1, episodeNumber: 9 })], NOW)[0].label).toBe("Lioness S01E09 airs");
  });

  it("puts a running job first, then the soonest, and keeps only as many as asked for", () => {
    rows.current = [row({ key: "search:late", next: new Date(NOW + 60 * 60_000).toISOString() }), row({ key: "search:soon", next: new Date(NOW + 60_000).toISOString() }), row({ key: "search:now", running: true })];
    expect(buildNext(undefined, [episode({ airDateUtc: new Date(NOW + 30_000).toISOString() })], NOW).map((n) => n.key)).toEqual(["search:now", "airs:e1", "search:soon", "search:late"]);
    expect(buildNext(undefined, [], NOW, 2)).toHaveLength(2);
    expect(buildNext(undefined, [], NOW)).toHaveLength(3);
    rows.current = Array.from({ length: 9 }, (_, n) => row({ key: `search:${n}`, next: new Date(NOW + n * 60_000).toISOString() }));
    expect(buildNext(undefined, [], NOW)).toHaveLength(6);
  });
});

describe("when a run is", () => {
  it("is now once it is due", () => {
    expect(whenText(NOW, NOW)).toBe("now");
    expect(whenText(NOW - 5, NOW)).toBe("now");
  });

  it("is a clock time today", () => {
    expect(whenText(local(15, 34), NOW)).toBe("3:34 pm");
    expect(whenText(NOW + 1, NOW)).toBe("12:00 pm");
  });

  it("says tomorrow, then the weekday", () => {
    expect(whenText(local(8, 30, 5), NOW)).toBe("tomorrow 8:30 am");
    expect(whenText(local(8, 30, 5), local(23, 59))).toBe("tomorrow 8:30 am");
    expect(whenText(local(8, 30, 7), NOW)).toBe(`${new Date(local(8, 30, 7)).toLocaleDateString("en-AU", { weekday: "short" })} 8:30 am`);
    expect(whenText(local(8, 30, 6), NOW)).toMatch(/^[A-Z][a-z]{2} 8:30 am$/);
  });
});
