/**
 * The twelve fact tiles of This instance (#1098): label, value and a plain second line, from the overview's `instance`
 * reading. Every tile is a real reading; one that cannot be read is left out, and without an overview at all the few the
 * Dashboard summary can answer stand in.
 */
import type { DashboardSummary } from "../../../lib/api";
import type { SystemOverviewInstance } from "../../../lib/api/types/system-overview";
import { ago } from "../format";
import { plainCount, sinceText, sizeText, splitAddress } from "./logic";
import { plural } from "../../../lib/plural";

export interface Fact {
  label: string;
  value: string;
  sub: string;
  tone?: "good" | "bad";
}

const RUNS_AS = { service: "Service", app: "App", docker: "Docker" } as const;

export function instanceFacts(instance: SystemOverviewInstance | null | undefined, summary: DashboardSummary | undefined, now: number): Fact[] {
  const system = summary?.system;
  if (!instance) {
    // Without the overview: what the summary already knows.
    const facts: Fact[] = [];
    if (system) {
      facts.push({ label: "Version", value: system.version, sub: system.updateAvailable ? "a newer version is ready" : "latest version", tone: system.updateAvailable ? undefined : "good" });
      if (system.machine) facts.push({ label: "Usage", value: `${Math.round(system.machine.cpuPercent)}% CPU`, sub: `${sizeText(system.machine.memoryBytes)} RAM` });
      facts.push({ label: "Workers", value: `${system.jobs.running} busy`, sub: system.jobs.queued > 0 ? `${system.jobs.queued} waiting` : "none waiting" });
      facts.push({ label: "Last backup", value: system.lastBackupUtc ? ago(system.lastBackupUtc, now) : "None yet", sub: system.backupsEnabled ? "automatic backups on" : "automatic backups off" });
    }
    return facts;
  }
  const i = instance;
  const { host, port } = splitAddress(i.address);
  const facts: Fact[] = [
    { label: "Version", value: i.version, sub: i.latest ? "latest version" : "a newer version is ready", tone: i.latest ? "good" : undefined },
    { label: "Usage", value: `${Math.round(i.cpuPercent)}% CPU`, sub: `${sizeText(i.memoryBytes)} RAM` },
    { label: "Answers in", value: `${Math.round(i.answerMs)} ms`, sub: `slowest ${Math.round(i.answerSlowestMs)} ms` },
    { label: "Workers", value: `${i.workersBusy} busy`, sub: i.oldestWaitingSeconds !== null && i.oldestWaitingSeconds > 0 ? `oldest waiting ${Math.round(i.oldestWaitingSeconds)} s` : "none waiting" },
    { label: "Jobs today", value: `${plainCount(i.jobsToday)} run`, sub: i.jobsFailedToday > 0 ? `${plainCount(i.jobsFailedToday)} failed` : "none failed", tone: i.jobsFailedToday > 0 ? "bad" : "good" },
    { label: "Requests", value: plainCount(i.requestsToday), sub: i.requestErrorsToday > 0 ? plural(i.requestErrorsToday, "error") : "no errors", tone: i.requestErrorsToday > 0 ? "bad" : "good" },
    { label: "Address", value: host || "–", sub: port ? `port ${port}` : "" },
    { label: "Runs as", value: RUNS_AS[i.runsAs] ?? "–", sub: sinceText(i.startedUtc, now) ? `since ${sinceText(i.startedUtc, now)}` : "" },
    { label: "Data", value: sizeText(i.dataBytes) || "–", sub: plural(i.databaseCount, "database") },
    { label: "Last backup", value: i.lastBackupUtc ? ago(i.lastBackupUtc, now) : "None yet", sub: i.lastBackupBytes !== null ? sizeText(i.lastBackupBytes) : i.lastBackupUtc ? "" : "back up below" },
    { label: "Restarts", value: i.restartsThisWeek > 0 ? plainCount(i.restartsThisWeek) : "None", sub: "this week", tone: i.restartsThisWeek > 0 ? "bad" : "good" },
    { label: "Browsers", value: `${i.browsersLive} live`, sub: "live now" }
  ];
  return facts;
}
