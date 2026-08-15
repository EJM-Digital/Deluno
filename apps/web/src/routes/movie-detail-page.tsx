import { useState } from "react";
import { Link, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import * as Dialog from "@radix-ui/react-dialog";
import {
  ArrowLeft,
  LoaderCircle,
  RefreshCw,
  Search,
  Trash2,
  X
} from "lucide-react";
import {
  fetchJson,
  type ActivityEventItem,
  type DecisionExplanationItem,
  type DownloadDispatchItem,
  type LibraryItem,
  type IntakeTitleOriginItem,
  type MetadataCastMember,
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
import { RemoveMediaDialog, type MediaRemovalPreview, type RemoveMediaOptions } from "../components/app/remove-media-dialog";
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
  origins: IntakeTitleOriginItem[];
  removalPreview: MediaRemovalPreview;
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
  const [movie, wanted, searchHistory, dispatches, importRecovery, activity, decisions, libraries, workflowStatus, origins, removalPreview] = await Promise.all([
    fetchJson<MovieListItem>(`/api/movies/${id}`),
    fetchJson<MovieWantedSummary>("/api/movies/wanted"),
    fetchJson<MovieSearchHistoryItem[]>("/api/movies/search-history"),
    fetchJson<DownloadDispatchItem[]>("/api/download-dispatches?mediaType=movies"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<ActivityEventItem[]>(`/api/activity?relatedEntityType=movie&relatedEntityId=${id}&take=20`),
    fetchJson<DecisionExplanationItem[]>(`/api/decisions?relatedEntityType=movie&relatedEntityId=${id}&take=40`),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<MovieWorkflowStatus>(`/api/movies/${id}/workflow-status`).catch(() => null),
    fetchJson<IntakeTitleOriginItem[]>(`/api/intake-title-origins?mediaType=movies&entityId=${encodeURIComponent(id)}`).catch(() => []),
    fetchJson<MediaRemovalPreview>(`/api/movies/${id}/removal-preview`).catch(() => ({ filePaths: [], folderPaths: [], warnings: [] }))
  ]);

  return { activity, decisions, dispatches, importRecovery, libraries, movie, origins, removalPreview, searchHistory, wanted, workflowStatus };
}

export function MovieDetailPage() {
  const loaderData = useLoaderData() as MovieDetailLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { activity, decisions, dispatches, importRecovery, libraries, movie, origins, removalPreview, searchHistory, wanted, workflowStatus } = loaderData;
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [isRemoveConfirmationOpen, setIsRemoveConfirmationOpen] = useState(false);
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
  const [activeDetailSection, setActiveDetailSection] = useState<"details" | "history">("details");
  const [isMetadataEditorOpen, setIsMetadataEditorOpen] = useState(false);

  const wantedItem = wanted.recentItems.find((item) => item.movieId === movie.id) ?? null;
  const library = wantedItem ? libraries.find((item) => item.id === wantedItem.libraryId) ?? null : null;
  const movieSearches = searchHistory.filter((item) => item.movieId === movie.id);
  const movieDispatches = dispatches.filter((item) => item.entityId === movie.id);
  const importCases = importRecovery.recentCases.filter(
    (item) => item.title.trim().toLowerCase() === movie.title.trim().toLowerCase()
  );
  const cast = readStoredCast(movie.metadataJson);
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

      setActionMessage(monitored ? "Background automation resumed for this movie." : "Background automation paused for this movie.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Movie update failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleRemoveFromDeluno(options: RemoveMediaOptions) {
    setBusyAction("remove");
    setActionMessage(null);
    try {
      const response = await authedFetch("/api/movies/bulk", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ movieIds: [movie.id], operation: "remove", ...options })
      });
      if (!response.ok) throw new Error("movie-remove-failed");

      const result = await response.json() as { successCount?: number };
      if ((result.successCount ?? 0) !== 1) throw new Error("movie-remove-failed");
      navigate("/movies", { replace: true });
    } catch {
      setActionMessage("Could not remove this movie from Deluno.");
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
      const response = await authedFetch(`/api/movies/${movie.id}/automation/defer`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: wantedItem.libraryId, hours: 24 })
      });
      if (!response.ok) throw new Error("movie-defer-failed");
      setActionMessage("Background automation deferred for 24 hours. You can still search manually.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Could not defer background automation for this movie.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSkipNextAutomationSearch() {
    if (!wantedItem) return;
    setBusyAction("skip-once");
    setActionMessage(null);
    try {
      const response = await authedFetch(`/api/movies/${movie.id}/automation/skip-once`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: wantedItem.libraryId })
      });
      if (!response.ok) throw new Error("movie-skip-once-failed");
      setActionMessage("The next scheduled search will be skipped. You can still search manually.");
      revalidator.revalidate();
    } catch {
      setActionMessage("Could not skip the next scheduled search for this movie.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSearchNow(mode: "automatic" | "interactive") {
    setBusyAction(`${mode}-search`);
    setActionMessage(null);

    try {
      const response = await authedFetch(`/api/movies/${movie.id}/search${mode === "interactive" ? "?mode=preview" : ""}`, { method: "POST" });
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
      setReleaseCandidates(mode === "interactive" ? payload.candidates ?? [] : []);
      setActionMessage(mode === "interactive" ? formatSearchActionMessage("movie", best, payload) : (best ? `Deluno selected ${best} using this movie’s Media Plan.` : "Deluno searched using this movie’s Media Plan."));
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
      <Link
        to="/movies"
        className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-4 w-4" />
        Back to Movies
      </Link>

      <Card className="relative isolate min-h-[19rem] overflow-hidden border-primary/25 bg-card">
        {movie.backdropUrl ? (
          <img
            src={movie.backdropUrl}
            alt=""
            className="pointer-events-none absolute inset-0 h-full w-full scale-105 object-cover opacity-[0.34] saturate-[0.8]"
          />
        ) : null}
        <div className="pointer-events-none absolute inset-0 bg-gradient-to-r from-card via-card/80 to-card/45" />
        <div className="pointer-events-none absolute inset-0 bg-gradient-to-t from-card/90 via-transparent to-card/25" />
        <CardContent className="relative p-[var(--tile-pad)] sm:p-[calc(var(--tile-pad)*1.15)]">
          <div className="grid min-h-[15rem] items-center gap-[var(--grid-gap)] md:grid-cols-[10rem_minmax(0,1fr)] xl:grid-cols-[10rem_minmax(0,1fr)_14rem]">
            {movie.posterUrl ? (
              <img src={movie.posterUrl} alt={`${movie.title} poster`} className="h-64 w-40 justify-self-center rounded-2xl border border-white/15 bg-surface-1 object-cover shadow-2xl md:justify-self-start" />
            ) : (
              <div className="flex h-64 w-40 justify-self-center items-center justify-center rounded-2xl border border-hairline bg-surface-1 px-3 text-center text-xs text-muted-foreground md:justify-self-start">Artwork is being refreshed</div>
            )}
            <div className="min-w-0 self-center">
              <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-primary">Movie</p>
              <div className="mt-1 flex flex-wrap items-baseline gap-x-3 gap-y-1">
                <h1 className="font-display text-4xl font-semibold tracking-tight text-foreground sm:text-5xl">{movie.title}</h1>
                {movie.releaseYear ? <span className="font-display text-2xl text-muted-foreground sm:text-3xl">{movie.releaseYear}</span> : null}
              </div>
              {movie.originalTitle && movie.originalTitle !== movie.title ? <p className="mt-1 text-sm text-muted-foreground">Also known as {movie.originalTitle}</p> : null}
              <div className="mt-4 flex flex-wrap gap-2">
                <Badge variant="default">{movie.monitored ? "Monitored" : "Not monitored"}</Badge>
                {wantedItem ? <Badge variant={wantedItem.wantedStatus === "missing" || wantedItem.wantedStatus === "upgrade" ? "warning" : "info"}>{formatWantedStatus(wantedItem.wantedStatus)}</Badge> : null}
                {movie.genres?.split(",").map((genre) => <span key={genre} className="rounded-full border border-primary/20 bg-primary/10 px-2.5 py-1 text-xs font-medium text-primary">{genre.trim()}</span>)}
              </div>
              <p className="mt-4 max-w-4xl text-sm leading-relaxed text-muted-foreground">
                {movie.overview ?? "No overview has been stored yet. Refresh metadata when you want Deluno to enrich this title."}
              </p>
              {cast.length ? (
                <section className="mt-5 border-t border-white/10 pt-4">
                  <div className="flex items-center justify-between gap-3">
                    <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground">Starring</p>
                    <span className="text-[11px] text-muted-foreground">{cast.length} credited</span>
                  </div>
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
              <div className="mt-3"><RatingStrip ratings={movie.ratings} fallbackRating={movie.rating} /></div>
              <div className="mt-4 space-y-2 border-t border-hairline pt-4 text-sm">
                <div className="flex items-center justify-between gap-3"><span className="text-muted-foreground">Source</span><span className="font-medium text-foreground">{movie.metadataProvider?.toUpperCase() ?? "Not linked"}</span></div>
                <div className="flex items-center justify-between gap-3"><span className="text-muted-foreground">IMDb</span><span className="font-medium text-foreground">{movie.imdbId ?? "—"}</span></div>
              </div>
              <Button variant="outline" className="mt-4 w-full" onClick={() => setIsMetadataEditorOpen(true)}>Edit metadata</Button>
            </aside>
          </div>
        </CardContent>
      </Card>

      <Dialog.Root open={isMetadataEditorOpen} onOpenChange={setIsMetadataEditorOpen}>
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-50 bg-black/65 backdrop-blur-sm" />
          <Dialog.Content className="fixed left-1/2 top-1/2 z-50 flex max-h-[88dvh] w-[calc(100%-2rem)] max-w-5xl -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl">
            <div className="flex items-start justify-between gap-[var(--grid-gap)] border-b border-hairline px-6 py-5">
              <div>
                <Dialog.Title className="font-display text-xl font-semibold tracking-tight text-foreground">Edit movie metadata</Dialog.Title>
                <Dialog.Description className="mt-1 text-sm text-muted-foreground">Correct the match or update the title details Deluno stores for this movie.</Dialog.Description>
              </div>
              <Dialog.Close asChild><Button variant="ghost" size="icon" aria-label="Close metadata editor"><X className="h-4 w-4" /></Button></Dialog.Close>
            </div>
            <div className="grid min-h-0 flex-1 gap-[var(--grid-gap)] overflow-y-auto p-6 lg:grid-cols-[minmax(0,1fr)_16rem]">
              <div className="space-y-[var(--page-gap)]">
                <div className="rounded-xl border border-hairline bg-surface-1 p-4">
                  <p className="text-sm font-semibold text-foreground">Find the correct match</p>
                  <p className="mt-1 text-sm text-muted-foreground">Search the provider, then select the right movie. Deluno refreshes the artwork, title, overview, IDs, genres, and ratings.</p>
                  <div className="mt-4"><MetadataCorrectionPanel busyAction={busyAction} mediaLabel="movie" query={metadataQuery} matches={metadataMatches} searchAttempted={metadataSearchAttempted} onQueryChange={setMetadataQuery} onSearch={handleMetadataSearch} onLink={handleMetadataLink} /></div>
                </div>
                <details className="rounded-xl border border-hairline bg-surface-1 px-4 py-3">
                  <summary className="cursor-pointer text-sm font-semibold text-foreground">Manual override</summary>
                  <p className="mt-1 text-sm text-muted-foreground">Use this only when the provider data needs a deliberate correction.</p>
                  <div className="mt-4"><ManualMetadataOverridePanel busyAction={busyAction} value={metadataOverride} onChange={setMetadataOverride} onSave={handleMetadataOverrideSave} /></div>
                </details>
              </div>
              <aside className="space-y-[var(--page-gap)] rounded-xl border border-hairline bg-surface-1 p-4">
                {movie.posterUrl ? <img src={movie.posterUrl} alt={`${movie.title} poster`} className="mx-auto w-32 rounded-lg border border-white/10 object-cover shadow-xl" /> : null}
                <div className="space-y-3 text-sm">
                  <div><p className="text-[10px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Provider</p><p className="mt-1 font-medium text-foreground">{movie.metadataProvider?.toUpperCase() ?? "Not linked"}</p></div>
                  <div><p className="text-[10px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Provider ID</p><p className="mt-1 break-all font-medium text-foreground">{movie.metadataProviderId ?? "—"}</p></div>
                  <div><p className="text-[10px] font-bold uppercase tracking-[0.16em] text-muted-foreground">IMDb</p><p className="mt-1 font-medium text-foreground">{movie.imdbId ?? "—"}</p></div>
                </div>
                <Button variant="outline" className="w-full" onClick={() => void handleRefreshMetadata()} disabled={busyAction !== null}><RefreshCw className="h-4 w-4" /> Refresh metadata</Button>
                {movie.externalUrl ? <Button asChild variant="outline" className="w-full"><a href={movie.externalUrl} target="_blank" rel="noreferrer">Open provider page</a></Button> : null}
              </aside>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>

      <div className="flex flex-wrap gap-2">
        <Button onClick={() => void handleSearchNow("automatic")} disabled={busyAction !== null} title="Deluno applies the active Media Plan and automatically sends the best acceptable release.">
          {busyAction === "automatic-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
          Automatic search
        </Button>
        <Button variant="outline" onClick={() => void handleSearchNow("interactive")} disabled={busyAction !== null} title="Review every candidate and choose the release yourself.">
          {busyAction === "interactive-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
          Interactive search
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

      <nav className="flex flex-wrap gap-1 rounded-xl border border-hairline bg-surface-1 p-1" aria-label="Movie detail sections">
        {[
          ["details", "Files & destination"],
          ["history", "History & activity"]
        ].map(([section, label]) => (
          <button
            key={section}
            type="button"
            onClick={() => setActiveDetailSection(section as "details" | "history")}
            className={activeDetailSection === section ? "rounded-lg bg-card px-4 py-2 text-sm font-semibold text-foreground shadow-sm" : "rounded-lg px-4 py-2 text-sm font-medium text-muted-foreground hover:text-foreground"}
          >
            {label}
          </button>
        ))}
      </nav>

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

      <div className={activeDetailSection === "details" ? "space-y-[var(--page-gap)]" : "grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.18fr)_minmax(380px,0.82fr)] 2xl:grid-cols-[minmax(0,1.35fr)_minmax(440px,0.65fr)]"}>
        <div className="space-y-[var(--page-gap)]">
          {activeDetailSection === "details" ? <RoutingCard
              library={library}
              currentQuality={workflowStatus?.currentQuality ?? wantedItem?.currentQuality ?? null}
              targetQuality={workflowStatus?.targetQuality ?? wantedItem?.targetQuality ?? "WEB 1080p"}
              workflow={library?.importWorkflow ?? "standard"}
              workflowStatus={workflowStatus}
            /> : null}
          {activeDetailSection === "details" ? <IntakeOriginsCard origins={origins} mediaLabel="movie" /> : null}

          {activeDetailSection === "history" ? <Card>
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
                  <Link to="/queue" className="inline-flex text-sm font-medium text-amber-400 hover:text-amber-300">
                    Open Transfers to manage download-client work →
                  </Link>
                </div>
              ) : null}
            </CardContent>
          </Card> : null}
        </div>

        {activeDetailSection === "history" ? <div className="space-y-[var(--page-gap)]">

          <Card id="decision-trail">
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

          <Card id="import-activity">
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
        </div> : null}
      </div>
      <RemoveMediaDialog
        open={isRemoveConfirmationOpen}
        onOpenChange={setIsRemoveConfirmationOpen}
        title={movie.title}
        mediaLabel="movie"
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
    const value = JSON.parse(metadataJson) as { cast?: unknown; Cast?: unknown };
    const cast = value.cast ?? value.Cast;
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
