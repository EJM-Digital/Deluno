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
import { AlertOctagon, Download, FolderInput, HardDriveDownload, Library, Search, ShieldCheck, type LucideIcon } from "lucide-react";
import type { ReactElement } from "react";
import { Link } from "react-router-dom";
import { Button } from "../../../components/ui/button";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { CountUp } from "../../../components/ui/count-up";
import { StatusLed } from "../../../components/ui/status-led";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { motionAllowed } from "../../../lib/page-motion";
import { dismissFailedJob } from "../../../lib/failed-jobs";
import { canTryAgain, isVanished } from "../../../lib/needs-action";
import { submitQueueRemoval } from "../../../lib/remove-download";
import type { Tone } from "../../../lib/status-tones";
import { cn } from "../../../lib/utils";
import { ActionButton, UNIFORM_BUTTON, formatEta, formatSize, formatSpeed, send, type ActionState } from "../downloads-kit";
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
  downloading: {
    label: "Downloading",
    short: "Downloading",
    icon: Download,
    accent: "--info",
    bar: "bg-info",
    text: "text-info",
    to: "/queue"
  },
  needs: { label: "Needs you", short: "Needs you", icon: AlertOctagon, accent: "--destructive", bar: "bg-destructive", text: "text-destructive", to: "/queue" },
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
    case "needs": return { line: "Nothing needs you", full: "Nothing needs you: no download is stuck and no import or hand-off has failed." };
    case "processor": return { line: "Nothing with it", full: "Nothing with the processor." };
    case "importing": return { line: "Nothing waiting", full: "Nothing waiting to import." };
    default: return imported > 0 ? { line: `${imported.toLocaleString("en-AU")} today`, full: `${imported.toLocaleString("en-AU")} imported today.` } : { line: "None today", full: "Nothing imported yet today." };
  }
}

interface CardContext {
  compact?: boolean;
  titles: TitleLookup;
  arrivals: ReadonlySet<string>;
  action: ActionState;
}

function CardButtons({ card, action, inline = false }: { card: JourneyCard; action: ActionState; inline?: boolean }) {
  const searchAgain = card.stage === "needs" && card.canSearchAgain && card.queueRef ? card.queueRef : null;
  const handoffId = card.failedHandoffId;
  const entry = card.recovery;
  const jobId = card.importJobId;
  if (!searchAgain && !handoffId && !entry && !jobId) return null;
  const button = (label: string, name: string, busyKey: string, run: () => Promise<string>, variant: "outline" | "ghost" = "outline") => (
    <ActionButton uniform label={label} name={name} busyKey={busyKey} state={action} variant={variant} onClick={() => void action.run(busyKey, run)} />
  );
  const base = entry ? `/api/${entry.mediaType === "movie" ? "movies" : "series"}/import-recovery/${encodeURIComponent(entry.id)}` : "";
  return (
    <div className={cn("relative flex flex-wrap gap-1.5", inline ? "mt-1" : "px-1.5 pb-1.5")}>
      {searchAgain
        ? button("Search again", `Search again for ${card.title}`, `stalled:${searchAgain.clientId}:${searchAgain.itemId}`, async () => (await submitQueueRemoval(searchAgain.clientId, searchAgain.itemId, "failed")).message)
        : null}
      {handoffId ? (
        <>
          {button("Retry", `Retry ${card.title}`, `handoff-retry:${handoffId}`, async () => (await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoffId)}/retry`, "That could not be tried again."))?.message ?? "Sent to the processor again")}
          {button("Give up", `Give up on ${card.title}`, `handoff-dismiss:${handoffId}`, async () => { await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoffId)}/dismiss`, "That could not be dismissed."); return "Dismissed"; }, "ghost")}
        </>
      ) : null}
      {entry ? (
        <>
          {canTryAgain(entry)
            ? button(isVanished(entry) ? "Search again" : "Retry", `${isVanished(entry) ? "Search again for" : "Retry"} ${card.title}`, `recovery-retry:${entry.id}`, async () => (await send(`${base}/retry`, "That could not be tried again."))?.message ?? `${card.title} is being tried again.`)
            : (
              <Button asChild size="sm" variant="outline" className={UNIFORM_BUTTON}>
                <Link to="/queue" aria-label={`Review ${card.title}`}>Review</Link>
              </Button>
            )}
          {button("Give up", `Give up on ${card.title}`, `recovery-dismiss:${entry.id}`, async () => { await send(`${base}/dismiss`, "That case could not be dismissed."); return `${card.title} dismissed`; }, "ghost")}
        </>
      ) : null}
      {jobId ? (
        <>
          {button("Retry", `Retry the failed import`, `job-retry:${jobId}`, async () => { await send("/api/jobs/retry-failed", "The failed imports could not be tried again."); return "Failed imports requeued"; })}
          {button("Give up", `Give up on the failed import`, `job-dismiss:${jobId}`, async () => { await dismissFailedJob(jobId); return "Import dismissed"; }, "ghost")}
        </>
      ) : null}
    </div>
  );
}

function renderCompactCard(card: JourneyCard, { titles, arrivals, action }: CardContext): ReactElement {
  const lane = LANES[card.stage];
  const face = faceOf(card, titles);
  const route = routeOf(card);
  const stuck = card.stage === "needs";
  const failed = Boolean(card.reason) && (stuck || card.state.tone === "bad");
  const second = failed ? card.reason : card.state.label;
  return (
    <li
      key={card.key}
      className={cn(
        "relative overflow-hidden rounded-lg border border-l-[3px] border-hairline bg-surface-1/60",
        stuck && "border-destructive/30 bg-destructive/[0.05]",
        arrivals.has(card.key) && motionAllowed() && "live-card-arrive"
      )}
      style={{ ["--card-glow" as string]: `var(${lane.accent})`, borderLeftColor: `hsl(var(${lane.accent}))` }}
    >
      <Link
        to={face.to}
        aria-label={`${face.title}: ${card.state.label}`}
        className="relative flex min-w-0 items-center gap-2 rounded-lg p-1.5 pb-0 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <ArtworkImage
          src={face.poster}
          alt=""
          loading="lazy"
          className="h-[2.375rem] w-[1.625rem] shrink-0 rounded bg-surface-3 object-cover shadow-sm"
          fallback={<span aria-hidden className={cn("flex h-[2.375rem] w-[1.625rem] shrink-0 items-center justify-center rounded bg-surface-3", lane.text)}><lane.icon className="h-3.5 w-3.5" /></span>}
        />
        <span className="min-w-0 flex-1">
          <span title={card.release && face.linked ? card.release : face.title} className="block truncate text-[length:var(--type-body-sm)] font-semibold leading-tight text-foreground">{face.title}</span>
          <span title={card.reasonFull ?? second ?? undefined} className={cn("block truncate text-[length:var(--type-caption)] leading-tight", failed ? "text-destructive" : TONE_TEXT[card.state.tone])}>{second}</span>
        </span>
      </Link>
      <div className="relative pb-1.5 pl-[2.55rem] pr-1.5 pt-0.5">
        {route ? <span title={route} className="block truncate text-[length:var(--type-micro)] leading-tight text-muted-foreground">{route}</span> : null}
        <CardButtons card={card} action={action} inline />
      </div>
    </li>
  );
}

function renderCard(card: JourneyCard, context: CardContext): ReactElement {
  if (context.compact) return renderCompactCard(card, context);
  const { titles, arrivals, action } = context;
  const lane = LANES[card.stage];
  const face = faceOf(card, titles);
  const downloading = card.stage === "downloading";
  const stuck = card.stage === "needs";
  const moving = downloading && (card.speedMbps ?? 0) > 0;
  const meta = stuck ? "" : [card.quality, card.sizeBytes ? formatSize(card.sizeBytes) : null].filter(Boolean).join(" · ");
  const route = routeOf(card);
  const eta = downloading && formatEta(card.etaSeconds) !== "–" ? formatEta(card.etaSeconds) : null;
  const showReason = card.reason && (stuck || card.state.tone === "bad");
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
        <span aria-hidden className="pointer-events-none absolute inset-0 overflow-hidden rounded-xl">
          <span className="absolute inset-0 bg-cover bg-center opacity-[0.16] blur-md" style={{ backgroundImage: `url(${JSON.stringify(face.poster)})` }} />
        </span>
      ) : null}
      <Link
        to={face.to}
        aria-label={`${face.title}: ${card.state.label}`}
        className="relative block rounded-xl p-1.5 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <span className="flex gap-2">
          {card.stage !== "searching" ? (
            <ArtworkImage
              src={face.poster}
              alt=""
              loading="lazy"
              className="h-[3rem] w-8 shrink-0 rounded-md bg-surface-3 object-cover shadow-sm"
              fallback={<span aria-hidden className={cn("flex h-[3rem] w-8 shrink-0 items-center justify-center rounded-md bg-surface-3", lane.text)}><lane.icon className="h-4 w-4" /></span>}
            />
          ) : (
            <span aria-hidden className={cn("flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-surface-3", lane.text)}><lane.icon className="h-4 w-4" /></span>
          )}
          <span className="min-w-0 flex-1">
            <span title={card.release && face.linked ? card.release : face.title} className="line-clamp-2 break-words text-[length:var(--type-body-sm)] font-semibold leading-tight text-foreground">{face.title}</span>
            {showReason ? (
              <span title={card.reasonFull ?? card.reason ?? undefined} className="mt-0.5 line-clamp-2 break-words text-[length:var(--type-caption)] leading-tight text-destructive">{card.reason}</span>
            ) : null}
            {!stuck || !card.reason ? (
              <span className={cn("mt-0.5 flex flex-wrap items-start gap-x-1.5 text-[length:var(--type-caption)] leading-tight", TONE_TEXT[card.state.tone])}>
                <span className="flex min-w-0 max-w-full items-start gap-1.5">
                  <span className="mt-[3px] flex shrink-0"><StatusLed tone={card.state.tone} size={6} pulse={card.state.tone === "bad"} /></span>
                  <span className="line-clamp-2 min-w-0 break-words">{card.state.label}</span>
                </span>
              </span>
            ) : null}
          </span>
        </span>
        {downloading ? (
          <span className="mt-1.5 block">
            <span aria-hidden className="block h-1.5 overflow-hidden rounded-full bg-surface-3">
              <span className={cn("live-fill block h-full rounded-full bg-info", moving && "live-shimmer")} style={{ width: `${Math.round(card.progress ?? 0)}%` }} />
            </span>
            <span className="mt-0.5 flex items-baseline justify-between gap-2 text-[length:var(--type-caption)] leading-tight tabular-nums text-muted-foreground">
              <span><CountUp value={Math.round(card.progress ?? 0)} format={(value) => `${Math.round(value)}%`} /> &middot; <CountUp value={card.speedMbps ?? 0} format={formatSpeed} /></span>
              {eta ? <span className="shrink-0">{eta}</span> : null}
            </span>
          </span>
        ) : null}
        <span className="mt-1 block">
          {meta ? <span className="block break-words text-[length:var(--type-caption)] leading-tight text-muted-foreground">{meta}</span> : null}
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
  const inFlight = counts.grabbed + counts.downloading + counts.processor + counts.importing;
  const context: CardContext = { titles, arrivals, action };
  // Lanes with something in them share the width; the empty ones are one short column that names each of them.
  // A stage the summary counts but whose cards are not listed yet is still a lane: it says how many and links to them.
  const busy = STAGES.filter((stage) => grouped[stage].length > 0 || counts[stage] > 0);
  const quiet = STAGES.filter((stage) => !busy.includes(stage));
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
          // Many cards in a lane become compact rows, so the lane is filled with titles and not with a few tall cards and "and N more".
          const lots = cards.length > 3;
          const laneContext: CardContext = lots ? { ...context, compact: true } : context;
          return (
            <div
              key={stage}
              id={`live-lane-${stage}`}
              role="group"
              aria-label={lane.label}
              className={cn("relative flex min-h-0 flex-col border-hairline transition-[flex-grow] duration-500 lg:min-w-0 lg:basis-0 lg:border-l lg:first:border-l-0", stage === "needs" ? "lg:flex-[1.35]" : "lg:flex-1")}
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
                wrap={(shown) => <ul className={cn("px-1.5", lots ? "space-y-1" : "space-y-1.5")}>{shown}</ul>}
                rows={cards.map((card) => renderCard(card, laneContext))}
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
