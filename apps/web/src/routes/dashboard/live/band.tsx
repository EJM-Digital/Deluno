/**
 * The top band of Live (#1098): three cards of one template, Speed, Working on now
 * and Next. Speed is in `speed-chart.tsx`. Working on now says what Deluno is doing
 * this second: the count, and the two newest operations with a bar where it knows
 * how far along they are. Next is the one list of what comes next, scheduled jobs
 * and episodes about to air together, with a countdown to the first and a clock
 * time for the ones beyond the hour.
 *
 * Every word is real: what is running comes from the events that start and end it,
 * from the journey cards and from the summary; what is next from the schedule and
 * the airing episodes. With nothing running it says so, and says when the last
 * search was.
 */
import { Activity, Download, FolderInput, Search, ShieldCheck, type LucideIcon } from "lucide-react";
import { Link } from "react-router-dom";
import type { DownloadThroughputSample, SeriesUpcomingEpisodeItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { ago } from "../format";
import type { WorkItem, WorkKind } from "../live-events";
import { BandCard, FigureUnit } from "./band-card";
import { countdown, formatCountdown } from "./countdown";
import { useNow } from "./hooks";
import type { JourneyCard } from "./journey";
import { SpeedCell } from "./speed-chart";
import { buildNext, whenText, type NextItem } from "./times";
import { faceOf, type TitleLookup } from "./titles";

const OP_ICON: Record<WorkKind, { icon: LucideIcon; tone: string; bar: string }> = {
  search: { icon: Search, tone: "text-[hsl(var(--stage-search))] bg-[hsl(var(--stage-search)/0.15)]", bar: "bg-[hsl(var(--stage-search))]" },
  grab: { icon: Download, tone: "text-[hsl(var(--stage-grab))] bg-[hsl(var(--stage-grab)/0.15)]", bar: "bg-[hsl(var(--stage-grab))]" },
  import: { icon: FolderInput, tone: "text-[hsl(var(--stage-import))] bg-[hsl(var(--stage-import)/0.15)]", bar: "bg-[hsl(var(--stage-import))]" },
  processor: { icon: ShieldCheck, tone: "text-[hsl(var(--stage-process))] bg-[hsl(var(--stage-process)/0.15)]", bar: "bg-[hsl(var(--stage-process))]" },
  jobs: { icon: Activity, tone: "text-info bg-info/15", bar: "bg-info" }
};

/** One thing Deluno is doing now: what it is, which title when it is one, and how far along when that is known. */
export interface Operation {
  key: string;
  kind: WorkKind;
  lead: string;
  name?: string;
  right?: string;
  /** 0 to 100, or nothing when it does not know how far along it is. */
  pct?: number;
  to: string;
}

/**
 * What is running, newest first within each kind: searches, grabs on their way to a client, titles with the processor,
 * titles being imported, and background jobs. The processor is named for whichever one has the title.
 */
export function buildOperations(work: readonly WorkItem[], cards: readonly JourneyCard[], titles: TitleLookup): Operation[] {
  const ops: Operation[] = [];
  for (const item of work) {
    if (item.kind === "search") ops.push({ key: item.key, kind: "search", lead: item.text, right: item.detail, pct: item.progress, to: item.to });
  }
  for (const item of work) {
    if (item.kind === "grab") ops.push({ key: item.key, kind: "grab", lead: item.text, to: item.to });
  }
  for (const card of cards) {
    const name = faceOf(card, titles).title;
    if (card.stage === "processor" && card.state.tone !== "bad") ops.push({ key: `process:${card.key}`, kind: "processor", lead: `${card.processor ?? "The processor"} is working on`, name, to: card.to });
    if (card.stage === "importing" && card.state.tone !== "bad") ops.push({ key: `import:${card.key}`, kind: "import", lead: "Importing", name, to: card.to });
  }
  for (const item of work) {
    if (item.kind === "jobs") ops.push({ key: item.key, kind: "jobs", lead: item.text, to: item.to });
  }
  return ops;
}

function WorkingNow({ summary, operations }: { summary: DashboardSummary | undefined; operations: Operation[] }) {
  const cycle = summary?.automation?.lastSearchCycle;
  const shown = operations.slice(0, 2);
  const more = operations.length - shown.length;
  return (
    <BandCard
      label="Working on now"
      aside={more > 0 ? <Link to="/activity" className="text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">{more.toLocaleString("en-AU")} more in Activity</Link> : undefined}
      figure={
        <>
          <span>{operations.length.toLocaleString("en-AU")}</span>
          <FigureUnit>{operations.length === 1 ? "thing running" : "things running"}</FigureUnit>
        </>
      }
    >
      {shown.length > 0 ? (
        <ul className="min-w-0">
          {shown.map((op) => {
            const { icon: Icon, tone, bar } = OP_ICON[op.kind];
            return (
              <li key={op.key} className="border-b border-hairline py-[7px] last:border-b-0">
                <Link to={op.to} className="grid grid-cols-[1.375rem_minmax(0,1fr)_auto] items-center gap-x-2.5 rounded-md text-[length:var(--type-body-sm)] leading-snug text-foreground hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                  <span aria-hidden className={cn("flex h-[1.375rem] w-[1.375rem] items-center justify-center rounded-md", tone)}><Icon className="h-3.5 w-3.5" /></span>
                  <span className="truncate">{op.lead}{op.name ? <> <strong className="font-semibold">{op.name}</strong></> : null}</span>
                  <span className="text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{op.right}</span>
                  {op.pct !== undefined ? (
                    <span aria-hidden className="col-span-2 col-start-2 mt-0.5 block h-1 overflow-hidden rounded-full bg-surface-3">
                      <span className={cn("live-fill block h-full rounded-full", bar)} style={{ width: `${Math.round(op.pct)}%` }} />
                    </span>
                  ) : null}
                </Link>
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="text-[length:var(--type-body-sm)] leading-snug text-muted-foreground">
          {cycle
            ? `Nothing is running right now. The last search was ${cycle.libraryName}, ${ago(cycle.completedUtc ?? cycle.startedUtc)}: ${cycle.planned.toLocaleString("en-AU")} checked, ${cycle.queued.toLocaleString("en-AU")} sent to a client.`
            : "Nothing is running right now. No search has run yet."}
        </p>
      )}
    </BandCard>
  );
}

/** What the big figure of Next says: a countdown under the hour, a clock time beyond it, "running" while it runs. */
export function nextFigure(item: NextItem, now: number): { text: string; kind: "running" | "due" | "count" | "clock" } {
  if (item.running) return { text: "running", kind: "running" };
  const remaining = item.at - now;
  if (remaining <= 0) return { text: "due now", kind: "due" };
  if (remaining < 3_600_000) return { text: formatCountdown(remaining), kind: "count" };
  return { text: whenText(item.at, now), kind: "clock" };
}

function NextCard({ items, now }: { items: NextItem[]; now: number }) {
  const first = items[0];
  const rest = items.slice(1);
  const figure = first ? nextFigure(first, now) : null;
  const fraction = first && !first.running && first.last !== null ? countdown(new Date(first.at).toISOString(), new Date(first.last).toISOString(), now)?.fraction ?? null : null;
  return (
    <BandCard
      label="Next"
      aside="on its own"
      figure={
        first && figure ? (
          <>
            <span className={cn(figure.kind === "running" && "text-info", figure.kind === "due" && "text-success")}>{figure.text}</span>
            <FigureUnit>{first.label}</FigureUnit>
          </>
        ) : (
          <FigureUnit>nothing is scheduled</FigureUnit>
        )
      }
    >
      {first ? (
        <div className="min-w-0">
          <span aria-hidden className="mb-1.5 block h-1 overflow-hidden rounded-full bg-surface-3">
            <span className="live-fill block h-full rounded-full bg-primary" style={{ width: `${Math.round((figure?.kind === "running" || figure?.kind === "due" ? 1 : fraction ?? 0) * 100)}%` }} />
          </span>
          <ul className="grid gap-0.5 text-[length:var(--type-caption)] text-muted-foreground">
            {rest.slice(0, 3).map((item) => (
              <li key={item.key} className="flex min-w-0 items-baseline justify-between gap-3">
                <Link to={item.to} className="min-w-0 truncate hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">then {item.label}</Link>
                <span className="shrink-0 tabular-nums text-foreground">{whenText(item.at, now)}</span>
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <p className="text-[length:var(--type-body-sm)] leading-snug text-muted-foreground">Searches and tasks that are switched off do not count down.</p>
      )}
    </BandCard>
  );
}

export function Band({ summary, samples, operations, upcoming, across = true }: {
  /** Three cards across; false stacks them one under another. */
  across?: boolean;
  summary: DashboardSummary | undefined;
  samples: DownloadThroughputSample[];
  operations: Operation[];
  upcoming: readonly SeriesUpcomingEpisodeItem[];
}) {
  const now = useNow(1000);
  const items = buildNext(summary, upcoming, now);
  return (
    <div className={"grid shrink-0 gap-[var(--grid-gap)] " + (across ? "grid-cols-3" : "grid-cols-1")}>
      <SpeedCell samples={samples} summary={summary} />
      <WorkingNow summary={summary} operations={operations} />
      <NextCard items={items} now={now} />
    </div>
  );
}
