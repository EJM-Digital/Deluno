import { useEffect, useMemo, useState } from "react";
import {
  Activity,
  Clock3,
  Download,
  Eye,
  ExternalLink,
  LoaderCircle,
  RefreshCw,
  Search,
  Star
} from "lucide-react";
import { Link } from "react-router-dom";
import type { MediaItem } from "../../lib/media-types";
import {
  fetchJson,
  type ActivityEventItem,
  type MovieListItem,
  type MovieSearchHistoryItem,
  type SeriesEpisodeInventoryItem,
  type SeriesInventoryDetail,
  type SeriesListItem,
  type SeriesSearchHistoryItem
} from "../../lib/api";
import { formatBytesFromGb } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { Button } from "../ui/button";
import { Card } from "../ui/card";
import { Sheet, SheetContent } from "../ui/sheet";

interface DetailState {
  overview: string;
  activity: ActivityEventItem[];
  history: HistoryEntry[];
  inventory: SeriesInventoryDetail | null;
}

interface HistoryEntry {
  id: string;
  label: string;
  detail: string;
  time: string;
  sortUtc: string;
  source: "activity" | "search";
}

const initialDetailState: DetailState = {
  overview: "",
  activity: [],
  history: [],
  inventory: null
};

export function MediaDetailSheet({
  item,
  onItemUpdated,
  onOpenChange
}: {
  item: MediaItem | null;
  onItemUpdated?: (item: MediaItem) => void;
  onOpenChange: (open: boolean) => void;
}) {
  const [detailState, setDetailState] = useState<DetailState>(initialDetailState);
  const [liveMonitored, setLiveMonitored] = useState(item?.monitored ?? false);
  const [isLoading, setIsLoading] = useState(false);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [isSavingMonitor, setIsSavingMonitor] = useState(false);
  const [isQueueingSearch, setIsQueueingSearch] = useState(false);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  useEffect(() => {
    setLiveMonitored(item?.monitored ?? false);
    setActionNotice(null);
  }, [item]);

  useEffect(() => {
    if (!item) {
      setDetailState(initialDetailState);
      setIsLoading(false);
      return;
    }

    let cancelled = false;
    setIsLoading(true);

    void loadDetails(item)
      .then((next) => {
        if (!cancelled) {
          setDetailState(next);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setDetailState({
            overview: item.overview,
            activity: [],
            history: [],
            inventory: null
          });
        }
      })
      .finally(() => {
        if (!cancelled) {
          setIsLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [item]);

  const statItems = useMemo(() => {
    if (!item) {
      return [];
    }

    const base = [
      { label: "Size", value: formatBytesFromGb(item.sizeGb) },
      { label: "Added", value: item.added },
      { label: "Quality", value: item.currentQuality ?? item.quality ?? "Unknown" },
      { label: "Target", value: item.targetQuality ?? item.quality ?? "Unknown" }
    ];

    if (item.type === "show" && detailState.inventory) {
      return [
        { label: "Seasons", value: String(detailState.inventory.seasonCount) },
        { label: "Episodes", value: String(detailState.inventory.episodeCount) },
        { label: "Imported", value: String(detailState.inventory.importedEpisodeCount) },
        { label: "Added", value: item.added }
      ];
    }

    return base;
  }, [detailState.inventory, item]);

  const inventoryPreview = useMemo(() => detailState.inventory?.episodes.slice(0, 6) ?? [], [detailState.inventory]);
  const workspaceHref = item ? (item.type === "movie" ? `/movies/${item.id}` : `/tv/${item.id}`) : null;
  const workspaceLabel = item?.type === "movie" ? "Open movie workspace" : "Open series workspace";

  async function handleRefresh() {
    if (!item) return;
    setIsRefreshing(true);
    setActionNotice(null);

    try {
      const next = await loadDetails(item);
      setDetailState(next);
    } catch {
      setActionNotice("Detail refresh failed.");
    } finally {
      setIsRefreshing(false);
    }
  }

  async function handleToggleMonitoring() {
    if (!item) return;

    const nextMonitored = !liveMonitored;
    setIsSavingMonitor(true);
    setActionNotice(null);

    try {
      const response = await fetch(item.type === "movie" ? "/api/movies/monitoring" : "/api/series/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          item.type === "movie"
            ? { movieIds: [item.id], monitored: nextMonitored }
            : { seriesIds: [item.id], monitored: nextMonitored }
        )
      });

      if (!response.ok) {
        throw new Error("monitoring-update-failed");
      }

      const updatedItem = { ...item, monitored: nextMonitored };
      setLiveMonitored(nextMonitored);
      onItemUpdated?.(updatedItem);
      setActionNotice(nextMonitored ? "Monitoring enabled." : "Monitoring disabled.");
    } catch {
      setActionNotice("Monitoring update failed.");
    } finally {
      setIsSavingMonitor(false);
    }
  }

  async function handleQueueSearch() {
    if (!item) return;

    setIsQueueingSearch(true);
    setActionNotice(null);

    try {
      const response = await fetch(
        item.type === "movie" ? `/api/movies/${item.id}/search` : `/api/series/${item.id}/search`,
        { method: "POST" }
      );
      if (!response.ok) {
        throw new Error("library-search-failed");
      }
      setActionNotice("Manual title search completed.");
    } catch {
      setActionNotice("Manual title search failed.");
    } finally {
      setIsQueueingSearch(false);
    }
  }

  return (
    <Sheet open={!!item} onOpenChange={onOpenChange}>
      <SheetContent className="overflow-y-auto p-0 sm:max-w-xl">
        {item ? (
          <div className="min-h-full bg-card">
            <div className="relative h-56 overflow-hidden border-b border-hairline">
              {item.backdrop ? (
                <img src={item.backdrop} alt={item.title} className="h-full w-full object-cover" />
              ) : (
                <div className="flex h-full w-full items-center justify-center bg-gradient-to-br from-surface-2 to-surface-3 text-muted-foreground">
                  <span className="font-display text-4xl font-semibold tracking-tight">{item.title.slice(0, 2).toUpperCase()}</span>
                </div>
              )}
              <div className="absolute inset-0 bg-gradient-to-t from-card via-card/45 to-transparent" />
              <div className="absolute inset-x-5 bottom-4 flex items-center justify-between gap-3">
                <div className="flex items-center gap-2">
                  <Badge
                    variant={
                      item.status === "missing"
                        ? "destructive"
                        : item.status === "downloading"
                          ? "info"
                          : "success"
                    }
                  >
                    {item.status}
                  </Badge>
                  <Badge>{item.currentQuality ?? item.quality ?? "Unknown quality"}</Badge>
                </div>
                <Button variant="outline" size="sm" onClick={handleRefresh} disabled={isRefreshing}>
                  {isRefreshing ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                  Refresh
                </Button>
              </div>
            </div>

            <div className="px-[var(--tile-pad)] pb-[calc(var(--tile-pad)*1.2)]">
              <div className="-mt-20 flex gap-4">
                {item.poster ? (
                  <img src={item.poster} alt={item.title} className="h-44 w-28 rounded-xl border border-hairline object-cover shadow-md" />
                ) : (
                  <div className="flex h-44 w-28 items-center justify-center rounded-xl border border-hairline bg-surface-2 text-center text-sm font-semibold text-muted-foreground shadow-md">
                    {item.title.slice(0, 2).toUpperCase()}
                  </div>
                )}
                <div className="pt-20">
                  <p className="text-xs uppercase tracking-[0.18em] text-muted-foreground">
                    {item.type === "movie" ? "Movie" : "TV Show"} · <span className="tabular">{item.year}</span>
                  </p>
                  <h2 className="font-display text-2xl font-semibold text-foreground">{item.title}</h2>
                  <div className="mt-2 flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
                    {item.rating !== null ? (
                      <span className="inline-flex items-center gap-1">
                        <Star className="h-4 w-4 fill-warning text-warning" />
                        <span className="tabular">{item.rating.toFixed(1)}</span>
                      </span>
                    ) : null}
                    <span>{item.genres.join(" · ")}</span>
                  </div>
                </div>
              </div>

              <div className="mt-5 flex flex-wrap items-center gap-2">
                <Badge variant="info">{liveMonitored ? "Monitored" : "Passive"}</Badge>
                {item.wantedReason ? <Badge variant="warning">{item.wantedReason}</Badge> : null}
                {item.nextEligibleSearchUtc ? <Badge variant="default">Next retry {formatWhen(item.nextEligibleSearchUtc)}</Badge> : null}
              </div>

              <RatingSummary item={item} />

              <div className="mt-5 flex flex-wrap gap-2">
                <Button onClick={handleQueueSearch} disabled={!item.libraryId || isQueueingSearch}>
                  {isQueueingSearch ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                  Search now
                </Button>
                <Button variant="outline" onClick={handleRefresh} disabled={isRefreshing}>
                  <Download className="h-4 w-4" />
                  Refresh detail
                </Button>
                <Button variant="ghost" onClick={handleToggleMonitoring} disabled={isSavingMonitor}>
                  {isSavingMonitor ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Eye className="h-4 w-4" />}
                  {liveMonitored ? "Unmonitor" : "Monitor"}
                </Button>
                {workspaceHref ? (
                  <Button asChild variant="ghost">
                    <Link to={workspaceHref}>
                      <ExternalLink className="h-4 w-4" />
                      {workspaceLabel}
                    </Link>
                  </Button>
                ) : null}
              </div>

              {actionNotice ? <p className="mt-3 text-sm text-muted-foreground">{actionNotice}</p> : null}

              <p className="mt-5 text-balance text-sm leading-6 text-muted-foreground">{detailState.overview || item.overview}</p>

              <div className="mt-6 grid grid-cols-2 gap-3 md:grid-cols-4">
                {statItems.map((entry) => (
                  <Stat key={entry.label} label={entry.label} value={entry.value} />
                ))}
              </div>

              {item.type === "show" && detailState.inventory ? (
                <Card className="mt-6">
                  <div className="border-b border-hairline px-4 py-3">
                    <p className="font-display text-base font-semibold text-foreground">Episode inventory</p>
                    <p className="text-xs text-muted-foreground">Imported and wanted-state coverage for this series</p>
                  </div>
                  <div className="space-y-3 px-4 py-4">
                    {inventoryPreview.map((episode) => (
                      <EpisodeRow key={episode.episodeId} episode={episode} />
                    ))}
                    {!inventoryPreview.length ? <p className="text-sm text-muted-foreground">No episode inventory available yet.</p> : null}
                  </div>
                </Card>
              ) : null}

              <Card className="mt-6">
                <div className="border-b border-hairline px-4 py-3">
                  <p className="font-display text-base font-semibold text-foreground">History</p>
                  <p className="text-xs text-muted-foreground">Search and activity events scoped to this title</p>
                </div>
                <div className="space-y-3 px-4 py-4">
                  {isLoading ? (
                    <div className="flex items-center gap-2 text-sm text-muted-foreground">
                      <LoaderCircle className="h-4 w-4 animate-spin" />
                      Loading title activity
                    </div>
                  ) : detailState.history.length ? (
                    detailState.history.map((entry) => (
                      <div key={entry.id} className="flex items-start gap-3">
                        <span className="mt-2 h-2 w-2 rounded-full bg-primary shadow-[0_0_8px_hsl(var(--primary)/0.5)]" />
                        <div className="min-w-0">
                          <div className="flex items-center gap-2">
                            <p className="text-sm text-foreground">{entry.label}</p>
                            <Badge variant={entry.source === "search" ? "info" : "default"}>{entry.source}</Badge>
                          </div>
                          <p className="mt-1 text-sm text-muted-foreground">{entry.detail}</p>
                          <p className="mt-1 inline-flex items-center gap-1 text-xs text-muted-foreground">
                            <Clock3 className="h-3.5 w-3.5" />
                            {entry.time}
                          </p>
                        </div>
                      </div>
                    ))
                  ) : (
                    <div className="flex items-center gap-2 text-sm text-muted-foreground">
                      <Activity className="h-4 w-4" />
                      No title activity recorded yet.
                    </div>
                  )}
                </div>
              </Card>
            </div>
          </div>
        ) : null}
      </SheetContent>
    </Sheet>
  );
}

async function loadDetails(item: MediaItem): Promise<DetailState> {
  if (item.type === "movie") {
    const [detail, searchHistory, activity] = await Promise.all([
      fetchJson<MovieListItem>(`/api/movies/${item.id}`),
      fetchJson<MovieSearchHistoryItem[]>("/api/movies/search-history"),
      fetchJson<ActivityEventItem[]>(`/api/activity?relatedEntityType=movie&relatedEntityId=${item.id}&take=12`)
    ]);

    return {
      overview: buildMovieOverview(item, detail),
      activity,
      history: buildHistory(searchHistory.filter((entry) => entry.movieId === item.id), activity),
      inventory: null
    };
  }

  const [detail, inventory, searchHistory, activity] = await Promise.all([
    fetchJson<SeriesListItem>(`/api/series/${item.id}`),
    fetchJson<SeriesInventoryDetail>(`/api/series/${item.id}/inventory`),
    fetchJson<SeriesSearchHistoryItem[]>("/api/series/search-history"),
    fetchJson<ActivityEventItem[]>(`/api/activity?relatedEntityType=series&relatedEntityId=${item.id}&take=12`)
  ]);

  return {
    overview: buildSeriesOverview(item, detail, inventory),
    activity,
    history: buildHistory(searchHistory.filter((entry) => entry.seriesId === item.id), activity),
    inventory
  };
}

function buildMovieOverview(item: MediaItem, detail: MovieListItem) {
  const parts = [
    `${detail.title} is currently ${item.monitored ? "actively monitored" : "tracked passively"} in Deluno.`,
    item.wantedReason ?? "Deluno is maintaining this title with live acquisition state."
  ];
  if (item.lastSearchUtc) parts.push(`Last search ${formatWhen(item.lastSearchUtc)}.`);
  return parts.join(" ");
}

function buildSeriesOverview(item: MediaItem, detail: SeriesListItem, inventory: SeriesInventoryDetail) {
  const importedShare =
    inventory.episodeCount > 0
      ? `${inventory.importedEpisodeCount} of ${inventory.episodeCount} tracked episodes are imported.`
      : "Episode inventory is still being built.";

  const parts = [
    `${detail.title} is ${item.monitored ? "actively monitored" : "tracked passively"} in Deluno.`,
    importedShare,
    item.wantedReason ?? "Episode wanted state and routing are available for this series."
  ];
  if (item.lastSearchUtc) parts.push(`Last search ${formatWhen(item.lastSearchUtc)}.`);
  return parts.join(" ");
}

function buildHistory(
  searchHistory: Array<MovieSearchHistoryItem | SeriesSearchHistoryItem>,
  activity: ActivityEventItem[]
): HistoryEntry[] {
  const searches: HistoryEntry[] = searchHistory.map((entry) => ({
    id: entry.id,
    label: formatHistoryLabel(entry.triggerKind),
    detail: entry.outcome + (entry.releaseName ? ` · ${entry.releaseName}` : ""),
    time: formatWhen(entry.createdUtc),
    sortUtc: entry.createdUtc,
    source: "search"
  }));

  const events: HistoryEntry[] = activity.map((entry) => ({
    id: entry.id,
    label: entry.category,
    detail: entry.message,
    time: formatWhen(entry.createdUtc),
    sortUtc: entry.createdUtc,
    source: "activity"
  }));

  return [...searches, ...events]
    .sort((left, right) => new Date(right.sortUtc).getTime() - new Date(left.sortUtc).getTime())
    .slice(0, 8);
}

function formatHistoryLabel(value: string) {
  switch (value) {
    case "manual":
      return "Manual search";
    case "library":
      return "Library automation";
    default:
      return value.replace(/(^\w|\.\w)/g, (match) => match.replace(".", " ").toUpperCase());
  }
}

function formatWhen(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}

function EpisodeRow({ episode }: { episode: SeriesEpisodeInventoryItem }) {
  return (
    <div className="flex items-center justify-between gap-3 rounded-xl border border-hairline bg-surface-1 px-3 py-3">
      <div className="min-w-0">
        <p className="text-sm text-foreground">
          <span className="tabular">
            S{String(episode.seasonNumber).padStart(2, "0")}E{String(episode.episodeNumber).padStart(2, "0")}
          </span>{" "}
          {episode.title ?? "Episode"}
        </p>
        <p className="mt-1 text-xs text-muted-foreground">
          {episode.wantedReason || (episode.hasFile ? "Imported into Deluno." : "Awaiting coverage.")}
        </p>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        <Badge variant={episode.wantedStatus === "missing" ? "destructive" : episode.wantedStatus === "upgrade" ? "warning" : "success"}>
          {episode.wantedStatus}
        </Badge>
        <Badge variant="info">{episode.monitored ? "Monitored" : "Passive"}</Badge>
      </div>
    </div>
  );
}

function RatingSummary({ item }: { item: MediaItem }) {
  const ratings = normalizeRatings(item);
  if (ratings.length === 0) {
    return null;
  }

  return (
    <div className="mt-5 grid grid-cols-2 gap-2 sm:grid-cols-4">
      {ratings.map((rating) => (
        <a
          key={rating.source}
          href={rating.url ?? undefined}
          target={rating.url ? "_blank" : undefined}
          rel={rating.url ? "noreferrer" : undefined}
          className="rounded-xl border border-hairline bg-surface-1 p-3 no-underline transition hover:border-primary/30 hover:bg-surface-2"
        >
          <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.16em] text-muted-foreground">
            {rating.label}
          </p>
          <p className="mt-1 tabular font-display text-[length:var(--type-title-sm)] font-semibold tracking-display text-foreground">
            {formatRatingValue(rating)}
          </p>
        </a>
      ))}
    </div>
  );
}

function normalizeRatings(item: MediaItem) {
  const ratings = (item.ratings ?? [])
    .filter((rating) => rating.score !== null || rating.voteCount !== null)
    .filter((rating, index, source) => source.findIndex((entry) => entry.source === rating.source) === index);

  if (ratings.length) {
    return ratings.slice(0, 4);
  }

  if (item.rating === null) {
    return [];
  }

  return [
    {
      source: "tmdb",
      label: "TMDb",
      score: item.rating,
      maxScore: 10,
      voteCount: null,
      url: null,
      kind: "community"
    }
  ];
}

function formatRatingValue(rating: NonNullable<MediaItem["ratings"]>[number]) {
  if (rating.score === null || rating.score === undefined) {
    return "Unknown";
  }

  if (rating.maxScore === 100 || rating.source === "rotten_tomatoes" || rating.source === "metacritic") {
    return `${Math.round(rating.score)}%`;
  }

  if (rating.maxScore) {
    return `${rating.score.toFixed(1)}/${rating.maxScore}`;
  }

  return rating.score.toFixed(1);
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-hairline bg-surface-1 p-3">
      <p className="text-[10px] uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <p className="mt-2 tabular text-sm font-medium text-foreground">{value}</p>
    </div>
  );
}
