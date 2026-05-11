import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { ArrowLeft, Clock, LoaderCircle, Search } from "lucide-react";
import {
  fetchJson,
  type SeriesListItem,
  type SeriesEpisodeInventoryItem,
  type SeriesInventoryDetail,
  type LibraryItem
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { Button } from "../components/ui/button";
import { Badge } from "../components/ui/badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { EmptyState } from "../components/shell/empty-state";
import { RouteSkeleton } from "../components/shell/skeleton";
import { useState, useMemo } from "react";

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}

interface EpisodeSearchLoaderData {
  libraries: LibraryItem[];
  series: SeriesListItem[];
  inventories: Record<string, SeriesEpisodeInventoryItem[]>;
}

export async function episodeSearchLoader(): Promise<EpisodeSearchLoaderData> {
  const [libraries, series] = await Promise.all([
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<SeriesListItem[]>("/api/series")
  ]);

  const inventories: Record<string, SeriesEpisodeInventoryItem[]> = {};
  for (const s of series) {
    const inventory = await fetchJson<SeriesInventoryDetail>(`/api/series/${s.id}/inventory`);
    inventories[s.id] = inventory.episodes;
  }

  return { libraries, series, inventories };
}

interface EpisodeSearchCandidate {
  episodeId: string;
  seriesId: string;
  seriesTitle: string;
  seasonNumber: number;
  episodeNumber: number;
  title: string | null;
  monitored: boolean;
  wantedStatus: string;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
}

export function EpisodeSearchPage() {
  const data = useLoaderData() as EpisodeSearchLoaderData | undefined;
  if (!data) return <RouteSkeleton />;

  const { libraries, series, inventories } = data;
  const revalidator = useRevalidator();
  const [busyIds, setBusyIds] = useState<Set<string>>(new Set());
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());

  const allCandidates = useMemo(() => {
    const candidates: EpisodeSearchCandidate[] = [];
    for (const s of series) {
      const episodes = inventories[s.id] || [];
      for (const ep of episodes) {
        if (ep.monitored && (ep.wantedStatus === "missing" || ep.wantedStatus === "upgrade")) {
          candidates.push({
            episodeId: ep.episodeId,
            seriesId: s.id,
            seriesTitle: s.title,
            seasonNumber: ep.seasonNumber,
            episodeNumber: ep.episodeNumber,
            title: ep.title,
            monitored: ep.monitored,
            wantedStatus: ep.wantedStatus,
            lastSearchUtc: ep.lastSearchUtc,
            nextEligibleSearchUtc: ep.nextEligibleSearchUtc
          });
        }
      }
    }
    return candidates.sort((a, b) =>
      (a.nextEligibleSearchUtc || a.lastSearchUtc || "").localeCompare(
        b.nextEligibleSearchUtc || b.lastSearchUtc || ""
      )
    );
  }, [series, inventories]);

  const eligible = allCandidates.filter((c) => !c.nextEligibleSearchUtc || new Date(c.nextEligibleSearchUtc) <= new Date());
  const waiting = allCandidates.filter((c) => c.nextEligibleSearchUtc && new Date(c.nextEligibleSearchUtc) > new Date());

  async function handleEpisodeSearch(episodeId: string, seriesId: string) {
    setBusyIds((prev) => new Set(prev).add(episodeId));
    try {
      const response = await authedFetch(`/api/series/${seriesId}/episodes/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds: [episodeId] })
      });

      if (!response.ok) {
        throw new Error("episode-search-failed");
      }

      revalidator.revalidate();
    } catch {
      // Error handling would go here
    } finally {
      setBusyIds((prev) => {
        const next = new Set(prev);
        next.delete(episodeId);
        return next;
      });
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
          <p className="mt-3 text-sm text-muted-foreground">Episode-level search eligibility</p>
          <h1 className="font-display text-3xl font-semibold text-foreground sm:text-4xl">Episode search</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            View and search individual episodes across all series. Eligible episodes are ready for search now.
          </p>
        </div>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <Card>
          <CardContent className="pt-6">
            <p className="text-sm text-muted-foreground">Eligible now</p>
            <p className="text-3xl font-semibold text-foreground mt-2">{eligible.length}</p>
            <p className="text-xs text-muted-foreground mt-1">Ready for search</p>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="pt-6">
            <p className="text-sm text-muted-foreground">Waiting for retry</p>
            <p className="text-3xl font-semibold text-foreground mt-2">{waiting.length}</p>
            <p className="text-xs text-muted-foreground mt-1">Search throttled until eligible</p>
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Eligible episodes</CardTitle>
          <CardDescription>
            Monitored episodes with missing or upgrade-able content, ready for search.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {eligible.length ? (
            <div className="space-y-2 max-h-96 overflow-y-auto">
              {eligible.map((episode) => (
                <div
                  key={episode.episodeId}
                  className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-surface-1 p-3"
                >
                  <div className="flex-1 min-w-0">
                    <p className="text-sm font-medium text-foreground">
                      <Link
                        to={`/series/${episode.seriesId}`}
                        className="hover:text-primary hover:underline"
                      >
                        {episode.seriesTitle}
                      </Link>
                    </p>
                    <p className="text-xs text-muted-foreground mt-1">
                      S{String(episode.seasonNumber).padStart(2, "0")}E{String(episode.episodeNumber).padStart(2, "0")}
                      {episode.title && ` - ${episode.title}`}
                    </p>
                    {episode.lastSearchUtc && (
                      <p className="text-xs text-muted-foreground">
                        Last searched {formatDateTime(episode.lastSearchUtc)}
                      </p>
                    )}
                  </div>
                  <div className="flex items-center gap-2">
                    <Badge
                      variant={
                        episode.wantedStatus === "missing"
                          ? "destructive"
                          : episode.wantedStatus === "upgrade"
                            ? "warning"
                            : "info"
                      }
                      className="text-xs"
                    >
                      {episode.wantedStatus}
                    </Badge>
                    <Button
                      size="sm"
                      variant="ghost"
                      onClick={() => void handleEpisodeSearch(episode.episodeId, episode.seriesId)}
                      disabled={busyIds.has(episode.episodeId)}
                    >
                      {busyIds.has(episode.episodeId) ? (
                        <LoaderCircle className="h-4 w-4 animate-spin" />
                      ) : (
                        <Search className="h-4 w-4" />
                      )}
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <EmptyState
              size="sm"
              variant="search"
              title="No eligible episodes"
              description="All monitored episodes are either satisfied or waiting for retry eligibility."
            />
          )}
        </CardContent>
      </Card>

      {waiting.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Waiting for retry</CardTitle>
            <CardDescription>
              Monitored episodes that were recently searched. Will become eligible after the retry window.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div className="space-y-2 max-h-96 overflow-y-auto">
              {waiting.map((episode) => (
                <div
                  key={episode.episodeId}
                  className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-surface-1 p-3"
                >
                  <div className="flex-1 min-w-0">
                    <p className="text-sm font-medium text-foreground">
                      <Link
                        to={`/series/${episode.seriesId}`}
                        className="hover:text-primary hover:underline"
                      >
                        {episode.seriesTitle}
                      </Link>
                    </p>
                    <p className="text-xs text-muted-foreground mt-1">
                      S{String(episode.seasonNumber).padStart(2, "0")}E{String(episode.episodeNumber).padStart(2, "0")}
                      {episode.title && ` - ${episode.title}`}
                    </p>
                    {episode.nextEligibleSearchUtc && (
                      <p className="text-xs text-muted-foreground flex items-center gap-1 mt-1">
                        <Clock className="h-3 w-3" />
                        Eligible {formatDateTime(episode.nextEligibleSearchUtc)}
                      </p>
                    )}
                  </div>
                  <Badge variant="default" className="text-xs">
                    waiting
                  </Badge>
                </div>
              ))}
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  );
}
