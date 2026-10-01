/**
 * The cards of the Live journey (#1098). Every full card is one template, the same
 * size wherever it is:
 *
 *   poster (2:3) | title (two lines)
 *                | one status line
 *                | why it is moving (a small tag) · when it should be done
 *   ─ bar along the bottom (a download, a stuck download in amber)
 *
 * When a station has more titles than fit its height its cards become compact
 * poster tiles (`CompactTile`) of the same family, so every title stays on screen.
 * Clicking either opens the title's own page. A title with no poster is a plain tile
 * with its initials.
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
export const CARD_HEIGHT = "h-[calc(0.75rem+var(--type-body-sm)*2.5+var(--type-caption)*1.35+var(--type-micro)*1.5+0.5rem)]";

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

/** The poster slot: always the same 2:3 box, a poster in it or a plain tile with the station's icon and the title's initials. */
export function PosterSlot({ src, title, icon: Icon, tone }: { src?: string | null; title: string; icon: LucideIcon; tone: string }) {
  const tile = (
    <span aria-hidden className="flex h-full w-full flex-col items-center justify-center gap-0.5 rounded-md bg-surface-3">
      <Icon className={cn("h-3.5 w-3.5", tone)} />
      <span className="text-[length:var(--type-micro)] font-semibold leading-none text-muted-foreground">{initialsOf(title)}</span>
    </span>
  );
  return (
    <span aria-hidden className="block h-[3.75rem] w-[2.5rem] shrink-0 overflow-hidden rounded-md shadow-sm">
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
    return { text: parts.join(" · "), tone: "info", pulse: false };
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
        className="relative grid h-full grid-cols-[2.5rem_minmax(0,1fr)] gap-2 overflow-hidden rounded-lg border border-l-[3px] border-hairline bg-surface-1/60 p-1.5 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        style={{ borderLeftColor: `hsl(var(${accentOf(card)}))` }}
      >
        <span data-card-poster={card.key}><PosterSlot src={face.poster} title={face.title} icon={lane.icon} tone={card.stuck ? "text-warning" : lane.text} /></span>
        <span className="flex min-h-0 min-w-0 flex-col justify-between">
          <span className="line-clamp-2 text-[length:var(--type-body-sm)] font-semibold leading-tight text-foreground">{face.title}</span>
          <span className={cn("flex min-w-0 items-center gap-1.5 text-[length:var(--type-caption)] leading-tight", card.stuck ? "text-warning" : TONE_TEXT[status.tone])}>
            {status.pulse ? <span aria-hidden className={cn("live-blink h-1.5 w-1.5 shrink-0 rounded-full", card.stuck ? "bg-warning" : "bg-current")} /> : null}
            <span className="truncate">{status.text}</span>
          </span>
          <span className="flex min-w-0 items-center justify-between gap-1.5 text-[length:var(--type-micro)] leading-tight text-muted-foreground">
            {tag ? <span title={tag === "seerr" ? "Asked for in Seerr" : undefined} className={cn("max-w-full shrink-0 truncate rounded-full px-1.5 py-px font-semibold", STARTED_TONE[tag])}>{STARTED_LABEL[tag]}</span> : <span />}
            {done ? <span className="min-w-0 truncate tabular-nums">{done}</span> : null}
          </span>
        </span>
        {bar ? (
          <span aria-hidden className="absolute inset-x-0 bottom-0 block h-[3px] bg-surface-3">
            <span className={cn("live-fill block h-full", bar.tone, moving && "live-shimmer")} style={{ width: `${bar.width}%` }} />
          </span>
        ) : null}
      </Link>
    </li>
  );
}

/** The same card as a poster tile, for a station with more titles than its height holds. Its words are in its name and tooltip. */
export function CompactTile({ card, titles, arrivals, indexers, ...rest }: CardContext & { card: JourneyCard; "data-fit-row"?: string }): ReactElement {
  const lane = LANES[card.stage];
  const face = faceOf(card, titles);
  const status = statusOf(card, indexers);
  const bar = barOf(card);
  const done = card.stage === "downloading" && !card.stuck ? doneAbout(card.finishAtMs) : null;
  const tip = [face.title, status.text, done, card.startedBy ? STARTED_LABEL[card.startedBy] : null].filter(Boolean).join(" · ");
  return (
    <li {...rest} data-journey-card="" data-compact="" data-glide={card.key} className={cn("relative", arrivals.has(card.key) && motionAllowed() && "live-card-arrive")} style={{ ["--card-glow" as string]: `var(${accentOf(card)})` }}>
      <Link
        to={face.to}
        aria-label={`${face.title}: ${status.text}`}
        title={tip}
        className="relative block aspect-[2/3] overflow-hidden rounded-md border-b-[3px] shadow-sm ring-1 ring-hairline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        style={{ borderBottomColor: `hsl(var(${accentOf(card)}))` }}
      >
        <span data-card-poster={card.key} className="block h-full w-full">
          <ArtworkImage
            src={face.poster}
            alt=""
            loading="lazy"
            className="h-full w-full bg-surface-3 object-cover"
            fallback={
              <span aria-hidden className="flex h-full w-full flex-col items-center justify-center gap-0.5 bg-surface-3">
                <lane.icon className={cn("h-3.5 w-3.5", card.stuck ? "text-warning" : lane.text)} />
                <span className="text-[length:var(--type-micro)] font-semibold leading-none text-muted-foreground">{initialsOf(face.title)}</span>
              </span>
            }
          />
        </span>
        {status.pulse ? <span aria-hidden className={cn("live-blink absolute right-1 top-1 h-1.5 w-1.5 rounded-full", card.stuck ? "bg-warning" : "bg-info")} /> : null}
        {bar ? (
          <span aria-hidden className="absolute inset-x-0 bottom-0 block h-[3px] bg-black/40">
            <span className={cn("live-fill block h-full", bar.tone)} style={{ width: `${bar.width}%` }} />
          </span>
        ) : null}
      </Link>
    </li>
  );
}

/** A figure that counts to its new value (a station's count). */
export function Count({ value }: { value: number }) {
  return <CountUp value={value} format={(next) => Math.round(next).toLocaleString("en-AU")} />;
}
