/**
 * The cards of the Live journey (#1098), ported from the mockup's token. Every card is one template, the same size
 * wherever it is (see journey.css):
 *
 *   poster 46x69 | title (two lines)
 *                | one status line (the percentage bold in the station's colour) with the time and why it is moving
 *                | a thin bar with a shimmer while bytes move
 *
 * The station's colour is the card's 3px left edge, on a card that fades from one surface to the next. Clicking a card
 * opens the title's own page. A title the library truly has no poster for is a plain tile with its initials.
 */
import type { LucideIcon } from "lucide-react";
import type { CSSProperties, ReactElement } from "react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { CountUp } from "../../../components/ui/count-up";
import { motionAllowed } from "../../../lib/page-motion";
import { cn } from "../../../lib/utils";
import { formatSpeed } from "../downloads-kit";
import { routeOf, type JourneyCard } from "./journey";
import { LANES } from "./lanes";
import { STARTED_LABEL, STARTED_TONE, type StartedBy } from "./provenance";
import { doneAbout } from "./times";
import { faceOf, type TitleLookup } from "./titles";

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

/** The poster slot: always the same 46 by 69 box (2:3); a poster in it or, only when the title has none, a plain tile with the station's icon and the title's initials. */
export function PosterSlot({ src, title, icon: Icon }: { src?: string | null; title: string; icon: LucideIcon }) {
  const tile = (
    <span aria-hidden className="lj-ph">
      <Icon />
      <span>{initialsOf(title)}</span>
    </span>
  );
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
  const percent = Math.floor(card.progress ?? 0);
  if (card.stage === "requested") {
    const asked = card.startedBy === "seerr" ? "Asked for in Seerr · looking" : indexers > 0 ? `Looking on ${indexers.toLocaleString("en-AU")} ${indexers === 1 ? "indexer" : "indexers"}` : "Looking for it";
    return { text: asked, tone: "info", pulse: true };
  }
  // Only what is known: the download has stopped moving. Why is in its tooltip; Deluno is not claimed to be doing anything it may not be.
  if (card.stuck) return { text: percent > 0 ? `Stuck at ${percent}% · not moving` : "Stuck · not moving", tone: "warn", pulse: true };
  if (card.stage === "grabbed") {
    const parts = [card.quality, card.indexer ? `from ${card.indexer}` : null].filter(Boolean);
    return { text: parts.length > 0 ? parts.join(" · ") : card.state.label, tone: card.state.tone, pulse: false };
  }
  if (card.stage === "downloading") {
    if (card.state.label !== "Downloading") return { text: card.state.label, tone: card.state.tone, pulse: false };
    const parts = [`${percent}%`, (card.speedMbps ?? 0) > 0 ? formatSpeed(card.speedMbps) : null, card.client].filter(Boolean);
    return { text: parts.join(" · "), tone: "info", pulse: false, lead: `${percent}%` };
  }
  if (card.stage === "processor") {
    if (card.state.tone === "bad") return { text: card.state.label, tone: "bad", pulse: false };
    return { text: `${card.processor ?? "The processor"} is working on it`, tone: "info", pulse: true };
  }
  if (card.stage === "importing") return { text: card.state.label, tone: card.state.tone, pulse: card.state.tone === "info" };
  return { text: card.state.label, tone: card.state.tone, pulse: false };
}

function barOf(card: JourneyCard): { width: number } | null {
  return card.stage === "downloading" ? { width: Math.round(card.progress ?? 0) } : null;
}

/** The status line: the percentage bold in the station's colour and the rest quiet; a stuck download is all amber, a failure plain red. */
function StatusLine({ card, status, done, tag }: { card: JourneyCard; status: CardStatus; done: string | null; tag: StartedBy | null }) {
  const lead = status.lead && status.text.startsWith(status.lead) ? status.lead : null;
  const tone = card.stuck || status.tone === "warn" ? "lj-s-warn" : status.tone === "bad" ? "lj-s-bad" : status.tone === "ok" ? "lj-s-ok" : undefined;
  return (
    <span className={cn("lj-s", tone)}>
      {status.pulse ? <span aria-hidden className="lj-pulse" /> : null}
      <span className="lj-s-text">
        {lead ? <b>{lead}</b> : null}
        {lead ? status.text.slice(lead.length) : status.text}
      </span>
      {done ? <span className="lj-when">{done}</span> : null}
      {tag ? <span title={tag === "seerr" ? "Asked for in Seerr" : undefined} className={cn("lj-tag", STARTED_TONE[tag])}>{STARTED_LABEL[tag]}</span> : null}
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
  const done = card.stage === "downloading" && !card.stuck ? doneAbout(card.finishAtMs) : null;
  const tag = card.startedBy && !(card.stage === "requested" && card.startedBy === "seerr") ? card.startedBy : null;
  const route = routeOf(card) || card.routeNote || "";
  const style: CSSProperties & Record<string, string | number> = { "--lj-st": card.stuck ? "var(--lj-warning)" : `hsl(var(${lane.accent}))` };
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
      title={[card.release && face.linked ? card.release : face.title, card.reasonFull || card.reason, route].filter(Boolean).join(" · ")}
      className={cn("lj-token lj-fresh", place?.hidden && "lj-hidden", arrivals.has(card.key) && motionAllowed() && "lj-arrive")}
      style={style}
      tabIndex={place?.hidden ? -1 : undefined}
    >
      <span data-card-poster={card.key}><PosterSlot src={face.poster} title={face.title} icon={lane.icon} /></span>
      <span className="lj-body">
        <span className="lj-t">{face.title}</span>
        <StatusLine card={card} status={status} done={done} tag={tag} />
        {bar ? <span aria-hidden className={cn("lj-pb block", moving && "lj-moving")}><i style={{ width: `${bar.width}%` }} /></span> : null}
      </span>
    </Link>
  );
}

/** A figure that counts to its new value (a station's count). */
export function Count({ value }: { value: number }) {
  return <CountUp value={value} format={(next) => Math.round(next).toLocaleString("en-AU")} />;
}
