/**
 * The journey of every title in flight (#1096), as one living board: searched,
 * grabbed, downloading, with the processor, importing, in the library. Each
 * stage is a lane with its own accent and a count that ticks; each title is a
 * card with its details, and a card glides into the next lane (with a brief
 * glow) when its stage changes. A lane with nothing in it says something true.
 *
 * The cards are built by `journey.ts` from the stored lists and the pushed
 * download readings; this only draws them. Lanes keep to whole cards with
 * "and N more" (FitList), so nothing is cut in half.
 */
import { ArrowRight, Download, FolderInput, HardDriveDownload, Library, Search, ShieldCheck, type LucideIcon } from "lucide-react";
import type { ReactElement } from "react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { CountUp } from "../../../components/ui/count-up";
import { StatusLed } from "../../../components/ui/status-led";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { motionAllowed } from "../../../lib/page-motion";
import type { Tone } from "../../../lib/status-tones";
import { cn } from "../../../lib/utils";
import { submitQueueRemoval } from "../../../lib/remove-download";
import { ActionButton, formatEta, formatSize, formatSpeed, type ActionState } from "../downloads-kit";
import { ago } from "../format";
import { FitList, Panel } from "../parts";
import { STAGES, type JourneyCard, type Stage } from "./journey";

interface LaneStyle {
  label: string;
  icon: LucideIcon;
  /** The accent: a bar across the top of the lane and the glow of a card arriving. */
  accent: string;
  bar: string;
  text: string;
  to: string;
}

const LANES: Record<Stage, LaneStyle> = {
  searching: { label: "Searching", icon: Search, accent: "--stage-search", bar: "bg-[hsl(var(--stage-search))]", text: "text-[hsl(var(--stage-search))]", to: "/search-cycles" },
  grabbed: { label: "Grabbed", icon: HardDriveDownload, accent: "--stage-grab", bar: "bg-[hsl(var(--stage-grab))]", text: "text-[hsl(var(--stage-grab))]", to: "/queue" },
  downloading: { label: "Downloading", icon: Download, accent: "--info", bar: "bg-info", text: "text-info", to: "/queue" },
  processor: { label: "With the processor", icon: ShieldCheck, accent: "--stage-process", bar: "bg-[hsl(var(--stage-process))]", text: "text-[hsl(var(--stage-process))]", to: "/queue" },
  importing: { label: "Importing", icon: FolderInput, accent: "--stage-import", bar: "bg-[hsl(var(--stage-import))]", text: "text-[hsl(var(--stage-import))]", to: "/queue" },
  imported: { label: "In the library", icon: Library, accent: "--success", bar: "bg-success", text: "text-success", to: "/activity" }
};

const TONE_TEXT: Record<Tone, string> = { ok: "text-success", warn: "text-warning", bad: "text-destructive", info: "text-foreground", idle: "text-muted-foreground" };

/** What an empty lane truthfully says. */
function emptyNote(stage: Stage, summary: DashboardSummary | undefined): string {
  const imported = summary?.pipeline?.today.imported ?? 0;
  switch (stage) {
    case "searching": {
      const cycle = summary?.automation?.lastSearchCycle;
      return cycle ? `No search running. The last was ${ago(cycle.completedUtc ?? cycle.startedUtc)}.` : "No search has run yet.";
    }
    case "grabbed": return "Nothing waiting to start.";
    case "downloading": return `Nothing downloading. ${imported > 0 ? `${imported.toLocaleString("en-AU")} imported today.` : "Nothing imported yet today."}`;
    case "processor": return "Nothing with the processor.";
    case "importing": return "Nothing waiting to import.";
    default: return imported > 0 ? `${imported.toLocaleString("en-AU")} imported today.` : "Nothing imported yet today.";
  }
}

function renderCard(card: JourneyCard, poster: string | undefined, arrived: boolean, action: ActionState, searchAgain: boolean): ReactElement {
  const lane = LANES[card.stage];
  const downloading = card.stage === "downloading";
  const meta = [card.quality, card.sizeBytes ? formatSize(card.sizeBytes) : null].filter(Boolean).join(" · ");
  const route = [card.indexer, card.client, card.processor].filter(Boolean).join(" → ");
  return (
    <li key={card.key} className={cn("rounded-xl border border-hairline bg-surface-1/60", arrived && motionAllowed() && "live-card-arrive")} style={{ ["--card-glow" as string]: `var(${lane.accent})` }}>
      <Link
        to={card.to}
        aria-label={`${card.title}: ${card.state.label}`}
        className="flex gap-2 rounded-xl p-2 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        {card.stage !== "searching" ? (
          <ArtworkImage src={poster} alt="" loading="lazy" className="h-11 w-7 shrink-0 rounded-md bg-surface-3 object-cover" fallback={<span aria-hidden className={cn("flex h-11 w-7 shrink-0 items-center justify-center rounded-md bg-surface-3", lane.text)}><lane.icon className="h-3.5 w-3.5" /></span>} />
        ) : null}
        <span className="min-w-0 flex-1">
          <span title={card.title} className="block truncate text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">{card.title}</span>
          <span className={cn("mt-0.5 flex items-center gap-1.5 text-[length:var(--type-caption)] leading-tight", TONE_TEXT[card.state.tone])}>
            <StatusLed tone={card.state.tone} size={6} pulse={card.state.tone === "bad"} />
            <span className="min-w-0 truncate">{card.state.label}</span>
            {downloading && formatEta(card.etaSeconds) !== "–" ? <span className="ml-auto shrink-0 tabular-nums text-muted-foreground">{formatEta(card.etaSeconds)}</span> : null}
          </span>
          {downloading ? (
            <span className="mt-1 block">
              <span aria-hidden className="block h-1.5 overflow-hidden rounded-full bg-surface-3">
                <span className={cn("live-fill block h-full rounded-full", card.state.tone === "warn" ? "bg-warning" : "bg-info")} style={{ width: `${Math.round(card.progress ?? 0)}%` }} />
              </span>
              <span className="mt-0.5 block truncate text-[length:var(--type-caption)] leading-tight tabular-nums text-muted-foreground">
                <CountUp value={Math.round(card.progress ?? 0)} format={(value) => `${Math.round(value)}%`} /> &middot; <CountUp value={card.speedMbps ?? 0} format={formatSpeed} />
              </span>
            </span>
          ) : null}
          {meta ? <span className="mt-0.5 block truncate text-[length:var(--type-caption)] leading-tight text-muted-foreground">{meta}</span> : null}
          {route ? <span className="block truncate text-[length:var(--type-caption)] leading-tight text-muted-foreground">{route}</span> : null}
          {!downloading && card.at && (card.stage === "imported" || card.stage === "searching") ? <span className="block truncate text-[length:var(--type-caption)] leading-tight text-muted-foreground">{ago(card.at)}</span> : null}
        </span>
      </Link>
      {searchAgain && card.queueRef ? (
        <div className="flex px-2 pb-2">
          <ActionButton
            label="Search again"
            name={`Search again for ${card.title}`}
            busyKey={`stalled:${card.queueRef.clientId}:${card.queueRef.itemId}`}
            state={action}
            onClick={() =>
              void action.run(`stalled:${card.queueRef!.clientId}:${card.queueRef!.itemId}`, async () => (await submitQueueRemoval(card.queueRef!.clientId, card.queueRef!.itemId, "failed")).message)
            }
          />
        </div>
      ) : null}
    </li>
  );
}

export function Flow({
  grouped,
  counts,
  summary,
  posters,
  arrivals,
  action,
  stalledWithButton
}: {
  grouped: Record<Stage, JourneyCard[]>;
  counts: Record<Stage, number>;
  summary: DashboardSummary | undefined;
  posters: ReadonlyMap<string, string>;
  arrivals: ReadonlySet<string>;
  action: ActionState;
  /** Stalled downloads that have a Search again button here (`clientId:itemId`). */
  stalledWithButton: ReadonlySet<string>;
}) {
  const inFlight = counts.grabbed + counts.downloading + counts.processor + counts.importing;
  return (
    <Panel
      title="Every title's journey"
      count={inFlight > 0 ? <CountUp value={inFlight} format={(value) => `${Math.round(value).toLocaleString("en-AU")} in flight`} /> : "nothing in flight"}
      to="/queue"
      toLabel="Transfers"
    >
      <div className="grid min-h-0 flex-1 grid-cols-2 lg:grid-cols-6">
        {STAGES.map((stage, index) => {
          const lane = LANES[stage];
          const cards = grouped[stage];
          const count = counts[stage];
          return (
            <div key={stage} role="group" aria-label={lane.label} className="relative flex min-h-0 flex-col border-hairline lg:border-l lg:first:border-l-0">
              <div aria-hidden className={cn("h-0.5 w-full", lane.bar, count === 0 && "opacity-30")} />
              <Link to={lane.to} aria-label={`${lane.label}: ${count}`} className="flex items-center gap-1.5 px-2.5 pb-1 pt-2 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
                <lane.icon aria-hidden className={cn("h-3.5 w-3.5 shrink-0", lane.text)} />
                <span className="min-w-0 flex-1 truncate text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-muted-foreground">{lane.label}</span>
                <span className={cn("text-[length:var(--type-body-sm)] font-semibold tabular-nums", count > 0 ? lane.text : "text-muted-foreground")}>
                  <CountUp value={count} format={(value) => Math.round(value).toLocaleString("en-AU")} />
                </span>
                {index < STAGES.length - 1 ? <ArrowRight aria-hidden className="hidden h-3 w-3 shrink-0 text-muted-foreground/60 lg:block" /> : null}
              </Link>
              {cards.length === 0 ? (
                <p className="px-2.5 py-2 text-[length:var(--type-caption)] leading-snug text-muted-foreground">{emptyNote(stage, summary)}</p>
              ) : (
                <FitList
                  moreTo={lane.to}
                  extra={Math.max(0, count - cards.length)}
                  wrap={(shown) => <ul className="space-y-1.5 px-2 pb-1">{shown}</ul>}
                  rows={cards.map((card) => renderCard(card, card.entityId ? posters.get(`${card.mediaType}:${card.entityId}`) : undefined, arrivals.has(card.key), action, card.state.label === "Stalled" && stalledWithButton.has(card.key)))}
                />
              )}
            </div>
          );
        })}
      </div>
    </Panel>
  );
}
