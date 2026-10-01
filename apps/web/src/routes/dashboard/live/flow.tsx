/**
 * The journey of every title in flight (#1096), as one living board: searching,
 * grabbed, downloading, with the processor, importing, in the library.
 * Each stage is a lane with its own accent and a count that ticks; each title is
 * a card with its poster, its name, its details and its route (indexer, client,
 * processor), and a card glides into the next lane (with a brief glow) when its
 * stage changes. A lane with nothing in it shrinks to its header and one line
 * of what is true, so the lanes with cards are wide enough to read.
 *
 * The cards are built by `journey.ts` from the stored lists and the pushed
 * download readings, and named by the library (`titles.ts`); this only draws
 * them. Lanes keep to whole cards with "and N more" (FitList), so nothing is cut
 * in half. The download bar only shimmers while bytes are really moving.
 */
import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { CountUp } from "../../../components/ui/count-up";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { type ActionState } from "../downloads-kit";
import { ago } from "../format";
import { FitList, Panel } from "../parts";
import { ArrivalsBody } from "./arrivals";
import { JourneyCardView, type CardContext } from "./card";
import type { ArrivalItem } from "./extras";
import { useElementHeight } from "./hooks";
import { STAGES, type JourneyCard, type Stage } from "./journey";
import { LANES } from "./lanes";
import type { TitleLookup } from "./titles";

/** The one line an empty lane says, and a fuller sentence for its tooltip. */
function emptyNote(stage: Stage, summary: DashboardSummary | undefined, count = 0): { line: string; full: string } {
  // A stage that has cards but is shown as a line (the board is too short for its lane) says how many.
  if (count > 0) return { line: `${count.toLocaleString("en-AU")} here, open to see them`, full: `${count.toLocaleString("en-AU")} in this stage. The board is too short here to list them; open Transfers.` };
  const imported = summary?.pipeline?.today.imported ?? 0;
  switch (stage) {
    case "requested": return { line: "Nothing waiting", full: "No request is waiting to be searched for." };
    case "searching": {
      const cycle = summary?.automation?.lastSearchCycle;
      if (!cycle) return { line: "None yet", full: "No search has run yet." };
      const when = ago(cycle.completedUtc ?? cycle.startedUtc);
      const found = cycle.queued > 0 ? `${cycle.queued.toLocaleString("en-AU")} sent to a client` : "nothing new";
      return { line: `${cycle.libraryName}, ${when}, ${found}`, full: `No search running. The last was ${cycle.libraryName}, ${when}: ${cycle.planned.toLocaleString("en-AU")} checked, ${found}.` };
    }
    case "grabbed": return { line: "Nothing waiting", full: "Nothing waiting to start." };
    case "downloading": return { line: "Nothing moving", full: `Nothing downloading. ${imported > 0 ? `${imported.toLocaleString("en-AU")} imported today.` : "Nothing imported yet today."}` };
    case "needs": return { line: "Nothing needs you", full: "Nothing needs you: no download is stuck and no import or hand-off has failed." };
    case "processor": return { line: "Nothing with it", full: "Nothing with the processor." };
    case "importing": return { line: "Nothing waiting", full: "Nothing waiting to import." };
    default: return imported > 0 ? { line: `${imported.toLocaleString("en-AU")} today`, full: `${imported.toLocaleString("en-AU")} imported today.` } : { line: "None today", full: "Nothing imported yet today." };
  }
}

/**
 * The lanes sit in columns. The stages before the download (requested, searching,
 * grabbed) share one column, stacked, and so do the stages after it (with the
 * processor, importing): each is short-lived, and side by side they would not
 * leave a card room to be read. A stage with nothing in it is not a lane: it is a
 * line in the quiet column.
 */
const COLUMNS: Stage[][] = [["needs"], ["requested", "searching", "grabbed"], ["downloading"], ["processor", "importing"], ["imported"]];

function LaneView({
  stage,
  cards,
  count,
  context,
  arrivalItems,
  freshArrivals,
  stacked
}: {
  stage: Stage;
  cards: JourneyCard[];
  count: number;
  context: CardContext;
  arrivalItems: ArrivalItem[];
  freshArrivals: ReadonlySet<string>;
  stacked: boolean;
}) {
  const lane = LANES[stage];
  return (
    <div
      id={`live-lane-${stage}`}
      role="group"
      aria-label={lane.label}
      // Stacked lanes share the column's height by the cards they hold (and a little for the header), so each has room for all of its own.
      style={stacked ? { flexGrow: Math.min(Math.max(cards.length, count), 8) + 0.45 } : undefined}
      className={cn("relative flex min-h-0 flex-col", stacked ? "basis-0 border-t border-hairline first:border-t-0" : "flex-1")}
    >
      <div aria-hidden className={cn("h-0.5 w-full", lane.bar, count === 0 && "opacity-30")} />
      <Link to={lane.to} aria-label={`${lane.label}: ${count}`} className="flex items-center gap-1.5 px-2.5 pb-1 pt-2 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
        <lane.icon aria-hidden className={cn("h-3.5 w-3.5 shrink-0", lane.text)} />
        <span className="min-w-0 flex-1 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.06em] text-muted-foreground">{lane.label}</span>
        <span className={cn("text-[length:var(--type-body-sm)] font-semibold tabular-nums", count > 0 ? lane.text : "text-muted-foreground")}>
          <CountUp value={count} format={(value) => Math.round(value).toLocaleString("en-AU")} />
        </span>
      </Link>
      {stage === "imported" ? (
        <ArrivalsBody items={arrivalItems} titles={context.titles} fresh={freshArrivals} count={count} />
      ) : (
        <FitList
          moreTo={lane.to}
          extra={Math.max(0, count - cards.length)}
          columns
          wrap={(shown) => <ul className={cn("grid items-start gap-1.5 px-1.5", hasButtons(cards) || stage === "needs" ? "grid-cols-[repeat(auto-fill,minmax(15rem,1fr))]" : "grid-cols-[repeat(auto-fill,minmax(10.5rem,1fr))]")}>{shown}</ul>}
          rows={cards.map((card) => <JourneyCardView key={card.key} card={card} {...context} />)}
        />
      )}
    </div>
  );
}

function QuietLine({ stage, count, summary, ...rest }: { stage: Stage; count: number; summary: DashboardSummary | undefined; "data-fit-row"?: string }) {
  const lane = LANES[stage];
  const note = emptyNote(stage, summary, count);
  return (
    <li {...rest} id={`live-lane-${stage}`}>
      <Link
        to={lane.to}
        title={note.full}
        aria-label={`${lane.label}: ${count}. ${note.full}`}
        className="flex items-start gap-2 rounded-lg px-1.5 py-1.5 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <lane.icon aria-hidden className={cn("mt-0.5 h-3.5 w-3.5 shrink-0 opacity-60", lane.text)} />
        <span className="min-w-0 flex-1">
          <span className="block text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">{lane.label}</span>
          <span className="block text-[length:var(--type-caption)] leading-tight text-muted-foreground">{note.line}</span>
        </span>
        <span className="text-[length:var(--type-body-sm)] font-semibold tabular-nums text-muted-foreground">{count.toLocaleString("en-AU")}</span>
      </Link>
    </li>
  );
}

/** Whether any card in the lane carries buttons: only then does the column need the width for two of them side by side. */
function hasButtons(cards: JourneyCard[]): boolean {
  return cards.some((card) => card.failedHandoffId || card.importJobId || card.recovery || (card.canSearchAgain && card.queueRef));
}

export function Flow({
  grouped,
  counts,
  summary,
  titles,
  arrivals,
  action,
  hasRequests = false,
  arrivalItems = [],
  freshArrivals = new Set<string>()
}: {
  grouped: Record<Stage, JourneyCard[]>;
  counts: Record<Stage, number>;
  summary: DashboardSummary | undefined;
  titles: TitleLookup;
  arrivals: ReadonlySet<string>;
  action: ActionState;
  /** Seerr is set up, so Requested is a stage of the journey even when nothing is waiting in it. */
  hasRequests?: boolean;
  /** Today's imports, newest first: the poster wall at the end of the journey. */
  arrivalItems?: ArrivalItem[];
  /** The arrivals that have just landed, which turn over into place. */
  freshArrivals?: ReadonlySet<string>;
}) {
  const inFlight = counts.requested + counts.searching + counts.grabbed + counts.downloading + counts.processor + counts.importing;
  const context: CardContext = { titles, arrivals, action };
  // Lanes with something in them are lanes; the empty ones are one short column that names each of them.
  // A stage the summary counts but whose cards are not listed yet is still a lane: it says how many and links to them.
  const present = STAGES.filter((stage) => stage !== "requested" || hasRequests || counts.requested > 0);
  const [boardRef, boardHeight] = useElementHeight<HTMLDivElement>();
  // A board too short to give each stacked lane a card (a fresh install with its setup banners above) shows the stacked stages as lines.
  const short = boardHeight > 0 && boardHeight < 460;
  const stacked = new Set(COLUMNS.filter((stages) => stages.length > 1).flat());
  const hasCards = present.filter((stage) => grouped[stage].length > 0 || counts[stage] > 0);
  const busy = hasCards.filter((stage) => !(short && stacked.has(stage)));
  const quiet = present.filter((stage) => !busy.includes(stage));
  const columns = COLUMNS.map((stages) => stages.filter((stage) => busy.includes(stage))).filter((stages) => stages.length > 0);
  return (
    <Panel
      title="Every title's journey"
      count={inFlight > 0 ? <CountUp value={inFlight} format={(value) => `${Math.round(value).toLocaleString("en-AU")} in flight`} /> : "nothing in flight"}
      to="/queue"
      toLabel="Transfers"
    >
      <div ref={boardRef} className="grid min-h-0 flex-1 grid-cols-2 lg:flex">
        {columns.map((stages) => {
          // A column is as wide as what is in it: fifty cards get most of the room and flow into two columns of cards, one card stays narrow.
          const total = stages.reduce((sum, stage) => sum + counts[stage], 0);
          const weight = stages[0] === "imported" ? 1.2 : stages[0] === "needs" ? 1 + Math.min(total, 40) / 3 : 1 + Math.min(total, 40) / 7;
          const wide = stages.some((stage) => hasButtons(grouped[stage]) || (stage === "needs" && counts.needs > 0));
          return (
            <div
              key={stages.join("-")}
              style={{ flexGrow: weight }}
              className={cn("relative flex min-h-0 flex-col border-hairline transition-[flex-grow] duration-500 lg:basis-0 lg:border-l lg:first:border-l-0", wide ? "lg:min-w-[15rem]" : "lg:min-w-[11.5rem]")}
            >
              {stages.map((stage) => (
                <LaneView key={stage} stage={stage} cards={grouped[stage]} count={counts[stage]} context={context} arrivalItems={arrivalItems} freshArrivals={freshArrivals} stacked={stages.length > 1} />
              ))}
            </div>
          );
        })}
        {quiet.length > 0 ? (
          <div
            id="live-lane-quiet"
            role="group"
            aria-label="Quiet"
            className={cn("relative col-span-2 flex min-h-0 flex-col border-hairline lg:border-l lg:first:border-l-0", busy.length === 0 ? "lg:min-w-0 lg:flex-1" : "lg:w-[10.5rem] lg:shrink-0")}
          >
            <div aria-hidden className="h-0.5 w-full bg-hairline" />
            <p className="px-2.5 pb-1 pt-2 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.06em] text-muted-foreground">Quiet</p>
            <FitList
              moreTo="/queue"
              wrap={(shown) => <ul className="px-1.5">{shown}</ul>}
              rows={quiet.map((stage) => <QuietLine key={stage} stage={stage} count={counts[stage]} summary={summary} />)}
            />
          </div>
        ) : null}
      </div>
    </Panel>
  );
}
