/**
 * The acquisition pipeline, right now (#270).
 *
 * Deluno's whole job is moving a wanted title through a fixed set of stages, so
 * the dashboard shows those stages rather than a single "downloading" number
 * that hides where work is actually sitting. Every count comes from the
 * download-client telemetry summary the page already fetches, and that summary
 * is invalidated by the queue realtime events — so this moves on its own.
 *
 * Drawn as a flow rather than a list because that is what it is: work enters at
 * the left and leaves at the right, and the eye should be able to find the
 * stage holding things up without reading five numbers. A track between two
 * stages animates only when there is something to carry, so a still board means
 * a still pipeline rather than a broken animation.
 *
 * Stalled is a stage, not a footnote: a transfer that has stopped looks
 * identical to one that is merely slow unless the pane says so.
 */
import { Link } from "react-router-dom";
import { CountUp } from "../ui/count-up";
import { StatusLed, type LedTone } from "../ui/status-led";
import { cn } from "../../lib/utils";
import type { DownloadTelemetrySummary, MonitoringPerformanceSummary } from "../../lib/api";
import type { ActiveDownload } from "../../lib/media-types";

interface Stage {
  label: string;
  short: string;
  count: number;
  tone: LedTone;
}

export function AcquisitionPipeline({
  summary,
  performance,
  inFlight = [],
  className
}: {
  summary: DownloadTelemetrySummary;
  /** Measured stage timings, when Deluno has enough completed runs to average. */
  performance?: MonitoringPerformanceSummary | null;
  /**
   * The individual transfers behind the stage counts. They used to live in a
   * separate full-width card, which meant the same subject was stated twice on
   * one screen — the counts here and the rows there (#270).
   */
  inFlight?: ActiveDownload[];
  className?: string;
}) {
  const stages: Stage[] = [
    { label: "Queued", short: "Queued", count: summary.queuedCount, tone: "idle" },
    { label: "Downloading", short: "Down", count: summary.activeCount, tone: "info" },
    { label: "Stalled", short: "Stalled", count: summary.stalledCount, tone: "warn" },
    { label: "Ready to import", short: "Ready", count: summary.importReadyCount, tone: "idle" },
    { label: "Importing", short: "Import", count: summary.processingCount, tone: "ok" }
  ];

  const total = stages.reduce((sum, stage) => sum + stage.count, 0);
  const timings = buildTimings(performance);
  const moving = summary.activeCount > 0 || summary.processingCount > 0;

  return (
    <section
      className={cn(
        "relative flex flex-col overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]",
        className
      )}
    >
      {moving ? (
        <span aria-hidden className="pointer-events-none absolute -left-16 -top-20 h-48 w-48 rounded-full bg-primary/12 blur-[70px]" />
      ) : null}

      <header className="relative flex items-baseline justify-between gap-3 px-[var(--card-pad-x)] pt-3">
        <span className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
          In the pipeline
        </span>
        <Link
          to="/queue"
          className="text-[length:var(--type-caption)] font-medium text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          Transfers
        </Link>
      </header>

      <div className="relative px-[var(--card-pad-x)]">
        <span className="block text-[length:var(--type-title-md)] font-semibold tabular-nums leading-tight text-foreground">
          <CountUp value={total} />
        </span>
        <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">
          {total === 0 ? "nothing moving" : `${total === 1 ? "item" : "items"} between grabbed and imported`}
        </span>
      </div>

      {/* The flow. Each stage is a node; the track after it carries work on to
          the next one and only animates when this stage actually holds any. */}
      <ol className="relative mt-3 flex flex-1 items-center px-[var(--card-pad-x)] pb-3">
        {stages.map((stage, index) => (
          <li key={stage.label} className="flex min-w-0 flex-1 items-center">
            <div className="flex min-w-0 flex-1 flex-col items-center gap-1 text-center">
              <span
                className={cn(
                  "flex h-8 w-8 items-center justify-center rounded-full border text-[length:var(--type-caption)] font-semibold tabular-nums transition-colors duration-500",
                  stage.count === 0
                    ? "border-hairline bg-surface-2 text-muted-foreground/60"
                    : stage.tone === "warn"
                      ? "border-warning/40 bg-warning/12 text-warning"
                      : stage.tone === "ok"
                        ? "border-success/40 bg-success/12 text-success"
                        : stage.tone === "info"
                          ? "border-primary/40 bg-primary/12 text-primary"
                          : "border-hairline bg-surface-2 text-foreground"
                )}
              >
                <CountUp value={stage.count} />
              </span>
              <span className="flex min-w-0 items-center gap-1">
                <StatusLed tone={stage.count === 0 ? "idle" : stage.tone} size={5} pulse={stage.count > 0 && stage.tone !== "idle"} />
                <span className="truncate text-[length:var(--type-micro)] text-muted-foreground">
                  <span className="hidden sm:inline">{stage.label}</span>
                  <span className="sm:hidden">{stage.short}</span>
                </span>
              </span>
            </div>

            {index < stages.length - 1 ? <FlowTrack active={stage.count > 0} /> : null}
          </li>
        ))}
      </ol>

      {inFlight.length ? (
        <div className="relative max-h-[168px] overflow-y-auto border-t border-hairline">
          {inFlight.slice(0, 8).map((download) => {
            const finished = download.progress >= 100 || download.speedMbps <= 0;
            return (
              <Link
                key={download.id}
                to="/queue"
                className="block border-b border-hairline px-[var(--card-pad-x)] py-2 transition-colors last:border-b-0 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
              >
                <span className="flex items-baseline justify-between gap-2">
                  <span className="min-w-0 truncate text-[length:var(--type-caption)] font-medium text-foreground">
                    {download.title}
                  </span>
                  <span className="shrink-0 text-[length:var(--type-micro)] tabular-nums text-muted-foreground">
                    {finished ? "waiting to import" : `${Math.round(download.progress)}% · ${download.speedMbps.toFixed(1)} MB/s`}
                  </span>
                </span>
                <span aria-hidden className="mt-1 block h-1 w-full overflow-hidden rounded-full bg-surface-3">
                  <span
                    className={cn("block h-full rounded-full transition-[width] duration-500", finished ? "bg-success/70" : "bg-primary")}
                    style={{ width: `${Math.min(100, Math.max(0, download.progress))}%` }}
                  />
                </span>
              </Link>
            );
          })}
        </div>
      ) : null}

      {timings.length ? (
        // How fast the pipeline actually runs, averaged over completed work.
        // Absent until Deluno has measured enough of it to say.
        <footer className="relative flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-hairline px-[var(--card-pad-x)] py-1.5">
          {timings.map((timing) => (
            <span key={timing.label} className="text-[length:var(--type-micro)] text-muted-foreground">
              {timing.label} <span className="font-medium tabular-nums text-foreground">{timing.value}</span>
            </span>
          ))}
        </footer>
      ) : null}
    </section>
  );
}

/** The connector between two stages. Carries a travelling mote while work flows. */
function FlowTrack({ active }: { active: boolean }) {
  return (
    <span aria-hidden className="relative mx-0.5 mb-5 h-px w-4 shrink-0 sm:w-6">
      <span className={cn("absolute inset-0 rounded-full", active ? "bg-primary/35" : "bg-hairline")} />
      {active ? (
        <span className="absolute inset-y-0 left-0 w-1 rounded-full bg-primary motion-safe:animate-[flow_1.8s_linear_infinite] motion-reduce:opacity-60" />
      ) : null}
    </span>
  );
}

function buildTimings(performance?: MonitoringPerformanceSummary | null) {
  if (!performance) return [];

  return [
    { label: "grabbed → seen", seconds: performance.averageGrabToDetectionSeconds },
    { label: "seen → imported", seconds: performance.averageDetectionToImportSeconds },
    { label: "search cycle", seconds: performance.averageSearchCycleSeconds }
  ]
    .filter((entry): entry is { label: string; seconds: number } => typeof entry.seconds === "number")
    .map((entry) => ({ label: entry.label, value: formatDuration(entry.seconds) }));
}

function formatDuration(seconds: number) {
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`;
  return `${(seconds / 3600).toFixed(1)}h`;
}
