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
import { JourneyCardView, type CardContext } from "./card";
import { STAGES, type JourneyCard, type Stage } from "./journey";
import { LANES } from "./lanes";
import type { TitleLookup } from "./titles";

/** The one line an empty lane says, and a fuller sentence for its tooltip. */
function emptyNote(stage: Stage, summary: DashboardSummary | undefined): { line: string; full: string } {
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

export function Flow({
  grouped,
  counts,
  summary,
  titles,
  arrivals,
  action,
  hasRequests = false
}: {
  grouped: Record<Stage, JourneyCard[]>;
  counts: Record<Stage, number>;
  summary: DashboardSummary | undefined;
  titles: TitleLookup;
  arrivals: ReadonlySet<string>;
  action: ActionState;
  /** Seerr is set up, so Requested is a stage of the journey even when nothing is waiting in it. */
  hasRequests?: boolean;
}) {
  const inFlight = counts.requested + counts.searching + counts.grabbed + counts.downloading + counts.processor + counts.importing;
  const context: CardContext = { titles, arrivals, action };
  // Lanes with something in them share the width; the empty ones are one short column that names each of them.
  // A stage the summary counts but whose cards are not listed yet is still a lane: it says how many and links to them.
  const present = STAGES.filter((stage) => stage !== "requested" || hasRequests || counts.requested > 0);
  const busy = present.filter((stage) => grouped[stage].length > 0 || counts[stage] > 0);
  const quiet = present.filter((stage) => !busy.includes(stage));
  return (
    <Panel
      title="Every title's journey"
      count={inFlight > 0 ? <CountUp value={inFlight} format={(value) => `${Math.round(value).toLocaleString("en-AU")} in flight`} /> : "nothing in flight"}
      to="/queue"
      toLabel="Transfers"
    >
      <div className="grid min-h-0 flex-1 grid-cols-2 lg:flex">
        {busy.map((stage) => {
          const lane = LANES[stage];
          const cards = grouped[stage];
          const count = counts[stage];
          // A lane is as wide as what is in it: a lane of fifty cards gets most of the room and flows into columns, a lane of one stays narrow.
          const weight = 1 + Math.min(count, 40) / 7;
          return (
            <div
              key={stage}
              id={`live-lane-${stage}`}
              role="group"
              aria-label={lane.label}
              style={{ flexGrow: weight }}
              className="relative flex min-h-0 flex-col border-hairline transition-[flex-grow] duration-500 lg:min-w-[15rem] lg:basis-0 lg:border-l lg:first:border-l-0"
            >
              <div aria-hidden className={cn("h-0.5 w-full", lane.bar, count === 0 && "opacity-30")} />
              <Link to={lane.to} aria-label={`${lane.label}: ${count}`} className="flex items-center gap-1.5 px-2.5 pb-1 pt-2 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
                <lane.icon aria-hidden className={cn("h-3.5 w-3.5 shrink-0", lane.text)} />
                <span className="min-w-0 flex-1 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.06em] text-muted-foreground">{lane.label}</span>
                <span className={cn("text-[length:var(--type-body-sm)] font-semibold tabular-nums", count > 0 ? lane.text : "text-muted-foreground")}>
                  <CountUp value={count} format={(value) => Math.round(value).toLocaleString("en-AU")} />
                </span>
              </Link>
              <FitList
                moreTo={lane.to}
                extra={Math.max(0, count - cards.length)}
                columns
                wrap={(shown) => <ul className="grid grid-cols-[repeat(auto-fill,minmax(15rem,1fr))] items-start gap-1.5 px-1.5">{shown}</ul>}
                rows={cards.map((card) => <JourneyCardView key={card.key} card={card} {...context} />)}
              />
            </div>
          );
        })}
        {quiet.length > 0 ? (
          <div
            id="live-lane-quiet"
            role="group"
            aria-label="Quiet"
            className={cn("relative col-span-2 flex min-h-0 flex-col border-hairline lg:border-l lg:first:border-l-0", busy.length === 0 ? "lg:min-w-0 lg:flex-1" : "lg:w-[12.5rem] lg:shrink-0")}
          >
            <div aria-hidden className="h-0.5 w-full bg-hairline" />
            <p className="px-2.5 pb-1 pt-2 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.06em] text-muted-foreground">Quiet</p>
            <ul className="min-h-0 flex-1 overflow-hidden px-1.5">
              {quiet.map((stage) => {
                const lane = LANES[stage];
                const note = emptyNote(stage, summary);
                return (
                  <li key={stage} id={`live-lane-${stage}`}>
                    <Link
                      to={lane.to}
                      title={note.full}
                      aria-label={`${lane.label}: ${counts[stage]}. ${note.full}`}
                      className="flex items-start gap-2 rounded-lg px-1.5 py-1.5 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
                    >
                      <lane.icon aria-hidden className={cn("mt-0.5 h-3.5 w-3.5 shrink-0 opacity-60", lane.text)} />
                      <span className="min-w-0 flex-1">
                        <span className="block text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">{lane.label}</span>
                        <span className="block text-[length:var(--type-caption)] leading-tight text-muted-foreground">{note.line}</span>
                      </span>
                      <span className="text-[length:var(--type-body-sm)] font-semibold tabular-nums text-muted-foreground">{counts[stage].toLocaleString("en-AU")}</span>
                    </Link>
                  </li>
                );
              })}
            </ul>
          </div>
        ) : null}
      </div>
    </Panel>
  );
}
