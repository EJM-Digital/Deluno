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
import type { CSSProperties } from "react";
import { Link } from "react-router-dom";
import type { DownloadThroughputSample, SeriesUpcomingEpisodeItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { ago } from "../format";
import type { WorkItem, WorkKind } from "../live-events";
import { BandCard, FigureUnit } from "./band-card";
import { countdown, formatCountdown } from "./countdown";
import { useNow } from "./hooks";
import { IMPORTING_NOW, type JourneyCard } from "./journey";
import { SpeedCell } from "./speed-chart";
import { buildNext, whenText, type NextItem } from "./times";
import { faceOf, type TitleLookup } from "./titles";

const OP_ICON: Record<WorkKind, { icon: LucideIcon; colour: string }> = {
  search: { icon: Search, colour: "var(--stage-search)" },
  grab: { icon: Download, colour: "var(--journey-grab)" },
  import: { icon: FolderInput, colour: "var(--journey-import)" },
  processor: { icon: ShieldCheck, colour: "var(--journey-process)" },
  jobs: { icon: Activity, colour: "var(--info)" }
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
    if (card.stage === "importing" && card.state.label === IMPORTING_NOW) ops.push({ key: `import:${card.key}`, kind: "import", lead: "Importing", name, to: card.to });
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
      aside={more > 0 ? <Link to="/activity" className="focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">{more.toLocaleString("en-AU")} more in Activity</Link> : undefined}
      figure={
        <>
          <span>{operations.length.toLocaleString("en-AU")}</span>
          <FigureUnit>{operations.length === 1 ? "thing running" : "things running"}</FigureUnit>
        </>
      }
    >
      {shown.length > 0 ? (
        <ul className="lb-ops">
          {shown.map((op) => {
            const { icon: Icon, colour } = OP_ICON[op.kind];
            return (
              <li key={op.key}>
                <Link to={op.to} className="lb-op focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" style={{ "--c": colour } as CSSProperties}>
                  <span aria-hidden className="lb-ico"><Icon /></span>
                  <span className="lb-txt">{op.lead}{op.name ? <> <b>{op.name}</b></> : null}</span>
                  <span className="lb-right">{op.right}</span>
                  {op.pct !== undefined ? <span aria-hidden className="lb-bar"><i style={{ width: `${Math.max(0, Math.min(100, op.pct))}%` }} /></span> : null}
                </Link>
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="lb-idle">
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
            <span>{figure.text}</span>
            <FigureUnit>{first.label}</FigureUnit>
          </>
        ) : (
          <FigureUnit>nothing is scheduled</FigureUnit>
        )
      }
    >
      {first ? (
        <div className="min-w-0">
          <span aria-hidden className="lb-next-bar"><i style={{ width: `${Math.round((figure?.kind === "running" || figure?.kind === "due" ? 1 : fraction ?? 0) * 100)}%` }} /></span>
          <ul className="lb-then">
            {rest.slice(0, 3).map((item) => (
              <li key={item.key}>
                <Link to={item.to} className="focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">then {item.label}</Link>
                <span>{whenText(item.at, now)}</span>
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <p className="lb-idle">Searches and tasks that are switched off do not count down.</p>
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
    <div className={cn("lb-now shrink-0", !across && "lb-stacked")}>
      <SpeedCell samples={samples} summary={summary} />
      <WorkingNow summary={summary} operations={operations} />
      <NextCard items={items} now={now} />
    </div>
  );
}
