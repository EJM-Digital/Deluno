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
  type DecisionExplanationItem,
  type DownloadDispatchItem,
  type LibraryItem,
  type MetadataSearchResult,
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
import { DecisionExplanationList } from "../components/app/decision-explanation-list";
import { RatingStrip } from "../components/app/rating-strip";
import { EmptyState } from "../components/shell/empty-state";
import { RouteSkeleton } from "../components/shell/skeleton";

interface MovieDetailLoaderData {
  activity: ActivityEventItem[];
  decisions: DecisionExplanationItem[];
  dispatches: DownloadDispatchItem[];
  importRecovery: MovieImportRecoverySummary;
  libraries: LibraryItem[];
  movie: MovieListItem;
  searchHistory: MovieSearchHistoryItem[];
  wanted: MovieWantedSummary;
  workflowStatus: MovieWorkflowStatus | null;
}

interface MovieWorkflowStatus {
  wantedStatus: string;
  reason: string;
  isReplacementAllowed: boolean;
  qualityDelta: number | null;
  currentQuality: string | null;
  targetQuality: string | null;
  preventLowerQualityReplacements: boolean;
  lastQualityDeltaDecision: number | null;
}

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

export async function movieDetailLoader({
  params
}: {
  params: { id?: string };
}): Promise<MovieDetailLoaderData> {
  const id = params.id!;
  const [movie, wanted, searchHistory, dispatches, importRecovery, activity, decisions, libraries, workflowStatus] = await Promise.all([
    fetchJson<MovieListItem>(`/api/movies/${id}`),
    fetchJson<MovieWantedSummary>("/api/movies/wanted"),
    fetchJson<MovieSearchHistoryItem[]>("/api/movies/search-history"),
    fetchJson<DownloadDispatchItem[]>("/api/download-dispatches?mediaType=movies"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<ActivityEventItem[]>(`/api/activity?relatedEntityType=movie&relatedEntityId=${id}&take=20`),
    fetchJson<DecisionExplanationItem[]>(`/api/decisions?relatedEntityType=movie&relatedEntityId=${id}&take=40`),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<MovieWorkflowStatus>(`/api/movies/${id}/workflow-status`).catch(() => null)
  ]);

  return { activity, decisions, dispatches, importRecovery, libraries, movie, searchHistory, wanted, workflowStatus };
}

export function MovieDetailPage() {
  const loaderData = useLoaderData() as MovieDetailLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { activity, decisions, dispatches, importRecovery, libraries, movie, searchHistory, wanted, workflowStatus } = loaderData;
  const revalidator = useRevalidator();
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [metadataQuery, setMetadataQuery] = useState(movie.title);
  const [metadataMatches, setMetadataMatches] = useState<MetadataSearchResult[]>([]);
  const [metadataSearchAttempted, setMetadataSearchAttempted] = useState(false);
  const [metadataOverride, setMetadataOverride] = useState<MetadataOverridePayload>({
    originalTitle: movie.originalTitle ?? "",
    overview: movie.overview ?? "",
    posterUrl: movie.posterUrl ?? "",
    backdropUrl: movie.backdropUrl ?? "",
    rating: movie.rating !== null && movie.rating !== undefined ? String(movie.rating) : "",
    genres: movie.genres ?? "",
    externalUrl: movie.externalUrl ?? "",
    imdbId: movie.imdbId ?? ""
  });
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);
  const [preventLowerQuality, setPreventLowerQuality] = useState(workflowStatus?.preventLowerQualityReplacements ?? true);

  const wantedItem = wanted.recentItems.find((item) => item.movieId === movie.id) ?? null;
  const library = wantedItem ? libraries.find((item) => item.id === wantedItem.libraryId) ?? null : null;
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

  async function handleGrabCandidate(candidate: SearchPlanCandidate, force = false, overrideReason?: string) {
    setBusyAction(`${force ? "force-grab" : "grab"}:${candidate.indexerName}:${candidate.releaseName}`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/movies/${movie.id}/grab`, {
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
        throw new Error("movie-grab-failed");
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

  async function handleMetadataSearch() {
    setBusyAction("metadata-search");
    setActionMessage(null);
    setMetadataSearchAttempted(true);
    try {
      const params = new URLSearchParams({
        query: metadataQuery.trim() || movie.title,
        mediaType: "movies"
      });
      if (movie.releaseYear) params.set("year", String(movie.releaseYear));
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
      const response = await authedFetch(`/api/movies/${movie.id}/metadata/link`, {
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
      const response = await authedFetch(`/api/movies/${movie.id}/metadata/override`, {
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

  async function handleUpdateReplacementProtection(enabled: boolean) {
    setBusyAction("replacement-protection");
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/movies/${movie.id}/replacement-protection`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ preventLowerQualityReplacements: enabled })
      });

      if (!response.ok) {
        throw new Error("Failed to update replacement protection setting.");
      }

      setPreventLowerQuality(enabled);
      setActionMessage(enabled ? "Replacement protection enabled." : "Replacement protection disabled.");
      revalidator.revalidate();
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : "Failed to update replacement protection.");
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
          variant={preventLowerQuality ? "outline" : "outline"}
          className={preventLowerQuality ? "border-primary/50 bg-primary/5" : ""}
          onClick={() => void handleUpdateReplacementProtection(!preventLowerQuality)}
          disabled={busyAction !== null}
          title="Prevent Deluno from replacing your current file with a lower quality release"
        >
          {busyAction === "replacement-protection" ? (
            <LoaderCircle className="h-4 w-4 animate-spin" />
          ) : (
            <ShieldCheck className="h-4 w-4" />
          )}
          {preventLowerQuality ? "Replacement protection ON" : "Replacement protection OFF"}
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
        <div className="rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-sm text-muted-foreground" role="status" aria-live="polite">
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
          <RoutingCard
            library={library}
            currentQuality={workflowStatus?.currentQuality ?? wantedItem?.currentQuality ?? null}
            targetQuality={workflowStatus?.targetQuality ?? wantedItem?.targetQuality ?? "WEB 1080p"}
            workflow={library?.importWorkflow ?? "standard"}
            workflowStatus={workflowStatus}
          />

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
                <MetadataStat label="Genres" value={movie.genres ?? "None"} />
              </div>
              <RatingStrip ratings={movie.ratings} fallbackRating={movie.rating} />
              <MetadataCorrectionPanel
                busyAction={busyAction}
                mediaLabel="movie"
                query={metadataQuery}
                matches={metadataMatches}
                searchAttempted={metadataSearchAttempted}
                onQueryChange={setMetadataQuery}
                onSearch={handleMetadataSearch}
                onLink={handleMetadataLink}
              />
              <ManualMetadataOverridePanel
                busyAction={busyAction}
                value={metadataOverride}
                onChange={setMetadataOverride}
                onSave={handleMetadataOverrideSave}
              />
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
              <CardTitle>Decision trail</CardTitle>
              <CardDescription>
                Search, grab, import, and retry decisions recorded for this movie.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <DecisionExplanationList decisions={decisions} />
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

function RoutingCard({
  currentQuality,
  library,
  targetQuality,
  workflow,
  workflowStatus
}: {
  currentQuality: string | null;
  library: LibraryItem | null;
  targetQuality: string | null;
  workflow: string;
  workflowStatus: MovieWorkflowStatus | null;
}) {
  const qualityCutoffMet = workflowStatus && currentQuality && targetQuality
    ? workflowStatus.qualityDelta !== null && workflowStatus.qualityDelta >= 0
    : null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Routing and destination</CardTitle>
        <CardDescription>
          Final filenames are previewed once Deluno has a source file. This shows the active library route now.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
        <div className="grid gap-3 sm:grid-cols-2">
          <MetadataStat label="Library" value={library?.name ?? "Not linked"} />
          <MetadataStat label="Root folder" value={library?.rootPath || "No root configured"} />
          <MetadataStat label="Downloads folder" value={library?.downloadsPath || "Client default"} />
          <MetadataStat label="Workflow" value={workflow === "refine-before-import" ? "Refine before import" : "Standard import"} />
          <MetadataStat label="Current quality" value={currentQuality ?? "Unknown"} />
          <MetadataStat label="Target quality" value={targetQuality ?? "WEB 1080p"} />
        </div>
        {workflowStatus && (
          <div className="rounded-xl border border-hairline bg-surface-1 p-4">
            <p className="font-display text-sm font-semibold tracking-tight text-foreground">Quality status</p>
            <div className="mt-3 grid gap-3 sm:grid-cols-2">
              <div>
                <p className="text-[10px] uppercase tracking-[0.16em] text-muted-foreground">Cutoff status</p>
                <p className="mt-1 text-sm font-medium text-foreground">
                  {qualityCutoffMet === true ? (
                    <span className="text-green-600 dark:text-green-400">✓ Met</span>
                  ) : qualityCutoffMet === false ? (
                    <span className="text-amber-600 dark:text-amber-400">⚠ Below target</span>
                  ) : (
                    <span className="text-muted-foreground">No data</span>
                  )}
                </p>
              </div>
              {workflowStatus.qualityDelta !== null && (
                <div>
                  <p className="text-[10px] uppercase tracking-[0.16em] text-muted-foreground">Last quality delta</p>
                  <p className="mt-1 text-sm font-medium text-foreground font-mono">
                    {workflowStatus.qualityDelta > 0 ? (
                      <span className="text-green-600 dark:text-green-400">+{workflowStatus.qualityDelta}</span>
                    ) : workflowStatus.qualityDelta < 0 ? (
                      <span className="text-red-600 dark:text-red-400">{workflowStatus.qualityDelta}</span>
                    ) : (
                      <span className="text-muted-foreground">0</span>
                    )}
                  </p>
                </div>
              )}
            </div>
            {workflowStatus.reason && (
              <p className="mt-3 text-sm text-muted-foreground">{workflowStatus.reason}</p>
            )}
          </div>
        )}
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

function isValidHttpUrl(value: string) {
  try {
    const parsed = new URL(value);
    return parsed.protocol === "http:" || parsed.protocol === "https:";
  } catch {
    return false;
  }
}
