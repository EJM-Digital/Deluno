import { useCallback, useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronRight } from "lucide-react";
import { Link, useLoaderData, useNavigate } from "react-router-dom";
import type { ActiveDownload, IndexerHealthItem, MediaItem } from "../lib/media-types";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson, fetchPageItems,
  type ActivityEventItem,
  type DownloadClientItem,
  type DownloadSharingSnapshot,
  type DownloadTelemetryOverview,
  type DownloadThroughputWindow,
  type IndexerItem,
  type LibraryItem,
  type LibraryAutomationStateItem,
  type CataloguePage,
  type MonitoringDashboardSnapshot,
  type MovieListItem,
  type MovieWantedSummary,
  type PlatformSettingsSnapshot,
  type PolicySetItem,
  type ProcessorConnectionItem,
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
import { AcquisitionPipeline } from "../components/app/acquisition-pipeline";
import { ActivityTicker } from "../components/app/activity-ticker";
import { DashboardHero } from "../components/app/dashboard-hero";
import { LibraryComposition } from "../components/app/library-composition";
import { SystemPulse } from "../components/app/system-pulse";
import { OnboardingBanner } from "../components/shell/onboarding-banner";
import { SetupProgressLadder } from "../components/shell/setup-progress-ladder";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { ListCard, ListEmpty } from "../components/ui/list-card";
import { MetricChart, type MetricPoint } from "../components/ui/metric-chart";
import { SegmentedControl } from "../components/ui/segmented-control";
import { TitleMarkDot } from "../components/ui/title-mark";
import {
  DEFAULT_HISTORY_DAYS,
  HISTORY_RANGES,
  readStoredHistoryDays,
  windowLabel,
  writeStoredHistoryDays,
  type HistoryDays
} from "../lib/dashboard-history-range";
import { useCoalescedRevalidate } from "../hooks/use-visible-interval";
import { StatusLed, type LedTone } from "../components/ui/status-led";
import { RealtimeGroups, useSignalREvent, useSignalRResync } from "../lib/use-signalr";

interface OutcomeSeries {
  succeeded: MetricPoint[];
  failed: MetricPoint[];
}

/** `/api/dashboard/metrics` — counts of stored rows grouped by day. */
interface DashboardMetrics {
  days: number;
  from: string;
  to: string;
  librarySize: MetricPoint[];
  titlesAdded: MetricPoint[];
  searches: OutcomeSeries;
  jobs: OutcomeSeries;
  importFailures: MetricPoint[];
  grabs: MetricPoint[];
}

interface DashboardLoaderData {
  sources: DashboardSources;
  metrics: DashboardMetrics | null;
  /** Operational state — readiness, storage, service health, latency. */
  monitoring: MonitoringDashboardSnapshot | null;
  /** Seeds the live ticker so it is populated before the first push arrives. */
  activity: ActivityEventItem[];
  /** Whether any post-processor connection is enabled. */
  hasProcessor: boolean;
  /** Stored throughput readings for the speed chart. */
  throughput: DownloadThroughputWindow | null;
  /** Finished downloads the clients are still sharing, and what they cost. */
  sharing: DownloadSharingSnapshot;
  /** Combined client throughput right now, in MB/s, both directions. */
  speedMbps: number;
  uploadMbps: number;
  activeDownloads: ActiveDownload[];
  activeDownloadCount: number;
  /** Finished in the client and waiting on import — in the pipeline, not transferring. */
  importReadyCount: number;
  indexerHealth: IndexerHealthItem[];
  indexerHealthPercent: number | null;
  configuredLibraryCount: number;
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
  coveredCount: number;
  upcomingCount: number;
  automation: LibraryAutomationStateItem[];
  searchCycles: SearchCycleRunItem[];
  retryWindows: SearchRetryWindowItem[];
  setupProgress: SetupProgressItem;
  setupStatus: SetupStatusModel;
}

interface DashboardSources {
  moviePage: CataloguePage<MovieListItem>;
  movieWanted: MovieWantedSummary;
  showPage: CataloguePage<SeriesListItem>;
  showWanted: SeriesWantedSummary;
  telemetry: DownloadTelemetryOverview;
  indexers: IndexerItem[];
  clients: DownloadClientItem[];
  libraries: LibraryItem[];
  automation: LibraryAutomationStateItem[];
  searchCycles: SearchCycleRunItem[];
  retryWindows: SearchRetryWindowItem[];
  upcomingEpisodes: SeriesUpcomingEpisodeItem[];
  setupProgress: SetupProgressItem;
  settings: PlatformSettingsSnapshot;
  policySets: PolicySetItem[];
  qualityProfiles: QualityProfileItem[];
  metrics: DashboardMetrics | null;
  monitoring: MonitoringDashboardSnapshot | null;
  activity: ActivityEventItem[];
  /** Only used to decide whether the pipeline has a Processing stage at all. */
  processors: ProcessorConnectionItem[];
  /** Stored throughput readings — what the speed has been, not what it is now. */
  throughput: DownloadThroughputWindow | null;
  /** What the download clients still hold after import, and why (#288). */
  sharing: DownloadSharingSnapshot;
}

function emptyDashboardSources(): DashboardSources {
  return {
    moviePage: { items: [], nextPageToken: null, hasMore: false, totalCount: 0, facets: null },
    movieWanted: EMPTY_MOVIE_WANTED,
    showPage: { items: [], nextPageToken: null, hasMore: false, totalCount: 0, facets: null },
    showWanted: EMPTY_SERIES_WANTED,
    telemetry: EMPTY_TELEMETRY,
    indexers: [], clients: [], libraries: [], automation: [], searchCycles: [], retryWindows: [], upcomingEpisodes: [],
    setupProgress: EMPTY_SETUP_PROGRESS,
    settings: emptyPlatformSettingsSnapshot,
    policySets: [], qualityProfiles: [], metrics: null, monitoring: null, activity: [], processors: [], throughput: null,
    sharing: EMPTY_SHARING
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

const EMPTY_MOVIE_WANTED: MovieWantedSummary = { totalWanted: 0, missingCount: 0, upgradeCount: 0, coveredCount: 0, upcomingCount: 0, recentItems: [] };
const EMPTY_SERIES_WANTED: SeriesWantedSummary = { totalWanted: 0, missingCount: 0, upgradeCount: 0, coveredCount: 0, upcomingCount: 0, recentItems: [] };
const EMPTY_TELEMETRY: DownloadTelemetryOverview = {
  summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
  clients: [],
  capturedUtc: new Date(0).toISOString()
};
const EMPTY_SHARING: DownloadSharingSnapshot = { holds: [], extraBytes: 0, driveNote: null, observedUtc: null };
const EMPTY_SETUP_PROGRESS: SetupProgressItem = { lastCompletedStep: 0, isSkipped: false, isCompleted: false, updatedUtc: new Date(0).toISOString() };

export async function dashboardLoader(): Promise<DashboardLoaderData> {
  const [moviePage, movieWanted, showPage, showWanted, telemetry, indexers, clients, libraries, automation, searchCycles, retryWindows, upcomingEpisodes, setupProgress, settings, policySets, qualityProfiles] = await Promise.all([
    fetchJson<CataloguePage<MovieListItem>>("/api/movies/page?pageSize=14&sort=added&direction=desc").catch((): CataloguePage<MovieListItem> => ({ items: [], nextPageToken: null, hasMore: false, totalCount: 0, facets: null })),
    fetchJson<MovieWantedSummary>("/api/movies/wanted").catch(() => EMPTY_MOVIE_WANTED),
    fetchJson<CataloguePage<SeriesListItem>>("/api/series/page?pageSize=14&sort=added&direction=desc").catch((): CataloguePage<SeriesListItem> => ({ items: [], nextPageToken: null, hasMore: false, totalCount: 0, facets: null })),
    fetchJson<SeriesWantedSummary>("/api/series/wanted").catch(() => EMPTY_SERIES_WANTED),
    fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry").catch(() => EMPTY_TELEMETRY),
    fetchJson<IndexerItem[]>("/api/indexers").catch((): IndexerItem[] => []),
    fetchJson<DownloadClientItem[]>("/api/download-clients").catch((): DownloadClientItem[] => []),
    fetchJson<LibraryItem[]>("/api/libraries").catch((): LibraryItem[] => []),
    fetchPageItems<LibraryAutomationStateItem>("/api/library-automation?pageSize=50").catch((): LibraryAutomationStateItem[] => []),
    fetchPageItems<SearchCycleRunItem>("/api/search-cycles?pageSize=8").catch((): SearchCycleRunItem[] => []),
    fetchPageItems<SearchRetryWindowItem>("/api/search-retry-windows?pageSize=8").catch((): SearchRetryWindowItem[] => []),
    fetchJson<SeriesUpcomingEpisodeItem[]>("/api/series/upcoming?take=12&hours=72").catch((): SeriesUpcomingEpisodeItem[] => []),
    fetchJson<SetupProgressItem>("/api/setup/progress").catch(() => EMPTY_SETUP_PROGRESS),
    fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => emptyPlatformSettingsSnapshot),
    fetchJson<PolicySetItem[]>("/api/policy-sets").catch((): PolicySetItem[] => []),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles").catch((): QualityProfileItem[] => [])
  ]);

  // A dashboard that cannot draw its charts or read its own health still has to
  // render, so these degrade to a stated gap rather than an error page.
  const [metrics, monitoring, activity, processors, throughput, sharing] = await Promise.all([
    fetchJson<DashboardMetrics>("/api/dashboard/metrics?days=30").catch(() => null),
    fetchJson<MonitoringDashboardSnapshot>("/api/monitoring/dashboard").catch(() => null),
    fetchPageItems<ActivityEventItem>("/api/activity?pageSize=10").catch((): ActivityEventItem[] => []),
    fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections").catch((): ProcessorConnectionItem[] => []),
    fetchJson<DownloadThroughputWindow>("/api/download-clients/throughput?hours=6").catch(() => null),
    fetchJson<DownloadSharingSnapshot>("/api/download-clients/sharing").catch(() => EMPTY_SHARING)
  ]);

  return buildDashboardData({
    moviePage, movieWanted, showPage, showWanted, telemetry, indexers, clients,
    libraries, automation, searchCycles, retryWindows, upcomingEpisodes,
    setupProgress, settings, policySets, qualityProfiles, metrics, monitoring, activity, processors, throughput, sharing
  });
}

function buildDashboardData(sources: DashboardSources): DashboardLoaderData {
  const {
    moviePage, movieWanted, showPage, showWanted, telemetry, indexers, clients,
    libraries, automation, searchCycles, retryWindows, upcomingEpisodes,
    setupProgress, settings, policySets, qualityProfiles, metrics, monitoring, activity, processors, throughput, sharing
  } = sources;
  const adaptedMovies = adaptMovieItems(moviePage.items);
  const adaptedShows = adaptSeriesItems(showPage.items);
  const activeDownloads = adaptTelemetryDownloads(telemetry);
  const indexerHealth = adaptIndexerHealth(indexers, clients);
  const monitoredCount = (moviePage.facets?.monitored ?? 0) + (showPage.facets?.monitored ?? 0);
  const healthyCount = indexerHealth.filter((item) => item.status === "healthy").length;

  return {
    sources,
    metrics,
    monitoring,
    activity,
    // A Processing stage only makes sense where something is actually
    // refining media before import.
    hasProcessor: processors.some((processor) => processor.isEnabled),
    throughput,
    sharing,
    speedMbps: telemetry.summary.totalSpeedMbps,
    uploadMbps: telemetry.summary.totalUploadSpeedMbps ?? 0,
    activeDownloads,
    // Downloading means downloading. Counting import-ready items here made
    // the stat read "1" with nothing transferring, beside a card row sitting
    // at 100% and 0.0 MB/s (#258). Finished-but-not-imported work is counted
    // separately below and shown on Transfers.
    activeDownloadCount: telemetry.summary.activeCount + telemetry.summary.queuedCount,
    importReadyCount: telemetry.summary.importReadyCount,
    indexerHealth,
    indexerHealthPercent: indexerHealth.length ? Math.round((healthyCount / indexerHealth.length) * 100) : null,
    configuredLibraryCount: libraries.length,
    missingCount: movieWanted.missingCount + showWanted.missingCount,
    movieCount: moviePage.totalCount ?? 0,
    movieMissingCount: movieWanted.missingCount,
    monitoredCount,
    recentlyAdded: [...adaptedMovies, ...adaptedShows].slice(0, 14),
    totalCount: (moviePage.totalCount ?? 0) + (showPage.totalCount ?? 0),
    showCount: showPage.totalCount ?? 0,
    showMissingCount: showWanted.missingCount,
    upcoming: buildDashboardUpcoming(upcomingEpisodes, showWanted, movieWanted),
    upgradeCount: movieWanted.upgradeCount + showWanted.upgradeCount,
    coveredCount: movieWanted.coveredCount + showWanted.coveredCount,
    upcomingCount: movieWanted.upcomingCount + showWanted.upcomingCount,
    automation,
    searchCycles,
    retryWindows,
    setupProgress,
    setupStatus: buildSetupStatus({ downloadClients: clients, indexers, libraries, policySets, qualityProfiles, settings })
  };
}

const DASHBOARD_REFRESH = {
  // Deliberate visible-only safety net: realtime drives normal freshness, while
  // this heartbeat recovers from a missed envelope without waking hidden tabs.
  staleTime: 60_000,
  refetchInterval: 60_000,
  refetchIntervalInBackground: false
} as const;

/**
 * Health moves on its own clock. Realtime nudges already invalidate this along
 * with everything else, so the interval is only the floor for the things no
 * event announces — a drive filling up, a client that quietly stopped
 * answering. Twice the shared heartbeat, and no faster: the readiness check
 * behind it writes a probe file, so polling it hard would be a real cost for
 * numbers that do not move in seconds.
 */
const MONITORING_REFRESH = {
  staleTime: 30_000,
  refetchInterval: 30_000,
  refetchIntervalInBackground: false
} as const;

/**
 * Every dashboard query, one hook each (#281).
 *
 * These used to be one `useQueries` tuple. TanStack infers that tuple element
 * by element, and past roughly twenty entries — or the moment one entry gains
 * an options callback — the inference collapses and *every* result silently
 * widens to `{}`. It is a cliff, not a slope, and it cost two debugging
 * sessions: nothing errors, the data still arrives, and the types quietly stop
 * protecting anything.
 *
 * So the tuple is gone. The only thing it ever bought was brevity, and each
 * hook now keeps its own type, its own cadence and its own options with no
 * ceiling to hit. Adding the next one cannot break the ones already here.
 *
 * The typed boundary below is deliberate and worth keeping: `buildDashboardData`
 * takes strongly typed parameters, which is the only reason the collapse was
 * ever visible rather than silent.
 */
function useDashboardData(initial: DashboardLoaderData, historyDays: HistoryDays) {
  const source = initial.sources;

  const moviePage = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["movies"], queryFn: () => fetchJson<CataloguePage<MovieListItem>>("/api/movies/page?pageSize=14&sort=added&direction=desc").catch(() => emptyDashboardSources().moviePage), initialData: source.moviePage });
  const movieWanted = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["movies", "wanted"], queryFn: () => fetchJson<MovieWantedSummary>("/api/movies/wanted").catch(() => EMPTY_MOVIE_WANTED), initialData: source.movieWanted });
  const showPage = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["series"], queryFn: () => fetchJson<CataloguePage<SeriesListItem>>("/api/series/page?pageSize=14&sort=added&direction=desc").catch(() => emptyDashboardSources().showPage), initialData: source.showPage });
  const showWanted = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["series", "wanted"], queryFn: () => fetchJson<SeriesWantedSummary>("/api/series/wanted").catch(() => EMPTY_SERIES_WANTED), initialData: source.showWanted });
  const indexers = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["indexers"], queryFn: () => fetchJson<IndexerItem[]>("/api/indexers").catch((): IndexerItem[] => []), initialData: source.indexers });
  const clients = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["download-clients"], queryFn: () => fetchJson<DownloadClientItem[]>("/api/download-clients").catch((): DownloadClientItem[] => []), initialData: source.clients });
  const libraries = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["libraries"], queryFn: () => fetchJson<LibraryItem[]>("/api/libraries").catch((): LibraryItem[] => []), initialData: source.libraries });
  const automation = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["library-automation"], queryFn: () => fetchPageItems<LibraryAutomationStateItem>("/api/library-automation?pageSize=50").catch((): LibraryAutomationStateItem[] => []), initialData: source.automation });
  const searchCycles = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["search-cycles"], queryFn: () => fetchPageItems<SearchCycleRunItem>("/api/search-cycles?pageSize=8").catch((): SearchCycleRunItem[] => []), initialData: source.searchCycles });
  const retryWindows = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["search-retry-windows"], queryFn: () => fetchPageItems<SearchRetryWindowItem>("/api/search-retry-windows?pageSize=8").catch((): SearchRetryWindowItem[] => []), initialData: source.retryWindows });
  const upcomingEpisodes = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["series", "upcoming"], queryFn: () => fetchJson<SeriesUpcomingEpisodeItem[]>("/api/series/upcoming?take=12&hours=72").catch((): SeriesUpcomingEpisodeItem[] => []), initialData: source.upcomingEpisodes });
  const setupProgress = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["setup-progress"], queryFn: () => fetchJson<SetupProgressItem>("/api/setup/progress").catch(() => EMPTY_SETUP_PROGRESS), initialData: source.setupProgress });
  const settings = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["settings"], queryFn: () => fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => emptyPlatformSettingsSnapshot), initialData: source.settings });
  const policySets = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["policy-sets"], queryFn: () => fetchJson<PolicySetItem[]>("/api/policy-sets").catch((): PolicySetItem[] => []), initialData: source.policySets });
  const qualityProfiles = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["quality-profiles"], queryFn: () => fetchJson<QualityProfileItem[]>("/api/quality-profiles").catch((): QualityProfileItem[] => []), initialData: source.qualityProfiles });
  const activity = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["activity", "dashboard"], queryFn: () => fetchPageItems<ActivityEventItem>("/api/activity?pageSize=10").catch((): ActivityEventItem[] => []), initialData: source.activity });
  const processors = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["processor-connections"], queryFn: () => fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections").catch((): ProcessorConnectionItem[] => []), initialData: source.processors });

  // Health moves on its own clock — see MONITORING_REFRESH.
  const monitoring = useQuery({ ...MONITORING_REFRESH, queryKey: ["monitoring-dashboard"], queryFn: () => fetchJson<MonitoringDashboardSnapshot>("/api/monitoring/dashboard").catch(() => null), initialData: source.monitoring });

  // The window is part of the cache key, so every range keeps its own entry and
  // switching back to one already read is instant. The loader only ever fetches
  // the default window, so that is the only key it can legitimately seed.
  const metrics = useQuery({
    ...DASHBOARD_REFRESH,
    queryKey: ["dashboard-metrics", historyDays],
    queryFn: () => fetchJson<DashboardMetrics>(`/api/dashboard/metrics?days=${historyDays}`).catch(() => null),
    initialData: historyDays === DEFAULT_HISTORY_DAYS ? source.metrics : undefined
  });

  // The pipeline's stage counts, and the set of transfers that exist. It sits on
  // the ordinary heartbeat again (#273): a real `DownloadProgress` publisher now
  // carries how far along each transfer is, so the bars move on events and this
  // no longer has to poll every three seconds to look alive. A transfer changing
  // stage still invalidates it, because that is a count rather than a reading.
  const telemetry = useQuery({
    ...DASHBOARD_REFRESH,
    queryKey: ["telemetry"],
    queryFn: () => fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry").catch(() => EMPTY_TELEMETRY),
    initialData: source.telemetry
  });

  // A minute-resolution series, so refreshing faster than the sampler writes
  // would only redraw the same points.
  const throughput = useQuery({ ...MONITORING_REFRESH, queryKey: ["download-throughput"], queryFn: () => fetchJson<DownloadThroughputWindow>("/api/download-clients/throughput?hours=6").catch(() => null), initialData: source.throughput });

  // What the clients still hold after import (#288). Written by the worker's
  // sharing pass rather than measured here, so it moves on that pass's clock —
  // and the numbers on it are days and gigabytes, which do not reward polling
  // any harder than the heartbeat.
  const sharing = useQuery({ ...DASHBOARD_REFRESH, queryKey: ["download-sharing"], queryFn: () => fetchJson<DownloadSharingSnapshot>("/api/download-clients/sharing").catch(() => EMPTY_SHARING), initialData: source.sharing });

  return buildDashboardData({
    moviePage: moviePage.data ?? source.moviePage,
    movieWanted: movieWanted.data ?? source.movieWanted,
    showPage: showPage.data ?? source.showPage,
    showWanted: showWanted.data ?? source.showWanted,
    telemetry: telemetry.data ?? source.telemetry,
    indexers: indexers.data ?? source.indexers,
    clients: clients.data ?? source.clients,
    libraries: libraries.data ?? source.libraries,
    automation: automation.data ?? source.automation,
    searchCycles: searchCycles.data ?? source.searchCycles,
    retryWindows: retryWindows.data ?? source.retryWindows,
    upcomingEpisodes: upcomingEpisodes.data ?? source.upcomingEpisodes,
    setupProgress: setupProgress.data ?? source.setupProgress,
    settings: settings.data ?? source.settings,
    policySets: policySets.data ?? source.policySets,
    qualityProfiles: qualityProfiles.data ?? source.qualityProfiles,
    metrics: metrics.data ?? source.metrics,
    monitoring: monitoring.data ?? source.monitoring,
    activity: activity.data ?? source.activity,
    processors: processors.data ?? source.processors,
    throughput: throughput.data ?? source.throughput,
    sharing: sharing.data ?? source.sharing
  });
}

export function DashboardPage() {
  const loaderData = useLoaderData() as DashboardLoaderData;
  const [historyDays, setHistoryDays] = useState<HistoryDays>(readStoredHistoryDays);
  const data = useDashboardData(loaderData, historyDays);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [liveSpeedMbps, setLiveSpeedMbps] = useState(() => data.speedMbps);
  // The newest reading per download-client queue item, laid over what telemetry
  // last returned. Cleared down to whatever telemetry still knows about, so a
  // finished transfer cannot leave a stale bar behind (#273).
  const [liveProgress, setLiveProgress] = useState<Record<string, { progress: number; speedMbps: number; status: string }>>({});
  // Upload has no realtime publisher of its own, so it follows the telemetry
  // poll rather than being held in state pretending otherwise.
  const liveUploadMbps = data.uploadMbps;
  const librarySubjects = useMemo(
    () => data.sources.libraries.map((library) => RealtimeGroups.Library(library.id)),
    [data.sources.libraries]
  );
  const invalidate = useCallback((keys: ReadonlyArray<readonly string[]>) => {
    keys.forEach((queryKey) => { void queryClient.invalidateQueries({ queryKey }); });
  }, [queryClient]);

  // Action events can arrive in a burst; coalesce their broad fallback refresh.
  // The 60-second visible-only query heartbeat remains the safety net.
  const nudge = useCoalescedRevalidate(() => { void queryClient.invalidateQueries(); }, 5_000);
  useSignalREvent("SearchRunCompleted", librarySubjects, nudge);
  useSignalREvent("HealthChanged", RealtimeGroups.Dashboard, nudge);
  useSignalREvent("QueueItemAdded", RealtimeGroups.Queue, nudge);
  useSignalREvent("QueueItemRemoved", RealtimeGroups.Queue, nudge);
  useSignalREvent("QueueItemStatusChanged", RealtimeGroups.Queue, nudge);
  useSignalREvent("ImportStateChanged", RealtimeGroups.Queue, nudge);
  useSignalREvent("MovieChanged", RealtimeGroups.Dashboard, () => invalidate([["movies"], ["movies", "wanted"], ["dashboard-metrics"]]));
  useSignalREvent("SeriesChanged", RealtimeGroups.Dashboard, () => invalidate([["series"], ["series", "wanted"], ["series", "upcoming"], ["dashboard-metrics"]]));
  useSignalREvent("LibraryChanged", librarySubjects, () => invalidate([["libraries"], ["library-automation"]]));
  useSignalREvent("SettingsChanged", RealtimeGroups.Dashboard, () => invalidate([["settings"], ["setup-progress"]]));
  useSignalREvent("QualityProfileChanged", RealtimeGroups.Dashboard, () => invalidate([["quality-profiles"], ["setup-progress"]]));
  useSignalREvent("PolicySetChanged", RealtimeGroups.Dashboard, () => invalidate([["policy-sets"], ["setup-progress"]]));
  useSignalREvent("IntakeSourceChanged", RealtimeGroups.Dashboard, () => invalidate([["setup-progress"]]));
  useSignalREvent("AutomationStateChanged", RealtimeGroups.Dashboard, () => invalidate([["library-automation"], ["search-cycles"], ["search-retry-windows"]]));
  useSignalREvent("IndexerChanged", RealtimeGroups.Dashboard, () => invalidate([["indexers"], ["dashboard-metrics"]]));
  useSignalREvent("DownloadClientChanged", RealtimeGroups.Dashboard, () => invalidate([["download-clients"], ["telemetry"], ["dashboard-metrics"]]));
  useSignalRResync(() => { void queryClient.invalidateQueries(); });
  // Real readings from the download client, keyed by its own queue-item id
  // (#273). They are applied over the telemetry rows rather than triggering a
  // refetch: a bar that only moves when the poll comes round is a bar that
  // jumps, and asking the client again on every event would undo the point of
  // being told.
  useSignalREvent("DownloadProgress", RealtimeGroups.Queue, (event) => {
    setLiveProgress((current) => {
      // A transfer changing *stage* moves the counts above the rows, and those
      // come from telemetry rather than from this event — so a status change is
      // the one reading worth a refetch. Progress never is: that is the whole
      // reason for being told rather than asking.
      if (current[event.id] && current[event.id].status !== event.status) {
        invalidate([["telemetry"]]);
      }

      return {
        ...current,
        [event.id]: { progress: event.progress, speedMbps: event.speedMbps, status: event.status }
      };
    });
  });

  useEffect(() => {
    setLiveSpeedMbps(data.speedMbps);
  }, [data.speedMbps]);

  // Telemetry is the authority on which transfers exist; events are only the
  // authority on how far along they are. Dropping overlays for rows telemetry
  // no longer lists is what stops a completed download lingering at 87%.
  const inFlightIds = data.activeDownloads.map((download) => download.id).join(",");
  useEffect(() => {
    const alive = new Set(inFlightIds ? inFlightIds.split(",") : []);
    setLiveProgress((current) => {
      const next = Object.fromEntries(Object.entries(current).filter(([id]) => alive.has(id)));
      return Object.keys(next).length === Object.keys(current).length ? current : next;
    });
  }, [inFlightIds]);

  // The live speed the card shows is the sum of what the client last said about
  // each transfer, so it moves with the bars rather than with the poll.
  const eventSpeedMbps = data.activeDownloads.reduce(
    (total, download) => total + (liveProgress[download.id]?.speedMbps ?? download.speedMbps),
    0
  );

  useEffect(() => {
    writeStoredHistoryDays(historyDays);
  }, [historyDays]);

  // Progress events feed the wave directly; it keeps its own rolling window on
  // an animation clock, so there is no series to accumulate here any more.
  const healthIssues = data.indexerHealth.filter((item) => item.status !== "healthy").length;
  const upcomingGroups = groupDashboardUpcoming(data.upcoming);
  const setupProgress = data.setupProgress;
  const heroState = describeSystem(data, healthIssues);

  /**
   * Everything that wants a decision from you, in one list, most urgent first.
   * Monitoring alerts lead: a failing readiness check or a drive about to fill
   * outranks a missing episode, and until now they were only visible in System.
   */
  const attention = [
    ...(data.monitoring?.alerts ?? []).map((alert) => ({
      id: `alert:${alert.code}`,
      // The endpoint returns open alerts only, so every one of them is asking
      // for something — there is no "suggestion" tier here.
      tone: "warn" as SetupAttentionTone,
      title: alert.summary,
      text: alert.details,
      href: "/system",
      action: "Open System"
    })),
    // Setup belongs to the ladder above, not here. Both lists were built from
    // the same `attentionItems`, so a part-configured install stated the same
    // three things twice on one screen, about 200px apart (#275). The ladder is
    // the better home for them: it carries the order they have to happen in,
    // and it takes itself off the page the moment the basics are done — at
    // which point this list is the only one left and loses nothing.
    //
    // What remains from setup is only what the ladder has no step for --
    // "Connection health needs review" is raised against connections that are
    // configured and unhealthy, which no ladder step covers.
    ...data.setupStatus.attentionItems
      .filter((item) => !data.setupStatus.steps.some((step) => step.id === item.id))
      .map((item) => ({
        id: `setup:${item.id}`,
        tone: item.tone,
        title: item.title,
        text: item.text,
        href: item.href,
        action: item.action
      })),
    // Two entries used to sit here and neither needed a person (#302).
    //
    // A count of missing titles, amber: Deluno searches for those on its own
    // schedule, and the count is already in the strip above in the Missing red.
    // And a count of pending retry windows, amber, described in its own text as
    // "waiting before it tries again" — the same self-resolving state the audit
    // ruled blue for a rate-limited indexer.
    //
    // Between them they made this card read **2** on an install where the
    // sidebar was simultaneously saying "All good · Nothing needs you". A badge
    // that lights up when nothing is wrong is how people learn to stop looking
    // at it. What is left here genuinely stops until somebody acts.
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
      {/* The guided prompt is the action-oriented entry point. The ladder below
          it is authoritative and can be used after the prompt is dismissed. */}
      <OnboardingBanner
        isSetupSuppressed={setupProgress.isSkipped || setupProgress.isCompleted}
        onDismiss={dismissOnboarding}
      />
      <SetupProgressLadder status={data.setupStatus} />

      {/* The opening statement: how the whole system is, at a glance. */}
      <DashboardHero
        headline={heroState.headline}
        detail={heroState.detail}
        tone={heroState.tone}
        // Nothing in the library is the one state where the dashboard has a
        // single obvious next step, so it says so where the headline already is.
        action={data.totalCount === 0 ? { label: "Add a movie", to: "/movies?add=true" } : undefined}
        stats={[
          {
            label: "In your library",
            value: data.totalCount,
            help: data.totalCount > 0 ? `${data.movieCount} movies · ${data.showCount} shows` : data.configuredLibraryCount > 0 ? "no media yet" : "no library set up yet",
            href: "/movies"
          },
          // The mark's names, not three of this page's own. "Watching for",
          // "Still missing" and "Could be upgraded" were invented here for
          // states the rest of Deluno already names — and *Still missing* was
          // amber, the signal that means a person has to act, for titles being
          // searched for on schedule (#302, DESIGN-001).
          {
            label: "Quality met",
            value: data.coveredCount,
            help: data.coveredCount === 0 ? "nothing at target yet" : "Deluno has stopped looking",
            mark: "covered" as const,
            href: "/movies?status=covered"
          },
          {
            label: "Upgradable",
            value: data.upgradeCount,
            help: data.upgradeCount === 0 ? "everything meets its profile" : "a better release would be accepted",
            mark: "upgrade" as const,
            href: "/search-cycles/upgrades"
          },
          {
            label: "Missing",
            value: data.missingCount,
            help: data.missingCount === 0 ? "nothing missing" : "no acceptable release yet",
            mark: "missing" as const,
            href: "/search-cycles/missing"
          }
        ]}
      />

      {/* THE PANE. Two grid rows carrying everything that answers "what is
          happening and what needs me", sized to sit on one screen: health and
          decisions above, then the three live panels. Each list panel caps its
          own height and scrolls inside itself, so one busy panel cannot push
          the rest of the board off the bottom of the page (#270). */}
      {/* Side by side these read left-to-right: how it is, then what wants you.
          Stacked on a phone they read top-to-bottom, and a diagnostics panel
          above the list of things asking for a decision is the wrong way round —
          so the two swap below the breakpoint and nowhere else (#278). */}
      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-3">
        <SystemPulse snapshot={data.monitoring} className="order-2 xl:order-1 xl:col-span-2" />

        <ListCard
          title="Needs you"
          count={attention.length === 0 ? "nothing right now" : `${attention.length}`}
          className="order-1 xl:order-2 xl:row-span-1"
        >
          {attention.length === 0 ? (
            <ListEmpty title="Nothing needs a decision" description="Deluno will raise anything that wants your attention here." />
          ) : (
            <div className="max-h-[164px] overflow-y-auto">
              {attention.map((item) => (
                <Link
                  key={item.id}
                  to={item.href}
                  className="flex min-h-[52px] items-center gap-2.5 border-b border-hairline px-[var(--card-pad-x)] py-2 transition-colors last:border-b-0 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
                >
                  <StatusLed tone={item.tone === "warn" ? "warn" : item.tone === "success" ? "ok" : "info"} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[length:var(--type-body-sm)] font-medium text-foreground">{item.title}</span>
                    <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{item.text}</span>
                  </span>
                  <ChevronRight aria-hidden className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                </Link>
              ))}
            </div>
          )}
        </ListCard>
      </div>

      {/* Acquisition in flight, in the order it happens: bytes arrive before
          anything can be imported, so the throughput chart leads and the stage
          strip that consumes it follows. The strip takes the wider half: it
          carries five labelled stages, and a line chart reads at any width
          while "Ready to import" does not.

          The chart is a 48-hour sampled series, which is the one question the
          hero's live wave cannot answer — that window starts empty every time
          the page opens, so "was it slow overnight" needs stored readings. */}
      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-3">
        {/* One speed surface, both directions (#289, #276).
            The reading is live and the shape behind it is stored, so the same
            card answers "what is it doing now" and "was it slow overnight" —
            the two questions that used to need two cards saying "Idle" at each
            other. Upload is here because Deluno now holds files back so a
            site's sharing rule can be met, which makes "am I actually seeding?"
            a question it has to be able to answer. */}
        <MetricChart
          label="Speed"
          value={`${formatSpeed(data.activeDownloads.length ? eventSpeedMbps : liveSpeedMbps)} down`}
          help={`${formatSpeed(liveUploadMbps)} up · peak ${formatSpeed(peakSpeed(data.throughput))} over the last ${data.throughput?.hours ?? 6} hours`}
          series={throughputSeries(data.throughput, "down")}
          compare={{
            series: throughputSeries(data.throughput, "up"),
            label: "upload",
            tone: "primary",
            value: "upload"
          }}
          tone="success"
          size="lg"
          axis="time"
          formatValue={(tenths) => `${(tenths / 10).toFixed(1)} MB/s`}
          emptyLabel="Nothing has moved in either direction in this window"
        />
        <AcquisitionPipeline
          className="xl:col-span-2"
          summary={data.sources.telemetry.summary}
          performance={data.monitoring?.performance}
          inFlight={data.activeDownloads.map((download) => ({ ...download, ...liveProgress[download.id] }))}
          sharing={data.sharing}
          showProcessing={data.hasProcessor}
        />

      </div>

      {/* What just happened, what is expected next, and what you hold as a
          result — the three answers that follow the pipeline above, and the
          ring leads straight into Recently added below it. */}
      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-3">
        <ActivityTicker seed={data.activity} limit={6} />

        <ListCard
          // This card carries episode air dates *and* scheduled search retries,
          // including for movies — so "Airing soon / Show / Episode" filed a
          // film under a TV heading with an episode of "Retry" (#258).
          title="Coming up"
          count="next 72 hours"
          actions={
            <Button asChild type="button" variant="outline" size="sm">
              <Link to="/calendar">Schedule</Link>
            </Button>
          }
        >
          {upcomingGroups.length === 0 ? (
            <ListEmpty title="Nothing scheduled" description="Air dates and release dates appear here as Deluno learns them." />
          ) : (
            <div className="max-h-[232px] overflow-y-auto">
              {upcomingGroups.flatMap((group) =>
                group.entries.slice(0, 3).map((entry) => (
                  <button
                    key={entry.id}
                    type="button"
                    onClick={() => navigate(entry.href)}
                    className="flex min-h-[52px] w-full items-center gap-2.5 border-b border-hairline px-[var(--card-pad-x)] py-2 text-left transition-colors last:border-b-0 hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
                  >
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[length:var(--type-body-sm)] font-medium text-foreground">{entry.title}</span>
                      <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{entry.episode}</span>
                    </span>
                    <span className="shrink-0 text-right">
                      <span className="block text-[length:var(--type-caption)] font-medium text-foreground">{group.day}</span>
                      <span className="block text-[length:var(--type-micro)] tabular-nums text-muted-foreground">{entry.dateLabel}</span>
                    </span>
                  </button>
                ))
              )}
            </div>
          )}
        </ListCard>
        <LibraryComposition
          // `covered` is on the payload and always was. This used to subtract
          // its way to a bucket it called "On disk" — a word DESIGN-001 retired,
          // and one that spanned two rungs, so it could never tell you which of
          // them still had work outstanding.
          covered={data.coveredCount}
          missing={data.missingCount}
          upgradable={data.upgradeCount}
          upcoming={data.upcomingCount}
          movieCount={data.movieCount}
          showCount={data.showCount}
        />
      </div>

      {/* The payoff, and the only band that answers "what did Deluno actually
          get me". That outranks the analytics below it, so it sits above them
          rather than in the basement.

          It renders only when there is something in it: the hero already says
          "In your library — 0, no media yet" and offers the add action, so an
          empty full-width band here would be the same sentence a second time
          on one screen (#270). */}
      {data.recentlyAdded.length > 0 ? (
        <ListCard
          title="Recently added"
          count={`${data.recentlyAdded.length} newest`}
          actions={
            <Button asChild type="button" variant="outline" size="sm">
              <Link to="/movies">Browse library</Link>
            </Button>
          }
        >
          <div className="dashboard-poster-grid p-[var(--card-pad-x)]">
            {data.recentlyAdded.slice(0, 12).map((item) => (
              <PosterPreview key={`${item.type}-${item.id}`} item={item} />
            ))}
          </div>
        </ListCard>
      ) : null}

      {/* Below the fold on purpose: history, not the live board. Charts at the
          large size here — this is where someone comes to read a trend, not to
          glance at one.

          Everything in this band shares one day axis and one window, so the
          range control governs the whole thing and nothing in it has to opt
          out. Three columns matching the live board at the same breakpoint, so
          every band on the page breaks to one column at the same width and the
          card edges line up all the way down. */}
      {data.metrics ? (
        <section className="flex flex-col gap-[var(--grid-gap)]">
          <header className="flex items-center justify-between gap-3">
            <h2 className="text-[length:var(--type-card-title)] font-semibold text-foreground">Trends</h2>
            <SegmentedControl
              aria-label="Trend range"
              value={historyDays}
              onValueChange={setHistoryDays}
              options={HISTORY_RANGES.map((range) => ({ value: range.value, label: range.label }))}
              className="w-auto shrink-0"
            />
          </header>
          <div className="grid gap-[var(--grid-gap)] xl:grid-cols-3">
            <MetricChart
              label="Searches"
              value={formatRate(data.metrics.searches)}
              help={`${sumSeries(data.metrics.searches.succeeded)} matched a release in ${windowLabel(data.metrics.days)}`}
              series={data.metrics.searches.succeeded}
              compare={{ series: data.metrics.searches.failed, label: "no match", tone: "warning" }}
              tone="success"
              size="lg"
            />
            <MetricChart
              label="Grabs"
              value={sumSeries(data.metrics.grabs).toLocaleString()}
              help={`sent to a download client in ${windowLabel(data.metrics.days)}`}
              series={data.metrics.grabs}
              compare={{ series: data.metrics.importFailures, label: "failed to import", tone: "danger" }}
              tone="primary"
              size="lg"
            />
            <MetricChart
              label="Background work"
              value={formatRate(data.metrics.jobs)}
              help={`${sumSeries(data.metrics.jobs.succeeded).toLocaleString()} jobs finished cleanly in ${windowLabel(data.metrics.days)}`}
              series={data.metrics.jobs.succeeded}
              compare={{ series: data.metrics.jobs.failed, label: "failed", tone: "danger" }}
              tone="success"
              size="lg"
            />
          </div>
        </section>
      ) : null}

    </div>
  );
}

/**
 * The one sentence at the top of the pane. Ranked by what would stop Deluno
 * working, so the headline is always the most consequential true statement —
 * never a cheerful default sitting above a broken system.
 */
function describeSystem(data: DashboardLoaderData, healthIssues: number): { headline: string; detail: string; tone: LedTone } {
  const services = data.monitoring?.services;
  const readiness = data.monitoring?.readiness;

  if (readiness && !readiness.ready) {
    return {
      headline: "Deluno is not ready",
      detail: `${readiness.failedChecks} of ${readiness.totalChecks} readiness checks are failing. Nothing will be searched or imported until they pass.`,
      tone: "bad"
    };
  }

  if (data.monitoring?.storage.lowStorage) {
    return {
      headline: "Running out of space",
      detail: "The drive holding your library is nearly full. Imports will start failing before it is completely gone.",
      tone: "bad"
    };
  }

  if (services && services.failedJobs > 0) {
    return {
      headline: `${services.failedJobs} ${services.failedJobs === 1 ? "job has" : "jobs have"} failed`,
      detail: "Deluno stopped retrying these. Open Activity to see what happened and put them back in the queue.",
      tone: "warn"
    };
  }

  if (healthIssues > 0) {
    return {
      headline: `${healthIssues} ${healthIssues === 1 ? "connection needs" : "connections need"} a look`,
      detail: "A search source or download client is not answering, so releases cannot be found or sent anywhere.",
      tone: "warn"
    };
  }

  if (data.activeDownloadCount > 0 || data.importReadyCount > 0) {
    const parts = [
      data.activeDownloadCount > 0 ? `${data.activeDownloadCount} downloading` : null,
      data.importReadyCount > 0 ? `${data.importReadyCount} waiting to import` : null
    ].filter(Boolean);
    return {
      headline: "Working on it",
      detail: `${parts.join(" · ")}. Everything else is watched and up to date.`,
      tone: "ok"
    };
  }

  if (data.totalCount === 0) {
    return {
      // Deliberately not "Nothing in the library yet" — that sentence already
      // belongs to the Recently added card lower down, and saying it twice on
      // one screen reads as a bug rather than emphasis.
      headline: "Ready and waiting",
      detail: "Deluno is set up and idle. Add a movie or a show and it will start watching for releases straight away.",
      tone: "idle"
    };
  }

  return {
    headline: "Everything is running",
    detail: data.missingCount > 0
      ? `${data.monitoredCount.toLocaleString()} titles are being watched for, and ${data.missingCount} of them have no acceptable release yet.`
      : `${data.monitoredCount.toLocaleString()} titles are being watched for and nothing is missing.`,
    tone: "ok"
  };
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
          <TitleMarkDot item={item} size={10} />
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
      .filter(isDueForRetry)
      .map((item) => ({
        id: `series-retry-${item.seriesId}`,
        time: new Date(item.nextEligibleSearchUtc!).getTime(),
        title: item.title,
        episode: retryIntent(item.wantedStatus),
        network: item.wantedReason,
        poster: null,
        href: `/tv/${item.seriesId}`,
        startsAt: item.nextEligibleSearchUtc!
      })),
    ...movieWanted.recentItems
      .filter(isDueForRetry)
      .map((item) => ({
        id: `movie-retry-${item.movieId}`,
        time: new Date(item.nextEligibleSearchUtc!).getTime(),
        title: item.title,
        episode: retryIntent(item.wantedStatus),
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

/**
 * A retry window outlives the reason it was opened. A title that has since been
 * satisfied still carries `nextEligibleSearchUtc`, and listing it produced the
 * contradiction "Search retry — this movie already meets your target quality"
 * (#270). Only `missing` and `upgrade` titles are still being looked for.
 */
function isDueForRetry(item: { wantedStatus: string; nextEligibleSearchUtc: string | null }) {
  return Boolean(item.nextEligibleSearchUtc) && (item.wantedStatus === "missing" || item.wantedStatus === "upgrade");
}

function retryIntent(wantedStatus: string) {
  return wantedStatus === "upgrade" ? "Looking for a better release" : "Looking for this title";
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

/**
 * Stored throughput readings as chart points. `MetricPoint.value` is an int, so
 * the series is tenths of a megabyte per second — a chart cannot show more
 * precision than that anyway, and rounding to whole MB/s would flatten every
 * slow transfer to zero.
 */
/**
 * `MetricPoint.value` is an integer, so a series in MB/s is carried in tenths
 * and scaled back by the chart's own `formatValue`.
 */
function throughputSeries(window: DownloadThroughputWindow | null, direction: "down" | "up"): MetricPoint[] {
  return (window?.samples ?? []).map((sample) => ({
    date: sample.capturedUtc,
    value: Math.round((direction === "down" ? sample.speedMbps : sample.uploadMbps ?? 0) * 10)
  }));
}

function peakSpeed(window: DownloadThroughputWindow | null) {
  const samples = window?.samples ?? [];
  return samples.length === 0
    ? 0
    : Math.max(...samples.map((sample) => Math.max(sample.speedMbps, sample.uploadMbps ?? 0)));
}

/**
 * Always a reading, never a word. "Idle" was the same sentence the hero was
 * already saying a foot higher (#276), and it hid the difference between
 * nothing downloading and nothing at all: a seeding install is not idle.
 */
function formatSpeed(mbps: number) {
  return `${Math.max(0, mbps).toFixed(1)} MB/s`;
}

/** Totals a day series. */
function sumSeries(points: MetricPoint[]) {
  return points.reduce((total, point) => total + point.value, 0);
}

/**
 * A hit rate only means something once something was tried, so with no searches
 * this says so rather than printing a confident 0%.
 */
function formatRate(outcome: { succeeded: MetricPoint[]; failed: MetricPoint[] }) {
  const matched = sumSeries(outcome.succeeded);
  const attempts = matched + sumSeries(outcome.failed);
  return attempts === 0 ? "None yet" : `${Math.round((matched / attempts) * 100)}%`;
}
