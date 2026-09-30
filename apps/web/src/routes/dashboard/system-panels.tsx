/**
 * The panels of the Dashboard's Setup health tab (#1087): the problems with
 * their own buttons, this computer's load, the scheduled tasks, automation per
 * library, updates and backups, and storage per drive, under one headline
 * strip (is Deluno ready, which version, tasks, jobs).
 *
 * Where the numbers come from:
 *  - the Dashboard summary (a prop): system, health, automation and storage;
 *  - one stored list read cache-first: the machine's own history (samples the
 *    background sampler already wrote).
 * Nothing here calls a client, indexer, media server or disk while it waits.
 */
import { Link } from "react-router-dom";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable } from "../../components/ui/list-card";
import { MetricChart, type MetricPoint } from "../../components/ui/metric-chart";
import { StatusLed } from "../../components/ui/status-led";
import { SummaryStrip, type SummaryCell } from "../../components/ui/summary-strip";
import { fetchJson, type MachineTelemetryWindow } from "../../lib/api";
import type { DashboardScheduledTask, DashboardSummary } from "../../lib/api/types/operations";
import { defineSources } from "../../lib/route-cache";
import { cn } from "../../lib/utils";
import type { DashboardAttention } from "./attention";
import { ActionButton, FactRow, Panel, PanelLink, PanelNote, ago, formatSize, plainWords, plural, send, until, useAction } from "./downloads-kit";
import { FitList } from "./parts";

const MACHINE_HOURS = 6;

export const setupPanelSources = defineSources({
  machine: {
    key: ["src", "machine-6h"],
    label: "This computer",
    fetch: () => fetchJson<MachineTelemetryWindow>(`/api/monitoring/machine?hours=${MACHINE_HOURS}`)
  }
});

type Action = ReturnType<typeof useAction>;

const STARTUP_PREFIX = "system:background-problem:startup:";

/* --------------------------------------------------------------- strip */

function formatUptime(seconds: number): string {
  if (seconds < 90) return "a moment";
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h`;
  return `${Math.round(hours / 24)} days`;
}

function taskFailed(task: DashboardScheduledTask): boolean {
  return task.lastResult.toLowerCase() === "failed";
}

export function SetupStrip({ summary, problems, jobsGaveUp }: { summary: DashboardSummary | undefined; problems: number; jobsGaveUp: number }) {
  const system = summary?.system;
  const tasks = summary?.automation?.tasks ?? [];
  const failedTasks = tasks.filter(taskFailed).length;
  const nextTask = tasks
    .filter((task) => task.nextRunUtc)
    .sort((a, b) => Date.parse(a.nextRunUtc ?? "") - Date.parse(b.nextRunUtc ?? ""))[0];

  let state: { value: string; tone?: SummaryCell["tone"]; help: string };
  if (!system) state = { value: "Starting", help: "Waiting for Deluno to report in" };
  else if (system.internetOutage) state = { value: "No internet", tone: "warning", help: `Up ${formatUptime(system.uptimeSeconds)}. Searches wait until it is back` };
  else if (problems > 0) state = { value: "Needs a look", tone: "warning", help: `Up ${formatUptime(system.uptimeSeconds)}. ${plural(problems, "thing")} below` };
  else state = { value: "Ready", tone: "success", help: `Up ${formatUptime(system.uptimeSeconds)}` };

  const cells: SummaryCell[] = [
    { label: "Deluno", value: state.value, tone: state.tone, help: state.help, href: "/system" },
    {
      label: "Version",
      value: system?.version ?? "Unknown",
      help: system?.updateAvailable ? "a newer version is ready" : "you are on the latest version",
      tone: system?.updateAvailable ? "warning" : undefined,
      href: "/system/updates"
    },
    {
      label: "Scheduled tasks",
      value: tasks.length.toLocaleString("en-AU"),
      help: nextTask ? `next: ${nextTask.name} ${until(nextTask.nextRunUtc)}` : "nothing scheduled to run soon",
      tone: failedTasks > 0 ? "danger" : undefined,
      href: "/system"
    },
    {
      label: "Jobs",
      value: `${system?.jobs.running ?? 0} running`,
      help: `${system?.jobs.queued ?? 0} waiting`,
      tone: jobsGaveUp > 0 ? "danger" : undefined,
      href: "/activity"
    }
  ];
  return <SummaryStrip cells={cells} />;
}

/* ------------------------------------------------------------ problems */

const linkButton =
  "inline-flex h-[var(--control-height-sm)] items-center rounded-[10px] border border-hairline px-3 text-[length:var(--button-font-size-sm)] font-medium text-foreground hover:bg-secondary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring";

export function Problems({ summary, attention, action }: { summary: DashboardSummary | undefined; attention: DashboardAttention; action: Action }) {
  const health = summary?.health;
  const system = summary?.system;
  // A snoozed problem is not counted, so it is not listed; one that Downloads has its own row for is that row.
  const problems = attention.healthListed;
  const failedLanes = system?.lanes.filter((lane) => lane.state === "failing") ?? [];
  const failedJobs = attention.jobsGaveUp;
  // Health problems the summary did not carry: the list panel says "and N more" for these too.
  const more = attention.items.find((item) => item.id === "health:more")?.count ?? 0;
  // What this panel lists: connections have their own rows in the panels below, so they are not here.
  const ownRows = new Set(["clients", "indexers", "processors"]);
  const others = attention.items.filter((item) => item.tab === "setup" && !ownRows.has(item.id) && !item.id.startsWith("health:") && item.id !== "failed-jobs" && item.id !== "lanes");
  const total = others.reduce((sum, item) => sum + item.count, 0) + problems.length + failedJobs + failedLanes.length + more;

  return (
    <Panel
      title="Problems"
      count={total > 0 ? `${total} open` : undefined}
      to="/system"
      linkLabel="Health"
      className="flex-[13]"
      footer={(health?.unknown ?? 0) > 0 ? `${health?.unknown} not checked yet` : health?.lastRunUtc ? `Last checked ${ago(health.lastRunUtc)}` : "Not checked yet"}
    >
      {total === 0 ? (
        <PanelNote>Nothing is wrong. Deluno checks downloads, indexers, libraries and connections every 15 minutes{health?.lastRunUtc ? `, most recently ${ago(health.lastRunUtc)}` : ""}.</PanelNote>
      ) : (
        <FitList moreTo="/system" moreLabel={(hidden) => `and ${hidden.toLocaleString("en-AU")} more on Health`} extra={more} rows={[
          ...others.map((item) => (
            <li key={item.id} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
              <StatusLed tone={item.tone === "bad" ? "bad" : "warn"} />
              <div className="min-w-0 flex-1 leading-tight">
                <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{item.title}</span>
                <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{item.text}</span>
              </div>
              <Link to={item.to} aria-label={`${item.action}: ${item.title}`} className={linkButton}>{item.action}</Link>
            </li>
          )),
          ...problems.map((problem) => {
            const startup = problem.id.startsWith(STARTUP_PREFIX);
            const key = problem.id.slice("system:background-problem:".length);
            return (
              <li key={problem.id} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
                <StatusLed tone="bad" />
                <div className="min-w-0 flex-1 leading-tight">
                  <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{problem.title}</span>
                  <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{problem.message}</span>
                </div>
                {startup ? (
                  <ActionButton
                    label="Retry"
                    name={`Retry ${problem.title}`}
                    busyKey={`problem-retry:${problem.id}`}
                    state={action}
                    onClick={() =>
                      void action.run(`problem-retry:${problem.id}`, async () => {
                        const result = await send(`/api/system/health/background-problems/${encodeURIComponent(key)}/retry`, "Deluno could not try that again. Check its logs.");
                        return result?.message ?? "Tried again";
                      })
                    }
                  />
                ) : (
                  <Link to={problem.link || "/system"} aria-label={`Fix ${problem.title}`} className={linkButton}>Fix it</Link>
                )}
                <ActionButton
                  label="Snooze"
                  name={`Snooze ${problem.title} for a day`}
                  busyKey={`problem-snooze:${problem.id}`}
                  state={action}
                  variant="ghost"
                  onClick={() =>
                    void action.run(`problem-snooze:${problem.id}`, async () => {
                      await send(`/api/system/health/${encodeURIComponent(problem.id)}/snooze`, "That could not be snoozed.", { body: { hours: 24 } });
                      return `${problem.title} snoozed for a day`;
                    })
                  }
                />
              </li>
            );
          }),
          ...(failedJobs > 0 ? [
            <li key="jobs-gave-up" className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
              <StatusLed tone="bad" />
              <div className="min-w-0 flex-1 leading-tight">
                <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{plural(failedJobs, "job")} gave up</span>
                <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">They failed on every attempt and are waiting for you.</span>
              </div>
              <ActionButton
                label="Retry"
                name="Retry the jobs that gave up"
                busyKey="jobs-retry"
                state={action}
                onClick={() => void action.run("jobs-retry", async () => { await send("/api/jobs/retry-failed", "The failed jobs could not be requeued."); return "Failed jobs requeued"; })}
              />
              <Link to="/activity" aria-label="Review the jobs that gave up" className={linkButton}>Review</Link>
            </li>
          ] : []),
          ...failedLanes.map((lane) => (
            <li key={lane.name} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
              <StatusLed tone="bad" />
              <div className="min-w-0 flex-1 leading-tight">
                <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{plainWords(lane.name, "A worker")} isn{"’"}t running</span>
                <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{lane.lastError ?? "It stopped and hasn’t come back."}</span>
              </div>
              <Link to="/system" aria-label={`Look at ${plainWords(lane.name, "the worker")}`} className={linkButton}>Look</Link>
            </li>
          ))
        ]} />
      )}
    </Panel>
  );
}

/* ---------------------------------------------------------- automation */

export function Automation({ summary }: { summary: DashboardSummary | undefined }) {
  const automation = summary?.automation;
  const gate = automation?.queuePause;
  const cycle = automation?.lastSearchCycle;
  const notes: string[] = [];
  if (automation && !automation.autoStartJobs) notes.push("Automatic jobs are off, so Deluno waits for you to start them.");
  if (gate?.enabled && gate.moviesPaused) notes.push(`Movie searches wait while downloads clear (${gate.moviesWaiting} waiting).`);
  if (gate?.enabled && gate.tvPaused) notes.push(`TV searches wait while downloads clear (${gate.tvWaiting} waiting).`);
  if (gate?.enabled && gate.unreadable) notes.push("Deluno can’t read the download queue, so automatic searches are on hold.");

  return (
    <Panel title="Automation" to="/search-cycles" linkLabel="Automation" className="flex-[10]" footer={cycle ? `Last cycle: ${cycle.libraryName} checked ${cycle.planned}, sent ${cycle.queued}, held back ${cycle.skipped}, ${ago(cycle.completedUtc ?? cycle.startedUtc)}.` : undefined}>
      {!automation || automation.libraries.length === 0 ? (
        <PanelNote>No library is set up yet, so there is nothing to search for. Add a library and Deluno starts looking.</PanelNote>
      ) : (
        <FitList moreTo="/search-cycles" rows={[
          ...automation.libraries.map((library) => {
            const bad = Boolean(library.lastError);
            return (
              <li key={library.libraryId} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-1.5 last:border-b-0">
                <StatusLed tone={bad ? "bad" : library.nextSearchUtc ? "ok" : "idle"} />
                <div className="min-w-0 flex-1 leading-tight">
                  <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{library.name}</span>
                  <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">
                    {bad ? library.lastError : library.lastCompletedUtc ? `Last search ${ago(library.lastCompletedUtc)}` : "Not searched yet"}
                  </span>
                </div>
                <span className="whitespace-nowrap text-[length:var(--type-caption)] text-muted-foreground">
                  {library.nextSearchUtc ? `next ${until(library.nextSearchUtc)}` : plainWords(library.status, "waiting")}
                </span>
              </li>
            );
          }),
          ...notes.map((note) => (
            <li key={note} className="border-b border-hairline px-[var(--card-pad-x)] py-1.5 text-[length:var(--type-caption)] text-warning last:border-b-0">{note}</li>
          ))
        ]} />
      )}
    </Panel>
  );
}

/* ----------------------------------------------------------- machine */

export function Machine({ summary, window }: { summary: DashboardSummary | undefined; window: MachineTelemetryWindow | undefined }) {
  const machine = summary?.system?.machine;
  const samples = window?.samples ?? [];
  const cpu: MetricPoint[] = samples.map((sample) => ({ date: sample.capturedUtc, value: Math.round(sample.cpuPercent) }));
  const memory: MetricPoint[] = samples.map((sample) => ({ date: sample.capturedUtc, value: Math.round(sample.memoryBytes / (1024 * 1024)) }));
  const average = (points: MetricPoint[]) => (points.length ? Math.round(points.reduce((sum, point) => sum + point.value, 0) / points.length) : 0);
  const totalMemory = [...samples].reverse().find((sample) => sample.totalMemoryBytes)?.totalMemoryBytes ?? null;
  const flat = "rounded-none border-0 shadow-none";

  return (
    <ListCard title="This computer" count={`last ${MACHINE_HOURS} hours`} actions={<PanelLink to="/system">Health</PanelLink>} className="shrink-0">
      {machine ? (
        <div className="grid min-h-0 grid-cols-2 gap-x-2">
          <MetricChart
            className={flat}
            label="CPU"
            value={`${Math.round(machine.cpuPercent)}%`}
            help={cpu.length >= 3 ? `busiest ${Math.max(...cpu.map((p) => p.value))}%, average ${average(cpu)}%` : "Deluno’s share of the processor"}
            series={cpu}
            axis="time"
            tone="success"
            formatValue={(value) => `${value}%`}
            emptyLabel="Not enough readings yet to draw a line."
            footer={`${plural(cpu.length, "reading")}`}
          />
          <MetricChart
            className={flat}
            label="Memory"
            value={formatSize(machine.memoryBytes)}
            help={totalMemory ? `of ${formatSize(totalMemory)}, average ${formatSize(average(memory) * 1024 * 1024)}` : `average ${formatSize(average(memory) * 1024 * 1024)}`}
            series={memory}
            axis="time"
            tone="primary"
            formatValue={(value) => `${value} MB`}
            emptyLabel="Not enough readings yet to draw a line."
            footer={`${plural(memory.length, "reading")}`}
          />
        </div>
      ) : (
        <PanelNote>Deluno hasn{"’"}t taken a reading of this computer yet. The first one arrives within a minute of starting.</PanelNote>
      )}
    </ListCard>
  );
}

/* ---------------------------------------------------- scheduled tasks */

function taskState(task: DashboardScheduledTask): { tone: "ok" | "bad" | "info" | "idle"; text: string } {
  const result = task.lastResult.toLowerCase();
  if (result === "failed") return { tone: "bad", text: "Failed last time" };
  if (result === "running") return { tone: "info", text: "Running now" };
  if (result === "not-run" || !task.lastCompletedUtc) return { tone: "idle", text: "Not run yet" };
  return { tone: "ok", text: `Done ${ago(task.lastCompletedUtc)}` };
}

export function ScheduledTasks({ summary }: { summary: DashboardSummary | undefined }) {
  const tasks = summary?.automation?.tasks ?? [];
  const failed = tasks.filter(taskFailed).length;
  return (
    <Panel
      title="Scheduled tasks"
      count={tasks.length > 0 ? (failed > 0 ? `${failed} failed` : "none failed") : undefined}
      to="/system"
      linkLabel="System"
      className="flex-1"
    >
      {tasks.length === 0 ? (
        <PanelNote>No scheduled tasks have been reported yet.</PanelNote>
      ) : (
        <FitList
          moreTo="/system"
          moreLabel={(hidden) => `and ${hidden.toLocaleString("en-AU")} more tasks`}
          wrap={(shown) => (
            <ListTable
              chevron={false}
              columns={[
                { label: "Task", width: "minmax(0,1.6fr)", mobile: true },
                { label: "Last time", width: "minmax(0,1fr)" },
                { label: "Next", width: "6.5rem", align: "end" }
              ]}
            >
              {shown}
            </ListTable>
          )}
          rows={tasks.map((task) => {
            const state = taskState(task);
            return (
              <ListRow key={task.key} className="min-h-[2rem] py-0.5">
                <ListNameCell
                  name={
                    <span className="flex items-center gap-2">
                      <StatusLed tone={state.tone} />
                      <span className="truncate">{task.name}</span>
                    </span>
                  }
                />
                <ListCell primary={state.text} />
                <ListCell align="end" numeric primary={task.nextRunUtc ? until(task.nextRunUtc) : "–"} />
              </ListRow>
            );
          })}
        />
      )}
    </Panel>
  );
}

/* ------------------------------------------------- updates & backups */

export function UpdatesAndBackups({ summary }: { summary: DashboardSummary | undefined }) {
  const system = summary?.system;
  return (
    <ListCard title="Updates and backups" actions={<PanelLink to="/system/backups">Backups</PanelLink>} className="shrink-0">
      <div className="py-1">
        <FactRow label="Updates">
          {system?.updateAvailable ? (
            <Link className="text-primary hover:underline" to="/system/updates">{`${system.latestVersion ?? "A new version"} is ready to install`}</Link>
          ) : (
            "up to date"
          )}
        </FactRow>
        <FactRow label="Last backup">{system?.lastBackupUtc ? ago(system.lastBackupUtc) : "no backup yet"}</FactRow>
        <FactRow label="Automatic backups">
          {system?.backupsEnabled ? (system.nextBackupUtc ? `on, next ${until(system.nextBackupUtc)}` : "on") : (
            <span className="inline-flex items-center gap-2">
              <span>off</span>
              <Link to="/system/backups" aria-label="Turn on automatic backups" className="text-primary hover:underline">Turn on</Link>
            </span>
          )}
        </FactRow>
      </div>
    </ListCard>
  );
}

/* -------------------------------------------------------------- storage */

const ROLE_WORDS: Record<string, string> = { data: "Deluno", library: "Library", downloads: "Downloads", processor: "Processor" };

export function Storage({ summary }: { summary: DashboardSummary | undefined }) {
  const storage = summary?.storage;
  const volumes = storage?.volumes ?? [];
  return (
    <Panel
      title="Storage"
      to="/settings/libraries"
      linkLabel="Libraries"
      className="flex-[9]"
      footer={storage ? (storage.readingUtc ? `Drives read ${ago(storage.readingUtc)}, then about once a minute.` : "Drives haven’t been read yet. The first reading arrives within a minute.") : undefined}
    >
      {volumes.length === 0 ? (
        <PanelNote>No drive has been read yet, so free space is unknown. Add a library or a download client and it shows here.</PanelNote>
      ) : (
        <FitList moreTo="/settings/libraries" moreLabel={(hidden) => `and ${hidden} more drives`} rows={
          volumes.map((volume) => {
            const used = volume.totalBytes && volume.freeBytes !== null ? 1 - volume.freeBytes / volume.totalBytes : null;
            const roles = volume.roles.map((role) => ROLE_WORDS[role] ?? "Other").join(", ");
            return (
              <li key={volume.id} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-1.5 last:border-b-0">
                <div className="min-w-0 flex-[1.2] leading-tight">
                  <span className="block truncate text-[length:var(--type-body-sm)] text-foreground">{volume.label}</span>
                  <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{roles}</span>
                </div>
                <span aria-hidden className="block h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-surface-3">
                  <span className={cn("block h-full rounded-full", used !== null && used > 0.9 ? "bg-warning" : "bg-primary")} style={{ width: `${Math.round((used ?? 0) * 100)}%` }} />
                </span>
                <span className="w-24 whitespace-nowrap text-right text-[length:var(--type-caption)] text-muted-foreground">
                  {!volume.available ? "can’t be read" : volume.freeBytes === null ? "not read yet" : `${formatSize(volume.freeBytes)} free`}
                </span>
              </li>
            );
          })} />
      )}
    </Panel>
  );
}
