import { useState } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import {
  Activity,
  ArrowLeft,
  Clapperboard,
  LoaderCircle,
  RefreshCw,
  Search,
  ShieldCheck
} from "lucide-react";
import {
  fetchJson,
  type ActivityEventItem,
  type DownloadDispatchItem,
  type MovieImportRecoverySummary,
  type MovieListItem,
  type MovieSearchHistoryItem,
  type MovieWantedSummary
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { KpiCard } from "../components/app/kpi-card";
import { EmptyState } from "../components/shell/empty-state";

interface MovieDetailLoaderData {
  activity: ActivityEventItem[];
  dispatches: DownloadDispatchItem[];
  importRecovery: MovieImportRecoverySummary;
  movie: MovieListItem;
  searchHistory: MovieSearchHistoryItem[];
  wanted: MovieWantedSummary;
}

export async function movieDetailLoader({
  params
}: {
  params: { id?: string };
}): Promise<MovieDetailLoaderData> {
  const id = params.id!;
  const [movie, wanted, searchHistory, dispatches, importRecovery, activity] = await Promise.all([
    fetchJson<MovieListItem>(`/api/movies/${id}`),
    fetchJson<MovieWantedSummary>("/api/movies/wanted"),
    fetchJson<MovieSearchHistoryItem[]>("/api/movies/search-history"),
    fetchJson<DownloadDispatchItem[]>("/api/download-dispatches?mediaType=movies"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<ActivityEventItem[]>(`/api/activity?relatedEntityType=movie&relatedEntityId=${id}&take=20`)
  ]);

  return { activity, dispatches, importRecovery, movie, searchHistory, wanted };
}

export function MovieDetailPage() {
  const loaderData = useLoaderData() as MovieDetailLoaderData | undefined;
  const { activity, dispatches, importRecovery, movie, searchHistory, wanted } = loaderData ?? {
    activity: [],
    dispatches: [],
    importRecovery: {
      openCount: 0,
      qualityCount: 0,
      unmatchedCount: 0,
      corruptCount: 0,
      downloadFailedCount: 0,
      importFailedCount: 0,
      recentCases: []
    },
    movie: {
      id: "unknown",
      title: "Unknown movie",
      releaseYear: null,
      imdbId: null,
      monitored: false,
      metadataProvider: null,
      metadataProviderId: null,
      originalTitle: null,
      overview: null,
      posterUrl: null,
      backdropUrl: null,
      rating: null,
      genres: null,
      externalUrl: null,
      metadataJson: null,
      metadataUpdatedUtc: null,
      createdUtc: new Date(0).toISOString(),
      updatedUtc: new Date(0).toISOString()
    },
    searchHistory: [],
    wanted: { totalWanted: 0, missingCount: 0, upgradeCount: 0, waitingCount: 0, recentItems: [] }
  };
  const revalidator = useRevalidator();
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);

  const wantedItem = wanted.recentItems.find((item) => item.movieId === movie.id) ?? null;
  const movieSearches = searchHistory.filter((item) => item.movieId === movie.id);
  const movieDispatches = dispatches.filter((item) => item.entityId === movie.id);
  const importCases = importRecovery.recentCases.filter(
    (item) => item.title.trim().toLowerCase() === movie.title.trim().toLowerCase()
  );

  async function handleMonitoring(monitored: boolean) {
    setBusyAction(monitored ? "monitor" : "unmonitor");
    setActionMessage(null);

    try {
      const response = await authedFetch("/api/movies/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ movieIds: [movie.id], monitored })
      });

      if (!response.ok) {
        throw new Error("movie-monitoring-failed");
      }

      setActionMessage(monitored ? "Movie monitored." : "Movie unmonitored.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Movie update failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSearchNow() {
    setBusyAction("search");
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/movies/${movie.id}/search?mode=preview`, { method: "POST" });
      if (!response.ok) {
        throw new Error("movie-search-failed");
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
      setActionMessage(formatSearchActionMessage("movie", best, payload));
      revalidator.revalidate();
    } catch {
      setActionMessage("Search request failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleGrabCandidate(candidate: SearchPlanCandidate) {
    setBusyAction(`grab:${candidate.indexerName}:${candidate.releaseName}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/movies/${movie.id}/grab`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          releaseName: candidate.releaseName,
          indexerName: candidate.indexerName,
          downloadUrl: candidate.downloadUrl
        })
      });

      if (!response.ok) {
        throw new Error("movie-grab-failed");
      }

      const payload = (await response.json()) as {
        releaseName?: string;
        indexerName?: string | null;
        dispatchStatus?: string;
        dispatchMessage?: string;
      };
      const best = payload.releaseName
        ? `${payload.releaseName}${payload.indexerName ? ` via ${payload.indexerName}` : ""}`
        : candidate.releaseName;
      setActionMessage(formatSearchActionMessage("movie", best, { ...payload, candidates: [candidate] }));
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
      const response = await authedFetch(`/api/movies/${movie.id}/metadata/refresh`, { method: "POST" });
      if (!response.ok) {
        throw new Error("movie-metadata-refresh-failed");
      }

      setActionMessage("Movie metadata refreshed.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Metadata refresh failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleDismissImportCase(id: string) {
    setBusyAction(`import-${id}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/movies/import-recovery/${id}`, { method: "DELETE" });
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
            to="/movies"
            className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
            Back to Movies
          </Link>
          <p className="mt-3 text-sm text-muted-foreground">Movie workspace</p>
          <h1 className="font-display text-3xl font-semibold text-foreground sm:text-4xl">
            {movie.title}
            {movie.releaseYear ? (
              <span className="ml-3 text-muted-foreground">{movie.releaseYear}</span>
            ) : null}
          </h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Search, dispatch, import, and monitoring context for this movie.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Badge variant={movie.monitored ? "success" : "default"}>
            {movie.monitored ? "Monitored" : "Passive"}
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
          {busyAction === "search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
          Search now
        </Button>
        <Button
          variant="outline"
          onClick={() => void handleMonitoring(!movie.monitored)}
          disabled={busyAction !== null}
        >
          {busyAction === "monitor" || busyAction === "unmonitor" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <ShieldCheck className="h-4 w-4" />
          )}
          {movie.monitored ? "Unmonitor" : "Monitor"}
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
        <Button variant="ghost" onClick={() => revalidator.revalidate()} disabled={revalidator.state !== "idle"}>
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
          label="Wanted state"
          value={wantedItem ? formatWantedStatus(wantedItem.wantedStatus) : "Tracked"}
          icon={Clapperboard}
          meta={wantedItem?.wantedReason ?? "No explicit wanted pressure is currently recorded."}
          sparkline={[5, 6, 5, 6, 7, 8, 7, 8, 9, 8, 8, 9, 10, 9, 9]}
        />
        <KpiCard
          label="Searches"
          value={String(movieSearches.length)}
          icon={Search}
          meta="Recorded search attempts tied to this title."
          sparkline={[1, 1, 2, 2, 3, 3, 3, 4, 4, 5, 5, 5, 6, 6, 6]}
        />
        <KpiCard
          label="Dispatches"
          value={String(movieDispatches.length)}
          icon={Clapperboard}
          meta="Releases sent to a download client for this title."
          sparkline={[0, 1, 0, 1, 1, 2, 1, 2, 3, 2, 2, 3, 3, 4, 4]}
        />
        <KpiCard
          label="Activity"
          value={String(activity.length)}
          icon={Activity}
          meta="Entity-scoped activity events recorded for this movie."
          sparkline={[2, 2, 3, 3, 4, 4, 5, 4, 5, 6, 6, 7, 7, 8, 8]}
        />
      </div>

      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.18fr)_minmax(380px,0.82fr)] 2xl:grid-cols-[minmax(0,1.35fr)_minmax(440px,0.65fr)]">
        <div className="space-y-[var(--page-gap)]">
          <Card>
            <CardHeader>
              <CardTitle>Search and dispatch</CardTitle>
              <CardDescription>
                Search outcomes, chosen releases, and dispatch trail.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
              {movieSearches.length ? (
                movieSearches.slice(0, 8).map((item) => (
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
                      {item.indexerName ?? "No source yet"} ·{" "}
                      {item.triggerKind === "manual" ? "Manual" : "Scheduled"}
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
                  description="Manual and scheduled searches for this movie will appear here once they run."
                />
              )}

              {movieDispatches.length ? (
                <div className="space-y-3 pt-2">
                  {movieDispatches.slice(0, 6).map((item) => (
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
        </div>

        <div className="space-y-[var(--page-gap)]">
          <Card>
            <CardHeader>
              <CardTitle>Metadata</CardTitle>
              <CardDescription>
                Provider identity, artwork context, ratings, and overview stored for this title.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
              {movie.overview ? (
                <p className="text-sm leading-relaxed text-muted-foreground">{movie.overview}</p>
              ) : (
                <p className="text-sm text-muted-foreground">
                  No provider overview has been stored yet. Refresh metadata to enrich this title.
                </p>
              )}
              <div className="grid gap-3 sm:grid-cols-2">
                <MetadataStat label="Provider" value={movie.metadataProvider ?? "Not linked"} />
                <MetadataStat label="Provider ID" value={movie.metadataProviderId ?? "None"} />
                <MetadataStat label="Rating" value={movie.rating ? movie.rating.toFixed(1) : "Unknown"} />
                <MetadataStat label="Genres" value={movie.genres ?? "None"} />
              </div>
              {movie.externalUrl ? (
                <Button asChild variant="outline" size="sm">
                  <a href={movie.externalUrl} target="_blank" rel="noreferrer">
                    Open provider page
                  </a>
                </Button>
              ) : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Import and activity</CardTitle>
              <CardDescription>
                Recovery pressure and event trail for this movie.
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
                          {busyAction === `import-${item.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                          Dismiss
                        </Button>
                      </div>
                    </div>
                    <p className="mt-2 text-sm text-muted-foreground">{item.summary}</p>
                    <p className="mt-1 text-xs text-muted-foreground">{item.recommendedAction}</p>
                  </div>
                ))
              ) : (
                <p className="text-sm text-muted-foreground">No import issues recorded for this movie.</p>
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
}

function ReleaseCandidatePicker({
  candidates,
  busyAction,
  onGrab
}: {
  candidates: SearchPlanCandidate[];
  busyAction: string | null;
  onGrab: (candidate: SearchPlanCandidate) => Promise<void>;
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
          return (
            <div key={`${candidate.indexerName}:${candidate.releaseName}`} className="rounded-xl border border-hairline bg-surface-1 p-4">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge variant={index === 0 ? "success" : "default"}>{index === 0 ? "Best match" : `#${index + 1}`}</Badge>
                    <Badge variant={candidate.meetsCutoff ? "success" : "warning"}>{candidate.quality}</Badge>
                    <span className="font-mono text-[11px] text-muted-foreground">score {candidate.score}</span>
                    {candidate.seeders !== null && candidate.seeders !== undefined ? (
                      <span className="font-mono text-[11px] text-muted-foreground">{candidate.seeders} seeders</span>
                    ) : null}
                    {candidate.sizeBytes ? (
                      <span className="font-mono text-[11px] text-muted-foreground">{formatBytes(candidate.sizeBytes)}</span>
                    ) : null}
                  </div>
                  <p className="mt-2 truncate text-sm font-semibold text-foreground">{candidate.releaseName}</p>
                  <p className="mt-1 text-xs text-muted-foreground">{candidate.indexerName} · {candidate.summary}</p>
                </div>
                <Button
                  size="sm"
                  disabled={busyAction !== null || !candidate.downloadUrl}
                  onClick={() => void onGrab(candidate)}
                >
                  {busyAction === busyKey ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                  {candidate.downloadUrl ? "Grab release" : "No URL"}
                </Button>
              </div>
            </div>
          );
        })}
      </CardContent>
    </Card>
  );
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
    seeders: (item.seeders ?? item.Seeders ?? null) as number | null
  };
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
    candidates?: unknown[];
  }
) {
  if (!best) {
    return payload.summary ?? `Manual ${mediaLabel} search completed with no accepted release.`;
  }

  const candidateCount = payload.candidates?.length ?? 0;
  const candidateLabel = `${candidateCount} candidate${candidateCount === 1 ? "" : "s"} scored`;

  switch (payload.dispatchStatus) {
    case "sent":
      return `Manual ${mediaLabel} search sent ${best} to the download client. ${candidateLabel}.`;
    case "planned":
      return `Manual ${mediaLabel} search matched ${best}, but no downloadable URL was available yet. ${candidateLabel}.`;
    case "failed":
      return `Manual ${mediaLabel} search matched ${best}, but the download client rejected the grab${payload.dispatchMessage ? `: ${payload.dispatchMessage}` : "."}`;
    default:
      return `Manual ${mediaLabel} search matched ${best}. ${candidateLabel}.`;
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
