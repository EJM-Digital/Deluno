/**
 * The journey (#1098): the titles that are on their way to the library, as stations
 * left to right (requested, grabbed, downloading, with the processor when one is
 * connected, importing) with a dashed rail running between them while anything is
 * moving. Each title is a card in its station; a card glides to the next station
 * when its stage changes (see `useGlide`).
 *
 * The space decides everything. Stations sit side by side when each has room and
 * stack on a phone. A station draws full cards while they fit its height; with more
 * titles than that its cards become the same card in miniature (a list), so every title is on
 * screen, and if even the tiles run past the height the station scrolls inside
 * itself. Nothing says "and N more".
 */
import type { CSSProperties, ReactElement } from "react";
import { useMemo } from "react";
import { Link } from "react-router-dom";
import { cn } from "../../../lib/utils";
import { ago } from "../format";
import { LiveCard as Panel } from "./live-card";
import { CARD_HEIGHT, Count, CompactTile, JourneyCardView, type CardContext } from "./card";
import { boardLayouts, CARD_GAP_REM, cardsBudget, journeyMode, stationWidths } from "./layout";
import { useElementSize, useRemPx } from "./hooks";
import type { JourneyCard, Stage } from "./journey";
import { LANES } from "./lanes";
import { journeyHeading } from "./times";

/** The stations a title passes through, in order. The processor's is left out when none is connected. */
export function stationsFor(hasProcessor: boolean): Stage[] {
  return hasProcessor ? ["requested", "grabbed", "downloading", "processor", "importing"] : ["requested", "grabbed", "downloading", "importing"];
}

const SCROLLER = "overscroll-contain [scrollbar-width:thin]";

function Station({ stage, count, usual, quiet }: { stage: Stage; count: number; usual?: string; quiet: boolean }) {
  const lane = LANES[stage];
  return (
    <div role="group" aria-label={lane.label} className={cn("relative z-[1] flex min-w-0 items-center gap-2 px-2 pb-2 pt-1.5", quiet && "opacity-50")}>
      <Link
        to={lane.to}
        aria-label={`${lane.label}: ${count}`}
        className="flex min-w-0 items-center gap-2 rounded-md bg-card pr-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <span
          aria-hidden
          className={cn("flex h-[1.875rem] w-[1.875rem] shrink-0 items-center justify-center rounded-full border-2 bg-surface-2 transition-shadow duration-300", lane.text)}
          style={{ borderColor: `hsl(var(${lane.accent}))`, boxShadow: count > 0 ? `0 0 0 4px hsl(var(${lane.accent}) / 0.15), 0 0 16px hsl(var(${lane.accent}) / 0.4)` : undefined }}
        >
          <lane.icon className="h-3.5 w-3.5" />
        </span>
        <span className="min-w-0 leading-tight">
          <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{lane.label}</span>
          <span className="block truncate text-[length:var(--type-caption)] tabular-nums text-muted-foreground">
            {count === 0 ? "none" : <><Count value={count} /> {stage === "downloading" ? "moving" : "here"}</>}{usual ? <> · {usual}</> : null}
          </span>
        </span>
      </Link>
    </div>
  );
}

function Cards({ stage, cards, density, columns = 1, context }: { stage: Stage; cards: JourneyCard[]; density: "full" | "compact"; columns?: number; context: CardContext }) {
  if (cards.length === 0) return null;
  const compact = density === "compact";
  return (
    <ul
      aria-label={`${LANES[stage].label} titles`}
      className="grid content-start"
      style={{ gap: `${CARD_GAP_REM}rem`, gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` } satisfies CSSProperties}
    >
      {cards.map((card) => (compact ? <CompactTile key={card.key} card={card} {...context} /> : <JourneyCardView key={card.key} card={card} {...context} />))}
    </ul>
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
  budget
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
  /** The tallest the whole journey may be, in pixels. It is only a ceiling: the journey is as tall as its cards need, and gives the rest back. */
  budget?: number;
}): ReactElement {
  const stations = useMemo(() => stationsFor(hasProcessor), [hasProcessor]);
  const [bodyRef, body] = useElementSize<HTMLDivElement>();
  const [probeRef, probe] = useElementSize<HTMLSpanElement>();
  const [areaRef, area] = useElementSize<HTMLSpanElement>();
  const rem = useRemPx();
  const mode = journeyMode(body.width, stations.length, rem);
  const stacked = mode === "stacked";
  const onTheirWay = stations.reduce((sum, stage) => sum + counts[stage], 0);
  const rail = onTheirWay > 0;
  const [boxRef, box] = useElementSize<HTMLDivElement>();
  // What the cards may use: the ceiling less everything around them (header, stations, spacing), which does not change with the cards.
  const available = cardsBudget(budget, Math.max(0, box.height - area.height));
  const room = available !== undefined && !stacked ? available : area.height;
  // Stations get width by need: the empty ones shrink to a narrow column, the busy ones share the rest by their cards.
  const widths = useMemo(() => stationWidths(Math.max(0, body.width - 1.5 * rem), stations.map((stage) => grouped[stage].length), rem), [body.width, stations, grouped, rem]);
  const measured = !stacked && widths.every((width) => width > 0);
  // One card form for the whole board: every station draws what the others draw.
  const layouts = useMemo(() => boardLayouts(stations.map((stage) => grouped[stage].length), widths.map((width) => width - 0.5 * rem), room, probe.height, rem, mode), [stations, grouped, widths, room, probe.height, rem, mode]);

  return (
    <div ref={boxRef} className="flex min-h-0 min-w-0 flex-col" style={budget === undefined ? undefined : { maxHeight: budget }}>
    <Panel title="The journey" count={journeyHeading(onTheirWay, by)} to="/queue" toLabel="Transfers" className="min-h-0 flex-1">
      <div ref={bodyRef} className="relative flex min-h-0 flex-1 flex-col">
        <div className={cn("relative min-h-0 flex-1", stacked ? "flex flex-col gap-1 overflow-y-auto px-2 pb-2 pt-1" : "grid px-3")} style={stacked ? undefined : { gridTemplateColumns: measured ? widths.map((width) => `${width}px`).join(" ") : `repeat(${stations.length}, minmax(0, 1fr))`, gridTemplateRows: "auto minmax(0, 1fr)" }}>
          {onTheirWay === 0 ? <p className={cn("flex items-center px-[var(--card-pad-x)] text-[length:var(--type-body-sm)] text-muted-foreground", stacked ? "min-h-10" : "col-span-full row-start-2 pb-2")} style={stacked ? undefined : { minHeight: probe.height || undefined }}>{calmLine(lastArrival)}</p> : null}
          {stacked ? null : <div aria-hidden className="pointer-events-none absolute inset-x-12 top-[1.3125rem] h-0.5 rounded-full bg-hairline"><span className={cn("live-rail absolute inset-0 opacity-60", rail && "live-rail-run")} /></div>}
          {stacked
            ? stations.map((stage) => {
                if (grouped[stage].length === 0 && counts[stage] === 0) return null;
                return (
                  <div key={`stacked-${stage}`} data-station={stage} data-cards="full">
                    <Station stage={stage} count={counts[stage]} usual={usual[stage]} quiet={false} />
                    <div className="px-1 pb-1"><Cards stage={stage} cards={grouped[stage]} density="full" context={context} /></div>
                  </div>
                );
              })
            : stations.map((stage, index) => (
                <div key={`station-${stage}`} style={{ gridColumn: index + 1, gridRow: 1 }}>
                  <Station stage={stage} count={counts[stage]} usual={usual[stage]} quiet={counts[stage] === 0} />
                </div>
              ))}
          {stacked
            ? null
            : stations.map((stage, index) => {
                const cards = grouped[stage];
                const { density, columns } = layouts[index];
                return (
                  <div key={`cards-${stage}`} data-station={stage} data-cards={density} data-columns={columns} style={{ gridColumn: index + 1, gridRow: 2 }} className={cn("min-h-0 min-w-0 overflow-y-auto px-1 pb-2", SCROLLER)}>
                    <Cards stage={stage} cards={cards} density={density} columns={columns} context={context} />
                  </div>
                );
              })}
          {/* The space under the stations, measured: what the cards have to fit. */}
          {stacked ? null : <span ref={areaRef} aria-hidden className="pointer-events-none invisible absolute inset-0" style={{ gridColumn: "1 / -1", gridRow: 2 }} />}
          {/* Measures one full card at this window's type scale, so the rows that fit are counted in real pixels. */}
          <span ref={probeRef} aria-hidden className={cn("pointer-events-none invisible absolute left-0 top-0 w-px", CARD_HEIGHT)} />
        </div>
      </div>
    </Panel>
    </div>
  );
}
