import { useMemo, useState } from "react";
import { Link, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import {
  ArrowRight,
  ArrowLeft,
  CheckSquare2,
  LoaderCircle,
  RefreshCw,
  RotateCw,
  Search,
  ShieldCheck,
  Square,
  Trash2
} from "lucide-react";
import {
  fetchJson,
  type ActivityEventItem,
  type DecisionExplanationItem,
  type DownloadDispatchItem,
  type LibraryItem,
  type MetadataCastMember,
  type IntakeTitleOriginItem,
  type MetadataSearchResult,
  type SeriesEpisodeInventoryItem,
  type SeriesImportRecoverySummary,
  type SeriesInventoryDetail,
  type SeriesListItem,
  type SeriesSearchHistoryItem,
  type SeriesWantedSummary
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { RemoveMediaDialog, type MediaRemovalPreview, type RemoveMediaOptions } from "../components/app/remove-media-dialog";
import { DecisionExplanationList } from "../components/app/decision-explanation-list";
import { RatingStrip } from "../components/app/rating-strip";
import { EpisodeSearchHistory } from "../components/app/episode-search-history";
import { EpisodeMonitoringWidget } from "../components/app/episode-monitoring-widget";
import { EmptyState } from "../components/shell/empty-state";
import { RouteSkeleton } from "../components/shell/skeleton";

interface ShowDetailLoaderData {
  activity: ActivityEventItem[];
  decisions: DecisionExplanationItem[];
  dispatches: DownloadDispatchItem[];
  importRecovery: SeriesImportRecoverySummary;
  inventory: SeriesInventoryDetail;
  libraries: LibraryItem[];
  origins: IntakeTitleOriginItem[];
  removalPreview: MediaRemovalPreview;
  searchHistory: SeriesSearchHistoryItem[];
  series: SeriesListItem;
  wanted: SeriesWantedSummary;
}

type EpisodeFilter = "all" | "missing" | "upgrade" | "monitored" | "imported";

interface MetadataOverridePayload {
  originalTitle: string;
  overview: string;
  posterUrl: string;
  backdropUrl: string;
  rating: string;
  genres: string;
  externalUrl: string;
  imdbId: string;
}

export async function showDetailLoader({
  params
}: {
  params: { id?: string };
}): Promise<ShowDetailLoaderData> {
  const id = params.id!;
  const [series, wanted, searchHistory, dispatches, importRecovery, inventory, activity, decisions, libraries, origins, removalPreview] =
    await Promise.all([
      fetchJson<SeriesListItem>(`/api/series/${id}`),
      fetchJson<SeriesWantedSummary>("/api/series/wanted"),
      fetchJson<SeriesSearchHistoryItem[]>("/api/series/search-history"),
      fetchJson<DownloadDispatchItem[]>("/api/download-dispatches?mediaType=tv"),
      fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery"),
      fetchJson<SeriesInventoryDetail>(`/api/series/${id}/inventory`),
      fetchJson<ActivityEventItem[]>(
        `/api/activity?relatedEntityType=series&relatedEntityId=${id}&take=20`
      ),
      fetchJson<DecisionExplanationItem[]>(`/api/decisions?relatedEntityType=series&relatedEntityId=${id}&take=40`),
      fetchJson<LibraryItem[]>("/api/libraries"),
      fetchJson<IntakeTitleOriginItem[]>(`/api/intake-title-origins?mediaType=tv&entityId=${encodeURIComponent(id)}`).catch(() => []),
      fetchJson<MediaRemovalPreview>(`/api/series/${id}/removal-preview`).catch(() => ({ filePaths: [], folderPaths: [], warnings: [] }))
    ]);

  return { activity, decisions, importRecovery, inventory, libraries, origins, removalPreview, searchHistory, series, wanted, dispatches };
}

export function ShowDetailPage() {
  const loaderData = useLoaderData() as ShowDetailLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { activity, decisions, dispatches, importRecovery, inventory, libraries, origins, removalPreview, searchHistory, series, wanted } = loaderData;
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const [selectedEpisodeIds, setSelectedEpisodeIds] = useState<string[]>([]);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [isRemoveConfirmationOpen, setIsRemoveConfirmationOpen] = useState(false);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [metadataQuery, setMetadataQuery] = useState(series.title);
  const [metadataMatches, setMetadataMatches] = useState<MetadataSearchResult[]>([]);
  const [metadataSearchAttempted, setMetadataSearchAttempted] = useState(false);
  const [metadataOverride, setMetadataOverride] = useState<MetadataOverridePayload>({
    originalTitle: series.originalTitle ?? "",
    overview: series.overview ?? "",
    posterUrl: series.posterUrl ?? "",
    backdropUrl: series.backdropUrl ?? "",
    rating: series.rating !== null && series.rating !== undefined ? String(series.rating) : "",
    genres: series.genres ?? "",
    externalUrl: series.externalUrl ?? "",
    imdbId: series.imdbId ?? ""
  });
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);
  const [episodeFilter, setEpisodeFilter] = useState<EpisodeFilter>("all");
  const [query, setQuery] = useState("");
  const [activeDetailSection, setActiveDetailSection] = useState<"episodes" | "details" | "automation" | "history">("episodes");

  const wantedItem = wanted.recentItems.find((item) => item.seriesId === series.id) ?? null;
  const library = wantedItem ? libraries.find((item) => item.id === wantedItem.libraryId) ?? null : null;
  const seriesSearches = searchHistory.filter((item) => item.seriesId === series.id);
  const seriesDispatches = dispatches.filter((item) => item.entityId === series.id);
  const importCases = importRecovery.recentCases.filter(
    (item) => item.title.trim().toLowerCase() === series.title.trim().toLowerCase()
  );
  const cast = readStoredCast(series.metadataJson);

  const visibleEpisodes = useMemo(
    () => inventory.episodes.filter((episode) => matchesEpisodeFilter(episode, episodeFilter, query)),
    [episodeFilter, inventory.episodes, query]
  );
  const visibleSeasons = useMemo(() => buildSeasonGroups(visibleEpisodes), [visibleEpisodes]);
  const allVisibleSelected =
    visibleEpisodes.length > 0 &&
    visibleEpisodes.every((episode) => selectedEpisodeIds.includes(episode.episodeId));
  const missingCount = inventory.episodes.filter(
    (item) => item.wantedStatus === "missing" || !item.hasFile
  ).length;
  const nextStep = importCases.length
    ? {
        eyebrow: "Needs attention",
        title: "Review the import issue",
        description: `${importCases.length} import issue${importCases.length === 1 ? "" : "s"} need${importCases.length === 1 ? "s" : ""} a decision before this show is fully settled.`,
        action: "Review import",
        href: "#import-activity"
      }
    : releaseCandidates.length
      ? {
          eyebrow: "Release ready",
          title: "Choose a release",
          description: "Deluno found matching releases. Review the choices below and send the one you want to downloads.",
          action: "Choose release",
          href: "#release-choices"
        }
      : !series.monitored
        ? {
            eyebrow: "Monitoring paused",
            title: "Resume automatic care",
            description: "This show is not being watched for missing episodes or quality improvements.",
            action: "Monitor show",
            onAction: () => void handleSeriesMonitoring(true)
          }
        : missingCount > 0
          ? {
              eyebrow: "Episodes missing",
              title: "Find missing episodes",
              description: `${missingCount} episode${missingCount === 1 ? " is" : "s are"} missing from your library.`,
              action: "Find episodes",
              onAction: () => void handleSearchNow("automatic")
            }
          : null;
  const upgradeCount = inventory.episodes.filter((item) => item.wantedStatus === "upgrade").length;

  async function handleEpisodeMonitoring(monitored: boolean) {
    if (!selectedEpisodeIds.length) {
      return;
    }

    setBusyAction(monitored ? "episode-monitor" : "episode-unmonitor");
    setActionMessage(null);

    try {
      const response = await authedFetch("/api/series/episodes/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds: selectedEpisodeIds, monitored })
      });

      if (!response.ok) {
        throw new Error("episode-monitoring-failed");
      }

      setActionMessage(monitored ? "Episodes monitored." : "Episodes unmonitored.");
      setSelectedEpisodeIds([]);
      revalidator.revalidate();
    } catch {
      setActionMessage("Episode update failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSeriesMonitoring(monitored: boolean) {
    setBusyAction(monitored ? "series-monitor" : "series-unmonitor");
    setActionMessage(null);

    try {
      const response = await authedFetch("/api/series/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seriesIds: [series.id], monitored })
      });

      if (!response.ok) {
        throw new Error("series-monitoring-failed");
      }

      setActionMessage(monitored ? "Background automation resumed for this series." : "Background automation paused for this series.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Series update failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleRemoveFromDeluno(options: RemoveMediaOptions) {
    setBusyAction("remove");
    setActionMessage(null);
    try {
      const response = await authedFetch("/api/series/bulk", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seriesIds: [series.id], operation: "remove", ...options })
      });
      if (!response.ok) throw new Error("series-remove-failed");

      const result = await response.json() as { successCount?: number };
      if ((result.successCount ?? 0) !== 1) throw new Error("series-remove-failed");
      navigate("/tv", { replace: true });
    } catch {
      setActionMessage("Could not remove this TV show from Deluno.");
    } finally {
      setBusyAction(null);
      setIsRemoveConfirmationOpen(false);
    }
  }

  async function handleDeferAutomation() {
    if (!wantedItem) return;
    setBusyAction("defer");
    setActionMessage(null);
    try {
      const response = await authedFetch(`/api/series/${series.id}/automation/defer`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: wantedItem.libraryId, hours: 24 })
      });
      if (!response.ok) throw new Error("series-defer-failed");
      setActionMessage("Background automation deferred for 24 hours. You can still search manually.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Could not defer background automation for this series.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSkipNextAutomationSearch() {
    if (!wantedItem) return;
    setBusyAction("skip-once");
    setActionMessage(null);
    try {
      const response = await authedFetch(`/api/series/${series.id}/automation/skip-once`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: wantedItem.libraryId })
      });
      if (!response.ok) throw new Error("series-skip-once-failed");
      setActionMessage("The next scheduled search will be skipped. You can still search manually.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Could not skip the next scheduled search for this series.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSearchNow(mode: "automatic" | "interactive") {
    setBusyAction(`${mode}-search`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/series/${series.id}/search${mode === "interactive" ? "?mode=preview" : ""}`, { method: "POST" });
      if (!response.ok) {
        throw new Error("series-search-failed");
      }

      const payload = (await response.json()) as {
        outcome?: string;
        summary?: string;
        releaseName?: string | null;
        indexerName?: string | null;
        dispatchStatus?: string | null;
        dispatchMessage?: string | null;
        candidates?: SearchPlanCandidate[];
      };
      const best = payload.releaseName ? `${payload.releaseName}${payload.indexerName ? ` via ${payload.indexerName}` : ""}` : null;
      setReleaseCandidates(mode === "interactive" ? payload.candidates ?? [] : []);
      setActionMessage(mode === "interactive" ? formatSearchActionMessage("series", best, payload) : (best ? `Deluno selected ${best} using this series’ Media Plan.` : "Deluno searched using this series’ Media Plan."));
      revalidator.revalidate();
    } catch {
      setActionMessage("Search request failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleGrabCandidate(candidate: SearchPlanCandidate, force = false, overrideReason?: string) {
    setBusyAction(`${force ? "force-grab" : "grab"}:${candidate.indexerName}:${candidate.releaseName}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/series/${series.id}/grab`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          releaseName: candidate.releaseName,
          indexerId: candidate.indexerId,
          indexerName: candidate.indexerName,
          candidateQuality: candidate.quality,
          downloadUrl: candidate.downloadUrl,
          sizeBytes: candidate.sizeBytes,
          seeders: candidate.seeders,
          force,
          overrideReason: force ? overrideReason || `User forced this release despite scorer result: ${candidate.summary}` : null
        })
      });

      if (!response.ok) {
        throw new Error("series-grab-failed");
      }

      const payload = (await response.json()) as {
        releaseName?: string;
        indexerName?: string | null;
        forceOverride?: boolean;
        dispatchStatus?: string;
        dispatchMessage?: string;
      };
      const best = payload.releaseName
        ? `${payload.releaseName}${payload.indexerName ? ` via ${payload.indexerName}` : ""}`
        : candidate.releaseName;
      setActionMessage(formatSearchActionMessage("series", best, { ...payload, candidates: [candidate] }));
      setReleaseCandidates([]);
      revalidator.revalidate();
    } catch {
      setActionMessage("Release could not be sent to the download client.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleRefreshMetadata() {
    setBusyAction("metadata");
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/series/${series.id}/metadata/refresh`, { method: "POST" });
      if (!response.ok) {
        throw new Error("series-metadata-refresh-failed");
      }

      setActionMessage("TV metadata refreshed.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Metadata refresh failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleMetadataSearch() {
    setBusyAction("metadata-search");
    setActionMessage(null);
    setMetadataSearchAttempted(true);
    try {
      const params = new URLSearchParams({
        query: metadataQuery.trim() || series.title,
        mediaType: "tv"
      });
      if (series.startYear) params.set("year", String(series.startYear));
      const results = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      setMetadataMatches(results.slice(0, 6));
      setActionMessage(results.length ? `${results.length} metadata match${results.length === 1 ? "" : "es"} found.` : "No metadata matches found.");
    } catch {
      setActionMessage("Metadata search failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleMetadataLink(result: MetadataSearchResult) {
    setBusyAction(`metadata-link:${result.providerId}`);
    setActionMessage(null);
    try {
      const response = await authedFetch(`/api/series/${series.id}/metadata/link`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ providerId: result.providerId })
      });
      if (!response.ok) throw new Error("metadata-link-failed");
      setMetadataMatches([]);
      setActionMessage(`Linked metadata to ${result.title}${result.year ? ` (${result.year})` : ""}.`);
      revalidator.revalidate();
    } catch {
      setActionMessage("Metadata match could not be linked.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleMetadataOverrideSave() {
    setBusyAction("metadata-override");
    setActionMessage(null);
    try {
      if (metadataOverride.rating.trim()) {
        const rating = Number(metadataOverride.rating);
        if (!Number.isFinite(rating) || rating < 0 || rating > 10) {
          setActionMessage("Rating must be a number between 0 and 10.");
          return;
        }
      }

      if (metadataOverride.posterUrl.trim() && !isValidHttpUrl(metadataOverride.posterUrl.trim())) {
        setActionMessage("Poster URL must be a valid http/https URL.");
        return;
      }

      if (metadataOverride.backdropUrl.trim() && !isValidHttpUrl(metadataOverride.backdropUrl.trim())) {
        setActionMessage("Backdrop URL must be a valid http/https URL.");
        return;
      }

      if (metadataOverride.externalUrl.trim() && !isValidHttpUrl(metadataOverride.externalUrl.trim())) {
        setActionMessage("External URL must be a valid http/https URL.");
        return;
      }

      const payload = {
        originalTitle: metadataOverride.originalTitle.trim() || null,
        overview: metadataOverride.overview.trim() || null,
        posterUrl: metadataOverride.posterUrl.trim() || null,
        backdropUrl: metadataOverride.backdropUrl.trim() || null,
        rating: metadataOverride.rating.trim() ? Number(metadataOverride.rating) : null,
        genres: metadataOverride.genres.trim() || null,
        externalUrl: metadataOverride.externalUrl.trim() || null,
        imdbId: metadataOverride.imdbId.trim() || null
      };

      const response = await authedFetch(`/api/series/${series.id}/metadata/override`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });
      if (!response.ok) {
        throw new Error("metadata-override-failed");
      }

      setActionMessage("Manual metadata overrides saved.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Manual metadata override failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleEpisodeSearch(episodeIds: string[]) {
    if (!episodeIds.length) {
      return;
    }

    setBusyAction("episode-search");
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/series/${series.id}/episodes/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds })
      });

      if (!response.ok) {
        throw new Error("episode-search-failed");
      }

      const payload = (await response.json()) as {
        searchedEpisodes?: number;
        matchedCount?: number;
        sentCount?: number;
        plannedCount?: number;
        failedCount?: number;
      };
      const searchedEpisodes = payload.searchedEpisodes ?? episodeIds.length;
      const matchedCount = payload.matchedCount ?? 0;
      setActionMessage(
        matchedCount > 0
          ? `Searched ${searchedEpisodes} episode${searchedEpisodes === 1 ? "" : "s"} and matched ${matchedCount}. ${formatDispatchSummary(payload)}`
          : `Searched ${searchedEpisodes} episode${searchedEpisodes === 1 ? "" : "s"}.`
      );
      setSelectedEpisodeIds([]);
      revalidator.revalidate();
    } catch {
      setActionMessage("Episode search failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSeasonSearch(seasonNumber: number) {
    setBusyAction(`season-search-${seasonNumber}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/series/${series.id}/seasons/${seasonNumber}/search`, {
        method: "POST"
      });
      if (!response.ok) {
        throw new Error("season-search-failed");
      }

      const payload = (await response.json()) as {
        matchedCount?: number;
        seasonNumber?: number;
        dispatchStatus?: string | null;
        dispatchMessage?: string | null;
      };
      const resolvedSeasonNumber = payload.seasonNumber ?? seasonNumber;
      const matchedCount = payload.matchedCount ?? 0;
      setActionMessage(
        matchedCount > 0
          ? `Season ${resolvedSeasonNumber} search completed with ${matchedCount} episode matches. ${formatDispatchSummary(payload)}`
          : `Season ${resolvedSeasonNumber} search completed.`
      );
      revalidator.revalidate();
    } catch {
      setActionMessage("Season search failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleDismissImportCase(id: string) {
    setBusyAction(`import-${id}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/series/import-recovery/${id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) {
        throw new Error("Import case could not be dismissed.");
      }
      setActionMessage("Import issue dismissed.");
      revalidator.revalidate();
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : "Import case could not be dismissed.");
    } finally {
      setBusyAction(null);
    }
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <Link
        to="/tv"
        className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-4 w-4" />
        Back to TV
      </Link>

      <Card className="relative isolate min-h-[19rem] overflow-hidden border-primary/25 bg-card">
        {series.backdropUrl ? <img src={series.backdropUrl} alt="" className="pointer-events-none absolute inset-0 h-full w-full scale-105 object-cover opacity-[0.34] saturate-[0.8]" /> : null}
        <div className="pointer-events-none absolute inset-0 bg-gradient-to-r from-card via-card/80 to-card/45" />
        <div className="pointer-events-none absolute inset-0 bg-gradient-to-t from-card/90 via-transparent to-card/25" />
        <CardContent className="relative p-[var(--tile-pad)] sm:p-[calc(var(--tile-pad)*1.15)]">
          <div className="grid min-h-[15rem] items-center gap-[var(--grid-gap)] md:grid-cols-[10rem_minmax(0,1fr)] xl:grid-cols-[10rem_minmax(0,1fr)_14rem]">
            {series.posterUrl ? (
              <img src={series.posterUrl} alt={`${series.title} poster`} className="h-64 w-40 justify-self-center rounded-2xl border border-white/15 bg-surface-1 object-cover shadow-2xl md:justify-self-start" />
            ) : (
              <div className="flex h-64 w-40 justify-self-center items-center justify-center rounded-2xl border border-hairline bg-surface-1 px-3 text-center text-xs text-muted-foreground md:justify-self-start">Artwork is being refreshed</div>
            )}
            <div className="min-w-0 self-center">
              <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-primary">TV series</p>
              <div className="mt-1 flex flex-wrap items-baseline gap-x-3 gap-y-1">
                <h1 className="font-display text-4xl font-semibold tracking-tight text-foreground sm:text-5xl">{series.title}</h1>
                {series.startYear ? <span className="font-display text-2xl text-muted-foreground sm:text-3xl">{series.startYear}</span> : null}
              </div>
              {series.originalTitle && series.originalTitle !== series.title ? <p className="mt-1 text-sm text-muted-foreground">Also known as {series.originalTitle}</p> : null}
              <div className="mt-4 flex flex-wrap gap-2">
                <Badge variant="default">{series.monitored ? "Monitored" : "Not monitored"}</Badge>
                {wantedItem ? <Badge variant={wantedItem.wantedStatus === "missing" || wantedItem.wantedStatus === "upgrade" ? "warning" : "info"}>{formatWantedStatus(wantedItem.wantedStatus)}</Badge> : null}
                {importCases.length ? <Badge variant="warning">{importCases.length} import issue{importCases.length === 1 ? "" : "s"}</Badge> : null}
                {series.genres?.split(",").map((genre) => <span key={genre} className="rounded-full border border-primary/20 bg-primary/10 px-2.5 py-1 text-xs font-medium text-primary">{genre.trim()}</span>)}
              </div>
              <p className="mt-4 max-w-4xl text-sm leading-relaxed text-muted-foreground">
                {series.overview ?? "No overview has been stored yet. Refresh metadata when you want Deluno to enrich this series."}
              </p>
              {cast.length > 0 ? (
                <section className="mt-5 border-t border-white/10 pt-4">
                  <div className="flex items-center justify-between gap-3"><p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground">Starring</p><span className="text-[11px] text-muted-foreground">{cast.length} credited</span></div>
                  <div className="mt-3 flex flex-wrap gap-x-5 gap-y-3">
                    {cast.slice(0, 6).map((person) => (
                      <div key={`${person.name}-${person.character ?? ""}`} className="flex min-w-0 items-center gap-2.5">
                        {person.profileUrl ? <img src={person.profileUrl} alt="" className="h-10 w-10 shrink-0 rounded-full border border-white/15 bg-surface-2 object-cover shadow-lg" /> : <div className="h-10 w-10 shrink-0 rounded-full border border-white/15 bg-surface-2" />}
                        <span className="max-w-28 min-w-0 leading-tight"><span className="block truncate text-xs font-semibold text-foreground">{person.name}</span>{person.character ? <span className="mt-0.5 block truncate text-[11px] text-muted-foreground">{person.character}</span> : null}</span>
                      </div>
                    ))}
                  </div>
                </section>
              ) : null}
            </div>
            <aside className="w-full self-center rounded-xl border border-white/10 bg-card/80 p-4 backdrop-blur-sm">
              <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground">Ratings &amp; IDs</p>
              <p className="mt-1 text-xs text-muted-foreground">The metadata Deluno is using</p>
              <div className="mt-3"><RatingStrip ratings={series.ratings} fallbackRating={series.rating} /></div>
              <div className="mt-4 space-y-2 border-t border-hairline pt-4 text-sm">
                <div className="flex items-center justify-between gap-3"><span className="text-muted-foreground">Source</span><span className="font-medium text-foreground">{series.metadataProvider?.toUpperCase() ?? "Not linked"}</span></div>
                <div className="flex items-center justify-between gap-3"><span className="text-muted-foreground">IMDb</span><span className="font-medium text-foreground">{series.imdbId ?? "—"}</span></div>
              </div>
              {series.externalUrl ? <Button asChild variant="outline" className="mt-4 w-full"><a href={series.externalUrl} target="_blank" rel="noreferrer">Open provider page</a></Button> : null}
            </aside>
          </div>
        </CardContent>
      </Card>

      <details className="rounded-xl border border-hairline bg-card px-4 py-3">
        <summary className="cursor-pointer text-sm font-medium text-muted-foreground">Metadata tools and provider record</summary>
        <div className="mt-4 space-y-[var(--page-gap)]">
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <MetadataStat label="Provider" value={series.metadataProvider ?? "Not linked"} />
            <MetadataStat label="Provider ID" value={series.metadataProviderId ?? "None"} />
            <MetadataStat label="IMDb" value={series.imdbId ?? "Not linked"} />
          </div>
          <MetadataCorrectionPanel busyAction={busyAction} mediaLabel="series" query={metadataQuery} matches={metadataMatches} searchAttempted={metadataSearchAttempted} onQueryChange={setMetadataQuery} onSearch={handleMetadataSearch} onLink={handleMetadataLink} />
          <ManualMetadataOverridePanel busyAction={busyAction} value={metadataOverride} onChange={setMetadataOverride} onSave={handleMetadataOverrideSave} />
        </div>
      </details>

      <div className="flex flex-wrap gap-2">
        <Button onClick={() => void handleSearchNow("automatic")} disabled={busyAction !== null} title="Deluno applies the active Media Plan and automatically sends the best acceptable release.">
          {busyAction === "automatic-search" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <Search className="h-4 w-4" />
          )}
          Automatic search
        </Button>
        <Button variant="outline" onClick={() => void handleSearchNow("interactive")} disabled={busyAction !== null} title="Review every candidate and choose the release yourself.">
          {busyAction === "interactive-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
          Interactive search
        </Button>
        <Button
          variant="outline"
          onClick={() =>
            void handleEpisodeSearch(
              selectedEpisodeIds.length
                ? selectedEpisodeIds
                : visibleEpisodes.map((item) => item.episodeId)
            )
          }
          disabled={busyAction !== null || (!selectedEpisodeIds.length && !visibleEpisodes.length)}
        >
          {busyAction === "episode-search" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <Search className="h-4 w-4" />
          )}
          Search current slice
        </Button>
        <Button
          variant="outline"
          onClick={() => void handleSeriesMonitoring(!series.monitored)}
          disabled={busyAction !== null}
          title={series.monitored ? "Pause background searching for this series without removing it from your library." : "Resume background searching for this series."}
        >
          {busyAction === "series-monitor" || busyAction === "series-unmonitor" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <ShieldCheck className="h-4 w-4" />
          )}
          {series.monitored ? "Pause automation" : "Resume automation"}
        </Button>
        <Button
          variant="ghost"
          className="text-destructive hover:bg-destructive/10 hover:text-destructive"
          onClick={() => setIsRemoveConfirmationOpen(true)}
          disabled={busyAction !== null}
        >
          <Trash2 className="h-4 w-4" />
          Remove from Deluno
        </Button>
      </div>

      <nav className="flex flex-wrap gap-1 rounded-xl border border-hairline bg-surface-1 p-1" aria-label="TV series detail sections">
        {[
          ["episodes", "Episodes"],
          ["details", "Files & destination"],
          ["automation", "Automation"],
          ["history", "History & activity"]
        ].map(([section, label]) => (
          <button key={section} type="button" onClick={() => setActiveDetailSection(section as "episodes" | "details" | "automation" | "history")} className={activeDetailSection === section ? "rounded-lg bg-card px-4 py-2 text-sm font-semibold text-foreground shadow-sm" : "rounded-lg px-4 py-2 text-sm font-medium text-muted-foreground hover:text-foreground"}>{label}</button>
        ))}
      </nav>

      {activeDetailSection === "automation" ? (
        <div className="space-y-[var(--page-gap)]">
          {nextStep ? (
            <Card className="overflow-hidden border-primary/20 bg-gradient-to-r from-primary/[0.08] via-primary/[0.03] to-transparent"><CardContent className="flex flex-col gap-[var(--grid-gap)] p-5 sm:flex-row sm:items-center sm:justify-between"><div className="min-w-0"><p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-primary">{nextStep.eyebrow}</p><h2 className="mt-1 font-display text-lg font-semibold tracking-tight text-foreground">{nextStep.title}</h2><p className="mt-1 max-w-2xl text-sm leading-relaxed text-muted-foreground">{nextStep.description}</p></div>{nextStep.href ? <Button asChild className="shrink-0"><a href={nextStep.href}>{nextStep.action}<ArrowRight className="h-4 w-4" /></a></Button> : <Button onClick={nextStep.onAction} disabled={busyAction !== null} className="shrink-0">{nextStep.action}</Button>}</CardContent></Card>
          ) : null}
          <Card><CardHeader><CardTitle>Automation controls</CardTitle><CardDescription>Fine-tune how Deluno looks after this series without changing its episode inventory.</CardDescription></CardHeader><CardContent className="flex flex-wrap gap-2"><Button variant="outline" onClick={() => void handleDeferAutomation()} disabled={busyAction !== null || !wantedItem}><RotateCw className="h-4 w-4" /> Defer 24h</Button><Button variant="outline" onClick={() => void handleSkipNextAutomationSearch()} disabled={busyAction !== null || !wantedItem}><RotateCw className="h-4 w-4" /> Skip next search</Button><Button variant="outline" onClick={() => void handleRefreshMetadata()} disabled={busyAction !== null}><RefreshCw className="h-4 w-4" /> Refresh metadata</Button></CardContent></Card>
        </div>
      ) : null}

      {actionMessage ? (
        <div className="rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-sm text-muted-foreground" role="status" aria-live="polite">
          {actionMessage}
        </div>
      ) : null}

      {releaseCandidates.length ? (
        <div id="release-choices">
        <ReleaseCandidatePicker
          candidates={releaseCandidates}
          busyAction={busyAction}
          onGrab={handleGrabCandidate}
        />
        </div>
      ) : null}

      {activeDetailSection === "details" ? (
        <div className="space-y-[var(--page-gap)]">
          <RoutingCard
            library={library}
            currentQuality={wantedItem?.currentQuality ?? null}
            targetQuality={wantedItem?.targetQuality ?? "WEB 1080p"}
            workflow={library?.importWorkflow ?? "standard"}
          />
          <IntakeOriginsCard origins={origins} mediaLabel="series" />
        </div>
      ) : null}

      {activeDetailSection === "episodes" || activeDetailSection === "history" ? (
      <div className={activeDetailSection === "history" ? "space-y-[var(--page-gap)]" : "grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.28fr)_minmax(380px,0.82fr)] 2xl:grid-cols-[minmax(0,1.5fr)_minmax(440px,0.65fr)]"}>
        <div className={activeDetailSection === "episodes" ? "space-y-[var(--page-gap)]" : "hidden"}>
          <Card>
            <CardHeader>
              <CardTitle>Episode operations</CardTitle>
              <CardDescription>
                Filter, inspect, and bulk-manage real episode inventory rows.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
              <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_auto]">
                <div className="flex flex-col gap-3">
                  <input
                    value={query}
                    onChange={(event) => setQuery(event.target.value)}
                    placeholder="Filter by episode code or title"
                  className="density-control-text h-[var(--control-height)] rounded-xl border border-hairline bg-surface-1 px-[var(--field-pad-x)] text-foreground outline-none ring-0 placeholder:text-muted-foreground"
                  />
                  <div className="flex flex-wrap gap-2">
                    {episodeFilterOptions.map((option) => (
                      <button
                        key={option.key}
                        type="button"
                        onClick={() => setEpisodeFilter(option.key)}
                        className={
                          episodeFilter === option.key
                            ? "rounded-full border border-primary/40 bg-primary/10 px-3 py-1.5 text-xs text-primary"
                            : "rounded-full border border-hairline bg-card px-3 py-1.5 text-xs text-muted-foreground hover:text-foreground"
                        }
                      >
                        {option.label}
                      </button>
                    ))}
                  </div>
                </div>
                <div className="flex flex-wrap gap-2 lg:justify-end">
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() =>
                      setSelectedEpisodeIds(
                        allVisibleSelected ? [] : visibleEpisodes.map((item) => item.episodeId)
                      )
                    }
                  >
                    {allVisibleSelected ? (
                      <CheckSquare2 className="h-4 w-4" />
                    ) : (
                      <Square className="h-4 w-4" />
                    )}
                    {selectedEpisodeIds.length ? `${selectedEpisodeIds.length} selected` : "Select visible"}
                  </Button>
                  <Button
                    size="sm"
                    onClick={() => void handleEpisodeMonitoring(true)}
                    disabled={!selectedEpisodeIds.length || busyAction !== null}
                  >
                    {busyAction === "episode-monitor" ? (
                      <LoaderCircle className="h-4 w-4 animate-spin" />
                    ) : null}
                    Monitor selected
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => void handleEpisodeMonitoring(false)}
                    disabled={!selectedEpisodeIds.length || busyAction !== null}
                  >
                    {busyAction === "episode-unmonitor" ? (
                      <LoaderCircle className="h-4 w-4 animate-spin" />
                    ) : null}
                    Unmonitor selected
                  </Button>
                </div>
              </div>

              <div className="rounded-xl border border-hairline bg-surface-1 px-3 py-3 text-sm text-muted-foreground">
                Showing {visibleEpisodes.length} of {inventory.episodeCount} episodes in this series.
              </div>

              {visibleSeasons.length ? (
                visibleSeasons.map((season) => (
                  <div key={season.seasonNumber} className="rounded-xl border border-hairline bg-card">
                    <div className="border-b border-hairline px-4 py-3">
                      <div className="flex flex-col gap-2 md:flex-row md:items-center md:justify-between">
                        <div>
                          <p className="font-display text-base font-semibold text-foreground">
                            {formatSeasonLabel(season.seasonNumber)}
                          </p>
                          <p className="text-sm text-muted-foreground">
                            {season.importedCount}/{season.episodes.length} imported · {season.missingCount} missing ·{" "}
                            {season.monitoredCount} monitored
                          </p>
                        </div>
                        <div className="flex flex-wrap gap-2">
                          <Button
                            variant="outline"
                            size="sm"
                            onClick={() => void handleSeasonSearch(season.seasonNumber)}
                            disabled={busyAction !== null}
                          >
                            {busyAction === `season-search-${season.seasonNumber}` ? (
                              <LoaderCircle className="h-4 w-4 animate-spin" />
                            ) : (
                              <Search className="h-4 w-4" />
                            )}
                            Search season
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() =>
                              setSelectedEpisodeIds((current) => {
                                const seasonIds = season.episodes.map((item) => item.episodeId);
                                const allSelected = seasonIds.every((id) => current.includes(id));
                                return allSelected
                                  ? current.filter((id) => !seasonIds.includes(id))
                                  : [...new Set([...current, ...seasonIds])];
                              })
                            }
                          >
                            {season.episodes.every((item) =>
                              selectedEpisodeIds.includes(item.episodeId)
                            ) ? (
                              <CheckSquare2 className="h-4 w-4" />
                            ) : (
                              <Square className="h-4 w-4" />
                            )}
                            Select season
                          </Button>
                        </div>
                      </div>
                    </div>
                    <div className="divide-y divide-hairline">
                      {season.episodes.map((episode) => {
                        const checked = selectedEpisodeIds.includes(episode.episodeId);
                        return (
                          <button
                            key={episode.episodeId}
                            type="button"
                            className={
                              checked
                                ? "grid w-full grid-cols-[auto_minmax(0,1fr)_auto] gap-3 bg-surface-1 px-4 py-3 text-left"
                                : "grid w-full grid-cols-[auto_minmax(0,1fr)_auto] gap-3 px-4 py-3 text-left hover:bg-surface-1"
                            }
                            onClick={() =>
                              setSelectedEpisodeIds((current) =>
                                checked
                                  ? current.filter((id) => id !== episode.episodeId)
                                  : [...current, episode.episodeId]
                              )
                            }
                          >
                            <span className="pt-0.5">
                              {checked ? (
                                <CheckSquare2 className="h-4 w-4 text-primary" />
                              ) : (
                                <Square className="h-4 w-4 text-muted-foreground" />
                              )}
                            </span>
                            <div className="min-w-0">
                              <div className="flex flex-wrap items-center gap-2">
                                <p className="text-sm font-medium text-foreground">
                                  {formatEpisodeCode(episode)}
                                </p>
                                <Badge
                                  variant={
                                    episode.wantedStatus === "missing" || episode.wantedStatus === "upgrade"
                                      ? "warning"
                                      : "info"
                                  }
                                >
                                  {formatWantedStatus(episode.wantedStatus)}
                                </Badge>
                                <Badge variant="default">
                                  {episode.monitored ? "Monitored" : "Not monitored"}
                                </Badge>
                                <Button
                                  size="sm"
                                  variant="ghost"
                                  className="h-7 px-2 text-xs"
                                  onClick={(event) => {
                                    event.stopPropagation();
                                    void handleEpisodeSearch([episode.episodeId]);
                                  }}
                                  disabled={busyAction !== null}
                                >
                                  {busyAction === "episode-search" ? (
                                    <LoaderCircle className="h-3.5 w-3.5 animate-spin" />
                                  ) : (
                                    <Search className="h-3.5 w-3.5" />
                                  )}
                                  Search
                                </Button>
                              </div>
                              <p className="mt-1 text-sm text-foreground">
                                {episode.title ?? "Episode title pending"}
                              </p>
                              <p className="mt-1 text-xs text-muted-foreground">
                                {episode.airDateUtc ? `Airs ${formatDateTime(episode.airDateUtc)} · ` : ""}
                                {episode.wantedReason}
                              </p>
                            </div>
                            <div className="text-right text-xs text-muted-foreground">
                              <p>{episode.hasFile ? "Imported" : "Missing file"}</p>
                              <p className="mt-1">{formatDateTime(episode.updatedUtc)}</p>
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  </div>
                ))
              ) : (
                <EmptyState
                  size="sm"
                  variant="search"
                  title="No matching episodes"
                  description="Try a different filter — monitored, missing, or upgrade targets."
                />
              )}
            </CardContent>
          </Card>
        </div>

        <div className="space-y-[var(--page-gap)]">
          {activeDetailSection === "episodes" ? <EpisodeMonitoringWidget
            episodes={inventory.episodes}
            selectedCount={selectedEpisodeIds.length}
            onMonitor={handleEpisodeMonitoring}
            isBusy={busyAction?.startsWith("episode-monitor") ?? false}
          /> : null}

          {activeDetailSection === "history" ? <>
          <Card id="decision-trail">
            <CardHeader>
              <CardTitle>Decision trail</CardTitle>
              <CardDescription>
                Search, grab, import, and retry decisions recorded for this series.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <DecisionExplanationList decisions={decisions} />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Search and dispatch</CardTitle>
              <CardDescription>
                Recent search outcomes and releases sent to download clients.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
              {seriesSearches.length ? (
                seriesSearches.slice(0, 8).map((item) => (
                  <div key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                    <div className="flex items-center justify-between gap-3">
                      <p className="text-sm font-medium text-foreground">
                        {item.releaseName ?? "No release selected"}
                      </p>
                      <Badge variant={item.outcome === "matched" ? "success" : "warning"}>
                        {item.outcome}
                      </Badge>
                    </div>
                    <p className="mt-2 text-sm text-muted-foreground">
                      {formatSearchHistoryContext(item)} · {formatTriggerKind(item.triggerKind)}
                    </p>
                    <p className="mt-1 text-xs text-muted-foreground">{formatDateTime(item.createdUtc)}</p>
                    <SearchCandidateBreakdown detailsJson={item.detailsJson} />
                  </div>
                ))
              ) : (
                <EmptyState
                  size="sm"
                  variant="custom"
                  title="No search history"
                  description="Manual and scheduled searches for this series will appear here once they run."
                />
              )}

              {seriesDispatches.length ? (
                <div className="space-y-3 pt-2">
                  {seriesDispatches.slice(0, 6).map((item) => (
                    <div key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                      <div className="flex items-center justify-between gap-3">
                        <p className="text-sm font-medium text-foreground">{item.releaseName}</p>
                        <Badge variant={getDispatchBadgeVariant(item.status)}>
                          {formatDispatchStatus(item.status)}
                        </Badge>
                      </div>
                      <p className="mt-2 text-sm text-muted-foreground">
                        {item.indexerName} · {item.downloadClientName}
                      </p>
                    </div>
                  ))}
                  <Link to="/queue" className="inline-flex text-sm font-medium text-amber-400 hover:text-amber-300">
                    Open Transfers to manage download-client work →
                  </Link>
                </div>
              ) : null}
            </CardContent>
          </Card>

          <EpisodeSearchHistory searches={seriesSearches} />

          <Card id="import-activity">
            <CardHeader>
              <CardTitle>Import and activity</CardTitle>
              <CardDescription>
                Import pressure and entity-scoped activity for this title.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
              {importCases.length ? (
                importCases.map((item) => (
                  <div key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                    <div className="flex items-center justify-between gap-3">
                      <p className="text-sm font-medium text-foreground">
                        {formatFailureKind(item.failureKind)}
                      </p>
                      <div className="flex items-center gap-2">
                        <Badge variant="warning">Import</Badge>
                        <Button
                          size="sm"
                          variant="ghost"
                          onClick={() => void handleDismissImportCase(item.id)}
                          disabled={busyAction === `import-${item.id}`}
                        >
                          {busyAction === `import-${item.id}` ? (
                            <LoaderCircle className="h-4 w-4 animate-spin" />
                          ) : null}
                          Dismiss
                        </Button>
                      </div>
                    </div>
                    <p className="mt-2 text-sm text-muted-foreground">{item.summary}</p>
                    <p className="mt-1 text-xs text-muted-foreground">{item.recommendedAction}</p>
                  </div>
                ))
              ) : (
                <p className="text-sm text-muted-foreground">
                  No import issues recorded for this series.
                </p>
              )}

              {activity.length ? (
                <div className="space-y-3 pt-2">
                  {activity.slice(0, 8).map((item) => (
                    <div key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                      <p className="text-sm font-medium text-foreground">{item.message}</p>
                      <p className="mt-1 text-xs text-muted-foreground">
                        {item.category} · {formatDateTime(item.createdUtc)}
                      </p>
                    </div>
                  ))}
                </div>
              ) : null}
            </CardContent>
          </Card>
          </> : null}
        </div>
      </div>
      ) : null}
      <RemoveMediaDialog
        open={isRemoveConfirmationOpen}
        onOpenChange={setIsRemoveConfirmationOpen}
        title={series.title}
        mediaLabel="TV show"
        removalPreview={removalPreview}
        importListCount={origins.length}
        busy={busyAction === "remove"}
        onConfirm={(options) => void handleRemoveFromDeluno(options)}
      />
    </div>
  );
}

function IntakeOriginsCard({ origins, mediaLabel }: { origins: IntakeTitleOriginItem[]; mediaLabel: string }) {
  if (!origins.length) return null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>How this {mediaLabel} was added</CardTitle>
        <CardDescription>
          Import-list origin is kept for context. Removing a list never removes this {mediaLabel} or its files.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-2">
        {origins.map((origin) => (
          <div key={origin.id} className="rounded-xl border border-hairline bg-surface-1 p-3">
            <p className="text-sm font-medium text-foreground">{origin.sourceName}</p>
            <p className="mt-1 text-xs text-muted-foreground">
              Import list · {origin.provider} · first seen {formatDateTime(origin.firstSeenUtc)}
            </p>
          </div>
        ))}
      </CardContent>
    </Card>
  );
}

function MetadataStat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-hairline bg-surface-1 p-3">
      <p className="text-[10px] uppercase tracking-[0.16em] text-muted-foreground">{label}</p>
      <p className="mt-1 truncate text-sm font-medium text-foreground">{value}</p>
    </div>
  );
}

function readStoredCast(metadataJson: string | null): MetadataCastMember[] {
  if (!metadataJson) return [];

  try {
    const parsed = JSON.parse(metadataJson) as { cast?: unknown; Cast?: unknown };
    const cast = parsed.cast ?? parsed.Cast;
    if (!Array.isArray(cast)) return [];

    return cast.flatMap((person) => {
      if (typeof person !== "object" || person === null) return [];
      const item = person as Record<string, unknown>;
      const name = item.name ?? item.Name;
      if (typeof name !== "string" || !name.trim()) return [];

      return [{
        name,
        character: typeof (item.character ?? item.Character) === "string" ? (item.character ?? item.Character) as string : null,
        profileUrl: typeof (item.profileUrl ?? item.ProfileUrl) === "string" ? (item.profileUrl ?? item.ProfileUrl) as string : null
      }];
    });
  } catch {
    return [];
  }
}

function RoutingCard({
  currentQuality,
  library,
  targetQuality,
  workflow
}: {
  currentQuality: string | null;
  library: LibraryItem | null;
  targetQuality: string | null;
  workflow: string;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Routing and destination</CardTitle>
        <CardDescription>
          Final episode filenames are previewed once Deluno has a source file. This shows the active series route now.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3 sm:grid-cols-2">
        <MetadataStat label="Library" value={library?.name ?? "Not linked"} />
        <MetadataStat label="Root folder" value={library?.rootPath || "No root configured"} />
        <MetadataStat label="Downloads folder" value={library?.downloadsPath || "Client default"} />
        <MetadataStat label="Workflow" value={workflow === "refine-before-import" ? "Refine before import" : "Standard import"} />
        <MetadataStat label="Current quality" value={currentQuality ?? "Unknown"} />
        <MetadataStat label="Target quality" value={targetQuality ?? "WEB 1080p"} />
      </CardContent>
    </Card>
  );
}

function MetadataCorrectionPanel({
  busyAction,
  matches,
  mediaLabel,
  searchAttempted,
  onLink,
  onQueryChange,
  onSearch,
  query
}: {
  busyAction: string | null;
  matches: MetadataSearchResult[];
  mediaLabel: string;
  searchAttempted: boolean;
  onLink: (result: MetadataSearchResult) => Promise<void>;
  onQueryChange: (value: string) => void;
  onSearch: () => Promise<void>;
  query: string;
}) {
  return (
    <div className="rounded-xl border border-hairline bg-surface-1 p-4">
      <p className="font-display text-sm font-semibold tracking-tight text-foreground">Correct metadata match</p>
      <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
        Search the provider, choose the right {mediaLabel}, then Deluno refreshes artwork, IDs, genres, ratings, and overview from that match.
      </p>
      <div className="mt-3 grid gap-2 sm:grid-cols-[minmax(0,1fr)_auto]">
        <input
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
          className="h-10 rounded-lg border border-hairline bg-background px-3 text-sm text-foreground outline-none focus:border-primary"
          placeholder={`Search ${mediaLabel} metadata`}
        />
        <Button type="button" variant="outline" onClick={() => void onSearch()} disabled={busyAction === "metadata-search"}>
          {busyAction === "metadata-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
          Find match
        </Button>
      </div>
      {matches.length ? (
        <div className="mt-3 space-y-2">
          {matches.map((match) => (
            <div key={`${match.provider}:${match.providerId}`} className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-background/40 p-3">
              <div className="min-w-0">
                <p className="truncate text-sm font-semibold text-foreground">
                  {match.title} {match.year ? <span className="text-muted-foreground">({match.year})</span> : null}
                </p>
                <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">{match.overview ?? `${match.provider.toUpperCase()} ${match.providerId}`}</p>
              </div>
              <Button
                type="button"
                size="sm"
                onClick={() => void onLink(match)}
                disabled={busyAction === `metadata-link:${match.providerId}`}
              >
                {busyAction === `metadata-link:${match.providerId}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Use
              </Button>
            </div>
          ))}
        </div>
      ) : null}
      {searchAttempted && matches.length === 0 ? (
        <p className="mt-3 text-xs text-muted-foreground">No matches found. Try adding year, original title, or IMDb ID keywords.</p>
      ) : null}
    </div>
  );
}

function ManualMetadataOverridePanel({
  busyAction,
  value,
  onChange,
  onSave
}: {
  busyAction: string | null;
  value: MetadataOverridePayload;
  onChange: (value: MetadataOverridePayload) => void;
  onSave: () => Promise<void>;
}) {
  return (
    <div className="rounded-xl border border-hairline bg-surface-1 p-4">
      <p className="font-display text-sm font-semibold tracking-tight text-foreground">Manual override</p>
      <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
        Use this when provider metadata is incomplete. Saved values are persisted as local overrides.
      </p>
      <div className="mt-3 grid gap-2 sm:grid-cols-2">
        <input
          value={value.originalTitle}
          onChange={(event) => onChange({ ...value, originalTitle: event.target.value })}
          className="h-10 rounded-lg border border-hairline bg-background px-3 text-sm text-foreground outline-none focus:border-primary"
          placeholder="Original title"
        />
        <input
          value={value.imdbId}
          onChange={(event) => onChange({ ...value, imdbId: event.target.value })}
          className="h-10 rounded-lg border border-hairline bg-background px-3 text-sm text-foreground outline-none focus:border-primary"
          placeholder="IMDb ID"
        />
        <input
          value={value.posterUrl}
          onChange={(event) => onChange({ ...value, posterUrl: event.target.value })}
          className="h-10 rounded-lg border border-hairline bg-background px-3 text-sm text-foreground outline-none focus:border-primary"
          placeholder="Poster URL"
        />
        <input
          value={value.backdropUrl}
          onChange={(event) => onChange({ ...value, backdropUrl: event.target.value })}
          className="h-10 rounded-lg border border-hairline bg-background px-3 text-sm text-foreground outline-none focus:border-primary"
          placeholder="Backdrop URL"
        />
        <input
          value={value.rating}
          onChange={(event) => onChange({ ...value, rating: event.target.value })}
          className="h-10 rounded-lg border border-hairline bg-background px-3 text-sm text-foreground outline-none focus:border-primary"
          placeholder="Rating (0-10)"
        />
        <input
          value={value.genres}
          onChange={(event) => onChange({ ...value, genres: event.target.value })}
          className="h-10 rounded-lg border border-hairline bg-background px-3 text-sm text-foreground outline-none focus:border-primary"
          placeholder="Genres (comma separated)"
        />
        <input
          value={value.externalUrl}
          onChange={(event) => onChange({ ...value, externalUrl: event.target.value })}
          className="h-10 rounded-lg border border-hairline bg-background px-3 text-sm text-foreground outline-none focus:border-primary sm:col-span-2"
          placeholder="External URL"
        />
      </div>
      <div className="mt-3">
        <textarea
          value={value.overview}
          onChange={(event) => onChange({ ...value, overview: event.target.value })}
          className="min-h-24 w-full rounded-lg border border-hairline bg-background px-3 py-2 text-sm text-foreground outline-none focus:border-primary"
          placeholder="Overview"
        />
      </div>
      <div className="mt-3">
        <Button type="button" variant="outline" size="sm" onClick={() => void onSave()} disabled={busyAction === "metadata-override"}>
          {busyAction === "metadata-override" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
          Save manual metadata
        </Button>
      </div>
    </div>
  );
}

const episodeFilterOptions: Array<{ key: EpisodeFilter; label: string }> = [
  { key: "all", label: "All" },
  { key: "missing", label: "Missing" },
  { key: "upgrade", label: "Upgrade" },
  { key: "monitored", label: "Monitored" },
  { key: "imported", label: "Imported" }
];

function matchesEpisodeFilter(
  episode: SeriesEpisodeInventoryItem,
  filter: EpisodeFilter,
  query: string
) {
  const episodeCode = formatEpisodeCode(episode).toLowerCase();
  const haystack = `${episodeCode} ${episode.title ?? ""}`.toLowerCase();
  const matchesQuery = !query.trim() || haystack.includes(query.trim().toLowerCase());

  const matchesFilter =
    filter === "all" ||
    (filter === "missing" && (episode.wantedStatus === "missing" || !episode.hasFile)) ||
    (filter === "upgrade" && episode.wantedStatus === "upgrade") ||
    (filter === "monitored" && episode.monitored) ||
    (filter === "imported" && episode.hasFile);

  return matchesQuery && matchesFilter;
}

function buildSeasonGroups(episodes: SeriesEpisodeInventoryItem[]) {
  const groups = new Map<number, SeriesEpisodeInventoryItem[]>();
  for (const episode of episodes) {
    const current = groups.get(episode.seasonNumber) ?? [];
    current.push(episode);
    groups.set(episode.seasonNumber, current);
  }

  return [...groups.entries()]
    .sort((left, right) => left[0] - right[0])
    .map(([seasonNumber, seasonEpisodes]) => {
      const sortedEpisodes = [...seasonEpisodes].sort(
        (left, right) => left.episodeNumber - right.episodeNumber
      );
      return {
        seasonNumber,
        episodes: sortedEpisodes,
        importedCount: sortedEpisodes.filter((item) => item.hasFile).length,
        monitoredCount: sortedEpisodes.filter((item) => item.monitored).length,
        missingCount: sortedEpisodes.filter((item) => item.wantedStatus === "missing" || !item.hasFile)
          .length
      };
    });
}

function formatEpisodeCode(episode: SeriesEpisodeInventoryItem) {
  return `S${String(episode.seasonNumber).padStart(2, "0")}E${String(
    episode.episodeNumber
  ).padStart(2, "0")}`;
}

function formatSeasonLabel(seasonNumber: number) {
  return seasonNumber === 0 ? "Specials" : `Season ${seasonNumber}`;
}

function formatWantedStatus(value: string) {
  switch (value) {
    case "missing":
      return "Missing";
    case "upgrade":
      return "Upgrade";
    case "waiting":
      return "Waiting";
    case "covered":
      return "Covered";
    default:
      return "Tracked";
  }
}

interface SearchPlanCandidate {
  releaseName: string;
  indexerId?: string | null;
  indexerName: string;
  quality: string;
  score: number;
  meetsCutoff: boolean;
  summary: string;
  downloadUrl?: string | null;
  sizeBytes?: number | null;
  seeders?: number | null;
  decisionStatus?: string;
  decisionReasons?: string[];
  riskFlags?: string[];
  qualityDelta?: number;
  customFormatScore?: number;
  seederScore?: number;
  sizeScore?: number;
  releaseGroup?: string | null;
  estimatedBitrateMbps?: number | null;
}

function ReleaseCandidatePicker({
  candidates,
  busyAction,
  onGrab
}: {
  candidates: SearchPlanCandidate[];
  busyAction: string | null;
  onGrab: (candidate: SearchPlanCandidate, force?: boolean, overrideReason?: string) => Promise<void>;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Choose a release</CardTitle>
        <CardDescription>
          Deluno scored these releases. Pick the one you want to send to the linked download client.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {candidates.map((candidate, index) => {
          const busyKey = `grab:${candidate.indexerName}:${candidate.releaseName}`;
          const forceBusyKey = `force-grab:${candidate.indexerName}:${candidate.releaseName}`;
          const isRejected = candidate.decisionStatus === "rejected";
          const shouldNudgeForce = isRejected || !candidate.meetsCutoff || index > 0;
          return (
            <div key={`${candidate.indexerName}:${candidate.releaseName}`} className="rounded-xl border border-hairline bg-surface-1 p-4">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge variant={index === 0 && !isRejected ? "success" : "default"}>{index === 0 && !isRejected ? "Best match" : `#${index + 1}`}</Badge>
                    <Badge variant={candidateGroupVariant(candidate)}>{candidateGroupLabel(candidate)}</Badge>
                    <Badge variant={isRejected ? "destructive" : candidate.meetsCutoff ? "success" : "warning"}>{candidate.decisionStatus || (candidate.meetsCutoff ? "eligible" : "below cutoff")}</Badge>
                    <Badge variant={candidate.meetsCutoff ? "success" : "warning"}>{candidate.quality}</Badge>
                    <span className="font-mono text-[11px] text-muted-foreground">score {candidate.score}</span>
                    {candidate.qualityDelta !== undefined ? (
                      <span className="font-mono text-[11px] text-muted-foreground">qΔ {candidate.qualityDelta > 0 ? "+" : ""}{candidate.qualityDelta}</span>
                    ) : null}
                    {candidate.estimatedBitrateMbps ? (
                      <span className="font-mono text-[11px] text-muted-foreground">{candidate.estimatedBitrateMbps} Mbps est.</span>
                    ) : null}
                    {candidate.seeders !== null && candidate.seeders !== undefined ? (
                      <span className="font-mono text-[11px] text-muted-foreground">{candidate.seeders} seeders</span>
                    ) : null}
                    {candidate.sizeBytes ? (
                      <span className="font-mono text-[11px] text-muted-foreground">{formatBytes(candidate.sizeBytes)}</span>
                    ) : null}
                  </div>
                  <p className="mt-2 truncate text-sm font-semibold text-foreground">{candidate.releaseName}</p>
                  <p className="mt-1 text-xs text-muted-foreground">{candidate.indexerName} · {candidate.summary}</p>
                  <DecisionReasonList candidate={candidate} />
                </div>
                <div className="flex flex-wrap justify-end gap-2">
                  <Button
                    size="sm"
                    disabled={busyAction !== null || !candidate.downloadUrl}
                    onClick={() => void onGrab(candidate, false)}
                  >
                    {busyAction === busyKey ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                    {candidate.downloadUrl ? "Grab" : "No URL"}
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    className={shouldNudgeForce ? "border-destructive/35 bg-destructive/10 text-destructive hover:bg-destructive/15" : undefined}
                    disabled={busyAction !== null || !candidate.downloadUrl}
                    title="Force sends this release even if Deluno would normally prefer or reject something else."
                    onClick={() => {
                      const reason = window.prompt("Why force this release? This reason is stored in activity and search history.", candidate.summary);
                      if (reason !== null && reason.trim()) {
                        void onGrab(candidate, true, reason.trim());
                      }
                    }}
                  >
                    {busyAction === forceBusyKey ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                    Force
                  </Button>
                </div>
              </div>
            </div>
          );
        })}
      </CardContent>
    </Card>
  );
}

function candidateGroupLabel(candidate: SearchPlanCandidate) {
  if (candidate.decisionStatus === "rejected") return "Rejected";
  if (["preferred", "eligible"].includes(candidate.decisionStatus || "") && candidate.meetsCutoff) return "Recommended";
  return "Needs review";
}

function candidateGroupVariant(candidate: SearchPlanCandidate) {
  if (candidate.decisionStatus === "rejected") return "destructive" as const;
  if (["preferred", "eligible"].includes(candidate.decisionStatus || "") && candidate.meetsCutoff) return "success" as const;
  return "warning" as const;
}

function SearchCandidateBreakdown({ detailsJson }: { detailsJson: string | null }) {
  const candidates = parseSearchCandidates(detailsJson);
  if (!candidates.length) return null;

  return (
    <div className="mt-3 rounded-xl border border-hairline bg-background/40 p-3">
      <div className="mb-2 flex items-center justify-between gap-3">
        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">Release scoring</p>
        <span className="font-mono text-[10px] text-muted-foreground">{candidates.length} candidates</span>
      </div>
      <div className="space-y-2">
        {candidates.slice(0, 3).map((candidate) => (
          <div key={`${candidate.indexerName}:${candidate.releaseName}`} className="rounded-lg border border-hairline bg-surface-1 p-2">
            <div className="flex flex-wrap items-center gap-2">
              <p className="min-w-0 flex-1 truncate text-xs font-medium text-foreground">{candidate.releaseName}</p>
              <Badge variant={candidate.meetsCutoff ? "success" : "default"}>{candidate.quality}</Badge>
              <span className="font-mono text-[10px] text-muted-foreground">{candidate.score}</span>
            </div>
            <p className="mt-1 text-[11px] text-muted-foreground">{candidate.summary}</p>
          </div>
        ))}
      </div>
    </div>
  );
}

function DecisionReasonList({ candidate }: { candidate: SearchPlanCandidate }) {
  const reasons = candidate.decisionReasons?.slice(0, 3) ?? [];
  const risks = candidate.riskFlags?.slice(0, 3) ?? [];
  if (!reasons.length && !risks.length) return null;

  return (
    <div className="mt-3 grid gap-2 md:grid-cols-2">
      {reasons.length ? (
        <div className="rounded-lg border border-hairline bg-background/35 p-2">
          <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">Why Deluno likes it</p>
          <ul className="mt-1 space-y-1 text-[11px] text-muted-foreground">
            {reasons.map((reason) => <li key={reason}>{reason}</li>)}
          </ul>
        </div>
      ) : null}
      {risks.length ? (
        <div className="rounded-lg border border-destructive/25 bg-destructive/5 p-2">
          <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-destructive">Risks</p>
          <ul className="mt-1 space-y-1 text-[11px] text-destructive/85">
            {risks.map((risk) => <li key={risk}>{risk}</li>)}
          </ul>
        </div>
      ) : null}
    </div>
  );
}

function parseSearchCandidates(detailsJson: string | null): SearchPlanCandidate[] {
  if (!detailsJson) return [];

  try {
    const parsed = JSON.parse(detailsJson) as {
      Candidates?: SearchPlanCandidate[];
      candidates?: SearchPlanCandidate[];
      searchPlan?: { Candidates?: SearchPlanCandidate[]; candidates?: SearchPlanCandidate[] };
    };
    const candidates = parsed.Candidates ?? parsed.candidates ?? parsed.searchPlan?.Candidates ?? parsed.searchPlan?.candidates ?? [];
    return candidates.map(normalizeSearchCandidate).filter((candidate) => candidate.releaseName && candidate.indexerName);
  } catch {
    return [];
  }
}

function normalizeSearchCandidate(value: SearchPlanCandidate | Record<string, unknown>): SearchPlanCandidate {
  const item = value as Record<string, unknown>;
  return {
    releaseName: String(item.releaseName ?? item.ReleaseName ?? ""),
    indexerId: (item.indexerId ?? item.IndexerId ?? null) as string | null,
    indexerName: String(item.indexerName ?? item.IndexerName ?? ""),
    quality: String(item.quality ?? item.Quality ?? ""),
    score: Number(item.score ?? item.Score ?? 0),
    meetsCutoff: Boolean(item.meetsCutoff ?? item.MeetsCutoff ?? false),
    summary: String(item.summary ?? item.Summary ?? ""),
    downloadUrl: (item.downloadUrl ?? item.DownloadUrl ?? null) as string | null,
    sizeBytes: (item.sizeBytes ?? item.SizeBytes ?? null) as number | null,
    seeders: (item.seeders ?? item.Seeders ?? null) as number | null,
    decisionStatus: String(item.decisionStatus ?? item.DecisionStatus ?? ""),
    decisionReasons: normalizeStringArray(item.decisionReasons ?? item.DecisionReasons),
    riskFlags: normalizeStringArray(item.riskFlags ?? item.RiskFlags),
    qualityDelta: Number(item.qualityDelta ?? item.QualityDelta ?? 0),
    customFormatScore: Number(item.customFormatScore ?? item.CustomFormatScore ?? 0),
    seederScore: Number(item.seederScore ?? item.SeederScore ?? 0),
    sizeScore: Number(item.sizeScore ?? item.SizeScore ?? 0),
    releaseGroup: (item.releaseGroup ?? item.ReleaseGroup ?? null) as string | null,
    estimatedBitrateMbps: (item.estimatedBitrateMbps ?? item.EstimatedBitrateMbps ?? null) as number | null
  };
}

function normalizeStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.map((item) => String(item)).filter(Boolean) : [];
}

function formatFailureKind(value: string) {
  switch (value) {
    case "quality":
      return "Quality rejected";
    case "unmatched":
      return "Needs matching";
    case "corrupt":
      return "Corrupt";
    case "downloadFailed":
      return "Download failed";
    case "importFailed":
      return "Import failed";
    default:
      return "Needs review";
  }
}

function formatSearchActionMessage(
  mediaLabel: string,
  best: string | null,
  payload: {
    summary?: string;
    dispatchStatus?: string | null;
    dispatchMessage?: string | null;
    forceOverride?: boolean;
    candidates?: unknown[];
  }
) {
  if (!best) {
    return payload.summary ?? `Manual ${mediaLabel} search completed with no accepted release.`;
  }

  const candidateCount = payload.candidates?.length ?? 0;
  const candidateLabel = `${candidateCount} candidate${candidateCount === 1 ? "" : "s"} scored`;
  const prefix = payload.forceOverride ? `Force grabbed ${mediaLabel}` : `Manual ${mediaLabel} search`;

  switch (payload.dispatchStatus) {
    case "sent":
      return `${prefix} sent ${best} to the download client. ${candidateLabel}.`;
    case "planned":
      return `${prefix} matched ${best}, but no downloadable URL was available yet. ${candidateLabel}.`;
    case "failed":
      return `${prefix} matched ${best}, but the download client rejected the grab${payload.dispatchMessage ? `: ${payload.dispatchMessage}` : "."}`;
    default:
      return `${prefix} matched ${best}. ${candidateLabel}.`;
  }
}

function formatDispatchSummary(payload: {
  dispatchStatus?: string | null;
  dispatchMessage?: string | null;
  sentCount?: number;
  plannedCount?: number;
  failedCount?: number;
}) {
  if (payload.sentCount !== undefined || payload.plannedCount !== undefined || payload.failedCount !== undefined) {
    const parts = [
      payload.sentCount ? `${payload.sentCount} sent` : null,
      payload.plannedCount ? `${payload.plannedCount} planned` : null,
      payload.failedCount ? `${payload.failedCount} failed` : null
    ].filter(Boolean);

    return parts.length ? `Dispatch: ${parts.join(", ")}.` : "No releases were dispatched.";
  }

  switch (payload.dispatchStatus) {
    case "sent":
      return "Release sent to the download client.";
    case "planned":
      return "Release matched, but no downloadable URL was available yet.";
    case "failed":
      return `Download client rejected the grab${payload.dispatchMessage ? `: ${payload.dispatchMessage}` : "."}`;
    default:
      return "Dispatch recorded.";
  }
}

function getDispatchBadgeVariant(status: string) {
  switch (status) {
    case "sent":
      return "success";
    case "failed":
      return "destructive";
    case "planned":
      return "warning";
    default:
      return "info";
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

function formatSearchHistoryContext(item: SeriesSearchHistoryItem) {
  const parts: string[] = [];

  if (item.seasonNumber !== null && item.episodeNumber !== null) {
    parts.push(
      `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`
    );
  }

  if (item.indexerName) {
    parts.push(item.indexerName);
  }

  return parts.length ? parts.join(" · ") : "No source yet";
}

function formatTriggerKind(value: string) {
  switch (value) {
    case "manual-episode":
      return "Episode";
    case "manual-season":
      return "Season";
    case "manual":
      return "Manual";
    default:
      return "Scheduled";
  }
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function isValidHttpUrl(value: string) {
  try {
    const parsed = new URL(value);
    return parsed.protocol === "http:" || parsed.protocol === "https:";
  } catch {
    return false;
  }
}
