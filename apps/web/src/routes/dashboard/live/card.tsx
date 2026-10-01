/**
 * The one token of the Live journey (#1096): a title that is moving, as a small
 * poster with its state beside it. Every token is the same size and has the same
 * parts in the same places:
 *
 *   poster slot (2:3, always there) | title (one line)
 *                                   | what is true (two lines reserved: a download shows its bar and speed)
 *                                   | route (one line, always reserved)
 *
 * A token with no poster shows a plain tile with the stage's icon and the title's
 * initials. Problems are not tokens: they are in the Needs you strip. The buttons
 * a problem has (`CardButtons`) are used by the drawer that lists them.
 */
import type { LucideIcon } from "lucide-react";
import type { ReactElement, ReactNode } from "react";
import { Link } from "react-router-dom";
import { Button } from "../../../components/ui/button";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { CountUp } from "../../../components/ui/count-up";
import { StatusLed } from "../../../components/ui/status-led";
import { motionAllowed } from "../../../lib/page-motion";
import { dismissFailedJob } from "../../../lib/failed-jobs";
import { canTryAgain, isVanished } from "../../../lib/needs-action";
import { submitQueueRemoval } from "../../../lib/remove-download";
import { cn } from "../../../lib/utils";
import { ActionButton, CARD_BUTTON, formatEta, formatSize, formatSpeed, send, type ActionState } from "../downloads-kit";
import { ago } from "../format";
import { routeOf, type JourneyCard } from "./journey";
import { LANES, TONE_TEXT } from "./lanes";
import { faceOf, type TitleLookup } from "./titles";

/** Every card is this tall. */
export const TOKEN_HEIGHT = "h-[calc(0.8rem+var(--type-body-sm)*1.25+var(--type-caption)*2.5+var(--type-micro)*1.25)]";

/** The two reserved lines of text, and the row of buttons: sized by the type scale, so they hold at every window size. */
const SAID_HEIGHT = "h-[calc(var(--type-caption)*2.5)]";
const ACTIONS_HEIGHT = "h-[calc(var(--type-caption)*2)]";

export interface CardContext {
  titles: TitleLookup;
  arrivals: ReadonlySet<string>;
  action: ActionState;
}

/** "The Wild Robot (2024)" reads "TW"; "Lioness S03E08" reads "LS": two letters, for a tile that has no poster. */
export function initialsOf(title: string): string {
  const words = title.replace(/\(\d{4}\)/g, " ").split(/[^A-Za-z0-9]+/).filter((word) => word && !/^(the|a|an|of)$/i.test(word) && !/^S\d+E\d+$/i.test(word));
  const letters = words.slice(0, 2).map((word) => word[0].toUpperCase()).join("");
  return letters || title.slice(0, 1).toUpperCase();
}

/** The poster slot: always the same 2:3 box, a poster in it or a plain tile with the lane's icon and the title's initials. */
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

/** The buttons a card carries, side by side: the main action first, Give up second. Nothing here is ever stacked. */
export function CardButtons({ card, action }: { card: JourneyCard; action: ActionState }) {
  const searchAgain = card.stage === "needs" && card.canSearchAgain && card.queueRef ? card.queueRef : null;
  const handoffId = card.failedHandoffId;
  const entry = card.recovery;
  const jobId = card.importJobId;
  const button = (label: string, name: string, busyKey: string, run: () => Promise<string>, variant: "outline" | "ghost" = "outline") => (
    <ActionButton card label={label} name={name} busyKey={busyKey} state={action} variant={variant} onClick={() => void action.run(busyKey, run)} />
  );
  const base = entry ? `/api/${entry.mediaType === "movie" ? "movies" : "series"}/import-recovery/${encodeURIComponent(entry.id)}` : "";
  // The most urgent problem decides the buttons: a refused hand-off, then a failed import, then a stopped download, then an import case.
  let main: ReactElement | null = null;
  let second: ReactElement | null = null;
  if (handoffId) {
    main = button("Retry", `Retry ${card.title}`, `handoff-retry:${handoffId}`, async () => (await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoffId)}/retry`, "That could not be tried again."))?.message ?? "Sent to the processor again");
    second = button("Give up", `Give up on ${card.title}`, `handoff-dismiss:${handoffId}`, async () => { await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoffId)}/dismiss`, "That could not be dismissed."); return "Dismissed"; }, "ghost");
  } else if (jobId) {
    main = button("Retry", "Retry the failed import", `job-retry:${jobId}`, async () => { await send("/api/jobs/retry-failed", "The failed imports could not be tried again."); return "Failed imports requeued"; });
    second = button("Give up", "Give up on the failed import", `job-dismiss:${jobId}`, async () => { await dismissFailedJob(jobId); return "Import dismissed"; }, "ghost");
  } else if (searchAgain) {
    main = button("Search again", `Search again for ${card.title}`, `stalled:${searchAgain.clientId}:${searchAgain.itemId}`, async () => (await submitQueueRemoval(searchAgain.clientId, searchAgain.itemId, "failed")).message);
  } else if (entry) {
    main = canTryAgain(entry)
      ? button(isVanished(entry) ? "Search again" : "Retry", `${isVanished(entry) ? "Search again for" : "Retry"} ${card.title}`, `recovery-retry:${entry.id}`, async () => (await send(`${base}/retry`, "That could not be tried again."))?.message ?? `${card.title} is being tried again.`)
      : (
        <Button asChild size="sm" variant="outline" className={CARD_BUTTON}>
          <Link to="/queue" aria-label={`Review ${card.title}`}>Review</Link>
        </Button>
      );
    second = button("Give up", `Give up on ${card.title}`, `recovery-dismiss:${entry.id}`, async () => { await send(`${base}/dismiss`, "That case could not be dismissed."); return `${card.title} dismissed`; }, "ghost");
  }
  return (
    <div className={cn("flex items-center gap-1.5", ACTIONS_HEIGHT)}>
      {main}
      {second}
    </div>
  );
}

/** The two reserved lines: what is true about the card right now. */
function Said({ card }: { card: JourneyCard }): ReactNode {
  const stuck = card.stage === "needs";
  const failed = Boolean(card.reason) && (stuck || card.state.tone === "bad");
  if (card.stage === "downloading") {
    const moving = (card.speedMbps ?? 0) > 0;
    const eta = formatEta(card.etaSeconds) !== "–" ? formatEta(card.etaSeconds) : null;
    return (
      <span className="flex h-full flex-col justify-center gap-1">
        <span aria-hidden className="block h-1.5 overflow-hidden rounded-full bg-surface-3">
          <span className={cn("live-fill block h-full rounded-full bg-info", moving && "live-shimmer")} style={{ width: `${Math.round(card.progress ?? 0)}%` }} />
        </span>
        <span className="flex items-baseline justify-between gap-2 text-[length:var(--type-caption)] leading-tight tabular-nums text-muted-foreground">
          <span className="truncate"><CountUp value={Math.round(card.progress ?? 0)} format={(value) => `${Math.round(value)}%`} /> &middot; <CountUp value={card.speedMbps ?? 0} format={formatSpeed} /></span>
          {eta ? <span className="shrink-0">{eta}</span> : null}
        </span>
      </span>
    );
  }
  if (failed) return <span className="line-clamp-2 break-words text-[length:var(--type-caption)] leading-tight text-destructive">{card.reason}</span>;
  const meta = [card.quality, card.sizeBytes ? formatSize(card.sizeBytes) : null].filter(Boolean).join(" · ") || (card.at && (card.stage === "imported" || card.stage === "searching" || card.stage === "requested") ? ago(card.at) : "");
  return (
    <span className="flex h-full flex-col justify-center text-[length:var(--type-caption)] leading-tight">
      <span className={cn("flex min-w-0 items-center gap-1.5", TONE_TEXT[card.state.tone])}>
        <span className="flex shrink-0"><StatusLed tone={card.state.tone} size={6} pulse={card.state.tone === "bad"} /></span>
        <span className="truncate">{card.state.label}</span>
      </span>
      {meta ? <span className="truncate text-muted-foreground">{meta}</span> : null}
    </span>
  );
}

export function JourneyToken({ card, titles, arrivals, route, ...rest }: Omit<CardContext, "action"> & { card: JourneyCard; route?: string; /** Marks a row a FitList measures. */ "data-fit-row"?: string }): ReactElement {
  const lane = LANES[card.stage];
  const face = faceOf(card, titles);
  const routeText = route ?? (routeOf(card) || card.routeNote || "");
  return (
    <li
      {...rest}
      data-journey-card=""
      className={cn(
        "relative grid grid-cols-[2.5rem_minmax(0,1fr)] gap-2 overflow-hidden rounded-lg border border-l-[3px] border-hairline bg-surface-1/60 p-1.5",
        TOKEN_HEIGHT,
        arrivals.has(card.key) && motionAllowed() && "live-card-arrive"
      )}
      style={{ ["--card-glow" as string]: `var(${lane.accent})`, borderLeftColor: `hsl(var(${lane.accent}))` }}
    >
      <PosterSlot src={face.poster} title={face.title} icon={lane.icon} tone={lane.text} />
      <Link
        to={face.to}
        aria-label={`${face.title}: ${card.state.label}`}
        title={card.release && face.linked ? card.release : face.title}
        className="flex min-h-0 min-w-0 flex-col rounded-md transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <span className="block truncate text-[length:var(--type-body-sm)] font-semibold leading-tight text-foreground">{face.title}</span>
        <span className={cn("block overflow-hidden", SAID_HEIGHT)}><Said card={card} /></span>
        <span title={routeText || undefined} className="block truncate text-[length:var(--type-micro)] leading-tight text-muted-foreground">{routeText || "—"}</span>
      </Link>
    </li>
  );
}
