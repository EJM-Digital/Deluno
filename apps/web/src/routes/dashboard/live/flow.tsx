/**
 * The journey of the titles that are moving (#1096): a track of stages, left to
 * right, as a title makes its way in: requested, grabbed, downloading, with the
 * processor, importing, and then the end of it, Arrived today.
 *
 * Only titles that are really moving are here. Each is a small poster token with
 * its state beside it (a download shows its bar and speed, which follow the
 * readings the clients push), and it glides into the next stage with a brief glow
 * when a pushed event moves it. A stage with nothing in it is not drawn. Problems
 * are not tokens: a download that stopped, an import that did not finish or a
 * refused hand-off is in the Needs you strip, once. When nothing is moving the
 * track says so in one calm line and the arrivals have the room.
 *
 * Tokens keep to whole rows with "and N more" (FitList), so nothing is cut in
 * half.
 */
import { ChevronRight } from "lucide-react";
import { Link } from "react-router-dom";
import { CountUp } from "../../../components/ui/count-up";
import { cn } from "../../../lib/utils";
import { ago } from "../format";
import { FitList, Panel } from "../parts";
import { ArrivalsBody, nameOf } from "./arrivals";
import { JourneyToken, type CardContext } from "./card";
import type { ArrivalItem } from "./extras";
import type { JourneyCard, Stage } from "./journey";
import { LANES } from "./lanes";
import type { TitleLookup } from "./titles";

/** The stages a title moves through, in order. Needs you and Searching are not stages of a title: problems are in their own strip, and a library search is said in Right now. */
export const MOVING: Stage[] = ["requested", "grabbed", "downloading", "processor", "importing"];

function StageView({ stage, cards, count, context }: { stage: Stage; cards: JourneyCard[]; count: number; context: Omit<CardContext, "action"> }) {
  const lane = LANES[stage];
  return (
    <div id={`live-lane-${stage}`} role="group" aria-label={lane.label} className="relative flex min-h-0 min-w-0 flex-1 flex-col">
      <div aria-hidden className={cn("h-0.5 w-full", lane.bar)} />
      <Link to={lane.to} aria-label={`${lane.label}: ${count}`} className="flex items-center gap-1.5 px-2.5 pb-1 pt-2 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
        <lane.icon aria-hidden className={cn("h-3.5 w-3.5 shrink-0", lane.text)} />
        <span className="min-w-0 flex-1 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.06em] text-muted-foreground">{lane.label}</span>
        <span className={cn("text-[length:var(--type-body-sm)] font-semibold tabular-nums", lane.text)}>
          <CountUp value={count} format={(value) => Math.round(value).toLocaleString("en-AU")} />
        </span>
      </Link>
      <FitList
        moreTo={lane.to}
        extra={Math.max(0, count - cards.length)}
        columns
        wrap={(shown) => <ul className="grid grid-cols-[repeat(auto-fill,minmax(10.5rem,1fr))] items-start gap-1.5 px-1.5">{shown}</ul>}
        rows={cards.map((card) => <JourneyToken key={card.key} card={card} {...context} />)}
      />
    </div>
  );
}

/** One calm, true line for a track with nothing on it: when the last title arrived. */
function calmLine(arrivalItems: ArrivalItem[], titles: TitleLookup): string {
  const last = arrivalItems[0];
  if (!last) return "Nothing moving right now";
  return `Nothing moving right now · last arrival ${nameOf(last, titles).title} ${ago(last.at)}`;
}

export function Flow({
  grouped,
  counts,
  titles,
  arrivals,
  arrivalItems = [],
  freshArrivals = new Set<string>()
}: {
  grouped: Record<Stage, JourneyCard[]>;
  counts: Record<Stage, number>;
  titles: TitleLookup;
  /** The cards that have just changed stage, which glide in with a glow. */
  arrivals: ReadonlySet<string>;
  /** Today's imports, newest first: the poster wall at the end of the journey. */
  arrivalItems?: ArrivalItem[];
  /** The arrivals that have just landed, which turn over into place. */
  freshArrivals?: ReadonlySet<string>;
}) {
  const context = { titles, arrivals };
  const moving = MOVING.filter((stage) => grouped[stage].length > 0 || counts[stage] > 0);
  const inFlight = moving.reduce((sum, stage) => sum + counts[stage], 0);
  return (
    <Panel
      title="Every title's journey"
      count={inFlight > 0 ? <CountUp value={inFlight} format={(value) => `${Math.round(value).toLocaleString("en-AU")} moving`} /> : "nothing moving"}
      to="/queue"
      toLabel="Transfers"
    >
      {moving.length === 0 ? <p className="shrink-0 border-b border-hairline px-[var(--card-pad-x)] py-2 text-[length:var(--type-body-sm)] text-muted-foreground">{calmLine(arrivalItems, titles)}</p> : null}
      <div className="flex min-h-0 flex-1">
        {moving.length > 0 ? (
          <div className="flex min-h-0 min-w-0 flex-[3.2]">
            {moving.map((stage, index) => (
              <div
                key={stage}
                style={{ flexGrow: 1 + Math.min(counts[stage], 24) / 6 }}
                className={cn("relative flex min-h-0 min-w-0 basis-0 transition-[flex-grow] duration-500 lg:min-w-[11.5rem]", index > 0 && "border-l border-hairline")}
              >
                {index > 0 ? <ChevronRight aria-hidden className="absolute -left-2 top-2.5 z-10 h-4 w-4 rounded-full bg-card text-muted-foreground" /> : null}
                <StageView stage={stage} cards={grouped[stage]} count={counts[stage]} context={context} />
              </div>
            ))}
          </div>
        ) : null}
        <div
          id="live-lane-imported"
          role="group"
          aria-label="In the library"
          className={cn("relative flex min-h-0 flex-col border-hairline", moving.length > 0 ? "flex-1 border-l lg:min-w-[12.5rem]" : "flex-1")}
        >
          <div aria-hidden className={cn("h-0.5 w-full", LANES.imported.bar)} />
          <Link to={LANES.imported.to} aria-label={`Arrived today: ${counts.imported}`} className="flex items-center gap-1.5 px-2.5 pb-1 pt-2 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
            <LANES.imported.icon aria-hidden className={cn("h-3.5 w-3.5 shrink-0", LANES.imported.text)} />
            <span className="min-w-0 flex-1 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.06em] text-muted-foreground">Arrived today</span>
            <span className={cn("text-[length:var(--type-body-sm)] font-semibold tabular-nums", counts.imported > 0 ? LANES.imported.text : "text-muted-foreground")}>
              <CountUp value={counts.imported} format={(value) => Math.round(value).toLocaleString("en-AU")} />
            </span>
          </Link>
          {counts.imported === 0 && arrivalItems.length === 0 ? (
            <p className="px-2.5 py-1 text-[length:var(--type-body-sm)] text-muted-foreground">Nothing has arrived yet today.</p>
          ) : (
            <ArrivalsBody items={arrivalItems} titles={titles} fresh={freshArrivals} count={counts.imported} wide={moving.length === 0} />
          )}
        </div>
      </div>
    </Panel>
  );
}
