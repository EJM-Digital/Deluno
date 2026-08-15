import { Link, useLoaderData } from "react-router-dom";
import {
  AlertTriangle,
  ArrowUpRight,
  Calendar,
  CheckCircle2,
  Download,
  Film,
  HardDrive,
  RadioTower,
  Sparkles,
  Tv,
  Zap
} from "lucide-react";
import type { ActiveDownload, IndexerHealthItem, MediaItem } from "../lib/media-types";
import { MEDIA_STATUS_PRESENTATION, mediaStatusIsActive } from "../lib/media-status-presentation";
import {
  fetchJson,
  type DownloadClientItem,
  type DownloadTelemetryOverview,
  type IndexerItem,
  type LibraryItem,
  type LibraryAutomationStateItem,
  type MovieListItem,
  type MovieWantedSummary,
  type SearchCycleRunItem,
  type SearchRetryWindowItem,
  type SeriesUpcomingEpisodeItem,
  type SeriesListItem,
  type SeriesWantedSummary
} from "../lib/api";
import { adaptIndexerHealth, adaptMovieItems, adaptSeriesItems, adaptTelemetryDownloads } from "../lib/ui-adapters";
import { JOB_STATUS, isJobActive, type JobStatus } from "../lib/job-status-constants";
import { cn } from "../lib/utils";
import { OnboardingBanner } from "../components/shell/onboarding-banner";
import { Badge } from "../components/ui/badge";
import { RouteSkeleton } from "../components/shell/skeleton";

interface DashboardLoaderData {
  activeDownloads: ActiveDownload[];
  activeDownloadCount: number;
  indexerHealth: IndexerHealthItem[];
  indexerHealthPercent: number | null;
  configuredLibraryCount: number;
  librarySizeTb: string;
  missingCount: number;
  monitoredCount: number;
  recentlyAdded: MediaItem[];
  totalCount: number;
  upcoming: DashboardUpcomingItem[];
  upgradeCount: number;
  waitingCount: number;
  automation: LibraryAutomationStateItem[];
  searchCycles: SearchCycleRunItem[];
  retryWindows: SearchRetryWindowItem[];
  onboarding: {
    hasIndexer: boolean;
    hasDownloadClient: boolean;
    hasLibrary: boolean;
  };
}

interface DashboardUpcomingItem {
  id: string;
  day: string;
  title: string;
  episode: string;
  dateLabel: string;
  network: string;
  poster: string | null;
  href: string;
  startsAt: string;
}

export async function dashboardLoader(): Promise<DashboardLoaderData> {
  const emptyMovieWanted: MovieWantedSummary = { totalWanted: 0, missingCount: 0, upgradeCount: 0, waitingCount: 0, recentItems: [] };
  const emptySeriesWanted: SeriesWantedSummary = { totalWanted: 0, missingCount: 0, upgradeCount: 0, waitingCount: 0, recentItems: [] };
  const emptyTelemetry: DownloadTelemetryOverview = {
    summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0 },
    clients: [],
    capturedUtc: new Date().toISOString()
  };

  const [movieItems, movieWanted, showItems, showWanted, telemetry, indexers, clients, libraries, automation, searchCycles, retryWindows, upcomingEpisodes] = await Promise.all([
    fetchJson<MovieListItem[]>("/api/movies").catch((): MovieListItem[] => []),
    fetchJson<MovieWantedSummary>("/api/movies/wanted").catch(() => emptyMovieWanted),
    fetchJson<SeriesListItem[]>("/api/series").catch((): SeriesListItem[] => []),
    fetchJson<SeriesWantedSummary>("/api/series/wanted").catch(() => emptySeriesWanted),
    fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry").catch(() => emptyTelemetry),
    fetchJson<IndexerItem[]>("/api/indexers").catch((): IndexerItem[] => []),
    fetchJson<DownloadClientItem[]>("/api/download-clients").catch((): DownloadClientItem[] => []),
    fetchJson<LibraryItem[]>("/api/libraries").catch((): LibraryItem[] => []),
    fetchJson<LibraryAutomationStateItem[]>("/api/library-automation").catch((): LibraryAutomationStateItem[] => []),
    fetchJson<SearchCycleRunItem[]>("/api/search-cycles?take=8").catch((): SearchCycleRunItem[] => []),
    fetchJson<SearchRetryWindowItem[]>("/api/search-retry-windows?take=8").catch((): SearchRetryWindowItem[] => []),
    fetchJson<SeriesUpcomingEpisodeItem[]>("/api/series/upcoming?take=12&hours=72").catch((): SeriesUpcomingEpisodeItem[] => [])
  ]);

  const adaptedMovies = adaptMovieItems(movieItems, movieWanted);
  const adaptedShows = adaptSeriesItems(showItems, showWanted);
  const allItems = [...adaptedMovies, ...adaptedShows];
  const activeDownloads = adaptTelemetryDownloads(telemetry);
  const indexerHealth = adaptIndexerHealth(indexers, clients);
  const librarySizeGb = allItems.reduce((sum, item) => sum + (item.sizeGb ?? 0), 0);
  const monitoredCount = allItems.filter((item) => item.monitored).length;
  const healthyCount = indexerHealth.filter((item) => item.status === "healthy").length;

  return {
    activeDownloads,
    activeDownloadCount: telemetry.summary.activeCount + telemetry.summary.queuedCount + telemetry.summary.importReadyCount,
    indexerHealth,
    indexerHealthPercent: indexerHealth.length ? Math.round((healthyCount / indexerHealth.length) * 100) : null,
    configuredLibraryCount: libraries.length,
    librarySizeTb: (librarySizeGb / 1024).toFixed(1),
    missingCount: movieWanted.missingCount + showWanted.missingCount,
    monitoredCount,
    recentlyAdded: allItems
      .slice()
      .sort((left, right) => right.added.localeCompare(left.added))
      .slice(0, 14),
    totalCount: allItems.length,
    upcoming: buildDashboardUpcoming(upcomingEpisodes, showWanted, movieWanted),
    upgradeCount: movieWanted.upgradeCount + showWanted.upgradeCount,
    waitingCount: movieWanted.waitingCount + showWanted.waitingCount,
    automation,
    searchCycles,
    retryWindows,
    onboarding: {
      hasIndexer: indexers.length > 0,
      hasDownloadClient: clients.length > 0,
      hasLibrary: libraries.length > 0
    }
  };
}

export function DashboardPage() {
  const data = useLoaderData() as DashboardLoaderData | undefined;
  if (!data) return <RouteSkeleton />;
  const healthIssues = data.indexerHealth.filter((item) => item.status !== "healthy").length;
  const topDownload = data.activeDownloads[0];
  const upcomingGroups = groupDashboardUpcoming(data.upcoming);
  const runningAutomation = data.automation.filter((item) => isJobActive(item.status as JobStatus) || item.searchRequested).length;
  const latestCycle = data.searchCycles[0] ?? null;

  return (
    <div className="space-y-[var(--grid-gap)]">
      <OnboardingBanner state={data.onboarding} />

      <section className="relative overflow-hidden rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card dark:border-white/[0.06]">
        <span aria-hidden className="pointer-events-none absolute -right-16 -top-20 h-48 w-48 rounded-full bg-primary/10 blur-3xl" />
        <div className="relative flex flex-col gap-[var(--grid-gap)] lg:flex-row lg:items-end lg:justify-between">
          <div className="min-w-0">
            <p className="flex items-center gap-2 text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground">
              <Sparkles className="h-3.5 w-3.5 text-primary" />
              Dashboard
            </p>
            <h2 className="mt-1 font-display text-[length:var(--type-title-md)] font-semibold tracking-tight text-foreground">
              Your media, your way.
            </h2>
            <p className="mt-1 max-w-2xl text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">
              Build your library, decide what to monitor, and let Deluno handle the work in the background.
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Link to="/movies?add=true" className="inline-flex h-[var(--control-height-sm)] items-center gap-2 rounded-xl bg-primary px-4 text-[13px] font-semibold text-primary-foreground shadow-glow transition hover:-translate-y-0.5">
              <Film className="h-4 w-4" />
              Add a movie
            </Link>
            <Link to="/tv?add=true" className="inline-flex h-[var(--control-height-sm)] items-center gap-2 rounded-xl border border-hairline bg-card px-4 text-[13px] font-semibold text-muted-foreground transition hover:border-primary/30 hover:text-foreground">
              <Tv className="h-4 w-4" />
              Add a show
            </Link>
            <Link to="/settings" className="inline-flex h-[var(--control-height-sm)] items-center gap-2 rounded-xl px-3 text-[13px] font-semibold text-primary transition hover:bg-primary/8">
              Tune automation
              <ArrowUpRight className="h-3.5 w-3.5" />
            </Link>
          </div>
        </div>
      </section>

      <section className="dashboard-metric-grid">
        <MetricPlane
          label="Library"
          value={data.totalCount.toLocaleString()}
          meta={data.totalCount > 0 ? `${data.librarySizeTb} TB indexed` : data.configuredLibraryCount > 0 ? `${data.configuredLibraryCount} library${data.configuredLibraryCount === 1 ? "" : "ies"} ready for media` : "No library configured yet"}
          icon={HardDrive}
          tone="primary"
          wide
        />
        <MetricPlane
          label="Monitored"
          value={data.monitoredCount.toLocaleString()}
          meta="Titles being tracked"
          icon={Film}
          tone="success"
        />
        <MetricPlane
          label="Queue"
          value={data.activeDownloadCount.toString()}
          meta={topDownload ? `${topDownload.speedMbps.toFixed(1)} MB/s active` : "No active transfers"}
          icon={Download}
          tone="info"
        />
        <MetricPlane
          label="Missing"
          value={data.missingCount.toString()}
          meta={`${data.upgradeCount} upgrades · ${data.waitingCount} waiting`}
          icon={AlertTriangle}
          tone={data.missingCount > 0 ? "warn" : "success"}
        />
        <MetricPlane
          label="Health"
          value={data.indexerHealthPercent === null ? "—" : `${data.indexerHealthPercent}`}
          unit={data.indexerHealthPercent === null ? undefined : "%"}
          meta={data.indexerHealth.length === 0 ? "No providers connected" : healthIssues > 0 ? `${healthIssues} providers need review` : "All providers healthy"}
          icon={RadioTower}
          tone={data.indexerHealth.length === 0 ? "neutral" : healthIssues > 0 ? "warn" : "success"}
        />
      </section>

      <section className="grid gap-[var(--grid-gap)] xl:grid-cols-12">
        <RenderPanel className="xl:col-span-8">
          <PanelHeader
            eyebrow="Live work"
            title="Transfers in progress"
            action={
              <Link to="/queue" className="inline-flex items-center gap-1 text-[length:var(--type-caption)] font-semibold text-primary">
                Open queue
                <ArrowUpRight className="h-3.5 w-3.5" />
              </Link>
            }
          />
          {data.activeDownloads.length ? (
            <div className="divide-y divide-hairline">
              {data.activeDownloads.slice(0, 5).map((download) => <DownloadSummaryRow key={download.id} download={download} />)}
            </div>
          ) : (
            <EmptyPanelText>No downloads, processing, or imports need your attention right now.</EmptyPanelText>
          )}
        </RenderPanel>

        <RenderPanel className="xl:col-span-4">
          <PanelHeader eyebrow="Status" title="Needs attention" />
          <div className="space-y-3">
            <DecisionRow
              tone={data.missingCount > 0 ? "warn" : "success"}
              title={data.missingCount > 0 ? "Missing media waiting" : "Everything looks good"}
              text={data.missingCount > 0 ? `${data.missingCount} titles are missing or still waiting for a valid release.` : "No missing media currently requires manual attention."}
              href="/movies"
            />
            <DecisionRow
              tone={data.indexerHealth.length === 0 ? "neutral" : healthIssues > 0 ? "warn" : "success"}
              title={data.indexerHealth.length === 0 ? "Providers not connected" : healthIssues > 0 ? "Provider health degraded" : "Providers healthy"}
              text={data.indexerHealth.length === 0 ? "Connect a search source and download client before Deluno can automate acquisition." : healthIssues > 0 ? `${healthIssues} indexer or client checks need review.` : "All configured providers are currently responding."}
              href="/indexers"
            />
            <DecisionRow
              tone={data.activeDownloadCount > 0 ? "info" : "neutral"}
              title={data.activeDownloadCount > 0 ? "Queue moving" : "Queue idle"}
              text={topDownload ? `${topDownload.title} is leading the active queue.` : "No active imports are waiting to be moved."}
              href="/queue"
            />
            <DecisionRow
              tone={runningAutomation > 0 ? "info" : data.retryWindows.length > 0 ? "warn" : "neutral"}
              title={runningAutomation > 0 ? "Automation active" : latestCycle ? "Last search cycle recorded" : "Automation waiting"}
              text={
                runningAutomation > 0
                  ? `${runningAutomation} library search${runningAutomation === 1 ? "" : "es"} queued or running.`
                  : latestCycle
                    ? `${latestCycle.libraryName}: ${latestCycle.plannedCount} checked, ${latestCycle.queuedCount} sent, ${latestCycle.skippedCount} held back.`
                    : "Scheduled searches will appear here once libraries are configured."
              }
              href="/system"
            />
          </div>
        </RenderPanel>

        <RenderPanel className="xl:col-span-8">
          <PanelHeader
            eyebrow="Library"
            title="Fresh in the library"
            action={
              <Link to="/movies" className="inline-flex items-center gap-1 text-[length:var(--type-caption)] font-semibold text-primary">
                Browse all
                <ArrowUpRight className="h-3.5 w-3.5" />
              </Link>
            }
          />
          <div className="dashboard-poster-grid">
            {data.recentlyAdded.slice(0, 12).map((item) => (
              <PosterPreview key={`${item.type}-${item.id}`} item={item} />
            ))}
          </div>
          {data.recentlyAdded.length === 0 ? (
            <EmptyPanelText>
              Your recently imported movies and shows will appear here. Add a title when you are ready to start your library.
            </EmptyPanelText>
          ) : null}
        </RenderPanel>

        <div className="grid gap-[var(--grid-gap)] xl:col-span-4">
          <RenderPanel>
            <PanelHeader eyebrow="Calendar" title="Next 72 hours" icon={Calendar} />
            <div className="space-y-[var(--page-gap)]">
              {upcomingGroups.length ? (
                upcomingGroups.slice(0, 3).map(({ day, entries }) => (
                  <div key={day} className="space-y-2">
                    <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.18em] text-primary">{day}</p>
                    {entries.slice(0, 2).map((entry) => (
                      <Link key={entry.id} to={entry.href} className="flex items-center gap-3 rounded-xl border border-hairline bg-surface-1/70 p-2.5 transition hover:border-primary/30 hover:bg-primary/5">
                        <Artwork src={entry.poster} title={entry.title} className="h-12 w-8 rounded-lg" />
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{entry.title}</span>
                          <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">
                            <span className="text-primary">{entry.episode}</span> · {entry.network}
                          </span>
                        </span>
                      </Link>
                    ))}
                  </div>
                ))
              ) : (
                <EmptyPanelText>No upcoming episodes or retry windows in the next 72 hours.</EmptyPanelText>
              )}
            </div>
          </RenderPanel>

          <RenderPanel>
            <PanelHeader eyebrow="Providers" title="Search providers" icon={RadioTower} />
            <div className="space-y-2">
              {data.indexerHealth.length ? (
                data.indexerHealth.slice(0, 6).map((item) => <HealthRow key={item.id} item={item} />)
              ) : (
                <EmptyPanelText>No providers configured yet.</EmptyPanelText>
              )}
            </div>
          </RenderPanel>
        </div>

      </section>
    </div>
  );
}

function MetricPlane({
  label,
  value,
  unit,
  meta,
  icon: Icon,
  tone,
  visual,
  wide = false
}: {
  label: string;
  value: string;
  unit?: string;
  meta: string;
  icon: typeof HardDrive;
  tone: "primary" | "success" | "warn" | "info" | "neutral";
  visual?: React.ReactNode;
  wide?: boolean;
}) {
  return (
    <article
      className={cn(
        "group relative min-h-[var(--metric-plane-min-height)] min-w-0 overflow-hidden rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card",
        "transition duration-200 hover:-translate-y-0.5 hover:border-primary/30 hover:shadow-lg dark:border-white/[0.06]",
        wide && "xl:col-span-2 2xl:col-span-2"
      )}
    >
      <AmbientTone tone={tone} />
      <div className="relative flex h-full flex-col justify-between">
        <div className="flex min-w-0 items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="density-nowrap text-[length:var(--metric-label-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
            <div className="mt-3 flex min-w-0 items-end gap-1.5">
              <span className="density-nowrap tabular font-display text-[length:var(--metric-value-size)] font-bold leading-none tracking-display text-foreground">
                {value}
              </span>
              {unit ? <span className="pb-1 text-[length:var(--metric-unit-size)] font-semibold text-muted-foreground">{unit}</span> : null}
            </div>
          </div>
          <span className={cn("flex h-10 w-10 shrink-0 items-center justify-center rounded-xl border", toneClass(tone, "icon"))}>
            <Icon className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" strokeWidth={1.9} />
          </span>
        </div>
        <div>
          {visual ? <div className="mb-2">{visual}</div> : null}
          <p className="truncate text-[length:var(--metric-meta-size)] font-medium text-muted-foreground">{meta}</p>
        </div>
      </div>
    </article>
  );
}

function RenderPanel({
  children,
  className
}: {
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <section className={cn("relative self-start overflow-hidden rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card dark:border-white/[0.06]", className)}>
      <span
        aria-hidden
        className="pointer-events-none absolute inset-x-5 top-0 h-px rounded-full"
        style={{ background: "linear-gradient(90deg, transparent, hsl(var(--primary)/0.34), hsl(var(--primary-2)/0.22), transparent)" }}
      />
      {children}
    </section>
  );
}

function PanelHeader({
  eyebrow,
  title,
  action,
  icon: Icon
}: {
  eyebrow: string;
  title: string;
  action?: React.ReactNode;
  icon?: typeof Sparkles;
}) {
  return (
    <div className="mb-[var(--grid-gap)] flex items-start justify-between gap-[var(--grid-gap)]">
      <div className="min-w-0">
        <p className="flex items-center gap-2 text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground">
          {Icon ? <Icon className="h-3.5 w-3.5 text-primary" /> : null}
          {eyebrow}
        </p>
        <h2 className="mt-1 font-display text-[length:var(--type-title-sm)] font-semibold tracking-tight text-foreground">{title}</h2>
      </div>
      {action ? <div className="shrink-0">{action}</div> : null}
    </div>
  );
}

function DecisionRow({
  title,
  text,
  tone,
  href
}: {
  title: string;
  text: string;
  tone: "success" | "warn" | "info" | "neutral";
  href: string;
}) {
  return (
    <Link to={href} className="group relative block overflow-hidden rounded-xl border border-hairline bg-surface-1/70 p-4 transition hover:border-primary/30 hover:bg-primary/5">
      <AmbientTone tone={tone} subtle />
      <div className="relative flex gap-3">
        <span className={cn("mt-1 h-2.5 w-2.5 shrink-0 rounded-full", toneClass(tone, "dot"))} />
        <span className="min-w-0">
          <span className="block text-[length:var(--type-body-sm)] font-semibold text-foreground">{title}</span>
          <span className="mt-1 block text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{text}</span>
        </span>
      </div>
    </Link>
  );
}

function DownloadSummaryRow({ download }: { download: ActiveDownload }) {
  return (
    <Link to="/queue" className="grid gap-3 px-[var(--tile-pad)] py-3 transition hover:bg-primary/5 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center">
      <span className="min-w-0">
        <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{download.title}</span>
        <span className="mt-1 block text-[length:var(--type-caption)] text-muted-foreground">
          {download.quality ?? "Quality not reported"} · {download.indexer || "source not reported"}
        </span>
        <span className="mt-2 block h-1.5 overflow-hidden rounded-full bg-muted/60">
          <span className="block h-full rounded-full bg-primary" style={{ width: `${Math.max(0, Math.min(100, download.progress))}%` }} />
        </span>
      </span>
      <span className="text-left sm:text-right">
        <span className="block font-mono text-[13px] font-semibold text-foreground">{Math.round(download.progress)}%</span>
        <span className="block text-[length:var(--type-caption)] text-muted-foreground">{download.speedMbps.toFixed(1)} MB/s · {download.etaMinutes > 0 ? `${download.etaMinutes} min left` : "finishing"}</span>
      </span>
    </Link>
  );
}

function PosterPreview({ item }: { item: MediaItem }) {
  return (
    <Link to={item.type === "show" ? `/tv/${item.id}` : `/movies/${item.id}`} className="group min-w-0">
      <div className="relative aspect-[2/3] overflow-hidden rounded-2xl border border-hairline bg-surface-2 shadow-card transition duration-200 group-hover:-translate-y-0.5 group-hover:border-primary/40 group-hover:shadow-lg">
        <Artwork src={item.poster} title={item.title} className="h-full w-full" />
        <div className="absolute left-2 top-2">
          <Badge className="border-white/15 bg-background/55 text-[length:var(--type-micro)] text-foreground backdrop-blur-md">
            {item.type === "show" ? "TV" : "Movie"}
          </Badge>
        </div>
        <div className="absolute right-2 top-2">
          <span className={cn("block h-2.5 w-2.5 rounded-full ring-2 ring-background/70", statusDot(item.status))} />
        </div>
        <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-background/95 via-background/55 to-transparent p-3 pt-12">
          <p className="line-clamp-1 text-[length:var(--type-body-sm)] font-semibold text-foreground">{item.title}</p>
          <p className="mt-0.5 flex items-center justify-between gap-2 text-[length:var(--type-caption)] text-muted-foreground">
            <span className="tabular">{item.year ?? "Unknown"}</span>
            <span className="tabular">{shortQuality(item.quality)}</span>
          </p>
        </div>
      </div>
    </Link>
  );
}

function Artwork({
  src,
  title,
  className
}: {
  src: string | null;
  title: string;
  className?: string;
}) {
  if (src) {
    return <img src={src} alt={title} className={cn("object-cover", className)} loading="lazy" />;
  }

  return (
    <span className={cn("flex items-center justify-center bg-gradient-to-br from-surface-2 to-surface-3 text-center text-muted-foreground", className)}>
      <span className="px-2 font-display text-lg font-bold tracking-tight">{title.slice(0, 2).toUpperCase()}</span>
    </span>
  );
}

function HealthRow({ item }: { item: IndexerHealthItem }) {
  return (
    <div className="flex items-center justify-between gap-3 rounded-xl border border-hairline bg-surface-1/70 px-3 py-2.5">
      <span className="min-w-0">
        <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{item.name}</span>
        <span className="block text-[length:var(--type-caption)] text-muted-foreground">
          {item.responseMs === null ? "Never checked" : `${item.responseMs} ms`}
        </span>
      </span>
      <span className="flex items-center gap-2 text-[length:var(--type-caption)] font-semibold capitalize text-muted-foreground">
        <span className={cn("h-2.5 w-2.5 rounded-full", healthDot(item.status))} />
        {item.status}
      </span>
    </div>
  );
}

function EmptyPanelText({ children }: { children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-dashed border-hairline bg-surface-1/60 p-4 text-[length:var(--type-body-sm)] text-muted-foreground">
      {children}
    </div>
  );
}

function AmbientTone({ tone, subtle = false }: { tone: "primary" | "success" | "warn" | "info" | "neutral"; subtle?: boolean }) {
  if (tone === "neutral") return null;
  const color = {
    primary: "hsl(var(--primary))",
    success: "hsl(var(--success))",
    warn: "hsl(var(--warning))",
    info: "hsl(var(--info))"
  }[tone];
  return (
    <span
      aria-hidden
      className="pointer-events-none absolute -right-16 -top-20 h-48 w-48 rounded-full blur-3xl"
      style={{ background: color, opacity: subtle ? 0.07 : 0.12 }}
    />
  );
}

function toneClass(tone: "primary" | "success" | "warn" | "info" | "neutral", part: "icon" | "dot") {
  if (part === "dot") {
    return {
      primary: "bg-primary shadow-[0_0_10px_hsl(var(--primary)/0.6)]",
      success: "bg-success shadow-[0_0_10px_hsl(var(--success)/0.6)]",
      warn: "bg-warning shadow-[0_0_10px_hsl(var(--warning)/0.6)]",
      info: "bg-info shadow-[0_0_10px_hsl(var(--info)/0.6)]",
      neutral: "bg-muted-foreground"
    }[tone];
  }

  return {
    primary: "border-primary/20 bg-primary/12 text-primary",
    success: "border-success/20 bg-success/12 text-success",
    warn: "border-warning/25 bg-warning/12 text-warning",
    info: "border-info/25 bg-info/12 text-info",
    neutral: "border-hairline bg-muted/40 text-muted-foreground"
  }[tone];
}

function statusDot(status: MediaItem["status"]) {
  return cn(MEDIA_STATUS_PRESENTATION[status].dot, mediaStatusIsActive(status) && "animate-pulse");
}

function healthDot(status: IndexerHealthItem["status"]) {
  return {
    healthy: "bg-success shadow-[0_0_10px_hsl(var(--success)/0.6)]",
    degraded: "bg-warning shadow-[0_0_10px_hsl(var(--warning)/0.6)]",
    down: "bg-destructive shadow-[0_0_10px_hsl(var(--destructive)/0.6)]"
  }[status];
}

function shortQuality(value: string | null) {
  if (!value) return "Unknown";
  if (value.includes("2160")) return "4K";
  if (value.includes("1080")) return "1080p";
  if (value.includes("720")) return "720p";
  return value;
}

function buildDashboardUpcoming(
  episodes: SeriesUpcomingEpisodeItem[],
  seriesWanted: SeriesWantedSummary,
  movieWanted: MovieWantedSummary
): DashboardUpcomingItem[] {
  const now = Date.now();
  const horizon = now + 1000 * 60 * 60 * 72;

  const episodeItems = episodes
    .map((episode) => ({ episode, time: new Date(episode.airDateUtc).getTime() }))
    .filter(({ time }) => time >= now && time <= horizon)
    .map(({ episode, time }) => ({
      id: episode.episodeId,
      day: formatDashboardDay(new Date(time)),
      title: episode.title,
      episode: `S${String(episode.seasonNumber).padStart(2, "0")}E${String(episode.episodeNumber).padStart(2, "0")}`,
      dateLabel: formatDashboardTime(new Date(time)),
      network: episode.episodeTitle ?? "Upcoming episode",
      poster: episode.posterUrl ?? null,
      href: `/tv/${episode.seriesId}`,
      startsAt: episode.airDateUtc
    }));

  const retryItems = [
    ...seriesWanted.recentItems
      .filter((item) => item.nextEligibleSearchUtc)
      .map((item) => ({
        id: `series-retry-${item.seriesId}`,
        time: new Date(item.nextEligibleSearchUtc!).getTime(),
        title: item.title,
        episode: "Retry",
        network: item.wantedReason,
        poster: null,
        href: `/tv/${item.seriesId}`,
        startsAt: item.nextEligibleSearchUtc!
      })),
    ...movieWanted.recentItems
      .filter((item) => item.nextEligibleSearchUtc)
      .map((item) => ({
        id: `movie-retry-${item.movieId}`,
        time: new Date(item.nextEligibleSearchUtc!).getTime(),
        title: item.title,
        episode: "Retry",
        network: item.wantedReason,
        poster: null,
        href: `/movies/${item.movieId}`,
        startsAt: item.nextEligibleSearchUtc!
      }))
  ]
    .filter((item) => item.time >= now && item.time <= horizon)
    .map((item) => ({
      id: item.id,
      day: formatDashboardDay(new Date(item.time)),
      title: item.title,
      episode: item.episode,
      dateLabel: formatDashboardTime(new Date(item.time)),
      network: item.network,
      poster: item.poster,
      href: item.href,
      startsAt: item.startsAt
    }));

  return [...episodeItems, ...retryItems]
    .sort((left, right) => new Date(left.startsAt).getTime() - new Date(right.startsAt).getTime())
    .slice(0, 12);
}

function groupDashboardUpcoming(items: DashboardUpcomingItem[]) {
  const groups: Array<{ day: string; entries: DashboardUpcomingItem[] }> = [];

  for (const item of items) {
    const existing = groups.find((group) => group.day === item.day);
    if (existing) {
      existing.entries.push(item);
    } else {
      groups.push({ day: item.day, entries: [item] });
    }
  }

  return groups;
}

function formatDashboardDay(date: Date) {
  const start = new Date();
  start.setHours(0, 0, 0, 0);
  const target = new Date(date);
  target.setHours(0, 0, 0, 0);
  const diffDays = Math.round((target.getTime() - start.getTime()) / (1000 * 60 * 60 * 24));

  if (diffDays === 0) return "Today";
  if (diffDays === 1) return "Tomorrow";

  return date.toLocaleDateString(undefined, { weekday: "long" });
}

function formatDashboardTime(date: Date) {
  return date.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
}

function emptyDashboardData(): DashboardLoaderData {
  return {
    activeDownloads: [],
    activeDownloadCount: 0,
    indexerHealth: [],
    indexerHealthPercent: null,
    configuredLibraryCount: 0,
    librarySizeTb: "0.0",
    missingCount: 0,
    monitoredCount: 0,
    recentlyAdded: [],
    totalCount: 0,
    upcoming: [],
    upgradeCount: 0,
    waitingCount: 0,
    automation: [],
    searchCycles: [],
    retryWindows: [],
    onboarding: {
      hasIndexer: false,
      hasDownloadClient: false,
      hasLibrary: false
    }
  };
}
