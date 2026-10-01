/**
 * What Deluno will do next (#1096): the next search for each library and the next
 * run of each scheduled task, for the ring in the Right now band that counts down
 * and fills between one run and the next. The times come from the summary's
 * automation section; the ring shows the last run against the next. A task
 * that is not scheduled has no row, and a search running shows as running.
 */
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { ago } from "../format";

export interface CountdownRow {
  key: string;
  label: string;
  hint: string;
  to: string;
  next: string;
  last: string | null;
  running: boolean;
  /** Sorted first: the ones the issue names (list sync, health check) and the searches. */
  rank: number;
}

/** The searches and tasks that have a next run, soonest first within their rank. */
export function buildCountdownRows(summary: DashboardSummary | undefined): CountdownRow[] {
  const automation = summary?.automation;
  const rows: CountdownRow[] = [];
  for (const library of automation?.libraries ?? []) {
    if (!library.nextSearchUtc && library.status !== "running") continue;
    rows.push({
      key: `search:${library.libraryId}`,
      label: `Search ${library.name}`,
      hint: library.status === "running" ? "Searching now" : library.lastCompletedUtc ? `last one ${ago(library.lastCompletedUtc)}` : "no search has run yet",
      to: "/search-cycles",
      next: library.nextSearchUtc ?? "",
      last: library.lastCompletedUtc,
      running: library.status === "running",
      rank: 0
    });
  }
  for (const task of automation?.tasks ?? []) {
    if (!task.nextRunUtc) continue;
    const named = task.key === "intake.automation";
    rows.push({
      key: `task:${task.key}`,
      label: task.name,
      hint: task.lastResult === "failed" ? "the last run failed" : task.lastCompletedUtc ? `last ran ${ago(task.lastCompletedUtc)}` : "has not run yet",
      to: "/?tab=system",
      next: task.nextRunUtc,
      last: task.lastCompletedUtc,
      running: false,
      rank: named ? 1 : 2
    });
  }
  return rows.sort((a, b) => a.rank - b.rank || Date.parse(a.next || "9999") - Date.parse(b.next || "9999"));
}
