/**
 * The journey of every title in flight (#1096), as one living board: searching,
 * grabbed, downloading, stuck, with the processor, importing, in the library.
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
import { AlertOctagon, ArrowRight, Download, FolderInput, HardDriveDownload, Library, Search, ShieldCheck, type LucideIcon } from "lucide-react";
import type { ReactElement } from "react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { CountUp } from "../../../components/ui/count-up";
import { StatusLed } from "../../../components/ui/status-led";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { motionAllowed } from "../../../lib/page-motion";
import { submitQueueRemoval } from "../../../lib/remove-download";
import type { Tone } from "../../../lib/status-tones";
import { cn } from "../../../lib/utils";
import { ActionButton, formatEta, formatSize, formatSpeed, send, type ActionState } from "../downloads-kit";
import { ago } from "../format";
import { FitList, Panel } from "../parts";
import { routeOf, STAGES, type JourneyCard, type Stage } from "./journey";
import { faceOf, type TitleLookup } from "./titles";

interface LaneStyle {
  label: string;
  /** The shorter name the lane header shows; the full `label` is its accessible name. */
  short: string;
  icon: LucideIcon;
  /** The accent: a bar across the top of the lane, a stripe on each card and the glow of a card arriving. */
  accent: string;
  bar: string;
  text: string;
  to: string;
}

const LANES: Record<Stage, LaneStyle> = {
  searching: { label: "Searching", short: "Searching", icon: Search, accent: "--stage-search", bar: "bg-[hsl(var(--stage-search))]", text: "text-[hsl(var(--stage-search))]", to: "/search-cycles" },
  grabbed: { label: "Grabbed", short: "Grabbed", icon: HardDriveDownload, accent: "--stage-grab", bar: "bg-[hsl(var(--stage-grab))]", text: "text-[hsl(var(--stage-grab))]", to: "/queue" },
  downloading: { label: "Downloading", short: "Downloading", icon: Download, accent: "--info", bar: "bg-info", text: "text-info", to: "/queue" },
  stuck: { label: "Stuck", short: "Stuck", icon: AlertOctagon, accent: "--destructive", bar: "bg-destructive", text: "text-destructive", to: "/queue" },
  processor: { label: "With the processor", short: "Processing", icon: ShieldCheck, accent: "--stage-process", bar: "bg-[hsl(var(--stage-process))]", text: "text-[hsl(var(--stage-process))]", to: "/queue" },
  importing: { label: "Importing", short: "Importing", icon: FolderInput, accent: "--stage-import", bar: "bg-[hsl(var(--stage-import))]", text: "text-[hsl(var(--stage-import))]", to: "/queue" },
  imported: { label: "In the library", short: "In the library", icon: Library, accent: "--success", bar: "bg-success", text: "text-success", to: "/activity" }
};

const TONE_TEXT: Record<Tone, string> = { ok: "text-success", warn: "text-warning", bad: "text-destructive", info: "text-foreground", idle: "text-muted-foreground" };

/** The one line an empty lane says, and a fuller sentence for its tooltip. */
function emptyNote(stage: Stage, summary: DashboardSummary | undefined): { line: string; full: string } {
  const imported = summary?.pipeline?.today.imported ?? 0;
  switch (stage) {
    case "searching": {
      const cycle = summary?.automation?.lastSearchCycle;
      return cycle ? { line: `Last ${ago(cycle.completedUtc ?? cycle.startedUtc)}`, full: `No search running. The last was ${ago(cycle.completedUtc ?? cycle.startedUtc)}.` } : { line: "None yet", full: "No search has run yet." };
    }
    case "grabbed": return { line: "Nothing waiting", full: "Nothing waiting to start." };
    case "downloading": return { line: "Nothing moving", full: `Nothing downloading. ${imported > 0 ? `${imported.toLocaleString("en-AU")} imported today.` : "Nothing imported yet today."}` };
    case "stuck": return { line: "Nothing stuck", full: "No download is stuck." };
    case "processor": return { line: "Nothing with it", full: "Nothing with the processor." };
    case "importing": return { line: "Nothing waiting", full: "Nothing waiting to import." };
    default: return imported > 0 ? { line: `${imported.toLocaleString("en-AU")} today`, full: `${imported.toLocaleString("en-AU")} imported today.` } : { line: "None today", full: "Nothing imported yet today." };
  }
}

interface CardContext {
  titles: TitleLookup;
  arrivals: ReadonlySet<string>;
  action: ActionState;
}

function CardButtons({ card, action }: { card: JourneyCard; action: ActionState }) {
  const searchAgain = card.stage === "stuck" && card.canSearchAgain && card.queueRef ? card.queueRef : null;
  const handoffId = card.failedHandoffId;
  if (!searchAgain && !handoffId) return null;
  return (
    <div className="flex gap-1.5 px-2 pb-2">
      {searchAgain ? (
        <ActionButton
          label="Search again"
          name={`Search again for ${card.title}`}
          busyKey={`stalled:${searchAgain.clientId}:${searchAgain.itemId}`}
          state={action}
          onClick={() => void action.run(`stalled:${searchAgain.clientId}:${searchAgain.itemId}`, async () => (await submitQueueRemoval(searchAgain.clientId, searchAgain.itemId, "failed")).message)}
        />
      ) : null}
      {handoffId ? (
        <>
          <ActionButton
            label="Retry"
            name={`Retry ${card.title}`}
            busyKey={`handoff-retry:${handoffId}`}
            state={action}
            onClick={() => void action.run(`handoff-retry:${handoffId}`, async () => (await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoffId)}/retry`, "That could not be tried again."))?.message ?? "Sent to the processor again")}
          />
          <ActionButton
            label="Give up"
            name={`Give up on ${card.title}`}
            busyKey={`handoff-dismiss:${handoffId}`}
            state={action}
            variant="ghost"
            onClick={() => void action.run(`handoff-dismiss:${handoffId}`, async () => { await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoffId)}/dismiss`, "That could not be dismissed."); return "Dismissed"; })}
          />
        </>
      ) : null}
    </div>
  );
}

function renderCard(card: JourneyCard, { titles, arrivals, action }: CardContext): ReactElement {
  const lane = LANES[card.stage];
  const face = faceOf(card, titles);
  const downloading = card.stage === "downloading";
  const stuck = card.stage === "stuck";
  const moving = downloading && (card.speedMbps ?? 0) > 0;
  const meta = [card.quality, card.sizeBytes ? formatSize(card.sizeBytes) : null].filter(Boolean).join(" · ");
  const route = routeOf(card);
  const eta = downloading && formatEta(card.etaSeconds) !== "–" ? formatEta(card.etaSeconds) : null;
  return (
    <li
      key={card.key}
      className={cn(
        "relative overflow-hidden rounded-xl border border-l-[3px] border-hairline bg-surface-1/60",
        stuck && "border-destructive/30 bg-destructive/[0.05]",
        arrivals.has(card.key) && motionAllowed() && "live-card-arrive"
      )}
      style={{ ["--card-glow" as string]: `var(${lane.accent})`, borderLeftColor: `hsl(var(${lane.accent}))` }}
    >
      {face.poster ? (
        <span aria-hidden className="pointer-events-none absolute inset-0 scale-150 bg-cover bg-center opacity-[0.14] blur-xl" style={{ backgroundImage: `url(${JSON.stringify(face.poster)})` }} />
      ) : null}
      <Link
        to={face.to}
        aria-label={`${face.title}: ${card.state.label}`}
        className="relative flex gap-2 rounded-xl p-2 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        {card.stage !== "searching" ? (
          <ArtworkImage
            src={face.poster}
            alt=""
            loading="lazy"
            className="h-[3.75rem] w-10 shrink-0 rounded-md bg-surface-3 object-cover shadow-sm"
            fallback={<span aria-hidden className={cn("flex h-[3.75rem] w-10 shrink-0 items-center justify-center rounded-md bg-surface-3", lane.text)}><lane.icon className="h-4 w-4" /></span>}
          />
        ) : (
          <span aria-hidden className={cn("flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-surface-3", lane.text)}><lane.icon className="h-4 w-4" /></span>
        )}
        <span className="min-w-0 flex-1">
          <span title={card.release && face.linked ? card.release : face.title} className="line-clamp-2 break-words text-[length:var(--type-body-sm)] font-semibold leading-tight text-foreground">{face.title}</span>
          <span className={cn("mt-0.5 flex flex-wrap items-start gap-x-1.5 text-[length:var(--type-caption)] leading-tight", TONE_TEXT[card.state.tone])}>
            <span className="flex min-w-0 max-w-full items-start gap-1.5">
              <span className="mt-[3px] flex shrink-0"><StatusLed tone={card.state.tone} size={6} pulse={card.state.tone === "bad"} /></span>
              <span className="line-clamp-2 min-w-0 break-words">{card.state.label}</span>
            </span>
            {eta ? <span className="ml-auto shrink-0 tabular-nums text-muted-foreground">{eta}</span> : null}
          </span>
          {card.reason && (stuck || card.state.tone === "bad") ? <span title={card.reason} className="line-clamp-2 break-words text-[length:var(--type-caption)] leading-tight text-muted-foreground">{card.reason}</span> : null}
          {downloading ? (
            <span className="mt-1 block">
              <span aria-hidden className="block h-1.5 overflow-hidden rounded-full bg-surface-3">
                <span className={cn("live-fill block h-full rounded-full bg-info", moving && "live-shimmer")} style={{ width: `${Math.round(card.progress ?? 0)}%` }} />
              </span>
              <span className="mt-0.5 block text-[length:var(--type-caption)] leading-tight tabular-nums text-muted-foreground">
                <CountUp value={Math.round(card.progress ?? 0)} format={(value) => `${Math.round(value)}%`} /> &middot; <CountUp value={card.speedMbps ?? 0} format={formatSpeed} />
              </span>
            </span>
          ) : null}
          {meta ? <span className="mt-0.5 block break-words text-[length:var(--type-caption)] leading-tight text-muted-foreground">{meta}</span> : null}
          {route ? <span title={route} className="block truncate text-[length:var(--type-caption)] leading-tight text-muted-foreground">{route}</span> : null}
          {face.linked && card.release ? <span title={card.release} className="block truncate text-[length:var(--type-micro)] leading-tight text-muted-foreground/70">{card.release}</span> : null}
          {!downloading && card.at && (card.stage === "imported" || card.stage === "searching") ? <span className="block text-[length:var(--type-caption)] leading-tight text-muted-foreground">{ago(card.at)}</span> : null}
        </span>
      </Link>
      <CardButtons card={card} action={action} />
    </li>
  );
}

export function Flow({
  grouped,
  counts,
  summary,
  titles,
  arrivals,
  action
}: {
  grouped: Record<Stage, JourneyCard[]>;
  counts: Record<Stage, number>;
  summary: DashboardSummary | undefined;
  titles: TitleLookup;
  arrivals: ReadonlySet<string>;
  action: ActionState;
}) {
  const inFlight = counts.grabbed + counts.downloading + counts.stuck + counts.processor + counts.importing;
  const context: CardContext = { titles, arrivals, action };
  return (
    <Panel
      title="Every title's journey"
      count={inFlight > 0 ? <CountUp value={inFlight} format={(value) => `${Math.round(value).toLocaleString("en-AU")} in flight`} /> : "nothing in flight"}
      to="/queue"
      toLabel="Transfers"
    >
      <div className="grid min-h-0 flex-1 grid-cols-2 lg:flex">
        {STAGES.map((stage, index) => {
          const lane = LANES[stage];
          const cards = grouped[stage];
          const count = counts[stage];
          const empty = cards.length === 0;
          const note = emptyNote(stage, summary);
          return (
            <div
              key={stage}
              id={`live-lane-${stage}`}
              role="group"
              aria-label={lane.label}
              // A lane with nothing in it keeps to a narrow strip; the lanes with cards share the rest.
              className={cn("relative flex min-h-0 flex-col border-hairline transition-[flex-grow] duration-500 lg:border-l lg:first:border-l-0", empty ? "lg:w-[6.75rem] lg:shrink-0" : "lg:min-w-0 lg:flex-1 lg:basis-0")}
            >
              <div aria-hidden className={cn("h-0.5 w-full", lane.bar, count === 0 && "opacity-30")} />
              <Link to={lane.to} aria-label={`${lane.label}: ${count}`} className="flex items-center gap-1.5 px-2.5 pb-1 pt-2 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
                <lane.icon aria-hidden className={cn("h-3.5 w-3.5 shrink-0", lane.text)} />
                <span title={lane.label} className="min-w-0 flex-1 truncate text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.06em] text-muted-foreground">{lane.short}</span>
                <span className={cn("text-[length:var(--type-body-sm)] font-semibold tabular-nums", count > 0 ? lane.text : "text-muted-foreground")}>
                  <CountUp value={count} format={(value) => Math.round(value).toLocaleString("en-AU")} />
                </span>
                {index < STAGES.length - 1 && !empty ? <ArrowRight aria-hidden className="hidden h-3 w-3 shrink-0 text-muted-foreground/60 lg:block" /> : null}
              </Link>
              {empty ? (
                <p title={note.full} className="px-2.5 pb-2 text-[length:var(--type-caption)] leading-snug text-muted-foreground">{note.line}</p>
              ) : (
                <FitList
                  moreTo={lane.to}
                  extra={Math.max(0, count - cards.length)}
                  wrap={(shown) => <ul className="space-y-1.5 px-2 pb-1">{shown}</ul>}
                  rows={cards.map((card) => renderCard(card, context))}
                />
              )}
            </div>
          );
        })}
      </div>
    </Panel>
  );
}
