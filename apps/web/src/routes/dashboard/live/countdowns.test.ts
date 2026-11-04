import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { buildCountdownRows } from "./countdowns";
import type { DashboardSummary } from "../../../lib/api/types/operations";

/** The rows of "what Deluno will do next" (#1096): each library's next search and each scheduled task's next run, in order. */
const NOW = "2026-10-01T12:00:00Z";
const summary = (automation: unknown) => ({ automation }) as unknown as DashboardSummary;
const library = (over: object) => ({ libraryId: "l1", name: "Movies", status: "idle", nextSearchUtc: "2026-10-01T13:00:00Z", lastCompletedUtc: "2026-10-01T11:00:00Z", ...over });
const task = (over: object) => ({ key: "t", name: "Task", nextRunUtc: "2026-10-01T14:00:00Z", lastCompletedUtc: "2026-10-01T11:55:00Z", lastResult: "ok", ...over });

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date(NOW));
});
afterEach(() => vi.useRealTimers());

describe("the next-run rows", () => {
  it("has none without a summary or without automation", () => {
    expect(buildCountdownRows(undefined)).toEqual([]);
    expect(buildCountdownRows(summary(undefined))).toEqual([]);
    expect(buildCountdownRows(summary({}))).toEqual([]);
  });

  it("shows a library's next search with when the last one finished", () => {
    expect(buildCountdownRows(summary({ libraries: [library({})] }))).toEqual([
      { key: "search:l1", label: "Search Movies", hint: "last one 1 h ago", to: "/search-cycles", next: "2026-10-01T13:00:00Z", last: "2026-10-01T11:00:00Z", running: false, rank: 0 }
    ]);
  });

  it("says no search has run yet when none has finished", () => {
    const [row] = buildCountdownRows(summary({ libraries: [library({ lastCompletedUtc: null })] }));
    expect(row.hint).toBe("no search has run yet");
    expect(row.last).toBeNull();
  });

  it("shows a search that is running now even with no next time", () => {
    const [row] = buildCountdownRows(summary({ libraries: [library({ status: "running", nextSearchUtc: null })] }));
    expect(row).toMatchObject({ hint: "Searching now", next: "", running: true });
  });

  it("leaves out a library that is idle with no next search", () => {
    expect(buildCountdownRows(summary({ libraries: [library({ nextSearchUtc: null })] }))).toEqual([]);
  });

  it("shows a scheduled task with its last run, a failure, or that it has not run", () => {
    const rows = buildCountdownRows(summary({ tasks: [
      task({ key: "a", name: "A" }),
      task({ key: "b", name: "B", lastResult: "failed" }),
      task({ key: "c", name: "C", lastCompletedUtc: null })
    ] }));
    expect(rows.map((row) => row.hint)).toEqual(["last ran 5 min ago", "the last run failed", "has not run yet"]);
    expect(rows[0]).toMatchObject({ key: "task:a", label: "A", to: "/?tab=system", running: false, rank: 2, last: "2026-10-01T11:55:00Z", next: "2026-10-01T14:00:00Z" });
  });

  it("leaves out a task that is not scheduled", () => {
    expect(buildCountdownRows(summary({ tasks: [task({ nextRunUtc: null })] }))).toEqual([]);
  });

  it("puts searches first, then the intake task, then the rest soonest first", () => {
    const rows = buildCountdownRows(summary({
      libraries: [library({ libraryId: "late", nextSearchUtc: "2026-10-01T20:00:00Z" }), library({ libraryId: "soon", nextSearchUtc: "2026-10-01T13:00:00Z" })],
      tasks: [task({ key: "z", nextRunUtc: "2026-10-01T12:30:00Z" }), task({ key: "y", nextRunUtc: "2026-10-01T12:10:00Z" }), task({ key: "intake.automation", nextRunUtc: "2026-10-02T00:00:00Z" })]
    }));
    expect(rows.map((row) => row.key)).toEqual(["search:soon", "search:late", "task:intake.automation", "task:y", "task:z"]);
  });

  it("puts a running search with no next time after the timed ones, wherever it is listed", () => {
    const run = (id: string) => library({ libraryId: id, status: "running", nextSearchUtc: null });
    const timed = (id: string, at: string) => library({ libraryId: id, nextSearchUtc: at });
    const a = buildCountdownRows(summary({ libraries: [run("r1"), timed("t1", "2026-10-01T13:00:00Z"), run("r2"), timed("t2", "2026-10-01T15:00:00Z")] }));
    const b = buildCountdownRows(summary({ libraries: [timed("t2", "2026-10-01T15:00:00Z"), run("r1"), timed("t1", "2026-10-01T13:00:00Z"), run("r2")] }));
    for (const rows of [a, b]) expect(rows.map((row) => row.key.slice(7)).slice(0, 2)).toEqual(["t1", "t2"]);
  });
});
