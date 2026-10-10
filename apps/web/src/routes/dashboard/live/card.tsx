/**
 * The cards of the Live journey (#1098), ported from the mockup's token. Every card is one template, the same size
 * wherever it is (see journey.css):
 *
 *   poster (2:3, as tall as the card allows) | title (one or two lines)
 *                                            | one status line (the percentage bold in the station's colour)
 *                                            | a thin bar with a shimmer while bytes move
 *                                            | the detail block: up to three lines of what matters at this stage (detail.ts)
 *
 * The station's colour is the card's 3px left edge, on a card that fades from one surface to the next. When its import
 * has finished the card is full and green, "Imported · in your library", for a moment before its poster flies to the
 * shelf. Clicking a card opens the title's own page. A title the library truly has no poster for is a plain tile with its
 * initials.
 */
import type { CSSProperties, ReactElement } from "react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { CountUp } from "../../../components/ui/count-up";
import { motionAllowed } from "../../../lib/page-motion";
import { cn } from "../../../lib/utils";
import { formatSpeed } from "../downloads-kit";
import { IMPORTING_NOW, processorHasStarted, routeOf, type JourneyCard } from "./journey";
import { LANES } from "./lanes";
import { startedLabel } from "./provenance";
import { detailLines, type Part } from "./detail";
import { faceOf, type TitleLookup } from "./titles";
import { floorProgress, formatProgressPercent } from "../../../lib/progress-format";

export interface CardContext {
  titles: TitleLookup;
  /** The cards that have just changed station, which glow as they arrive. */
  arrivals: ReadonlySet<string>;
  /** How many indexers a search is being sent to, for "Looking on 5 indexers". */
  indexers: number;
}

/** "The Wild Robot (2024)" reads "TW"; "Lioness S03E08" reads "LS": two letters, for a tile that has no poster. */
export function initialsOf(title: string): string {
  const words = title.replace(/\(\d{4}\)/g, " ").split(/[^A-Za-z0-9]+/).filter((word) => word && !/^(the|a|an|of)$/i.test(word) && !/^S\d+E\d+$/i.test(word));
  const letters = words.slice(0, 2).map((word) => word[0].toUpperCase()).join("");
  return letters || title.slice(0, 1).toUpperCase();
}

/** The tint of a poster's tile by what the title is, the same every time: films and shows wear the shelf's own library colours. */
export function tileHue(kind: string | undefined): number {
  if (kind === "movie" || kind === "movies") return 205;
  if (kind === "series" || kind === "episode" || kind === "tv") return 265;
  return 210;
}

/** A title's poster as Weir draws it: 2:3, its initials on a tinted tile until there is a picture, and wherever there is none. */
export function PosterTile({ title, kind, hue }: { title: string; kind?: string; /** The library's own colour when it is known (the shelf), as Weir colours by workflow. */ hue?: number }) {
  return (
    <span aria-hidden className="lj-tile" style={{ ["--tile-hue" as string]: hue ?? tileHue(kind) }}>
      <span>{initialsOf(title)}</span>
    </span>
  );
}

/** The poster slot: always the same 2:3 box; a poster in it or, only when the title has none, its initials on a tile. */
export function PosterSlot({ src, title, kind }: { src?: string | null; title: string; kind?: string }) {
  const tile = <PosterTile title={title} kind={kind} />;
  return (
    <span aria-hidden className="lj-poster">
      <ArtworkImage src={src} alt="" loading="lazy" className="h-full w-full object-cover" fallback={tile} />
    </span>
  );
}

/** What a card says about itself, as words and as the parts the card draws: the same sentence the accessible name and the tooltip use. */
export interface CardStatus {
  /** The whole line as plain text. */
  text: string;
  tone: "info" | "warn" | "bad" | "ok" | "idle";
  /** A dot that pulses beside it: something is being looked for or worked on right now. */
  pulse: boolean;
  /** The front of the line the card draws bold in its station's colour (a download's percentage). */
  lead?: string;
}

export function statusOf(card: JourneyCard, indexers: number): CardStatus {
  const percent = formatProgressPercent(card.progress);
  if (card.stage === "requested") {
    const asked = card.startedBy === "seerr" ? "Asked for in Seerr · looking" : indexers > 0 ? `Looking on ${indexers.toLocaleString("en-AU")} ${indexers === 1 ? "indexer" : "indexers"}` : "Looking for it";
    return { text: asked, tone: "info", pulse: true };
  }
  // Only what is known: the download has stopped moving. Why is in its tooltip; Deluno is not claimed to be doing anything it may not be.
  // The import has finished: full and green for a moment, before the poster flies to the library.
  if (card.landed) return { text: "✓ Imported · in your library", tone: "ok", pulse: false, lead: "✓ Imported" };
  if (card.stuck) return { text: floorProgress(card.progress) > 0 ? `Stuck at ${percent} · not moving` : "Stuck · not moving", tone: "warn", pulse: true };
  if (card.stage === "grabbed") {
    const parts = [card.quality, card.indexer ? `from ${card.indexer}` : null].filter(Boolean);
    return { text: parts.length > 0 ? parts.join(" · ") : card.state.label, tone: card.state.tone, pulse: false };
  }
  if (card.stage === "downloading") {
    if (card.state.label !== "Downloading") return { text: card.state.label, tone: card.state.tone, pulse: false };
    const parts = [percent, (card.speedMbps ?? 0) > 0 ? formatSpeed(card.speedMbps) : null, card.client].filter(Boolean);
    return { text: parts.join(" · "), tone: "info", pulse: false, lead: percent };
  }
  if (card.stage === "processor") {
    if (card.state.tone === "bad") return { text: card.state.label, tone: "bad", pulse: false };
    // Only the hand-off's real state: a hand-off still waiting to be submitted is not work in progress.
    if (!processorHasStarted(card)) return { text: `Waiting to hand over to ${card.processor ?? "the processor"}`, tone: "info", pulse: false };
    return { text: `${card.processor ?? "The processor"} is working on it`, tone: "info", pulse: true };
  }
  if (card.stage === "importing") return { text: card.state.label, tone: card.state.tone, pulse: card.state.tone === "info" };
  return { text: card.state.label, tone: card.state.tone, pulse: false };
}

/**
 * The thin bar: a download's progress; an import under way (no client reports how far it is, so it only shows that it is
 * moving) that fills to 100% when the import finishes. Nothing for a card that is only waiting.
 */
function barOf(card: JourneyCard): { width: number; waiting: boolean } | null {
  if (card.stage === "downloading") return { width: floorProgress(card.progress), waiting: false };
  if (card.stage === "importing" && card.landed) return { width: 100, waiting: false };
  if (card.stage === "importing" && card.state.label === IMPORTING_NOW) return { width: 35, waiting: true };
  return null;
}

function Parts({ parts }: { parts: Part[] }) {
  return <>{parts.map((part, index) => (typeof part === "string" ? <span key={index}>{part}</span> : <b key={index}>{part.bold}</b>))}</>;
}

/** The status line: the percentage bold in the station's colour and the rest quiet; a stuck download is all amber, a failure plain red. */
function StatusLine({ card, status }: { card: JourneyCard; status: CardStatus }) {
  const lead = status.lead && status.text.startsWith(status.lead) ? status.lead : null;
  const tone = card.stuck || status.tone === "warn" ? "lj-s-warn" : status.tone === "bad" ? "lj-s-bad" : status.tone === "ok" ? "lj-s-ok" : undefined;
  return (
    <span className={cn("lj-s", tone)}>
      {status.pulse ? <span aria-hidden className="lj-pulse" /> : null}
      <span className="lj-s-text">
        {lead ? <b>{lead}</b> : null}
        {lead ? status.text.slice(lead.length) : status.text}
      </span>
    </span>
  );
}

/** Where a card sits on the lanes: its station's column and its row, as the mockup positions its tokens. */
export interface TokenPlace {
  column: number;
  stations: number;
  /** The row's top, in px. */
  top: number;
  /** Beyond the rows that fit: kept in place (so it can glide back) but not shown. */
  hidden: boolean;
}

export function JourneyToken({ card, titles, arrivals, indexers, place }: CardContext & { card: JourneyCard; place?: TokenPlace }): ReactElement {
  const lane = LANES[card.stage];
  const face = faceOf(card, titles);
  const status = statusOf(card, indexers);
  const bar = barOf(card);
  const moving = card.stage === "downloading" && !card.stuck && (card.speedMbps ?? 0) > 0;
  const lines = detailLines(card, indexers, Date.now());
  const route = routeOf(card) || card.routeNote || "";
  // The card's edge is its station's colour; a title that stopped is edged in the colour of what happened, and an import that finished is green.
  const edge = card.landed ? "ok" : status.tone === "bad" ? "bad" : card.stuck || status.tone === "warn" ? "warn" : null;
  const style: CSSProperties & Record<string, string | number> = { "--lj-st": edge === "ok" ? "var(--lj-success)" : edge === "bad" ? "hsl(var(--destructive))" : edge === "warn" ? "var(--lj-warning)" : `hsl(var(${lane.accent}))` };
  if (place) {
    style.left = `calc(${place.column} * 100% / ${place.stations} + 6px)`;
    style.top = `${place.top}px`;
  }
  return (
    <Link
      to={face.to}
      data-journey-card=""
      data-glide-stage={card.stage}
      aria-label={`${face.title}: ${status.text}`}
      title={[card.release && face.linked ? card.release : face.title, card.startedBy ? startedLabel(card.startedBy, card.entity, card.release) : null, card.reasonFull || card.reason, route].filter(Boolean).join(" · ")}
      className={cn("lj-token lj-fresh", card.landed && "lj-done", edge === "bad" && "lj-bad", edge === "warn" && "lj-warn", place?.hidden && "lj-hidden", arrivals.has(card.key) && motionAllowed() && "lj-arrive")}
      style={style}
      tabIndex={place?.hidden ? -1 : undefined}
    >
      <span data-card-poster={card.key}><PosterSlot src={face.poster} title={face.title} kind={card.entity?.kind} /></span>
      <span className="lj-body">
        <span className="lj-t">{face.title}</span>
        <StatusLine card={card} status={status} />
        {bar ? <span aria-hidden className={cn("lj-pb block", (moving || bar.waiting) && "lj-moving", bar.waiting && "lj-wait")}><i style={{ width: `${bar.width}%` }} /></span> : null}
        {lines.length > 0 ? (
          <span className="lj-x">
            {lines.map((line, index) => (
              <span key={index} className="lj-xl">
                <span className={cn(line.mono && "lj-rel")}><Parts parts={line.parts} /></span>
                <span>{line.right}</span>
              </span>
            ))}
          </span>
        ) : null}
      </span>
    </Link>
  );
}

/** A figure that counts to its new value (a station's count). */
export function Count({ value }: { value: number }) {
  return <CountUp value={value} format={(next) => Math.round(next).toLocaleString("en-AU")} />;
}
