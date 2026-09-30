/**
 * The Dashboard's one problem list (#1082). Every row says what is wrong in
 * plain words and carries the button that goes straight to where it is
 * cleared. The other tabs keep to their own area's numbers and never restate
 * this list.
 */
import type { DashboardSummary } from "../../lib/api";
import { plural } from "./format";

export type NeedsYouTone = "warn" | "bad";

export interface NeedsYouRow {
  id: string;
  tone: NeedsYouTone;
  title: string;
  text: string;
  /** Where the fix button goes. */
  to: string;
  /** The fix button's label. */
  action: string;
}

/** Which screen a web path belongs to, in the words the sidebar uses. */
export function screenName(path: string): string {
  if (path.startsWith("/system")) return "System";
  if (path.startsWith("/indexers")) return "Find & Download";
  if (path.startsWith("/queue")) return "Transfers";
  if (path.startsWith("/activity")) return "Activity";
  if (path.startsWith("/movies")) return "Movies";
  if (path.startsWith("/tv")) return "TV Shows";
  if (path.startsWith("/search-cycles") || path.startsWith("/settings/automation")) return "Automation & Recovery";
  if (path.startsWith("/settings")) return "Settings";
  return "Deluno";
}

export interface SetupNeed {
  id: string;
  title: string;
  text: string;
  to: string;
  action: string;
}

/**
 * Everything in the summary that is asking for a person, most urgent first.
 * `setup` is what the setup checklist raises that its own steps do not cover.
 */
export function buildNeedsYou(summary: DashboardSummary | undefined, setup: SetupNeed[] = []): NeedsYouRow[] {
  const rows: NeedsYouRow[] = [];

  for (const item of setup) {
    rows.push({ id: `setup:${item.id}`, tone: "warn", title: item.title, text: item.text, to: item.to, action: item.action });
  }
  if (!summary) return rows;

  if (summary.system?.internetOutage) {
    rows.push({
      id: "outage",
      tone: "bad",
      title: "Deluno can't reach the internet",
      text: "Searching and cleanup are paused until it is back. Nothing was marked as a bad release.",
      to: "/system",
      action: "See System"
    });
  }

  const health = summary.health;
  for (const problem of health?.topProblems ?? []) {
    rows.push({
      id: `health:${problem.id}`,
      tone: "warn",
      title: problem.title,
      text: problem.message,
      to: problem.link || "/system",
      action: `Fix it in ${screenName(problem.link || "/system")}`
    });
  }
  const shown = health?.topProblems.length ?? 0;
  if (health && health.problems > shown) {
    rows.push({
      id: "health:more",
      tone: "warn",
      title: `${plural(health.problems - shown, "more problem")} to look at`,
      text: "The health checks found more than fit here.",
      to: "/system",
      action: "See them in System"
    });
  }

  const downloads = summary.downloads;
  if (downloads && downloads.stalled > 0) {
    rows.push({
      id: "stalled",
      tone: "warn",
      title: `${plural(downloads.stalled, "download is", "downloads are")} stalled`,
      text: "They have stopped moving in the download client.",
      to: "/queue",
      action: "Look in Transfers"
    });
  }
  if (downloads && downloads.failedDownloads > 0) {
    rows.push({
      id: "failed-downloads",
      tone: "bad",
      title: `${plural(downloads.failedDownloads, "download")} failed`,
      text: "Deluno will look for another release when the title is still wanted.",
      to: "/queue",
      action: "Look in Transfers"
    });
  }

  const imports = summary.imports;
  if (imports && imports.recoveryOpen > 0) {
    rows.push({
      id: "recovery",
      tone: "warn",
      title: `${plural(imports.recoveryOpen, "import needs", "imports need")} a look`,
      text: "Deluno could not finish these on its own. Retry them or give up on them.",
      to: "/activity",
      action: "Fix it in Activity"
    });
  }
  if (imports && imports.failedProcessorHandoffs > 0) {
    rows.push({
      id: "handoffs",
      tone: "warn",
      title: `${plural(imports.failedProcessorHandoffs, "hand-off")} to the processor failed`,
      text: "The finished download was not handed over for cleaning.",
      to: "/system",
      action: "Fix it in System"
    });
  }

  const system = summary.system;
  if (system && system.jobs.failed > 0) {
    rows.push({
      id: "failed-jobs",
      tone: "bad",
      title: `${plural(system.jobs.failed, "background job")} failed`,
      text: "Nothing else waits on them, but the work was not done.",
      to: "/activity",
      action: "Fix it in Activity"
    });
  }
  const failingLanes = system?.lanes.filter((lane) => lane.state === "failing").length ?? 0;
  if (failingLanes > 0) {
    rows.push({
      id: "lanes",
      tone: "bad",
      title: `${plural(failingLanes, "background worker")} keeps failing`,
      text: "Deluno keeps trying, but this part is not getting its work done.",
      to: "/system",
      action: "Fix it in System"
    });
  }

  const gate = summary.automation?.queuePause;
  if (gate?.enabled && gate.unreadable) {
    rows.push({
      id: "queue-gate",
      tone: "warn",
      title: "Deluno can't read the download queue",
      text: "Pausing searches when the queue is long is not working until it can.",
      to: "/settings/automation",
      action: "Fix it in Automation & Recovery"
    });
  }

  // Worst first; the order inside a tone is the order they were found.
  return [...rows].sort((left, right) => Number(right.tone === "bad") - Number(left.tone === "bad"));
}

export function overviewTabAttention(summary: DashboardSummary | undefined, setupCount = 0): number {
  return buildNeedsYou(summary).length + setupCount;
}
