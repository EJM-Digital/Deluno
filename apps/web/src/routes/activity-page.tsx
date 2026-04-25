import { useEffect, useMemo } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import {
  Activity as ActivityIcon,
  AlertTriangle,
  ArrowDownToLine,
  CheckCircle2,
  Clock3,
  Download,
  RefreshCw,
  Workflow,
  Zap
} from "lucide-react";
import {
  fetchJson,
  type ActivityEventItem,
  type DownloadDispatchItem,
  type JobQueueItem,
  type MovieImportRecoverySummary,
  type SeriesImportRecoverySummary
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { GlassTile, PageHero } from "../components/shell/page-hero";
import { EmptyState } from "../components/shell/empty-state";
import { Stagger, StaggerItem } from "../components/shell/motion";
import { toast } from "../components/shell/toaster";

interface ActivityLoaderData {
  activity: ActivityEventItem[];
  dispatches: DownloadDispatchItem[];
  jobs: JobQueueItem[];
  movieRecovery: MovieImportRecoverySummary;
  seriesRecovery: SeriesImportRecoverySummary;
}

export async function activityLoader(): Promise<ActivityLoaderData> {
  const [jobs, activity, dispatches, movieRecovery, seriesRecovery] = await Promise.all([
    fetchJson<JobQueueItem[]>("/api/jobs?take=24"),
    fetchJson<ActivityEventItem[]>("/api/activity?take=40"),
    fetchJson<DownloadDispatchItem[]>("/api/download-dispatches?take=20"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery")
  ]);

  return { activity, dispatches, jobs, movieRecovery, seriesRecovery };
}

export function ActivityPage() {
  const loaderData = useLoaderData() as ActivityLoaderData | undefined;
  const { activity, dispatches, jobs, movieRecovery, seriesRecovery } = loaderData ?? {
    activity: [],
    dispatches: [],
    jobs: [],
    movieRecovery: {
      openCount: 0,
      qualityCount: 0,
      unmatchedCount: 0,
      corruptCount: 0,
      downloadFailedCount: 0,
      importFailedCount: 0,
      recentCases: []
    },
    seriesRecovery: {
      openCount: 0,
      qualityCount: 0,
      unmatchedCount: 0,
      corruptCount: 0,
      downloadFailedCount: 0,
      importFailedCount: 0,
      recentCases: []
    }
  };
  const revalidator = useRevalidator();

  useEffect(() => {
    const timer = window.setInterval(() => {
      revalidator.revalidate();
    }, 10000);
    return () => window.clearInterval(timer);
  }, [revalidator]);

  const activeJobs = jobs.filter((job) => job.status === "queued" || job.status === "running").length;
  const runningJobs = jobs.filter((job) => job.status === "running").length;
  const completedJobs = jobs.filter((job) => job.status === "completed").length;
  const failedJobs = jobs.filter((job) => job.status === "failed").length;
  const openRecovery = movieRecovery.openCount + seriesRecovery.openCount;

  async function handleRetryFailedJobs() {
    try {
      const response = await authedFetch("/api/jobs/retry-failed", { method: "POST" });
      if (!response.ok) {
        throw new Error("Failed jobs could not be requeued.");
      }
      const result = (await response.json()) as { retried?: number };
      toast.success(`${result.retried ?? 0} failed job${result.retried === 1 ? "" : "s"} requeued`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Retry failed.");
    }
  }

  const statusCopy = useMemo(() => {
    if (failedJobs > 0) {
      return {
        tone: "warn" as const,
        text: (
          <>
            <span className="bg-gradient-to-r from-warning via-warning to-destructive bg-clip-text text-transparent">
              {failedJobs} job{failedJobs > 1 ? "s" : ""} need attention
            </span>
          </>
        )
      };
    }
    if (runningJobs > 0) {
      return {
        tone: "primary" as const,
        text: (
          <>
            <span className="bg-gradient-to-r from-primary via-primary to-[hsl(var(--primary-2))] bg-clip-text text-transparent">
              {runningJobs} job{runningJobs > 1 ? "s" : ""} running
            </span>
          </>
        )
      };
    }
    return {
      tone: "success" as const,
      text: (
        <>
          <span className="bg-gradient-to-r from-success via-success to-primary bg-clip-text text-transparent">
            All clear
          </span>
        </>
      )
    };
  }, [failedJobs, runningJobs]);

  return (
    <div className="space-y-[var(--page-gap)]">
      {/* ═══════ HERO ═══════ */}
      <PageHero
        eyebrow="Operations pulse"
        eyebrowIcon={<ActivityIcon className="h-3 w-3 text-primary" />}
        title={<>Everything moving through Deluno · {statusCopy.text}</>}
        subtitle={
          <>
            <span className="font-semibold text-foreground">{activeJobs}</span> in queue ·{" "}
            <span className="font-semibold text-foreground">{dispatches.length}</span> recent
            dispatches ·{" "}
            <span
              className={cn(
                "font-semibold",
                openRecovery > 0 ? "text-warning" : "text-success"
              )}
            >
              {openRecovery > 0 ? `${openRecovery} open` : "no"} recovery case
              {openRecovery === 1 ? "" : "s"}
            </span>
          </>
        }
        stats={[
          { label: "Active", value: activeJobs.toString(), tone: "primary" },
          { label: "Running", value: runningJobs.toString(), tone: "neutral" },
          { label: "Completed", value: completedJobs.toString(), tone: "success" },
          { label: "Failed", value: failedJobs.toString(), tone: failedJobs > 0 ? "danger" : "neutral" }
        ]}
        actions={
          <>
            <Button
              size="lg"
              variant="secondary"
              className="gap-2"
              onClick={() => revalidator.revalidate()}
            >
              <RefreshCw className="h-4 w-4" />
              Refresh
            </Button>
            {failedJobs > 0 ? (
              <Button size="lg" className="gap-2" onClick={() => void handleRetryFailedJobs()}>
                <Zap className="h-4 w-4" />
                Retry {failedJobs} failed
              </Button>
            ) : null}
          </>
        }
      />

      {/* ═══════ SUMMARY RAIL ═══════ */}
      <Stagger className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <StaggerItem className="h-full">
          <PulseMetric
            icon={Workflow}
            label="Queue"
            value={activeJobs}
            sub={`${runningJobs} running`}
            tone="primary"
          />
        </StaggerItem>
        <StaggerItem className="h-full">
          <PulseMetric
            icon={ArrowDownToLine}
            label="Dispatches"
            value={dispatches.length}
            sub="released to clients"
            tone="neutral"
          />
        </StaggerItem>
        <StaggerItem className="h-full">
          <PulseMetric
            icon={AlertTriangle}
            label="Recovery"
            value={openRecovery}
            sub={openRecovery > 0 ? "needs attention" : "all clear"}
            tone={openRecovery > 0 ? "warn" : "success"}
          />
        </StaggerItem>
        <StaggerItem className="h-full">
          <PulseMetric
            icon={CheckCircle2}
            label="Completed"
            value={completedJobs}
            sub={failedJobs > 0 ? `${failedJobs} failed` : "healthy"}
            tone={failedJobs > 0 ? "danger" : "success"}
          />
        </StaggerItem>
      </Stagger>

      {/* ═══════ TIMELINE + QUEUE ═══════ */}
      <div className="grid gap-5 xl:grid-cols-[minmax(0,1.45fr)_minmax(360px,0.95fr)]">
        {/* Queue + Dispatches */}
        <div className="space-y-[var(--page-gap)]">
          <GlassTile>
            <div className="flex items-center justify-between border-b border-hairline px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
              <div>
                <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
                  Background queue
                </p>
                <p className="text-[15px] font-semibold text-foreground">
                  {activeJobs} active · {jobs.length} total
                </p>
              </div>
              {runningJobs > 0 ? (
                <div className="flex items-center gap-1.5 rounded-full bg-info/12 px-2.5 py-1 dark:bg-info/18">
                  <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-info" />
                  <span className="text-[10px] font-bold uppercase tracking-wider text-info">
                    LIVE
                  </span>
                </div>
              ) : null}
            </div>
            <div className="divide-y divide-hairline">
              {jobs.length ? (
                jobs.map((job) => (
                  <Link
                    key={job.id}
                    to={relatedEntityHref(job.relatedEntityType, job.relatedEntityId)}
                      className="group grid gap-3 px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)] transition-colors hover:bg-muted/30 sm:grid-cols-[auto_minmax(0,1fr)_auto]"
                  >
                    <StatusPulse status={job.status} />
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="text-[13.5px] font-semibold text-foreground">
                          {formatJobType(job.jobType)}
                        </p>
                        <Badge variant={jobStatusVariant(job.status)}>
                          {formatJobStatus(job.status)}
                        </Badge>
                        {job.relatedEntityType ? (
                          <Badge>{formatEntityType(job.relatedEntityType)}</Badge>
                        ) : null}
                      </div>
                      <p className="mt-1 line-clamp-1 text-[12.5px] text-muted-foreground">
                        {job.lastError || `Source: ${formatJobSource(job.source)}`}
                      </p>
                      <div className="mt-1 flex flex-wrap gap-3 text-[11px] text-muted-foreground">
                        <span className="tabular">{job.attempts} attempts</span>
                        <span>{formatWhen(job.createdUtc)}</span>
                        {job.workerId ? (
                          <span className="font-mono-code">{job.workerId}</span>
                        ) : null}
                      </div>
                    </div>
                  </Link>
                ))
              ) : (
                <EmptyState
                  size="sm"
                  variant="custom"
                  title="Queue is clear"
                  description="Nothing in the background queue — Deluno is caught up."
                />
              )}
            </div>
          </GlassTile>

          <GlassTile>
            <div className="flex items-center justify-between border-b border-hairline px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
              <div>
                <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
                  Dispatch stream
                </p>
                <p className="text-[15px] font-semibold text-foreground">
                  {dispatches.length} recent releases
                </p>
              </div>
              <Badge variant="success">live</Badge>
            </div>
            <div className="divide-y divide-hairline">
              {dispatches.length ? (
                dispatches.map((dispatch) => (
                  <Link
                    key={dispatch.id}
                    to={
                      dispatch.mediaType === "tv"
                        ? `/tv/${dispatch.entityId}`
                        : `/movies/${dispatch.entityId}`
                    }
                      className="group flex items-center gap-3 px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.62)] transition-colors hover:bg-muted/30"
                  >
                    <span
                      className={cn(
                        "h-2 w-2 shrink-0 rounded-full",
                        dispatch.status === "sent"
                          ? "bg-success shadow-[0_0_6px_hsl(var(--success)/0.5)]"
                          : dispatch.status === "failed"
                            ? "bg-destructive shadow-[0_0_6px_hsl(var(--destructive)/0.5)]"
                          : "bg-warning shadow-[0_0_6px_hsl(var(--warning)/0.5)]"
                      )}
                    />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[13px] font-medium text-foreground">
                        {dispatch.releaseName}
                      </p>
                      <div className="mt-0.5 flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
                        <span className="font-medium text-foreground/80">{dispatch.indexerName}</span>
                        <span className="text-foreground/20">→</span>
                        <span>{dispatch.downloadClientName}</span>
                        <span className="text-foreground/20">·</span>
                        <span className="rounded bg-muted/50 px-1 text-[10px] font-bold uppercase tracking-wider text-muted-foreground dark:bg-white/[0.05]">
                          {dispatch.mediaType === "tv" ? "TV" : "Movie"}
                        </span>
                        <span className="rounded bg-muted/50 px-1 text-[10px] font-bold uppercase tracking-wider text-muted-foreground dark:bg-white/[0.05]">
                          {formatDispatchStatus(dispatch.status)}
                        </span>
                      </div>
                    </div>
                    <span className="shrink-0 text-[11px] tabular text-muted-foreground">
                      {formatWhen(dispatch.createdUtc)}
                    </span>
                  </Link>
                ))
              ) : (
                <EmptyState
                  size="sm"
                  variant="custom"
                  title="No dispatches yet"
                  description="Once Deluno hands a release to a client, it will land here."
                />
              )}
            </div>
          </GlassTile>
        </div>

        {/* Events timeline + recovery */}
        <div className="space-y-[var(--page-gap)]">
          <GlassTile>
            <div className="border-b border-hairline px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
              <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
                Live event stream
              </p>
              <p className="text-[15px] font-semibold text-foreground">Operational timeline</p>
            </div>
              <div className="relative px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.8)]">
              {/* Vertical connector line */}
              <span
                aria-hidden
                className="absolute bottom-4 left-[25px] top-6 w-px bg-gradient-to-b from-primary/40 via-hairline to-transparent"
              />
              <div className="space-y-3.5">
                {activity.length ? (
                  activity.slice(0, 14).map((event, i) => (
                    <Link
                      key={event.id}
                      to={relatedEntityHref(event.relatedEntityType, event.relatedEntityId)}
                      className="group relative flex gap-3 rounded-xl p-1.5 transition-colors hover:bg-muted/30"
                    >
                      <span
                        className={cn(
                          "relative z-10 mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full border-2 border-background bg-card",
                          i === 0 && "bg-primary border-primary shadow-[0_0_0_4px_hsl(var(--primary)/0.2)]"
                        )}
                      >
                        <span
                          className={cn(
                            "h-1.5 w-1.5 rounded-full",
                            i === 0 ? "bg-primary-foreground" : eventDotColor(event.category)
                          )}
                        />
                      </span>
                      <div className="min-w-0 flex-1 pb-0.5">
                        <div className="flex flex-wrap items-center gap-2">
                          <p className="text-[13px] font-semibold text-foreground">
                            {formatEventCategory(event.category)}
                          </p>
                          {event.relatedEntityType ? (
                            <Badge>{formatEntityType(event.relatedEntityType)}</Badge>
                          ) : null}
                        </div>
                        <p className="mt-0.5 line-clamp-2 text-[12px] leading-relaxed text-muted-foreground">
                          {event.message}
                        </p>
                        <p className="mt-0.5 inline-flex items-center gap-1 text-[10.5px] tabular text-muted-foreground/80">
                          <Clock3 className="h-2.5 w-2.5" />
                          {formatWhen(event.createdUtc)}
                        </p>
                      </div>
                    </Link>
                  ))
                ) : (
                  <EmptyState
                    size="sm"
                    variant="custom"
                    title="Nothing happening"
                    description="The event stream will light up as jobs start rolling."
                  />
                )}
              </div>
            </div>
          </GlassTile>

            <GlassTile className="p-[var(--tile-pad)]">
            <div className="mb-3 flex items-center justify-between">
              <div>
                <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
                  Recovery pressure
                </p>
                <p className="text-[15px] font-semibold text-foreground">Import failures</p>
              </div>
              {openRecovery > 0 ? (
                <Badge variant="warning">{openRecovery} open</Badge>
              ) : (
                <Badge variant="success">clear</Badge>
              )}
            </div>
            <div className="space-y-3">
              <RecoveryPanel
                label="Movies"
                openCount={movieRecovery.openCount}
                corruptCount={movieRecovery.corruptCount}
                importFailedCount={movieRecovery.importFailedCount}
                unmatchedCount={movieRecovery.unmatchedCount}
              />
              <RecoveryPanel
                label="TV"
                openCount={seriesRecovery.openCount}
                corruptCount={seriesRecovery.corruptCount}
                importFailedCount={seriesRecovery.importFailedCount}
                unmatchedCount={seriesRecovery.unmatchedCount}
              />
            </div>
          </GlassTile>
        </div>
      </div>
    </div>
  );
}

/* ══════════════════════ PRIMITIVES ══════════════════════ */

function PulseMetric({
  icon: Icon,
  label,
  value,
  sub,
  tone
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  value: number;
  sub: string;
  tone: "primary" | "success" | "warn" | "danger" | "neutral";
}) {
  const tones = {
    primary: "border-primary/20 bg-primary/5 dark:bg-primary/10",
    success: "border-success/20 bg-success/5 dark:bg-success/10",
    warn: "border-warning/20 bg-warning/5 dark:bg-warning/12",
    danger: "border-destructive/20 bg-destructive/5 dark:bg-destructive/10",
    neutral: "border-hairline bg-card dark:border-white/[0.06]"
  }[tone];

  const iconBg = {
    primary: "bg-primary/15 text-primary",
    success: "bg-success/15 text-success",
    warn: "bg-warning/15 text-warning",
    danger: "bg-destructive/15 text-destructive",
    neutral: "bg-muted/60 text-muted-foreground"
  }[tone];

  const valueColor = {
    primary: "text-primary",
    success: "text-success",
    warn: "text-warning",
    danger: "text-destructive",
    neutral: "text-foreground"
  }[tone];

  return (
    <div
      className={cn(
        "relative overflow-hidden rounded-2xl border px-[calc(var(--tile-pad)*0.8)] py-[calc(var(--tile-pad)*0.8)] shadow-card transition-all",
        "before:pointer-events-none before:absolute before:inset-x-0 before:top-0 before:h-px",
        "before:bg-gradient-to-r before:from-transparent before:via-hairline before:to-transparent",
        tones
      )}
    >
      <div className="flex items-start justify-between">
        <div className={cn("flex h-8 w-8 items-center justify-center rounded-xl", iconBg)}>
          <Icon className="h-4 w-4" />
        </div>
      </div>
      <p className="mt-3 text-[length:var(--metric-label-size)] font-bold uppercase tracking-[0.16em] text-muted-foreground/70">
        {label}
      </p>
      <p
        className={cn(
          "mt-0.5 tabular font-display text-[calc(var(--metric-unit-size)+0.65rem)] font-bold tracking-display",
          valueColor
        )}
      >
        {value}
      </p>
      <p className="mt-0.5 text-[length:var(--metric-meta-size)] text-muted-foreground">{sub}</p>
    </div>
  );
}

function StatusPulse({ status }: { status: string }) {
  const cfg = {
    running: {
      color: "bg-info",
      pulse: true,
      glow: "shadow-[0_0_8px_hsl(var(--info)/0.6)]"
    },
    queued: {
      color: "bg-warning",
      pulse: false,
      glow: "shadow-[0_0_6px_hsl(var(--warning)/0.4)]"
    },
    completed: {
      color: "bg-success",
      pulse: false,
      glow: "shadow-[0_0_6px_hsl(var(--success)/0.5)]"
    },
    failed: {
      color: "bg-destructive",
      pulse: true,
      glow: "shadow-[0_0_8px_hsl(var(--destructive)/0.6)]"
    }
  };
  const c = cfg[status as keyof typeof cfg] ?? cfg.queued;
  return (
    <span className="flex h-5 items-center justify-center">
      <span
        className={cn(
          "h-2 w-2 rounded-full",
          c.color,
          c.glow,
          c.pulse && "animate-pulse"
        )}
      />
    </span>
  );
}

function RecoveryPanel({
  corruptCount,
  importFailedCount,
  label,
  openCount,
  unmatchedCount
}: {
  corruptCount: number;
  importFailedCount: number;
  label: string;
  openCount: number;
  unmatchedCount: number;
}) {
  return (
    <div
      className={cn(
        "rounded-xl border p-3",
        openCount > 0
          ? "border-warning/25 bg-warning/5 dark:bg-warning/10"
          : "border-hairline bg-muted/30 dark:bg-white/[0.02]"
      )}
    >
      <div className="flex items-center justify-between gap-3">
        <p className="text-[13px] font-bold text-foreground">{label}</p>
        <Badge variant={openCount > 0 ? "warning" : "success"}>
          {openCount > 0 ? `${openCount} open` : "clear"}
        </Badge>
      </div>
      <div className="mt-3 grid grid-cols-3 gap-2">
        <MiniMetric value={unmatchedCount} label="Unmatched" />
        <MiniMetric value={importFailedCount} label="Failed" />
        <MiniMetric value={corruptCount} label="Corrupt" />
      </div>
    </div>
  );
}

function MiniMetric({ value, label }: { value: number; label: string }) {
  return (
    <div className="rounded-lg border border-hairline bg-card/80 px-2 py-1.5 text-center dark:border-white/[0.05] dark:bg-white/[0.02]">
      <p
        className={cn(
          "tabular text-base font-bold",
          value > 0 ? "text-foreground" : "text-muted-foreground/60"
        )}
      >
        {value}
      </p>
      <p className="text-[10px] text-muted-foreground">{label}</p>
    </div>
  );
}

/* ══════════════════════ FORMATTERS ══════════════════════ */

function formatWhen(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}

function formatJobType(value: string) {
  switch (value) {
    case "movies.catalog.refresh":
      return "Movie catalog refresh";
    case "series.catalog.refresh":
      return "TV catalog refresh";
    case "movies.quality.recalculate":
      return "Movie quality recalc";
    case "series.quality.recalculate":
      return "TV quality recalc";
    default:
      return value;
  }
}

function formatJobStatus(value: string) {
  switch (value) {
    case "queued":
      return "Waiting";
    case "running":
      return "Running";
    case "completed":
      return "Completed";
    case "failed":
      return "Attention";
    default:
      return value;
  }
}

function jobStatusVariant(
  value: string
): "default" | "success" | "warning" | "destructive" | "info" {
  switch (value) {
    case "running":
      return "info";
    case "completed":
      return "success";
    case "failed":
      return "destructive";
    default:
      return "warning";
  }
}

function formatJobSource(value: string) {
  switch (value) {
    case "movies":
      return "Movies";
    case "series":
      return "TV";
    default:
      return value;
  }
}

function formatEntityType(value: string) {
  switch (value) {
    case "movie":
      return "Movie";
    case "series":
      return "Series";
    case "library":
      return "Library";
    default:
      return value;
  }
}

function formatEventCategory(value: string) {
  switch (value) {
    case "job.queued":
      return "Queued";
    case "job.started":
      return "Started";
    case "job.completed":
      return "Completed";
    case "job.failed":
      return "Failed";
    case "library.import.existing":
      return "Library import";
    case "release.dispatched":
      return "Release dispatched";
    case "release.rejected":
      return "Release rejected";
    default:
      return value;
  }
}

function formatDispatchStatus(status: string) {
  switch (status) {
    case "sent":
      return "Sent";
    case "failed":
      return "Failed";
    case "planned":
      return "Needs URL";
    default:
      return status;
  }
}

function eventDotColor(category: string) {
  if (category.includes("failed") || category.includes("rejected")) return "bg-destructive";
  if (category.includes("completed") || category.includes("dispatched")) return "bg-success";
  if (category.includes("started") || category.includes("running")) return "bg-info";
  return "bg-muted-foreground/60";
}

function relatedEntityHref(type: string | null, id: string | null) {
  if (!type || !id) return "/activity";
  switch (type) {
    case "movie":
      return `/movies/${id}`;
    case "series":
      return `/tv/${id}`;
    default:
      return "/activity";
  }
}
