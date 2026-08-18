import { Link, useLoaderData, useNavigate } from "react-router-dom";
import {
  AlertTriangle,
  ArrowUpRight,
  Calendar,
  Download,
  Film,
  HardDrive,
  RadioTower,
  Sparkles,
  Tv
} from "lucide-react";
import type { ActiveDownload, IndexerHealthItem, MediaItem } from "../lib/media-types";
import { MEDIA_STATUS_PRESENTATION, mediaStatusIsActive } from "../lib/media-status-presentation";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  type DownloadClientItem,
  type DownloadTelemetryOverview,
  type IndexerItem,
  type LibraryItem,
  type LibraryAutomationStateItem,
  type MovieListItem,
  type MovieWantedSummary,
  type PlatformSettingsSnapshot,
  type PolicySetItem,
  type QualityProfileItem,
  type SearchCycleRunItem,
  type SearchRetryWindowItem,
  type SeriesUpcomingEpisodeItem,
  type SeriesListItem,
  type SeriesWantedSummary,
  type SetupProgressItem
} from "../lib/api";
import { adaptIndexerHealth, adaptMovieItems, adaptSeriesItems, adaptTelemetryDownloads } from "../lib/ui-adapters";
import { authedFetch } from "../lib/use-auth";
import { buildSetupStatus, type SetupAttentionTone, type SetupStatusModel } from "../lib/setup-status";
import { cn } from "../lib/utils";
import { OnboardingBanner } from "../components/shell/onboarding-banner";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SummaryStrip } from "../components/ui/summary-strip";
import { RouteSkeleton } from "../components/shell/skeleton";

interface DashboardLoaderData {
  activeDownloads: ActiveDownload[];
  activeDownloadCount: number;
  indexerHealth: IndexerHealthItem[];
  indexerHealthPercent: number | null;
  configuredLibraryCount: number;
  librarySizeTb: string;
  missingCount: number;
  movieCount: number;
  movieMissingCount: number;
  monitoredCount: number;
  recentlyAdded: MediaItem[];
  totalCount: number;
  showCount: number;
  showMissingCount: number;
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
  setupProgress: SetupProgressItem;
  setupStatus: SetupStatusModel;
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

  const [movieItems, movieWanted, showItems, showWanted, telemetry, indexers, clients, libraries, automation, searchCycles, retryWindows, upcomingEpisodes, setupProgress, settings, policySets, qualityProfiles] = await Promise.all([
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
    fetchJson<SeriesUpcomingEpisodeItem[]>("/api/series/upcoming?take=12&hours=72").catch((): SeriesUpcomingEpisodeItem[] => []),
    fetchJson<SetupProgressItem>("/api/setup/progress").catch((): SetupProgressItem => ({
      lastCompletedStep: 0,
      isSkipped: false,
      isCompleted: false,
      updatedUtc: new Date(0).toISOString()
    })),
    fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => emptyPlatformSettingsSnapshot),
    fetchJson<PolicySetItem[]>("/api/policy-sets").catch((): PolicySetItem[] => []),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles").catch((): QualityProfileItem[] => [])
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
    movieCount: adaptedMovies.length,
    movieMissingCount: movieWanted.missingCount,
    monitoredCount,
    recentlyAdded: allItems
      .slice()
      .sort((left, right) => right.added.localeCompare(left.added))
      .slice(0, 14),
    totalCount: allItems.length,
    showCount: adaptedShows.length,
    showMissingCount: showWanted.missingCount,
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
    },
    setupProgress,
    setupStatus: buildSetupStatus({ downloadClients: clients, indexers, libraries, policySets, qualityProfiles, settings })
  };
}

export function DashboardPage() {
  const data = useLoaderData() as DashboardLoaderData | undefined;
  const navigate = useNavigate();
  if (!data) return <RouteSkeleton />;

  const healthIssues = data.indexerHealth.filter((item) => item.status !== "healthy").length;
  const topDownload = data.activeDownloads[0];
  const upcomingGroups = groupDashboardUpcoming(data.upcoming);
  const setupProgress = data.setupProgress;

  /** Everything that wants a decision from you, in one list, most urgent first. */
  const attention = [
    ...data.setupStatus.attentionItems.map((item) => ({
      id: `setup:${item.id}`,
      tone: item.tone,
      title: item.title,
      text: item.text,
      href: item.href,
      action: item.action
    })),
    ...(data.missingCount > 0
      ? [{
          id: "missing",
          tone: "warn" as SetupAttentionTone,
          title: `${data.missingCount} ${data.missingCount === 1 ? "title is" : "titles are"} still missing`,
          text: `${data.movieMissingCount} movies and ${data.showMissingCount} TV shows have no acceptable release yet.`,
          href: "/movies",
          action: "Review"
        }]
      : []),
    ...(data.retryWindows.length > 0
      ? [{
          id: "retries",
          tone: "warn" as SetupAttentionTone,
          title: `${data.retryWindows.length} retry ${data.retryWindows.length === 1 ? "window" : "windows"} pending`,
          text: "A search or download failed and is waiting before it tries again.",
          href: "/system/audit",
          action: "See activity"
        }]
      : [])
  ];

  function dismissOnboarding() {
    void authedFetch("/api/setup/progress", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        lastCompletedStep: setupProgress.lastCompletedStep,
        isSkipped: true,
        isCompleted: setupProgress.isCompleted
      })
    }).catch(() => undefined);
  }

  return (
    <div className="flex flex-col gap-[var(--page-gap)]">
      <PageToolbar
        actions={
          <>
            <Button asChild type="button" variant="outline">
              <Link to="/tv?add=true">
                <Tv className="h-4 w-4" />
                Add a show
              </Link>
            </Button>
            <Button asChild type="button">
              <Link to="/movies?add=true">
                <Film className="h-4 w-4" />
                Add a movie
              </Link>
            </Button>
          </>
        }
      />

      <OnboardingBanner
        state={data.onboarding}
        isSetupSuppressed={setupProgress.isSkipped || setupProgress.isCompleted}
        onDismiss={dismissOnboarding}
      />

      <SummaryStrip
        cells={[
          {
            label: "In your library",
            value: data.totalCount.toLocaleString(),
            help: data.totalCount > 0 ? `${data.movieCount} movies · ${data.showCount} shows · ${data.librarySizeTb} TB` : data.configuredLibraryCount > 0 ? "no media yet" : "no library set up yet"
          },
          { label: "Being watched for", value: data.monitoredCount.toLocaleString(), help: "Deluno keeps looking for these" },
          {
            label: "Downloading",
            value: data.activeDownloadCount.toString(),
            help: topDownload ? `${topDownload.speedMbps.toFixed(1)} MB/s` : "nothing in flight"
          },
          {
            label: "Still missing",
            value: data.missingCount.toString(),
            help: data.upgradeCount ? `plus ${data.upgradeCount} could be upgraded` : "nothing waiting",
            tone: data.missingCount > 0 ? "warning" : undefined
          },
          {
            label: "Connections",
            value: data.indexerHealthPercent === null ? "—" : `${data.indexerHealthPercent}%`,
            help: data.indexerHealth.length === 0 ? "none set up yet" : healthIssues > 0 ? `${healthIssues} need a look` : "all responding",
            tone: data.indexerHealth.length && healthIssues > 0 ? "warning" : undefined
          }
        ]}
      />

      {attention.length > 0 ? (
        <ListCard title="Worth a look" count={`${attention.length} ${attention.length === 1 ? "thing needs" : "things need"} a decision from you`}>
          <ListTable columns={[{ label: "What" }, { label: "Why", width: "minmax(0,2fr)" }, { label: "Go", width: "150px", mobile: true, srOnly: true }]} chevron={false}>
            {attention.map((item) => (
              <ListRow key={item.id}>
                <ListNameCell name={item.title} sub={toneWord(item.tone)} />
                <ListCell primary={item.text} />
                <ListCell mobile align="end">
                  <Button asChild type="button" variant="outline" size="sm">
                    <Link to={item.href}>{item.action}</Link>
                  </Button>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      {data.activeDownloads.length ? (
        <ListCard
          title="Downloading now"
          count={`${data.activeDownloadCount} in flight`}
          actions={
            <Button asChild type="button" variant="outline" size="sm">
              <Link to="/queue">Open Transfers</Link>
            </Button>
          }
        >
          <ListTable columns={[{ label: "Release" }, { label: "Progress", width: "minmax(0,1.2fr)" }, { label: "Speed / left" }, { label: "From" }]} chevron={false}>
            {data.activeDownloads.slice(0, 6).map((download) => (
              <ListRow key={download.id}>
                <ListNameCell name={download.title} sub={download.quality ?? "Unknown quality"} />
                <ListCell>
                  <span aria-hidden className="block h-1.5 w-full overflow-hidden rounded-full bg-surface-3">
                    <span className="block h-full rounded-full bg-primary" style={{ width: `${Math.min(100, Math.max(0, download.progress))}%` }} />
                  </span>
                  <span className="mt-1 block text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{Math.round(download.progress)}%</span>
                </ListCell>
                <ListCell numeric primary={`${download.speedMbps.toFixed(1)} MB/s`} secondary={download.etaMinutes > 0 ? `${download.etaMinutes} min left` : undefined} />
                <ListCell primary={download.indexer} secondary={download.peers ? `${download.peers} peers` : undefined} />
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      {upcomingGroups.length ? (
        <ListCard
          title="Airing soon"
          count="The next 72 hours"
          actions={
            <Button asChild type="button" variant="outline" size="sm">
              <Link to="/calendar">Open Schedule</Link>
            </Button>
          }
        >
          <ListTable columns={[{ label: "Show" }, { label: "Episode", width: "minmax(0,1.4fr)" }, { label: "When", width: "170px", mobile: true }]}>
            {upcomingGroups.flatMap((group) =>
              group.entries.slice(0, 3).map((entry) => (
                <ListRow key={entry.id} onClick={() => navigate(entry.href)}>
                  <ListNameCell name={entry.title} sub={entry.network} />
                  <ListCell primary={entry.episode} />
                  <ListCell numeric mobile primary={group.day} secondary={entry.dateLabel} />
                </ListRow>
              ))
            )}
          </ListTable>
        </ListCard>
      ) : null}

      <ListCard
        title="Recently added"
        count={data.recentlyAdded.length ? `${data.recentlyAdded.length} newest` : undefined}
        actions={
          <Button asChild type="button" variant="outline" size="sm">
            <Link to="/movies">Browse library</Link>
          </Button>
        }
      >
        {data.recentlyAdded.length === 0 ? (
          <ListEmpty
            title="Nothing in the library yet"
            description="Movies and shows appear here as Deluno imports them. Add your first title whenever you are ready."
            actions={
              <Button asChild type="button" variant="outline">
                <Link to="/movies?add=true">Add a movie</Link>
              </Button>
            }
          />
        ) : (
          <div className="dashboard-poster-grid p-[var(--card-pad-x)]">
            {data.recentlyAdded.slice(0, 12).map((item) => (
              <PosterPreview key={`${item.type}-${item.id}`} item={item} />
            ))}
          </div>
        )}
      </ListCard>

      {data.indexerHealth.length ? (
        <ListCard
          title="Connections"
          count={healthIssues ? `${healthIssues} of ${data.indexerHealth.length} need a look` : "all responding"}
          actions={
            <Button asChild type="button" variant="outline" size="sm">
              <Link to="/indexers">Open Connections</Link>
            </Button>
          }
        >
          <ListTable columns={[{ label: "Connection" }, { label: "Response", width: "minmax(0,1fr)" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]} chevron={false}>
            {data.indexerHealth.map((item) => (
              <ListRow key={item.id}>
                <ListNameCell name={item.name} />
                <ListCell numeric primary={item.responseMs === null ? "—" : `${item.responseMs} ms`} />
                <ListCell mobile>
                  <Chip tone={item.status === "healthy" ? "ok" : item.status === "degraded" ? "warn" : "bad"}>{item.status}</Chip>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}
    </div>
  );
}

function toneWord(tone: SetupAttentionTone) {
  if (tone === "warn") return "Needs attention";
  if (tone === "success") return "Done";
  return "Suggestion";
}

function MetricPlane({
  label,
  value,
  unit,
  meta,
  icon: Icon,
  tone,
  visual
}: {
  label: string;
  value: string;
  unit?: string;
  meta: string;
  icon: typeof HardDrive;
  tone: "primary" | "success" | "warn" | "info" | "neutral";
  visual?: React.ReactNode;
}) {
  return (
    <article
      className={cn(
        "group relative min-h-[var(--metric-plane-min-height)] min-w-0 overflow-hidden rounded-xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card",
        "transition duration-200 hover:border-primary/30 hover:shadow-lg dark:border-white/[0.06]"
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
          <span className={cn("flex h-10 w-10 shrink-0 items-center justify-center rounded-lg border", toneClass(tone, "icon"))}>
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
    <section className={cn("relative self-start overflow-hidden rounded-xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card dark:border-white/[0.06]", className)}>
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
  href,
  action
}: {
  title: string;
  text: string;
  tone: SetupAttentionTone;
  href: string;
  action?: string;
}) {
  return (
    <Link to={href} className="group relative block overflow-hidden rounded-lg border border-hairline bg-surface-1/70 p-4 transition hover:border-primary/30 hover:bg-primary/5">
      <AmbientTone tone={tone} subtle />
      <div className="relative flex gap-3">
        <span className={cn("mt-1 h-2.5 w-2.5 shrink-0 rounded-full", toneClass(tone, "dot"))} />
        <span className="min-w-0">
          <span className="block text-[length:var(--type-body-sm)] font-semibold text-foreground">{title}</span>
          <span className="mt-1 block text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{text}</span>
          {action ? (
            <span className="mt-3 inline-flex items-center gap-1 text-[length:var(--type-micro)] font-bold uppercase tracking-[0.12em] text-primary">
              {action}
              <ArrowUpRight className="h-3 w-3" />
            </span>
          ) : null}
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
          {download.quality ?? "Quality not reported"} - {download.indexer || "source not reported"}
        </span>
        <span className="mt-2 block h-1.5 overflow-hidden rounded-full bg-muted/60">
          <span className="block h-full rounded-full bg-primary" style={{ width: `${Math.max(0, Math.min(100, download.progress))}%` }} />
        </span>
      </span>
      <span className="text-left sm:text-right">
        <span className="block font-mono text-[13px] font-semibold text-foreground">{Math.round(download.progress)}%</span>
        <span className="block text-[length:var(--type-caption)] text-muted-foreground">{download.speedMbps.toFixed(1)} MB/s - {download.etaMinutes > 0 ? `${download.etaMinutes} min left` : "finishing"}</span>
      </span>
    </Link>
  );
}

function PosterPreview({ item }: { item: MediaItem }) {
  return (
    <Link to={item.type === "show" ? `/tv/${item.id}` : `/movies/${item.id}`} className="group min-w-0">
      <div className="relative aspect-[2/3] overflow-hidden rounded-xl border border-hairline bg-surface-2 shadow-card transition duration-200 group-hover:border-primary/40 group-hover:shadow-lg">
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
    <div className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-surface-1/70 px-3 py-2.5">
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
    <div className="rounded-lg border border-dashed border-hairline bg-surface-1/60 p-4 text-[length:var(--type-body-sm)] text-muted-foreground">
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
      style={{ background: color, opacity: subtle ? 0.06 : 0.1 }}
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
    movieCount: 0,
    movieMissingCount: 0,
    monitoredCount: 0,
    recentlyAdded: [],
    totalCount: 0,
    showCount: 0,
    showMissingCount: 0,
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
    },
    setupProgress: {
      lastCompletedStep: 0,
      isSkipped: false,
      isCompleted: false,
      updatedUtc: new Date(0).toISOString()
    },
    setupStatus: buildSetupStatus({
      downloadClients: [],
      indexers: [],
      libraries: [],
      policySets: [],
      qualityProfiles: [],
      settings: emptyPlatformSettingsSnapshot
    })
  };
}
