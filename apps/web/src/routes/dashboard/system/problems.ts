/**
 * Health on System (#1098): every stored check as a row, the problems first, each with its area, how long it has been
 * wrong and up to two short buttons that say what happens (the full sentence is the hover note). One list for the
 * stored checks and for what the Dashboard counts beyond them (jobs that gave up, workers that stopped, a connection that
 * is down), so the "N need you" on This instance is exactly the number of problem rows here.
 */
import type { DashboardSummary, HealthCheckResultItem } from "../../../lib/api";
import { isHealthCheckSnoozed } from "../../../lib/health-snooze";
import { healthProblemActions, jobsGaveUpActions, screenLinkActions, STARTUP_PROBLEM_PREFIX, type AlertAction } from "../../../lib/alert-actions";
import type { AttentionSummary } from "../../../lib/attention-summary";
import { plainWords } from "../downloads-kit";
import { plural } from "../format";
import { AREAS, areaOf, areaOfAttention, type HealthArea } from "./logic";

export const STARTUP_PREFIX = STARTUP_PROBLEM_PREFIX;

/** A button: it opens a screen (`to`) or sends a request (`post`). Never both. */
export interface ProblemButton {
  label: string;
  /** Said on hover: what pressing it does, and what it leaves alone. */
  note: string;
  to?: string;
  post?: { method?: string; path: string; body?: unknown; failure: string; done: string };
}

/**
 * A button from the one shared definition (lib/alert-actions.ts), so a problem offers the same buttons here as it
 * does in the bell.
 */
export function problemButton(action: AlertAction): ProblemButton {
  return action.to
    ? { label: action.label, note: action.hint, to: action.to }
    : { label: action.label, note: action.hint, post: action.request ? { method: action.request.method, path: action.request.path, body: action.request.body, failure: action.request.failure, done: action.request.done } : undefined };
}

const buttonsFor = (actions: readonly AlertAction[]): ProblemButton[] => actions.map(problemButton);

export type RowTone = "bad" | "warn" | "ok" | "idle";

export interface HealthRow {
  id: string;
  area: HealthArea;
  tone: RowTone;
  title: string;
  why: string;
  /** When the problem began (or the check last ran, for a passing one). */
  atUtc: string | null;
  buttons: ProblemButton[];
  /** A quiet line for a row that is not a live problem: "Snoozed until 5 Oct". */
  note?: string;
}

export interface AreaTally {
  key: HealthArea;
  name: string;
  total: number;
  ok: number;
  tone: "bad" | "warn" | "ok";
}

export interface HealthModel {
  /** Every row, problems first: the ones that need you, then snoozed, then suggestions, unknown, passing. */
  rows: HealthRow[];
  tallies: AreaTally[];
  /** Checks that pass, and checks in all (suggestions are not checks that can fail). */
  pass: number;
  total: number;
  /** How many problems need you now: the rows with a bad or warn tone that are not snoozed. */
  need: number;
  /** Problems the summary counted that the stored checks have not listed yet. */
  more: number;
  /** When the checks were last run. */
  lastRunUtc: string | null;
}

const isSnoozed = (check: HealthCheckResultItem) => isHealthCheckSnoozed(check);

export function buildHealth(args: { summary: DashboardSummary | undefined; attention: AttentionSummary; checks: HealthCheckResultItem[] | undefined }): HealthModel {
  const { summary, attention, checks } = args;
  const system = summary?.system;
  const failedLanes = system?.lanes.filter((lane) => lane.state === "failing") ?? [];
  // Connections have their own rows, with their own buttons, in Connections; here they read as that and are counted once.
  // The server's rows (#1185): the connection rows, the Health problems, the jobs that gave up and the workers have their own rows below.
  const connectionKinds = new Set(["connection", "jobs", "lanes"]);
  const others = attention.rows.filter((item) => item.section === "deluno" && !connectionKinds.has(item.kind) && !item.kind.startsWith("health:"));
  const jobsGaveUp = attention.rows.filter((item) => item.kind === "jobs");
  const links = new Map((summary?.health?.topProblems ?? []).map((problem) => [problem.id, problem.link]));
  const covered = new Set(attention.rows.filter((row) => row.kind === "connection").map((row) => (row.id.startsWith("client:") ? `download-client:${row.id.slice(7)}` : row.id)));

  const all = checks ?? [];
  const real = all.filter((check) => check.status !== "suggestion");
  const open = all.filter((check) => check.status === "problem" && !isSnoozed(check));
  const rows: HealthRow[] = [];

  for (const item of others) {
    rows.push({ id: `item:${item.id}`, area: areaOfAttention(item.id), tone: item.tone === "bad" ? "bad" : "warn", title: item.title, why: item.text, atUtc: null, buttons: buttonsFor(screenLinkActions(item.action, item.to, `open:${item.id}`)) });
  }

  const listed = checks
    ? open.filter((check) => !covered.has(check.id)).map((check) => ({ id: check.id, title: check.name, message: check.message, detail: [check.cause, check.fix].filter(Boolean).join(" "), link: links.get(check.id) ?? "/system", category: check.category as string | undefined, at: check.problemSinceUtc ?? check.lastRunUtc, area: areaOf(check) }))
    : attention.rows.filter((row) => row.kind.startsWith("health:")).map((row) => {
      const problem = row.data as { id: string; category?: string; link?: string };
      return { id: problem.id, title: row.title, message: row.text, detail: "", link: problem.link || "/system", category: problem.category as string | undefined, at: row.sinceUtc, area: "machine" as HealthArea };
    });
  for (const problem of listed) {
    rows.push({
      id: `problem:${problem.id}`, area: problem.area, tone: "bad", title: problem.title, why: problem.detail ? `${problem.message} ${problem.detail}` : problem.message, atUtc: problem.at,
      buttons: buttonsFor(healthProblemActions({ id: problem.id, name: problem.title, category: problem.category, link: problem.link }))
    });
  }

  for (const job of jobsGaveUp) {
    const about = job.id === "failed-title-jobs" ? "title" : "background";
    rows.push({ id: about === "title" ? "title-jobs-gave-up" : "jobs-gave-up", area: "machine", tone: "bad", title: job.title, why: job.text, atUtc: null, buttons: buttonsFor(jobsGaveUpActions(about)) });
  }
  for (const lane of failedLanes) {
    rows.push({ id: `lane:${lane.name}`, area: "machine", tone: "bad", title: `${plainWords(lane.name, "A worker")} isn’t running`, why: lane.lastError ?? "It stopped and hasn’t come back.", atUtc: null, buttons: buttonsFor(screenLinkActions("Look", "/system", `look:${lane.name}`)) });
  }
  for (const check of checks ? open.filter((c) => covered.has(c.id)) : []) {
    rows.push({ id: `check:${check.id}`, area: areaOf(check), tone: "bad", title: check.name, why: `${check.message} Fix it in Connections.`, atUtc: check.problemSinceUtc ?? check.lastRunUtc, buttons: buttonsFor(healthProblemActions({ id: check.id, name: check.name, category: check.category as string, link: "/system" })) });
  }
  // The server lists every open problem, so none is "more" than what is listed.
  const need = rows.length;
  const more = 0;

  // Not live problems: snoozed ones (still clearable), then what is not checked yet, suggestions, and what passes.
  for (const check of all.filter((c) => c.status === "problem" && isSnoozed(c))) {
    rows.push({ id: `snoozed:${check.id}`, area: areaOf(check), tone: "idle", title: check.name, why: check.message, atUtc: check.problemSinceUtc ?? check.lastRunUtc, note: `Snoozed until ${new Date(check.snoozedUntilUtc ?? "").toLocaleDateString("en-AU", { day: "numeric", month: "short" })}`, buttons: [{ label: "Unsnooze", note: "Watches this again; it shows here as a problem if it is still true.", post: { path: `/api/system/health/${encodeURIComponent(check.id)}/unsnooze`, failure: "That could not be unsnoozed.", done: `${check.name} is being watched again` } }] });
  }
  for (const check of all.filter((c) => c.status === "unknown")) rows.push({ id: `unknown:${check.id}`, area: areaOf(check), tone: "idle", title: check.name, why: check.message, atUtc: null, note: "Not checked yet", buttons: [] });
  for (const check of all.filter((c) => c.status === "suggestion")) rows.push({ id: `suggestion:${check.id}`, area: areaOf(check), tone: "idle", title: check.name, why: check.message, atUtc: null, note: "Suggestion", buttons: [] });
  for (const check of all.filter((c) => c.status === "ok")) rows.push({ id: `ok:${check.id}`, area: areaOf(check), tone: "ok", title: check.name, why: check.message, atUtc: check.lastRunUtc, buttons: [] });

  const tallies: AreaTally[] = AREAS.map((area) => {
    const own = real.filter((check) => areaOf(check) === area.key);
    const problems = rows.filter((row) => row.area === area.key && (row.tone === "bad" || row.tone === "warn"));
    return { key: area.key, name: area.name, total: own.length, ok: own.filter((check) => check.status === "ok").length, tone: problems.some((row) => row.tone === "bad") ? "bad" : problems.length > 0 ? "warn" : "ok" };
  });

  return { rows, tallies, pass: real.filter((check) => check.status === "ok").length, total: real.length, need, more, lastRunUtc: summary?.health?.lastRunUtc ?? null };
}
