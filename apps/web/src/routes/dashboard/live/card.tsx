/**
 * The cards of the Live journey (#1098). Every full card is one template, the same
 * size wherever it is:
 *
 *   poster 46x69 | title (two lines)
 *                | one status line (the percentage bold in the station's colour)
 *                | a thin bar with a shimmer while bytes move
 *                | why it is moving (a small tag) . when it should be done
 *
 * The station's colour is the card's 3px left edge, on a card that fades from one
 * surface to the next. When a station has more titles than fit its height its cards
 * become the same card in miniature (`CompactTile`): a small poster, one line of title,
 * one line of status and the bar, in a list that scrolls inside the station, so every
 * title stays on screen. Clicking either opens the title's own page. A title the
 * library truly has no poster for is a plain tile with its initials.
 */
import type { LucideIcon } from "lucide-react";
import type { ReactElement } from "react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { CountUp } from "../../../components/ui/count-up";
import { motionAllowed } from "../../../lib/page-motion";
import { cn } from "../../../lib/utils";
import { formatSpeed } from "../downloads-kit";
import { routeOf, type JourneyCard } from "./journey";
import { LANES, TONE_TEXT } from "./lanes";
import { STARTED_LABEL, STARTED_TONE } from "./provenance";
import { doneAbout } from "./times";
import { faceOf, type TitleLookup } from "./titles";

/** Every full card is this tall: the type scale decides, so the cards hold their shape at every window size. */
export const CARD_HEIGHT = "h-[max(5.375rem,calc(1rem+var(--type-body-sm)*2.5+var(--type-caption)*1.4+var(--type-micro)*1.6+0.5rem))]";

/** What every card is filled with: a card that fades from one surface to the next. */
const CARD_FILL = "linear-gradient(180deg, hsl(var(--surface-2)), hsl(var(--surface-1)))";

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

/** The poster slot: always the same 46 by 69 box (2:3), or its miniature; a poster in it or, only when the title has none, a plain tile with the station's icon and the title's initials. */
export function PosterSlot({ src, title, icon: Icon, tone, small }: { src?: string | null; title: string; icon: LucideIcon; tone: string; small?: boolean }) {
  const tile = (
    <span aria-hidden className="flex h-full w-full flex-col items-center justify-center gap-0.5 rounded-md bg-surface-3">
      <Icon className={cn(small ? "h-3 w-3" : "h-3.5 w-3.5", tone)} />
      {small ? null : <span className="text-[length:var(--type-micro)] font-semibold leading-none text-muted-foreground">{initialsOf(title)}</span>}
    </span>
  );
  return (
    <span aria-hidden className={cn("block shrink-0 overflow-hidden shadow-sm", small ? "h-9 w-6 rounded" : "h-[4.3125rem] w-[2.875rem] rounded-md")}>
      <ArtworkImage src={src} alt="" loading="lazy" className="h-full w-full bg-surface-3 object-cover" fallback={tile} />
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
  if (card.stuck) return { text: percent > 0 ? `Stuck at ${percent}% · finding another copy` : "Stuck · finding another copy", tone: "warn", pulse: true };
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

function barOf(card: JourneyCard): { width: number; tone: string } | null {
  if (card.stage !== "downloading") return null;
  return { width: Math.round(card.progress ?? 0), tone: card.stuck ? "bg-warning" : "bg-info" };
}

/** The accent of a card: its station's colour, amber while it is stuck. */
function accentOf(card: JourneyCard): string {
  return card.stuck ? "--warning" : LANES[card.stage].accent;
}

/** The status line: the percentage bold in the station's colour and the rest quiet; a stuck download is all amber, a failure plain red. */
function StatusLine({ card, status, className }: { card: JourneyCard; status: CardStatus; className?: string }) {
  const lead = status.lead && status.text.startsWith(status.lead) ? status.lead : null;
  const tone = card.stuck ? "text-warning" : status.tone === "info" || status.tone === "idle" ? "text-muted-foreground" : TONE_TEXT[status.tone];
  return (
    <span className={cn("flex min-w-0 items-center gap-1.5 leading-tight", tone, className)}>
      {status.pulse ? <span aria-hidden className={cn("live-blink h-1.5 w-1.5 shrink-0 rounded-full", card.stuck ? "bg-warning" : "bg-current")} /> : null}
      <span className="truncate">
        {lead ? <b className="font-semibold" style={{ color: `hsl(var(${accentOf(card)}))` }}>{lead}</b> : null}
        {lead ? status.text.slice(lead.length) : status.text}
      </span>
    </span>
  );
}

function Bar({ bar, moving, className }: { bar: { width: number; tone: string }; moving: boolean; className: string }) {
  return (
    <span aria-hidden className={cn("block overflow-hidden rounded-full bg-surface-3", className)}>
      <span className={cn("live-fill block h-full rounded-full", bar.tone, moving && "live-shimmer")} style={{ width: `${bar.width}%` }} />
    </span>
  );
}

export function JourneyCardView({ card, titles, arrivals, indexers, ...rest }: CardContext & { card: JourneyCard; "data-fit-row"?: string }): ReactElement {
  const lane = LANES[card.stage];
  const face = faceOf(card, titles);
  const status = statusOf(card, indexers);
  const bar = barOf(card);
  const moving = card.stage === "downloading" && !card.stuck && (card.speedMbps ?? 0) > 0;
  const done = card.stage === "downloading" && !card.stuck ? doneAbout(card.finishAtMs) : null;
  const tag = card.startedBy && !(card.stage === "requested" && card.startedBy === "seerr") ? card.startedBy : null;
  const route = routeOf(card) || card.routeNote || "";
  return (
    <li {...rest} data-journey-card="" data-glide={card.key} className={cn("relative", CARD_HEIGHT, arrivals.has(card.key) && motionAllowed() && "live-card-arrive")} style={{ ["--card-glow" as string]: `var(${accentOf(card)})` }}>
      <Link
        to={face.to}
        aria-label={`${face.title}: ${status.text}`}
        title={[card.release && face.linked ? card.release : face.title, card.reasonFull || card.reason, route].filter(Boolean).join(" · ")}
        className="relative grid h-full grid-cols-[2.875rem_minmax(0,1fr)] gap-2.5 overflow-hidden rounded-[11px] border border-l-[3px] border-hairline p-2 transition-[filter] hover:brightness-110 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        style={{ borderLeftColor: `hsl(var(${accentOf(card)}))`, background: CARD_FILL }}
      >
        <span data-card-poster={card.key}><PosterSlot src={face.poster} title={face.title} icon={lane.icon} tone={card.stuck ? "text-warning" : lane.text} /></span>
        <span className="flex min-h-0 min-w-0 flex-col justify-between gap-0.5">
          <span className="line-clamp-2 text-[length:var(--type-body-sm)] font-semibold leading-tight text-foreground">{face.title}</span>
          <StatusLine card={card} status={status} className="text-[length:var(--type-caption)]" />
          {bar ? <Bar bar={bar} moving={moving} className="h-1" /> : null}
          <span className="flex min-w-0 items-center justify-between gap-1.5 text-[length:var(--type-micro)] leading-tight text-muted-foreground">
            {tag ? <span title={tag === "seerr" ? "Asked for in Seerr" : undefined} className={cn("max-w-full shrink-0 truncate rounded-full px-1.5 py-px font-semibold", STARTED_TONE[tag])}>{STARTED_LABEL[tag]}</span> : <span />}
            {done ? <span className="min-w-0 truncate tabular-nums">{done}</span> : null}
          </span>
        </span>
      </Link>
    </li>
  );
}

/** The same card in miniature, for a station with more titles than its height holds: a small poster, one line of title, one line of status and the bar, in a list that scrolls inside the station. */
export function CompactTile({ card, titles, arrivals, indexers, ...rest }: CardContext & { card: JourneyCard; "data-fit-row"?: string }): ReactElement {
  const lane = LANES[card.stage];
  const face = faceOf(card, titles);
  const status = statusOf(card, indexers);
  const bar = barOf(card);
  const moving = card.stage === "downloading" && !card.stuck && (card.speedMbps ?? 0) > 0;
  const done = card.stage === "downloading" && !card.stuck ? doneAbout(card.finishAtMs) : null;
  const tip = [face.title, status.text, done, card.startedBy ? STARTED_LABEL[card.startedBy] : null].filter(Boolean).join(" · ");
  return (
    <li {...rest} data-journey-card="" data-compact="" data-glide={card.key} className={cn("relative", arrivals.has(card.key) && motionAllowed() && "live-card-arrive")} style={{ ["--card-glow" as string]: `var(${accentOf(card)})` }}>
      <Link
        to={face.to}
        aria-label={`${face.title}: ${status.text}`}
        title={tip}
        className="relative grid h-[max(3rem,calc(0.75rem+var(--type-caption)*1.3+var(--type-micro)*1.4+0.375rem))] grid-cols-[1.5rem_minmax(0,1fr)] items-center gap-2 overflow-hidden rounded-lg border border-l-[3px] border-hairline p-1 transition-[filter] hover:brightness-110 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        style={{ borderLeftColor: `hsl(var(${accentOf(card)}))`, background: CARD_FILL }}
      >
        <span data-card-poster={card.key}><PosterSlot small src={face.poster} title={face.title} icon={lane.icon} tone={card.stuck ? "text-warning" : lane.text} /></span>
        <span className="flex min-w-0 flex-col gap-px">
          <span className="truncate text-[length:var(--type-caption)] font-semibold leading-tight text-foreground">{face.title}</span>
          <StatusLine card={card} status={status} className="text-[length:var(--type-micro)]" />
          {bar ? <Bar bar={bar} moving={moving} className="mt-px h-[3px]" /> : null}
        </span>
      </Link>
    </li>
  );
}

/** A figure that counts to its new value (a station's count). */
export function Count({ value }: { value: number }) {
  return <CountUp value={value} format={(next) => Math.round(next).toLocaleString("en-AU")} />;
}
