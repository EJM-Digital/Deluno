import { useMemo, useState } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import {
  Activity,
  ArrowLeft,
  CheckSquare2,
  LoaderCircle,
  RefreshCw,
  Search,
  ShieldCheck,
  Square,
  Tv2
} from "lucide-react";
import {
  fetchJson,
  type ActivityEventItem,
  type DecisionExplanationItem,
  type DownloadDispatchItem,
  type LibraryItem,
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
import { KpiCard } from "../components/app/kpi-card";
import { DecisionExplanationList } from "../components/app/decision-explanation-list";
import { RatingStrip } from "../components/app/rating-strip";
import { EmptyState } from "../components/shell/empty-state";
import { RouteSkeleton } from "../components/shell/skeleton";

interface ShowDetailLoaderData {
  activity: ActivityEventItem[];
  decisions: DecisionExplanationItem[];
  dispatches: DownloadDispatchItem[];
  importRecovery: SeriesImportRecoverySummary;
  inventory: SeriesInventoryDetail;
  libraries: LibraryItem[];
  searchHistory: SeriesSearchHistoryItem[];
  series: SeriesListItem;
  wanted: SeriesWantedSummary;
}

type EpisodeFilter = "all" | "missing" | "upgrade" | "monitored" | "imported";

export async function showDetailLoader({
  params
}: {
  params: { id?: string };
}): Promise<ShowDetailLoaderData> {
  const id = params.id!;
  const [series, wanted, searchHistory, dispatches, importRecovery, inventory, activity, decisions, libraries] =
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
      fetchJson<LibraryItem[]>("/api/libraries")
    ]);

  return { activity, decisions, dispatches, importRecovery, inventory, libraries, searchHistory, series, wanted };
}

export function ShowDetailPage() {
  const loaderData = useLoaderData() as ShowDetailLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { activity, decisions, dispatches, importRecovery, inventory, libraries, searchHistory, series, wanted } = loaderData;
  const revalidator = useRevalidator();
  const [selectedEpisodeIds, setSelectedEpisodeIds] = useState<string[]>([]);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [metadataQuery, setMetadataQuery] = useState(series.title);
  const [metadataMatches, setMetadataMatches] = useState<MetadataSearchResult[]>([]);
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);
  const [episodeFilter, setEpisodeFilter] = useState<EpisodeFilter>("all");
  const [query, setQuery] = useState("");

  const wantedItem = wanted.recentItems.find((item) => item.seriesId === series.id) ?? null;
  const library = wantedItem ? libraries.find((item) => item.id === wantedItem.libraryId) ?? null : null;
  const seriesSearches = searchHistory.filter((item) => item.seriesId === series.id);
  const seriesDispatches = dispatches.filter((item) => item.entityId === series.id);
  const importCases = importRecovery.recentCases.filter(
    (item) => item.title.trim().toLowerCase() === series.title.trim().toLowerCase()
  );

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

      setActionMessage(monitored ? "Series monitored." : "Series unmonitored.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Series update failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSearchNow() {
    setBusyAction("search");
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/series/${series.id}/search?mode=preview`, { method: "POST" });
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
      setReleaseCandidates(payload.candidates ?? []);
      setActionMessage(formatSearchActionMessage("series", best, payload));
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
          indexerName: candidate.indexerName,
          downloadUrl: candidate.downloadUrl,
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
      <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <Link
            to="/tv"
            className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
            Back to TV
          </Link>
          <p className="mt-3 text-sm text-muted-foreground">Series workspace</p>
          <h1 className="font-display text-3xl font-semibold text-foreground sm:text-4xl">
            {series.title}
            {series.startYear ? (
              <span className="ml-3 text-muted-foreground">{series.startYear}</span>
            ) : null}
          </h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Episode inventory, wanted state, search trail, and import pressure for this series.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Badge variant={series.monitored ? "success" : "default"}>
            {series.monitored ? "Monitored" : "Passive"}
          </Badge>
          {wantedItem ? (
            <Badge
              variant={
                wantedItem.wantedStatus === "missing"
                  ? "destructive"
                  : wantedItem.wantedStatus === "upgrade"
                    ? "warning"
                    : "info"
              }
            >
              {formatWantedStatus(wantedItem.wantedStatus)}
            </Badge>
          ) : null}
          {importCases.length ? (
            <Badge variant="warning">
              {importCases.length} import issue{importCases.length === 1 ? "" : "s"}
            </Badge>
          ) : null}
        </div>
      </div>

      <div className="flex flex-wrap gap-2">
        <Button onClick={() => void handleSearchNow()} disabled={busyAction !== null}>
          {busyAction === "search" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <Search className="h-4 w-4" />
          )}
          Search now
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
        >
          {busyAction === "series-monitor" || busyAction === "series-unmonitor" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <ShieldCheck className="h-4 w-4" />
          )}
          {series.monitored ? "Unmonitor series" : "Monitor series"}
        </Button>
        <Button
          variant="outline"
          onClick={() => void handleRefreshMetadata()}
          disabled={busyAction !== null}
        >
          {busyAction === "metadata" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <RefreshCw className="h-4 w-4" />
          )}
          Refresh metadata
        </Button>
        <Button
          variant="ghost"
          onClick={() => revalidator.revalidate()}
          disabled={revalidator.state !== "idle"}
        >
          <RefreshCw className="h-4 w-4" />
          Refresh
        </Button>
      </div>

      {actionMessage ? (
        <div className="rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-sm text-muted-foreground">
          {actionMessage}
        </div>
      ) : null}

      {releaseCandidates.length ? (
        <ReleaseCandidatePicker
          candidates={releaseCandidates}
          busyAction={busyAction}
          onGrab={handleGrabCandidate}
        />
      ) : null}

      <div className="fluid-kpi-grid">
        <KpiCard
          label="Seasons"
          value={String(inventory.seasonCount)}
          icon={Tv2}
          meta="Season containers currently tracked in Deluno."
          sparkline={[2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4]}
        />
        <KpiCard
          label="Episodes"
          value={String(inventory.episodeCount)}
          icon={CheckSquare2}
          meta="Episode inventory rows tied to this title."
          sparkline={[6, 8, 10, 11, 12, 14, 16, 18, 19, 20, 21, 22, 22, 23, 24]}
        />
        <KpiCard
          label="Missing"
          value={String(missingCount)}
          icon={Search}
          meta="Episodes still missing files or coverage."
          sparkline={[6, 5, 7, 6, 8, 7, 6, 5, 6, 4, 5, 4, 3, 4, 3]}
        />
        <KpiCard
          label="Upgrades"
          value={String(upgradeCount)}
          icon={Activity}
          meta="Episodes already imported but still below target quality."
          sparkline={[2, 3, 2, 3, 3, 4, 4, 5, 4, 4, 5, 5, 6, 5, 5]}
        />
      </div>

      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.28fr)_minmax(380px,0.82fr)] 2xl:grid-cols-[minmax(0,1.5fr)_minmax(440px,0.65fr)]">
        <div className="space-y-[var(--page-gap)]">
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
                                    episode.wantedStatus === "missing"
                                      ? "destructive"
                                      : episode.wantedStatus === "upgrade"
                                        ? "warning"
                                        : "info"
                                  }
                                >
                                  {formatWantedStatus(episode.wantedStatus)}
                                </Badge>
                                <Badge variant={episode.monitored ? "success" : "default"}>
                                  {episode.monitored ? "Monitored" : "Passive"}
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
          <RoutingCard
            library={library}
            currentQuality={wantedItem?.currentQuality ?? null}
            targetQuality={wantedItem?.targetQuality ?? "WEB 1080p"}
            workflow={library?.importWorkflow ?? "standard"}
          />

          <Card>
            <CardHeader>
              <CardTitle>Metadata</CardTitle>
              <CardDescription>
                Provider identity, artwork context, ratings, and overview stored for this series.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
              {series.overview ? (
                <p className="text-sm leading-relaxed text-muted-foreground">{series.overview}</p>
              ) : (
                <p className="text-sm text-muted-foreground">
                  No provider overview has been stored yet. Refresh metadata to enrich this series.
                </p>
              )}
              <div className="grid gap-3 sm:grid-cols-2">
                <MetadataStat label="Provider" value={series.metadataProvider ?? "Not linked"} />
                <MetadataStat label="Provider ID" value={series.metadataProviderId ?? "None"} />
                <MetadataStat label="Genres" value={series.genres ?? "None"} />
              </div>
              <RatingStrip ratings={series.ratings} fallbackRating={series.rating} />
              <MetadataCorrectionPanel
                busyAction={busyAction}
                mediaLabel="series"
                query={metadataQuery}
                matches={metadataMatches}
                onQueryChange={setMetadataQuery}
                onSearch={handleMetadataSearch}
                onLink={handleMetadataLink}
              />
              {series.externalUrl ? (
                <Button asChild variant="outline" size="sm">
                  <a href={series.externalUrl} target="_blank" rel="noreferrer">
                    Open provider page
                  </a>
                </Button>
              ) : null}
            </CardContent>
          </Card>

          <Card>
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
                </div>
              ) : null}
            </CardContent>
          </Card>

          <Card>
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
        </div>
      </div>
    </div>
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
  onLink,
  onQueryChange,
  onSearch,
  query
}: {
  busyAction: string | null;
  matches: MetadataSearchResult[];
  mediaLabel: string;
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
