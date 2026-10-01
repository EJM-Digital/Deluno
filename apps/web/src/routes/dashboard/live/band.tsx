/**
 * The Right now band (#1096): along the top of Live, four cells of what Deluno
 * is doing this second. Speed with its live trace (down and up), what is
 * running now (a search, an import, a download with the processor, with the
 * connection's heartbeat), the next thing it will do with a ring counting down,
 * and the indexers as named lights that flash when they are asked.
 *
 * Every word is real: what is running comes from the events that start and end
 * it and from the summary, the next run from the schedule, the lights from the
 * stored count of each indexer's queries. With nothing running it says so, and
 * says when the last search was.
 */
import { Activity, Download, FolderInput, Search, ShieldCheck, type LucideIcon } from "lucide-react";
import { Link } from "react-router-dom";
import type { DownloadClientItem, DownloadThroughputSample, IndexerItem } from "../../../lib/api";
import type { DashboardLiveExtras, DashboardSummary } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { ago } from "../format";
import type { WorkItem, WorkKind } from "../live-events";
import { ClientLights } from "./clients";
import { buildCountdownRows, ProgressRing } from "./countdowns";
import { countdown } from "./countdown";
import { HeartbeatValue, UpdatedAgo } from "./heartbeat";
import { useNow } from "./hooks";
import { IndexerLights, type IndexerScoreboardToday } from "./indexers";
import { SpeedCell } from "./speed-chart";
import { DriveGauges, ProtectedYou, QualityRing } from "./standing";

const WORK_ICON: Record<WorkKind, LucideIcon> = { search: Search, grab: Download, import: FolderInput, processor: ShieldCheck, jobs: Activity };

function Label({ children, to, name }: { children: string; to?: string; name?: string }) {
  const className = "text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-muted-foreground";
  return to ? (
    <Link to={to} aria-label={name} className={cn(className, "hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring")}>{children}</Link>
  ) : (
    <span className={className}>{children}</span>
  );
}

/** What Deluno is doing this second, and when the last search was when it is doing nothing. */
function DoingNow({ summary, work, updatedAt }: { summary: DashboardSummary | undefined; work: WorkItem[]; updatedAt: number }) {
  const cycle = summary?.automation?.lastSearchCycle;
  const shown = work.slice(0, 3);
  return (
    <section aria-label="Doing now" className="flex min-h-0 min-w-0 flex-col px-[var(--card-pad-x)] py-2.5">
      <div className="flex items-baseline justify-between gap-2">
        <Label>Right now</Label>
        <span className="flex items-center gap-2 whitespace-nowrap text-[length:var(--type-caption)] text-muted-foreground">
          <span className="font-medium"><HeartbeatValue /></span>
          <span><UpdatedAgo updatedAt={updatedAt} /></span>
        </span>
      </div>
      {shown.length > 0 ? (
        <ul className="mt-1.5 space-y-1">
          {shown.map((item) => {
            const Icon = WORK_ICON[item.kind];
            return (
              <li key={item.key}>
                <Link to={item.to} className="flex items-start gap-2 rounded-md text-[length:var(--type-body-sm)] leading-snug text-foreground hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                  <span aria-hidden className="relative mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-md bg-info/15 text-info">
                    <Icon className="h-3.5 w-3.5" />
                  </span>
                  <span className="line-clamp-2 min-w-0">{item.text}</span>
                </Link>
              </li>
            );
          })}
          {work.length > shown.length ? <li className="pl-7 text-[length:var(--type-caption)] text-muted-foreground">and {(work.length - shown.length).toLocaleString("en-AU")} more</li> : null}
        </ul>
      ) : (
        <div className="mt-1.5 text-[length:var(--type-body-sm)] leading-snug">
          <p className="font-medium text-foreground">Nothing is running right now.</p>
          <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">
            {cycle
              ? `The last search was ${cycle.libraryName}, ${ago(cycle.completedUtc ?? cycle.startedUtc)}: ${cycle.planned.toLocaleString("en-AU")} checked, ${cycle.queued.toLocaleString("en-AU")} sent to a client.`
              : "No search has run yet."}
          </p>
        </div>
      )}
    </section>
  );
}

/** The next thing Deluno will do, counting down, and the one after it. */
function NextUp({ summary }: { summary: DashboardSummary | undefined }) {
  const now = useNow(1000);
  const rows = buildCountdownRows(summary)
    .filter((row) => row.next || row.running)
    .sort((a, b) => Number(b.running) - Number(a.running) || Date.parse(a.next || "9999") - Date.parse(b.next || "9999"));
  const first = rows[0];
  const second = rows[1];
  const firstValue = first?.next ? countdown(first.next, first.last, now) : null;
  const secondValue = second?.next ? countdown(second.next, second.last, now) : null;
  return (
    <section aria-label="Next up" className="flex min-h-0 min-w-0 flex-col px-[var(--card-pad-x)] py-2.5">
      <div className="flex items-baseline justify-between gap-2">
        <Label to="/?tab=system" name="Schedule: Next up">Next up</Label>
      </div>
      {first ? (
        <>
          <Link to={first.to} aria-label={`${first.label}: ${first.running ? "running now" : firstValue ? (firstValue.due ? "due now" : `in ${firstValue.text}`) : "not scheduled"}`} className="mt-1.5 flex items-center gap-3 rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
            <span className="shrink-0 [&>svg]:h-11 [&>svg]:w-11"><ProgressRing fraction={firstValue?.fraction ?? null} due={firstValue?.due ?? false} running={first.running} /></span>
            <span className="min-w-0">
              <span className="block truncate text-[length:var(--type-body-sm)] font-medium text-foreground">{first.label}</span>
              <span className="block text-[length:var(--type-card-title)] font-semibold leading-tight tabular-nums text-foreground">
                {first.running ? <span className="text-info">running</span> : firstValue?.due ? <span className="text-success">due now</span> : firstValue?.text ?? "not scheduled"}
              </span>
              <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{first.hint}</span>
            </span>
          </Link>
          {second ? (
            <p className="mt-1 truncate text-[length:var(--type-caption)] text-muted-foreground">
              Then {second.label}: <span className="tabular-nums text-foreground">{second.running ? "running" : secondValue?.due ? "due now" : secondValue?.text ?? "not scheduled"}</span>
            </p>
          ) : null}
        </>
      ) : (
        <p className="mt-1.5 text-[length:var(--type-body-sm)] text-muted-foreground">Nothing is scheduled. Searches and tasks that are switched off do not count down.</p>
      )}
    </section>
  );
}

export function RightNow({
  summary,
  samples,
  indexers,
  scoreboard,
  work,
  updatedAt,
  now,
  clients = [],
  clientSpeeds,
  extras
}: {
  summary: DashboardSummary | undefined;
  samples: DownloadThroughputSample[];
  indexers: IndexerItem[];
  scoreboard?: IndexerScoreboardToday | null;
  work: WorkItem[];
  updatedAt: number;
  now: number;
  /** Every download client, with its on/off switch: a switched-off one is a grey light. */
  clients?: DownloadClientItem[];
  /** What each client is downloading at now, from the readings it pushes. */
  clientSpeeds?: ReadonlyMap<string, number>;
  /** Today's arrivals, refusals and reclaimable space; null from an older server. */
  extras?: DashboardLiveExtras | null;
}) {
  const peak = samples.reduce((top, sample) => Math.max(top, sample.speedMbps ?? 0), summary?.downloads?.downloadMbps ?? 0);
  return (
    <div className="shrink-0 overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]">
      <div className="grid sm:grid-cols-2 lg:grid-cols-[1.25fr_1.1fr_1fr_1.5fr] lg:divide-x lg:divide-hairline">
        <SpeedCell samples={samples} summary={summary} />
        <DoingNow summary={summary} work={work} updatedAt={updatedAt} />
        <NextUp summary={summary} />
        <div className="flex min-w-0 flex-col">
          <IndexerLights indexers={indexers} scoreboard={scoreboard} summary={summary} now={now} />
          <ClientLights rows={summary?.downloads?.clients ?? []} all={clients} speeds={clientSpeeds ?? NO_SPEEDS} scaleMbps={peak} />
        </div>
      </div>
      <div className="grid border-t border-hairline sm:grid-cols-2 lg:grid-cols-[1.25fr_1.1fr_2.5fr] lg:divide-x lg:divide-hairline">
        <QualityRing summary={summary} />
        <ProtectedYou extras={extras} blocklistCount={summary?.blocklist?.count} />
        <DriveGauges summary={summary} extras={extras} />
      </div>
    </div>
  );
}

const NO_SPEEDS: ReadonlyMap<string, number> = new Map();
