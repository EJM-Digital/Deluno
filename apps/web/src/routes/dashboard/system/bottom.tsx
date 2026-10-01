/**
 * The bottom row of System (#1098): Scheduled tasks (the last result with a tick or cross, and a Next countdown, in
 * aligned columns), the Log (what Deluno recorded, newest first, with its level) and Backups and databases.
 *
 * The log is the Activity record: Deluno has no line-by-line log feed to read, so this shows Activity events with their
 * level (the Errors and Warnings filters pick those out); "Full log" opens Activity, where the log files can be downloaded.
 */
import { useMemo, useState, type JSX } from "react";
import { Link } from "react-router-dom";
import type { BackupItem, BackupSettingsSnapshot, DashboardScheduledTask } from "../../../lib/api";
import type { SystemOverviewInstance } from "../../../lib/api/types/system-overview";
import { send, useAction } from "../downloads-kit";
import { useLiveActivity, type LiveActivityRow } from "../live-events";
import { useNow } from "../live/hooks";
import { countdown, plainCount, sizeText, span, timeOfDayText, whenText } from "./logic";

/* ---------------------------------------------------------- scheduled tasks */

export interface TaskView {
  key: string;
  name: string;
  state: "ok" | "fail" | "run" | "never";
  lastAt: number | null;
  nextAt: number | null;
}

function viewOfTask(task: DashboardScheduledTask): TaskView {
  const result = task.lastResult.toLowerCase();
  const last = task.lastCompletedUtc ? Date.parse(task.lastCompletedUtc) : NaN;
  const next = task.nextRunUtc ? Date.parse(task.nextRunUtc) : NaN;
  return {
    key: task.key,
    name: task.name,
    state: result === "failed" ? "fail" : result === "running" ? "run" : result === "not-run" || !Number.isFinite(last) ? "never" : "ok",
    lastAt: Number.isFinite(last) ? last : null,
    nextAt: Number.isFinite(next) ? next : null
  };
}

/** Running first, then the one due soonest; ones with no next time last. */
export function sortedTasks(tasks: readonly DashboardScheduledTask[]): TaskView[] {
  return tasks.map(viewOfTask).sort((a, b) => Number(b.state === "run") - Number(a.state === "run") || (a.nextAt ?? Infinity) - (b.nextAt ?? Infinity));
}

export function TasksCard({ tasks }: { tasks: readonly DashboardScheduledTask[] }): JSX.Element {
  const now = useNow(1000);
  const rows = useMemo(() => sortedTasks(tasks), [tasks]);
  const running = rows.filter((row) => row.state === "run").length;
  const failed = rows.filter((row) => row.state === "fail").length;
  return (
    <section className="sy-card sy-tasks" aria-label="Scheduled tasks">
      <div className="sy-card-h">
        <h2>Scheduled tasks</h2>
        <span className="sy-muted sy-num">{rows.length > 0 ? `${running > 0 ? `${running} running · ` : ""}${failed} failed` : ""}</span>
        <Link className="sy-link" to="/search-cycles" aria-label="Open automation">Automation →</Link>
      </div>
      <div className="sy-tk-head"><span /><span>Task</span><span>Last result</span><span>Next</span></div>
      <div className="sy-tk-list sy-scroll">
        {rows.length === 0 ? <div className="sy-empty sy-muted">No scheduled tasks have been reported yet.</div> : null}
        {rows.map((row) => (
          <div key={row.key} className={`sy-tk-row${row.state === "run" ? " sy-run" : row.state === "fail" ? " sy-fail" : ""}`}>
            <span className="sy-d" />
            <span className="sy-nm" title={row.name}>{row.name}</span>
            <span className="sy-r">
              {row.state === "run" ? <b style={{ color: "var(--sy-info)" }}>running</b>
                : row.state === "never" ? "—"
                : <><b style={{ color: row.state === "fail" ? "var(--sy-danger)" : "var(--sy-success)" }}>{row.state === "fail" ? "✗" : "✓"}</b> {span(now - (row.lastAt ?? now))} ago</>}
            </span>
            <span className="sy-r sy-num">{row.state === "run" || row.nextAt === null ? "—" : <b>{row.nextAt - now < 3_600_000 ? countdown(row.nextAt - now) : span(row.nextAt - now)}</b>}</span>
          </div>
        ))}
      </div>
    </section>
  );
}

/* -------------------------------------------------------------------- log */

type Level = "all" | "error" | "warn";
type ActivityInput = Parameters<typeof useLiveActivity>[0];

function levelOf(row: LiveActivityRow): "error" | "warn" | "info" {
  const severity = (row.severity ?? "").toLowerCase();
  return severity === "error" ? "error" : severity === "warning" ? "warn" : "info";
}

export function LogCard({ activity }: { activity: ActivityInput }): JSX.Element {
  const rows = useLiveActivity(activity);
  const [level, setLevel] = useState<Level>("all");
  const shown = rows.filter((row) => level === "all" || levelOf(row) === level);
  const midnight = new Date().setHours(0, 0, 0, 0);
  const today = rows.filter((row) => Date.parse(row.createdUtc) >= midnight);
  const errors = today.filter((row) => levelOf(row) === "error").length;
  const warnings = today.filter((row) => levelOf(row) === "warn").length;
  return (
    <section className="sy-card sy-logs" aria-label="Log">
      <div className="sy-card-h">
        <h2>Log</h2>
        <span className="sy-muted sy-num">{rows.length > 0 ? `${errors} errors · ${warnings} warnings today` : ""}</span>
        <span className="sy-lv" role="group" aria-label="Show">
          {([["all", "All"], ["error", "Errors"], ["warn", "Warnings"]] as const).map(([key, label]) => (
            <button key={key} type="button" className={level === key ? "sy-on" : undefined} aria-pressed={level === key} onClick={() => setLevel(key)}>{label}</button>
          ))}
        </span>
        <Link className="sy-link" to="/activity" aria-label="Open the full log in Activity">Full log →</Link>
      </div>
      <ul className="sy-log sy-scroll">
        {shown.map((row) => {
          const lvl = levelOf(row);
          return (
            <li key={row.id} className={row.fresh ? "sy-new" : undefined}>
              <span className="sy-tm">{new Date(row.createdUtc).toLocaleTimeString("en-AU", { hour: "numeric", minute: "2-digit", second: "2-digit" })}</span>
              <span className={`sy-lvl sy-${lvl}`}>{lvl === "warn" ? "warning" : lvl}</span>
              <span className="sy-msg" title={row.label ?? row.message}>{row.message}</span>
            </li>
          );
        })}
        {shown.length === 0 ? <li className="sy-muted" style={{ display: "block" }}>{level === "all" ? "Nothing has been logged yet." : "Nothing at this level."}</li> : null}
      </ul>
    </section>
  );
}

/* ---------------------------------------------------- backups and databases */

export function BackupsCard({ backups, settings, instance }: { backups: readonly BackupItem[]; settings: BackupSettingsSnapshot | undefined; instance: SystemOverviewInstance | null }): JSX.Element {
  const action = useAction();
  const now = useNow(60_000);
  const latest = useMemo(() => [...backups].sort((a, b) => Date.parse(b.createdUtc) - Date.parse(a.createdUtc)).slice(0, 3), [backups]);
  const schedule = settings
    ? settings.enabled ? [`${settings.frequency.toLowerCase()} at ${timeOfDayText(settings.timeOfDay)}`, `keeping ${settings.retentionCount}`, settings.backupFolder].filter(Boolean).join(" · ") : "automatic backups are off"
    : "";
  const next = settings?.enabled && settings.nextRunUtc ? `next in ${span(Date.parse(settings.nextRunUtc) - now)}` : "";
  return (
    <section className="sy-card sy-upd" aria-label="Backups and databases">
      <div className="sy-card-h">
        <h2>Backups and databases</h2>
        <Link className="sy-link" to="/system/backups" aria-label="Open backups">Backups →</Link>
      </div>
      <div className="sy-upd-body sy-scroll">
        <div className="sy-bk-h"><span className="sy-label">Backups</span><span className="sy-muted" title={schedule}>{schedule}</span></div>
        <div className="sy-bk-list">
          {latest.length === 0 ? <div className="sy-muted" style={{ fontSize: 12 }}>No backup has been made yet.</div> : null}
          {latest.map((backup) => (
            <div key={backup.id} className="sy-bk-row"><span className="sy-d" /><span>{whenText(backup.createdUtc, now)}</span><span className="sy-r">{sizeText(backup.sizeBytes)}</span><span className="sy-r">complete</span></div>
          ))}
        </div>
        <div className="sy-bk-btns">
          <button type="button" className="sy-btn" aria-disabled={action.busy === "backup"} title="Makes a fresh backup of Deluno’s data straight away. Nothing is changed." onClick={() => void action.run("backup", async () => { await send("/api/backups", "The backup did not go through.", { body: { reason: "manual" } }); return "Backup created"; })}>
            {action.busy === "backup" ? "Backing up…" : "Back up now"}
          </button>
          <Link className="sy-btn" to="/system/backups" title="Opens Backups, where a backup is checked before it replaces anything.">Restore…</Link>
          <span className="sy-muted sy-num">{next}</span>
        </div>
        {instance ? (
          <div className="sy-dbs">
            <div className="sy-bk-h"><span className="sy-label">Databases · {sizeText(instance.dataBytes)}</span><span className="sy-muted">{plainCount(instance.databaseCount)} {instance.databaseCount === 1 ? "database" : "databases"}</span></div>
          </div>
        ) : null}
      </div>
    </section>
  );
}
