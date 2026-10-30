/**
 * The journey (#1098): the titles that are on their way to the library, as stations
 * left to right (requested, grabbed, downloading, with the processor when one is
 * connected, importing). It is the approved mockup's journey ported rule for rule (see
 * journey.css): the stations are one arrow-shaped ribbon, each segment tinted in its
 * stage colour with its icon, name and count (busy ones stronger with a moving sheen,
 * empty ones dimmed); under it equal columns with dashed dividers hold every title as
 * one card of one size, placed in its station's column. There are always three rows of
 * cards: on a short window the cards shrink (down to 60px, a one-line title and a
 * smaller poster) and on a tall one they grow, rather than losing a row. A card whose
 * stage changes glides to its new column (a CSS transition on left and top), and a new
 * one fades in.
 *
 * A station with more titles than the three rows shows three and ends with "and N more"
 * under In progress; every title of it is in that link's tooltip. On a phone the stations
 * stack and the same cards flow one under another.
 */
import type { CSSProperties, ReactElement } from "react";
import { useLayoutEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { cn } from "../../../lib/utils";
import { ago } from "../format";
import { LiveCard as Panel } from "./live-card";
import { Count, JourneyToken, type CardContext } from "./card";
import { fitDetails } from "./detail-fit";
import { cardsBudget, journeyMode, journeySize, JOURNEY_ROWS, MORE_PX } from "./layout";
import { useElementSize, useRemPx } from "./hooks";
import type { JourneyCard, Stage } from "./journey";
import { LANES } from "./lanes";
import { useStillWhileResizing } from "./motion";
import { faceOf } from "./titles";
import { journeyHeading } from "./times";
import "./journey.css";

/** The stations a title passes through, in order. The processor's is left out when none is connected. */
export function stationsFor(hasProcessor: boolean): Stage[] {
  return hasProcessor ? ["requested", "grabbed", "downloading", "processor", "importing"] : ["requested", "grabbed", "downloading", "importing"];
}

/** The space under the lanes inside the card, in px. */
const LANES_BOTTOM_PX = 10;

/** One segment of the ribbon: the stage's icon, its name and how many titles are there. */
function Station({ stage, count, usual }: { stage: Stage; count: number; usual?: string }) {
  const lane = LANES[stage];
  return (
    <div role="group" aria-label={lane.label} className={cn("lj-station", count > 0 ? "lj-active" : "lj-empty")} style={{ ["--lj-st" as string]: `hsl(var(${lane.accent}))` } satisfies CSSProperties}>
      <Link to={lane.to} aria-label={`${lane.label}: ${count}`} title={usual ? `${lane.label}: ${count} · ${usual}` : undefined} className="lj-station-link">
        <span aria-hidden className="lj-dot"><lane.icon /></span>
        <span className="lj-text">
          <span className="lj-nm">{lane.label}</span>
          <span className="lj-ct">{count === 0 ? "none" : <><Count value={count} /> {stage === "downloading" ? "moving" : "here"}</>}</span>
        </span>
      </Link>
    </div>
  );
}

/** One calm, true line for a track with nothing on it. */
function calmLine(lastArrival: { title: string; at: string } | null): string {
  return lastArrival ? `Nothing moving right now · last arrival ${lastArrival.title} ${ago(lastArrival.at)}` : "Nothing moving right now";
}

export function Journey({
  grouped,
  counts,
  context,
  hasProcessor,
  usual,
  by,
  lastArrival,
  budget,
  fill = false
}: {
  grouped: Record<Stage, JourneyCard[]>;
  counts: Record<Stage, number>;
  context: CardContext;
  /** A processor is connected, so its station is on the track. */
  hasProcessor: boolean;
  /** "usually 12 min" for the stations that have at least three finished journeys to say it from today. */
  usual: Partial<Record<Stage, string>>;
  /** When every title on its way should be in. */
  by: number | undefined;
  /** The newest title in the library, for the line that says nothing is moving. */
  lastArrival: { title: string; at: string } | null;
  /** The journey fills its grid cell (the shared page grid): its lanes take the height left under the stations and the rows that fit are counted from it. */
  fill?: boolean;
  /** Without a grid cell (the page scrolls): the tallest the whole journey may be, in pixels. It is only a ceiling. */
  budget?: number;
}): ReactElement {
  const stations = useMemo(() => stationsFor(hasProcessor), [hasProcessor]);
  const n = stations.length;
  useStillWhileResizing();
  const [bodyRef, body] = useElementSize<HTMLDivElement>();
  const [lanesRef, lanesBox] = useElementSize<HTMLDivElement>();
  const rem = useRemPx();
  const stacked = journeyMode(body.width, n, rem) === "stacked";
  const onTheirWay = stations.reduce((sum, stage) => sum + counts[stage], 0);
  const boxRef = useRef<HTMLDivElement>(null);
  // What sits above the lanes (header, stations): measured, so a page that scrolls can count the rows its budget holds in real pixels.
  const [chrome, setChrome] = useState(0);
  useLayoutEffect(() => {
    const box = boxRef.current;
    const lanes = lanesRef.current;
    if (!box || !lanes || fill) return;
    const next = Math.round(lanes.getBoundingClientRect().top - box.getBoundingClientRect().top);
    if (next !== chrome) setChrome(next);
  });
  const budgeted = cardsBudget(budget, chrome + LANES_BOTTOM_PX);
  const room = stacked ? undefined : fill ? (lanesBox.height > 0 ? lanesBox.height : undefined) : budgeted;
  const size = journeySize(room);
  const rows = JOURNEY_ROWS;
  const lanesHeight = fill ? undefined : room !== undefined ? room : rows * size.step - (size.step - size.card) + MORE_PX;

  const places = new Map<string, { column: number; top: number; hidden: boolean }>();
  const more: { stage: Stage; column: number; left: number; titles: string }[] = [];
  stations.forEach((stage, column) => {
    const here = grouped[stage];
    here.forEach((card, row) => places.set(card.key, { column, top: Math.min(row, rows - 1) * size.step, hidden: row >= rows }));
    if (here.length > rows) more.push({ stage, column, left: here.length - rows, titles: here.map((card) => faceOf(card, context.titles).title).join(", ") });
  });
  // One stable order for every card, so a card that changes station keeps its element and glides instead of being re-made.
  const everyCard = stations.flatMap((stage) => grouped[stage]).sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0));

  // The detail block of each card shows as many whole lines as the card's height holds: measured after every change, and again once the fonts are in.
  useLayoutEffect(() => {
    const host = lanesRef.current ?? bodyRef.current;
    if (!host) return undefined;
    fitDetails(host);
    let live = true;
    const again = () => {
      if (live) fitDetails(host);
    };
    void document.fonts?.ready.then(again);
    // A weight that loads later than the first paint moves the lines the room is counted from.
    document.fonts?.addEventListener?.("loadingdone", again);
    return () => {
      live = false;
      document.fonts?.removeEventListener?.("loadingdone", again);
    };
  });

  return (
    <div ref={boxRef} className={cn("flex min-h-0 min-w-0 flex-col", fill && "h-full")} style={fill || budget === undefined ? undefined : { maxHeight: budget }}>
      <Panel title="Pipeline" count={journeyHeading(onTheirWay, by)} to="/queue" toLabel="In progress" className="min-h-0 flex-1">
        <div ref={bodyRef} className={cn("lj relative flex min-h-0 flex-1 flex-col", stacked && "lj-stack overflow-y-auto")} style={{ ["--lj-n" as string]: n, ["--lj-card-h" as string]: `${size.card}px`, ["--lj-step" as string]: `${size.step}px`, ["--lj-poster-h" as string]: `${size.poster}px`, ["--lj-title-lines" as string]: size.oneLine ? 1 : 2 } satisfies CSSProperties}>
          {stacked ? (
            <>
              {onTheirWay === 0 ? <p className="lj-calm" style={{ position: "static" }}>{calmLine(lastArrival)}</p> : null}
              {stations.map((stage) => {
                if (grouped[stage].length === 0 && counts[stage] === 0) return null;
                return (
                  <div key={`stacked-${stage}`} data-station={stage}>
                    <Station stage={stage} count={counts[stage]} usual={usual[stage]} />
                    {grouped[stage].map((card) => <JourneyToken key={card.key} card={card} {...context} />)}
                  </div>
                );
              })}
            </>
          ) : (
            <>
              <div className="lj-stations">
                {stations.map((stage) => <Station key={stage} stage={stage} count={counts[stage]} usual={usual[stage]} />)}
              </div>
              <div ref={lanesRef} className="lj-lanes" style={{ height: lanesHeight, flex: lanesHeight === undefined ? undefined : "none", marginBottom: LANES_BOTTOM_PX }}>
                {stations.slice(1).map((stage, index) => <div key={`bg-${stage}`} aria-hidden className="lj-lane-bg" style={{ left: `${((index + 1) * 100) / n}%` }} />)}
                {onTheirWay === 0 ? <p className="lj-calm">{calmLine(lastArrival)}</p> : null}
                {everyCard.map((card) => {
                  const place = places.get(card.key);
                  return <JourneyToken key={card.key} card={card} {...context} place={place ? { column: place.column, stations: n, top: place.top, hidden: place.hidden } : undefined} />;
                })}
                {more.map((line) => (
                  <Link key={`more-${line.stage}`} to="/queue" title={`${LANES[line.stage].label}: ${line.titles}`} className="lj-more" style={{ left: `calc(${line.column} * 100% / ${n} + 8px)`, top: rows * size.step }}>
                    and {line.left.toLocaleString("en-AU")} more →
                  </Link>
                ))}
              </div>
            </>
          )}
        </div>
      </Panel>
    </div>
  );
}
