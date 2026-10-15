/**
 * The bottom row of System (#1098): Scheduled tasks (the last result with a tick or cross, and a Next countdown, in
 * aligned columns), the Log (what Deluno recorded, newest first, with its level) and Backups and databases.
 *
 * The log is Deluno's own log for today (GET /api/system/log: every warning and error plus the notable information lines,
 * kept current by the pushed SystemLogEntry event); "Full log" opens System, where the log files can be downloaded.
 */
import { useMemo, useState, type JSX } from "react";
import { Link } from "react-router-dom";
import type { BackupItem, BackupSettingsSnapshot, DashboardScheduledTask } from "../../../lib/api";
import type { SystemDatabasesResponse, SystemLogEntry, SystemLogResponse } from "../../../lib/api/types/system-log";
import { FilterChips } from "../../../components/ui/filter-chips";
import { send, useAction } from "../downloads-kit";
import { useNow } from "../live/hooks";
import { countdown, plainCount, sizeText, span, timeOfDayText, whenText } from "./logic";

/* ---------------------------------------------------------- scheduled tasks */

export interface TaskView {
  key: string;
  name: string;
  state: "ok" | "fail" | "run" | "never";
  lastAt: number | null;
  nextAt: number | null;
  /** Why the last run failed, in everyday words; only for a failed task. */
  why: string | null;
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
    nextAt: Number.isFinite(next) ? next : null,
    why: result === "failed" ? (task.lastError?.trim() || "It did not finish. The Log has the details.") : null
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
            <span className="sy-nm" title={row.why ? `${row.name}: ${row.why}` : row.name}>{row.name}{row.why ? <span className="sy-why">{row.why}</span> : null}</span>
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

type Level = "all" | "error" | "warning";

const NO_ENTRIES: readonly SystemLogEntry[] = [];

/** Back-to-back lines with the same level and message become one line with a count, so a refusal repeated ten times
 *  reads once ("×10") instead of filling the card with the same sentence. The newest line's time is kept. */
export function collapseRepeats(rows: readonly SystemLogEntry[]): { row: SystemLogEntry; count: number }[] {
  const lines: { row: SystemLogEntry; count: number }[] = [];
  for (const row of rows) {
    const last = lines[lines.length - 1];
    if (last && last.row.level === row.level && last.row.message === row.message) last.count += 1;
    else lines.push({ row, count: 1 });
  }
  return lines;
}

export function LogCard({ log }: { log: SystemLogResponse | null }): JSX.Element {
  const [level, setLevel] = useState<Level>("all");
  const rows = log?.entries ?? NO_ENTRIES;
  const shown = rows.filter((row) => level === "all" || row.level === level);
  return (
    <section className="sy-card sy-logs" aria-label="Log">
      <div className="sy-card-h">
        <h2>Log</h2>
        <span className="sy-muted sy-num">{log ? `${log.errorsToday} errors · ${log.warningsToday} warnings today` : ""}</span>
        <FilterChips<Level>
          label="Show"
          size="sm"
          className="sy-lv"
          value={level}
          onValueChange={setLevel}
          options={[{ value: "all", label: "All" }, { value: "error", label: "Errors" }, { value: "warning", label: "Warnings" }]}
        />
        <Link className="sy-link" to="/system" aria-label="Open the full log files">Full log →</Link>
      </div>
      <ul className="sy-log sy-scroll">
        {collapseRepeats(shown).map(({ row, count }) => (
          <li key={row.id}>
            <span className="sy-tm">{new Date(row.timeUtc).toLocaleTimeString("en-AU", { hour: "numeric", minute: "2-digit", second: "2-digit" })}</span>
            <span className={`sy-lvl sy-${row.level === "warning" ? "warn" : row.level}`}>{row.level}</span>
            <span className="sy-msg" title={row.source ? `${row.source}: ${row.message}` : row.message}>{row.message}</span>
            {count > 1 ? <span className="sy-muted sy-num" aria-label={`${count} times`}>×{count}</span> : null}
          </li>
        ))}
        {shown.length === 0 ? <li className="sy-muted" style={{ display: "block" }}>{!log ? "The log has not been read yet." : level === "all" ? "Nothing notable has been logged today." : "Nothing at this level."}</li> : null}
      </ul>
    </section>
  );
}

/* ---------------------------------------------------- backups and databases */

/** "integrity checked 4h ago · compacted 3d ago": only what has happened is said. */
export function maintenanceLine(databases: SystemDatabasesResponse, now: number): string {
  const parts: string[] = [];
  if (databases.integrityCheckedUtc) {
    const ago = now - Date.parse(databases.integrityCheckedUtc);
    parts.push(`${databases.integrityOk === false ? "integrity problem found" : "integrity checked"} ${ago < 60_000 ? "just now" : `${span(ago)} ago`}`);
  } else {
    parts.push("integrity not checked yet");
  }
  if (databases.compactedUtc) parts.push(`compacted ${span(now - Date.parse(databases.compactedUtc))} ago`);
  return parts.join(" · ");
}

export function BackupsCard({ backups, settings, databases }: { backups: readonly BackupItem[]; settings: BackupSettingsSnapshot | undefined; databases: SystemDatabasesResponse | null }): JSX.Element {
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
        {databases && databases.databases.length > 0 ? (
          <div className="sy-dbs">
            <div className="sy-bk-h"><span className="sy-label">Databases · {sizeText(databases.totalBytes)}</span><span className={databases.integrityOk === false ? "sy-muted sy-bad" : "sy-muted"} title={databases.integrityNote ?? undefined}>{maintenanceLine(databases, now)}</span></div>
            {databases.databases.map((database) => (
              <div key={database.key} className="sy-db"><span>{database.name}</span><span className="sy-bar"><i style={{ width: `${Math.max(2, (database.sizeBytes / Math.max(1, ...databases.databases.map((item) => item.sizeBytes))) * 100)}%` }} /></span><span className="sy-r sy-num">{sizeText(database.sizeBytes)}</span></div>
            ))}
          </div>
        ) : null}
      </div>
    </section>
  );
}
