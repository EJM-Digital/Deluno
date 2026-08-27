/**
 * Movie detail — hero → toolbar sections → list cards → drawers.
 *
 * Same shape as the TV show page, minus episodes: a detail page keeps its `h1`
 * because the topbar names the section ("Movies") rather than the film, and the
 * hero artwork is content. Everything below it obeys the list → drawer grammar.
 *
 * Contracts: GET /api/movies/{id}, /workflow-status, /removal-preview; PUT
 * /api/movies/monitoring, /api/movies/{id}/replacement-protection; POST
 * /api/movies/{id}/search, /grab, /automation/defer, /automation/skip-once,
 * /api/movies/bulk.
 */
import { useState } from "react";
import { Link, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { ArrowLeft, LoaderCircle, RefreshCw, Search, Trash2 } from "lucide-react";
import {
  fetchJson, fetchPageItems,
  type ActivityEventItem,
  type DecisionExplanationItem,
  type DownloadDispatchItem,
  type LibraryItem,
  type IntakeTitleOriginItem,
  type MetadataCastMember,
  type MovieImportRecoverySummary,
  type MovieListItem,
  type MovieSearchHistoryItem
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { describeSearchReason } from "../lib/search-reasons";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent } from "../components/ui/card";
import { RemoveMediaDialog, type MediaRemovalPreview, type RemoveMediaOptions } from "../components/app/remove-media-dialog";
import { DecisionExplanationList } from "../components/app/decision-explanation-list";
import { MediaMetadataDrawer } from "../components/app/media-metadata-drawer";
import { RatingStrip } from "../components/app/rating-strip";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { Input } from "../components/ui/input";
import {
  LIST_TRACK,
  ListCard,
  ListCell,
  ListEmpty,
  ListNameCell,
  ListRow,
  ListTable
} from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { SummaryStrip } from "../components/ui/summary-strip";
import { Switch } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { wantedStatusPresentation } from "../lib/media-status-presentation";

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
  workflowStatus: MovieWorkflowStatus | null;
}

/**
 * `GET /api/movies/{id}/workflow-status`. It does not return `qualityDelta` or
 * `isReplacementAllowed` — the old interface declared both, so the panel keyed
 * on them could only ever say "No data". Cutoff comes from the wanted item.
 */
interface MovieWorkflowStatus {
  wantedStatus: string;
  reason: string;
  currentQuality: string | null;
  targetQuality: string | null;
  preventLowerQualityReplacements: boolean;
  lastQualityDeltaDecision: number | null;
}

type DetailSection = "destination" | "history";

export async function movieDetailLoader({
  params
}: {
  params: { id?: string };
}): Promise<MovieDetailLoaderData> {
  const id = params.id!;
  const [movie, searchHistory, dispatches, importRecovery, activity, decisions, libraries, workflowStatus, origins, removalPreview] = await Promise.all([
    fetchJson<MovieListItem>(`/api/movies/${id}`),
    fetchJson<MovieSearchHistoryItem[]>("/api/movies/search-history"),
    fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?mediaType=movies&pageSize=20"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchPageItems<ActivityEventItem>(`/api/activity?relatedEntityType=movie&relatedEntityId=${id}&pageSize=20`),
    fetchPageItems<DecisionExplanationItem>(`/api/decisions?relatedEntityType=movie&relatedEntityId=${id}&pageSize=40`),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<MovieWorkflowStatus>(`/api/movies/${id}/workflow-status`).catch(() => null),
    fetchJson<IntakeTitleOriginItem[]>(`/api/intake-title-origins?mediaType=movies&entityId=${encodeURIComponent(id)}`).catch(() => []),
    fetchJson<MediaRemovalPreview>(`/api/movies/${id}/removal-preview`).catch(() => ({ filePaths: [], folderPaths: [], warnings: [] }))
  ]);

  return { activity, decisions, dispatches, importRecovery, libraries, movie, origins, removalPreview, searchHistory, workflowStatus };
}

export function MovieDetailPage() {
  const loaderData = useLoaderData() as MovieDetailLoaderData;
  const { activity, decisions, dispatches, importRecovery, libraries, movie, origins, removalPreview, searchHistory, workflowStatus } = loaderData;
  const navigate = useNavigate();
  const revalidator = useRevalidator();

  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [isRemoveConfirmationOpen, setIsRemoveConfirmationOpen] = useState(false);
  const [isMetadataOpen, setIsMetadataOpen] = useState(false);
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);
  const [openCandidate, setOpenCandidate] = useState<SearchPlanCandidate | null>(null);
  const [forceReason, setForceReason] = useState<string | null>(null);
  const [openSearchId, setOpenSearchId] = useState<string | null>(null);
  const [section, setSection] = useState<DetailSection>("destination");

  /*
    The title's own record carries its search state.

    This used to search the wanted summary — a list of the 25 most recently
    updated titles — for the one title the page was already showing. Open the
    26th and the lookup missed: no library, no target quality, no cutoff, and a
    Defer button that could only 404. The same defect the grid had, on the
    screen that shows a single title, found by asking where else that shape
    lived.
  */
  const wantedItem = movie.wantedStatus
    ? {
        libraryId: movie.libraryId ?? "",
        wantedStatus: movie.wantedStatus,
        wantedReason: movie.wantedReason ?? "",
        currentQuality: movie.currentQuality ?? null,
        targetQuality: movie.targetQuality ?? null,
        qualityCutoffMet: movie.qualityCutoffMet ?? false
      }
    : null;
  const library = wantedItem ? libraries.find((item) => item.id === wantedItem.libraryId) ?? null : null;
  const movieSearches = searchHistory.filter((item) => item.movieId === movie.id);
  const movieDispatches = dispatches.filter((item) => item.entityId === movie.id);
  const importCases = importRecovery.recentCases.filter(
    (item) => item.title.trim().toLowerCase() === movie.title.trim().toLowerCase()
  );
  const cast = readStoredCast(movie.metadataJson);
  const openSearch = movieSearches.find((item) => item.id === openSearchId) ?? null;

  const currentQuality = workflowStatus?.currentQuality ?? wantedItem?.currentQuality ?? null;
  const targetQuality = workflowStatus?.targetQuality ?? wantedItem?.targetQuality ?? "WEB 1080p";
  const cutoffMet = wantedItem ? wantedItem.qualityCutoffMet : null;
  const lastDelta = workflowStatus?.lastQualityDeltaDecision ?? null;
  // Deferring only touches a wanted state that is actually being searched for, so
  // offering it on a settled title produced an enabled button and a 404.
  const isBeingSearchedFor = wantedItem?.wantedStatus === "missing" || wantedItem?.wantedStatus === "upgrade";

  const nextStep = importCases.length
    ? {
        eyebrow: "Needs attention",
        title: `Review ${importCases.length} import issue${importCases.length === 1 ? "" : "s"}`,
        description: "Something Deluno brought in could not be filed. It needs a decision before this film is settled.",
        action: "Open import issues",
        onAction: () => setSection("history")
      }
    : releaseCandidates.length
      ? {
          eyebrow: "Release ready",
          title: "Choose a release",
          description: "Deluno scored the candidates it found. Pick the one to send to your download client.",
          action: "Review candidates",
          onAction: () => setSection("destination")
        }
      : !movie.monitored
        ? {
            eyebrow: "Monitoring paused",
            title: "Resume automatic care",
            description: "This film is not being watched for a missing file or a quality improvement.",
            action: "Resume automation",
            onAction: () => void handleMonitoring(true)
          }
        : !movie.hasFile
          ? {
              eyebrow: "File missing",
              title: "Find this film",
              description: "Deluno can search every indexer you have connected using this film's Library Profile.",
              action: "Search now",
              onAction: () => void handleSearchNow("automatic")
            }
          : cutoffMet === false
            ? {
                eyebrow: "Below target",
                title: "Look for an upgrade",
                description: `The file on disk is ${currentQuality ?? "an unknown quality"}; the plan asks for ${targetQuality}.`,
                action: "Search now",
                onAction: () => void handleSearchNow("automatic")
              }
            : null;

  async function handleMonitoring(monitored: boolean) {
    setBusyAction("monitor");
    try {
      const response = await authedFetch("/api/movies/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ movieIds: [movie.id], monitored })
      });
      if (!response.ok) throw new Error("movie-monitoring-failed");
      revalidator.revalidate();
    } catch {
      toast.error("This film's monitoring could not be changed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleRemoveFromDeluno(options: RemoveMediaOptions) {
    setBusyAction("remove");
    try {
      const response = await authedFetch("/api/movies/bulk", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ movieIds: [movie.id], operation: "remove", ...options })
      });
      if (!response.ok) throw new Error("movie-remove-failed");

      const result = (await response.json()) as { successCount?: number };
      if ((result.successCount ?? 0) !== 1) throw new Error("movie-remove-failed");
      toast.success(`${movie.title} removed from Deluno`);
      navigate("/movies", { replace: true });
    } catch {
      toast.error("This movie could not be removed.");
    } finally {
      setBusyAction(null);
      setIsRemoveConfirmationOpen(false);
    }
  }

  async function handleDeferAutomation() {
    if (!wantedItem) return;
    setBusyAction("defer");
    try {
      const response = await authedFetch(`/api/movies/${movie.id}/automation/defer`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: wantedItem.libraryId, hours: 24 })
      });
      if (!response.ok) throw new Error("movie-defer-failed");
      toast.success("Deferred for 24 hours. Manual searches still work.");
      revalidator.revalidate();
    } catch {
      toast.error("Background automation could not be deferred.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSkipNextAutomationSearch() {
    if (!wantedItem) return;
    setBusyAction("skip-once");
    try {
      const response = await authedFetch(`/api/movies/${movie.id}/automation/skip-once`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: wantedItem.libraryId })
      });
      if (!response.ok) throw new Error("movie-skip-once-failed");
      toast.success("The next scheduled search will be skipped.");
      revalidator.revalidate();
    } catch {
      toast.error("The next scheduled search could not be skipped.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleReplacementProtection(enabled: boolean) {
    setBusyAction("replacement-protection");
    try {
      const response = await authedFetch(`/api/movies/${movie.id}/replacement-protection`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ preventLowerQualityReplacements: enabled })
      });
      if (!response.ok) throw new Error("replacement-protection-failed");
      revalidator.revalidate();
    } catch {
      toast.error("Replacement protection could not be changed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleMetadataRefresh() {
    setBusyAction("metadata-refresh");
    try {
      const response = await authedFetch(`/api/movies/${movie.id}/metadata/refresh`, { method: "POST" });
      if (!response.ok) throw new Error("movie-metadata-refresh-failed");
      toast.success(`${movie.title} metadata refreshed.`);
      revalidator.revalidate();
    } catch {
      toast.error("This movie's metadata could not be refreshed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSearchNow(mode: "automatic" | "interactive") {
    setBusyAction(`${mode}-search`);

    try {
      const response = await authedFetch(`/api/movies/${movie.id}/search${mode === "interactive" ? "?mode=preview" : ""}`, { method: "POST" });
      if (!response.ok) throw new Error("movie-search-failed");

      const payload = (await response.json()) as {
        outcome?: string;
        summary?: string;
        releaseName?: string | null;
        indexerName?: string | null;
        dispatchStatus?: string | null;
        dispatchMessage?: string | null;
        reason?: string;
        candidates?: SearchPlanCandidate[];
      };
      const best = payload.releaseName ? `${payload.releaseName}${payload.indexerName ? ` via ${payload.indexerName}` : ""}` : null;
      setReleaseCandidates(mode === "interactive" ? payload.candidates ?? [] : []);

      if (mode === "interactive") {
        const found = payload.candidates?.length ?? 0;
        setSection("destination");
        if (found) toast.success(`${found} release${found === 1 ? "" : "s"} scored. Choose one below.`);
        else {
          const explained = describeSearchReason(payload.reason, payload.summary ?? "No releases matched this film's Library Profile.");
          const action = explained.action;
          toast.info(explained.title, {
            description: explained.description,
            action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
          });
        }
      } else {
        if (best) {
          toast.success(`Deluno selected ${best} using this film's Library Profile.`);
        } else {
          const explained = describeSearchReason(payload.reason, "Search finished with no accepted release.");
          const action = explained.action;
          toast.info(explained.title, {
            description: explained.description,
            action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
          });
        }
      }
      revalidator.revalidate();
    } catch {
      toast.error("The search request failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleGrabCandidate(candidate: SearchPlanCandidate, force = false, overrideReason?: string) {
    setBusyAction(force ? "force-grab" : "grab");

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
      if (!response.ok) throw new Error("movie-grab-failed");

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
      toast.success(formatGrabMessage(best, payload));
      setOpenCandidate(null);
      setReleaseCandidates([]);
      revalidator.revalidate();
    } catch {
      toast.error("That release could not be sent to the download client.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleDismissImportCase(id: string) {
    setBusyAction(`import-${id}`);
    try {
      const response = await authedFetch(`/api/movies/import-recovery/${id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error("dismiss-failed");
      toast.success("Import issue dismissed.");
      revalidator.revalidate();
    } catch {
      toast.error("That import issue could not be dismissed.");
    } finally {
      setBusyAction(null);
    }
  }

  return (
    <div className="grid gap-[var(--page-gap)]">
      {/* One toolbar: which part of the film you want, where you came from, and
          the two searches. The topbar names the section, the hero names the film. */}
      <PageToolbar
        left={
          <SegmentedControl<DetailSection>
            aria-label="Section"
            className="w-auto"
            value={section}
            onValueChange={setSection}
            options={[
              { value: "destination", label: "Destination" },
              { value: "history", label: "History" }
            ]}
          />
        }
        actions={
          <>
            <Button asChild type="button" variant="outline">
              <Link to="/movies">
                <ArrowLeft className="h-4 w-4" />
                All movies
              </Link>
            </Button>
            <Button
              type="button"
              variant="outline"
              onClick={() => void handleSearchNow("interactive")}
              disabled={busyAction !== null}
              title="Review every candidate and choose the release yourself."
            >
              {busyAction === "interactive-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Choose a release
            </Button>
            <Button
              type="button"
              onClick={() => void handleSearchNow("automatic")}
              disabled={busyAction !== null}
              title="Deluno applies the active Library Profile and sends the best acceptable release."
            >
              {busyAction === "automatic-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Search now
            </Button>
          </>
        }
      />

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
                {wantedItem ? <Badge variant={wantedItem.wantedStatus === "missing" || wantedItem.wantedStatus === "upgrade" ? "warning" : "info"}>{wantedStatusPresentation(wantedItem.wantedStatus).label}</Badge> : null}
                {importCases.length ? <Badge variant="warning">{importCases.length} import issue{importCases.length === 1 ? "" : "s"}</Badge> : null}
                {movie.genres?.split(",").map((genre) => <span key={genre} className="rounded-full border border-primary/20 bg-primary/10 px-2.5 py-1 text-xs font-medium text-primary">{genre.trim()}</span>)}
              </div>
              <p className="mt-4 max-w-4xl text-sm leading-relaxed text-muted-foreground">
                {movie.overview ?? "No overview has been stored yet. Refresh metadata when you want Deluno to enrich this title."}
              </p>
              {cast.length ? (
                <section className="mt-5 border-t border-white/10 pt-4">
                  <div className="flex items-center justify-between gap-3">
                    <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.18em] text-muted-foreground">Starring</p>
                    <span className="text-[length:var(--type-caption)] text-muted-foreground">{cast.length} credited</span>
                  </div>
                  <div className="mt-3 flex flex-wrap gap-x-5 gap-y-3">
                  {cast.slice(0, 6).map((person) => (
                    <div key={`${person.name}-${person.character ?? ""}`} className="flex min-w-0 items-center gap-2.5">
                      {person.profileUrl ? <img src={person.profileUrl} alt="" className="h-10 w-10 shrink-0 rounded-full border border-white/15 bg-surface-2 object-cover shadow-lg" /> : <div className="h-10 w-10 shrink-0 rounded-full border border-white/15 bg-surface-2" />}
                      <span className="max-w-28 min-w-0 leading-tight"><span className="block truncate text-xs font-semibold text-foreground">{person.name}</span>{person.character ? <span className="mt-0.5 block truncate text-[length:var(--type-caption)] text-muted-foreground">{person.character}</span> : null}</span>
                    </div>
                  ))}
                  </div>
                </section>
              ) : null}
            </div>
            <aside className="w-full self-center rounded-xl border border-white/10 bg-card/80 p-4 backdrop-blur-sm">
              <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.18em] text-muted-foreground">Ratings &amp; IDs</p>
              <p className="mt-1 text-xs text-muted-foreground">The metadata Deluno is using</p>
              <div className="mt-3"><RatingStrip ratings={movie.ratings} fallbackRating={movie.rating} /></div>
              <div className="mt-4 space-y-2 border-t border-hairline pt-4 text-sm">
                <div className="flex items-center justify-between gap-3"><span className="text-muted-foreground">Source</span><span className="font-medium text-foreground">{movie.metadataProvider?.toUpperCase() ?? "Not linked"}</span></div>
                <div className="flex items-center justify-between gap-3"><span className="text-muted-foreground">IMDb</span><span className="font-medium text-foreground">{movie.imdbId ?? "—"}</span></div>
              </div>
              <Button variant="outline" className="mt-4 w-full" onClick={() => void handleMetadataRefresh()} disabled={busyAction !== null}>
                {busyAction === "metadata-refresh" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                Refresh metadata
              </Button>
              <Button variant="outline" className="mt-2 w-full" onClick={() => setIsMetadataOpen(true)}>Edit metadata</Button>
              {/* Destructive, so it sits with the other "manage this title" controls
                  rather than beside the two searches in the toolbar. */}
              <Button
                variant="ghost"
                className="mt-2 w-full text-destructive hover:bg-destructive/10 hover:text-destructive"
                onClick={() => setIsRemoveConfirmationOpen(true)}
                disabled={busyAction !== null}
              >
                <Trash2 className="h-4 w-4" />
                Remove from Deluno
              </Button>
            </aside>
          </div>
        </CardContent>
      </Card>

      <SummaryStrip
        cells={[
          {
            label: "File",
            value: movie.hasFile ? "On disk" : "Missing",
            tone: movie.hasFile ? undefined : "warning",
            help: movie.hasFile ? "imported and verified" : "nothing imported yet"
          },
          { label: "Quality", value: currentQuality ?? "Unknown", help: `plan asks for ${targetQuality}` },
          {
            label: "Cutoff",
            value: cutoffMet === true ? "Met" : cutoffMet === false ? "Below target" : "No data",
            tone: cutoffMet === false ? "warning" : cutoffMet === true ? "success" : undefined,
            // "last delta 0" told the user nothing they could act on (#259).
            // Say what the comparison meant instead of printing its number.
            help: cutoffMet === true
              ? `meets ${targetQuality}`
              : cutoffMet === false
                ? `wants ${targetQuality}`
                : lastDelta === null
                  ? "nothing compared yet"
                  : lastDelta > 0
                    ? "last release scored better"
                    : lastDelta < 0
                      ? "last release scored worse"
                      : "last release scored the same"
          },
          { label: "Monitoring", value: movie.monitored ? "On" : "Paused", help: movie.monitored ? "searched on schedule" : "no automatic searches" },
          {
            label: "Import issues",
            value: importCases.length,
            tone: importCases.length ? "warning" : undefined,
            help: importCases.length ? "need a decision" : "nothing stuck"
          }
        ]}
      />

      {nextStep ? (
        <ListCard title="Next step" count={nextStep.eyebrow}>
          <ListTable chevron={false} columns={[{ label: "What Deluno suggests" }, { label: "Action", width: "auto", align: "end", mobile: true }]}>
            <ListRow>
              <ListNameCell name={nextStep.title} sub={nextStep.description} />
              <div role="cell" className="flex justify-end">
                <Button type="button" size="sm" onClick={nextStep.onAction} disabled={busyAction !== null}>
                  {nextStep.action}
                </Button>
              </div>
            </ListRow>
          </ListTable>
        </ListCard>
      ) : null}

      {section === "destination" ? (
        <>
          {releaseCandidates.length ? (
            <ListCard title="Choose a release" count={`${releaseCandidates.length} scored`}>
              <ListTable
                columns={[
                  { label: "Release" },
                  { label: "Quality", mobile: true },
                  { label: "Size" },
                  { label: "Score", align: "end" },
                  { label: "Decision", width: LIST_TRACK.status }
                ]}
              >
                {releaseCandidates.map((candidate, index) => (
                  <ListRow
                    key={`${candidate.indexerName}:${candidate.releaseName}`}
                    onClick={() => setOpenCandidate(candidate)}
                    selected={openCandidate?.releaseName === candidate.releaseName}
                  >
                    <ListNameCell
                      name={candidate.releaseName}
                      sub={`${index === 0 && candidate.decisionStatus !== "rejected" ? "Best match · " : ""}${candidate.indexerName}`}
                    />
                    <ListCell primary={candidate.quality} mobile />
                    <ListCell primary={candidate.sizeBytes ? formatBytes(candidate.sizeBytes) : "—"} />
                    <ListCell primary={candidate.score} align="end" numeric />
                    <ListCell>
                      <Chip tone={candidateTone(candidate)}>{candidateLabel(candidate)}</Chip>
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            </ListCard>
          ) : null}

          <ListCard title="Routing and destination" count={library?.name ?? "No library linked"}>
            <ListTable chevron={false} columns={[{ label: "Setting" }, { label: "Value", width: "minmax(0,2fr)", mobile: true }]}>
              {([
                // `path: true` marks a machine string, so it renders in the
                // code face like every other path in the app — this table is
                // not a ListCell `mono` site and was missed by that pass (#259).
                { label: "Library", value: library?.name ?? "Not linked" },
                { label: "Root folder", value: library?.rootPath || "No root configured", path: Boolean(library?.rootPath) },
                { label: "Downloads folder", value: library?.downloadsPath || "Download client default", path: Boolean(library?.downloadsPath) },
                { label: "Import workflow", value: library?.importWorkflow === "refine-before-import" ? "Refine before import" : "Standard import" },
                { label: "Current quality", value: currentQuality ?? "Unknown" },
                { label: "Target quality", value: targetQuality }
              ] as Array<{ label: string; value: string; path?: boolean }>).map(({ label, value, path }) => (
                <ListRow key={label}>
                  <ListNameCell name={label} />
                  <ListCell primary={path ? <span className="font-mono text-[length:var(--type-caption)]">{value}</span> : value} mobile />
                </ListRow>
              ))}
            </ListTable>
          </ListCard>

          <ListCard title="Automation" count={movie.monitored ? "Watching this film" : "Paused"}>
            <ListTable chevron={false} columns={[{ label: "Control" }, { label: "Action", width: "auto", align: "end", mobile: true }]}>
              <ListRow>
                <ListNameCell
                  name="Background automation"
                  sub={
                    workflowStatus?.reason ||
                    "Deluno searches for a missing file and quality upgrades on its own schedule."
                  }
                />
                <div role="cell" className="flex justify-end">
                  <Switch
                    aria-label="Background automation"
                    checked={movie.monitored}
                    disabled={busyAction !== null}
                    onCheckedChange={(checked) => void handleMonitoring(checked)}
                  />
                </div>
              </ListRow>
              <ListRow>
                <ListNameCell
                  name="Protect against downgrades"
                  sub="Refuse a replacement that would be lower quality than the file already on disk."
                />
                <div role="cell" className="flex justify-end">
                  <Switch
                    aria-label="Protect against downgrades"
                    checked={workflowStatus?.preventLowerQualityReplacements ?? true}
                    disabled={busyAction !== null || !workflowStatus}
                    onCheckedChange={(checked) => void handleReplacementProtection(checked)}
                  />
                </div>
              </ListRow>
              <ListRow>
                <ListNameCell
                  name="Defer for 24 hours"
                  sub={
                    isBeingSearchedFor
                      ? "Pause scheduled searches for a day. Manual searches still work."
                      : "Nothing to defer — Deluno is not searching for this film."
                  }
                />
                <div role="cell" className="flex justify-end">
                  <Button type="button" size="sm" variant="outline" onClick={() => void handleDeferAutomation()} disabled={busyAction !== null || !isBeingSearchedFor}>
                    {busyAction === "defer" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                    Defer
                  </Button>
                </div>
              </ListRow>
              <ListRow>
                <ListNameCell
                  name="Skip the next search"
                  sub={
                    isBeingSearchedFor
                      ? "Let one scheduled cycle pass without searching this film."
                      : "Nothing to skip — Deluno is not searching for this film."
                  }
                />
                <div role="cell" className="flex justify-end">
                  <Button type="button" size="sm" variant="outline" onClick={() => void handleSkipNextAutomationSearch()} disabled={busyAction !== null || !isBeingSearchedFor}>
                    {busyAction === "skip-once" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                    Skip once
                  </Button>
                </div>
              </ListRow>
            </ListTable>
          </ListCard>

          {origins.length ? (
            <ListCard title="How this film was added" count={`${origins.length} import list${origins.length === 1 ? "" : "s"}`}>
              <ListTable chevron={false} columns={[{ label: "Source" }, { label: "Provider", mobile: true }, { label: "First seen" }]}>
                {origins.map((origin) => (
                  <ListRow key={origin.id}>
                    <ListNameCell name={origin.sourceName} sub="Removing the list never removes this film or its files." />
                    <ListCell primary={origin.provider} mobile />
                    <ListCell primary={formatDateTime(origin.firstSeenUtc)} />
                  </ListRow>
                ))}
              </ListTable>
            </ListCard>
          ) : null}
        </>
      ) : null}

      {section === "history" ? (
        <>
          {importCases.length ? (
            <ListCard title="Import issues" count={`${importCases.length} open`}>
              <ListTable chevron={false} columns={[{ label: "Issue" }, { label: "What to do", width: "minmax(0,1.4fr)" }, { label: "Action", width: "auto", align: "end", mobile: true }]}>
                {importCases.map((item) => (
                  <ListRow key={item.id}>
                    <ListNameCell name={formatFailureKind(item.failureKind)} sub={item.summary} />
                    <ListCell primary={item.recommendedAction} />
                    <div role="cell" className="flex justify-end">
                      <Button type="button" size="sm" variant="outline" onClick={() => void handleDismissImportCase(item.id)} disabled={busyAction === `import-${item.id}`}>
                        {busyAction === `import-${item.id}` ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                        Dismiss
                      </Button>
                    </div>
                  </ListRow>
                ))}
              </ListTable>
            </ListCard>
          ) : null}

          <DecisionExplanationList decisions={decisions} />

          <ListCard title="Searches" count={movieSearches.length ? `Latest ${Math.min(movieSearches.length, 12)} of ${movieSearches.length}` : undefined}>
            {movieSearches.length === 0 ? (
              <ListEmpty
                title="No searches yet"
                description="Manual and scheduled searches for this film appear here with what they scored."
              />
            ) : (
              <ListTable
                columns={[
                  { label: "Release" },
                  { label: "Trigger", mobile: true },
                  { label: "When" },
                  { label: "Outcome", width: LIST_TRACK.status }
                ]}
              >
                {movieSearches.slice(0, 12).map((item) => (
                  <ListRow key={item.id} onClick={() => setOpenSearchId(item.id)} selected={openSearchId === item.id}>
                    <ListNameCell name={item.releaseName ?? "No release selected"} sub={item.indexerName ?? "No source yet"} />
                    <ListCell primary={formatTriggerKind(item.triggerKind)} mobile />
                    <ListCell primary={formatDateTime(item.createdUtc)} />
                    <ListCell>
                      <Chip tone={searchOutcomeTone(item.outcome)}>{formatSearchOutcome(item.outcome)}</Chip>
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            )}
          </ListCard>

          <ListCard
            title="Sent to downloads"
            count={movieDispatches.length ? `${movieDispatches.length} dispatch${movieDispatches.length === 1 ? "" : "es"}` : undefined}
            actions={
              movieDispatches.length ? (
                <Button asChild type="button" size="sm" variant="outline">
                  <Link to="/queue">Open Transfers</Link>
                </Button>
              ) : null
            }
          >
            {movieDispatches.length === 0 ? (
              <ListEmpty
                title="Nothing sent yet"
                description="Releases Deluno hands to a download client are listed here, with what the client said back."
              />
            ) : (
              <ListTable chevron={false} columns={[{ label: "Release" }, { label: "Client", mobile: true }, { label: "When" }, { label: "Status", width: LIST_TRACK.status }]}>
                {movieDispatches.slice(0, 8).map((item) => (
                  <ListRow key={item.id}>
                    <ListNameCell name={item.releaseName} sub={item.indexerName} />
                    <ListCell primary={item.downloadClientName} mobile />
                    <ListCell primary={formatDateTime(item.createdUtc)} />
                    <ListCell>
                      <Chip tone={dispatchTone(item.status)}>{formatDispatchStatus(item.status)}</Chip>
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            )}
          </ListCard>

          <ListCard title="Activity" count={activity.length ? `Latest ${Math.min(activity.length, 10)} of ${activity.length}` : undefined}>
            {activity.length === 0 ? (
              <ListEmpty title="Nothing has happened yet" description="Every event Deluno records against this film shows up here." />
            ) : (
              <ListTable chevron={false} columns={[{ label: "Event" }, { label: "Category", mobile: true }, { label: "When" }]}>
                {activity.slice(0, 10).map((item) => (
                  <ListRow key={item.id}>
                    <ListNameCell name={item.message} />
                    <ListCell primary={item.category} mobile />
                    <ListCell primary={formatDateTime(item.createdUtc)} />
                  </ListRow>
                ))}
              </ListTable>
            )}
          </ListCard>
        </>
      ) : null}

      {/* ------------------------------------------------------------ drawers */}

      <Drawer
        open={openCandidate !== null}
        onOpenChange={(next) => {
          if (!next) {
            setOpenCandidate(null);
            setForceReason(null);
          }
        }}
        title={openCandidate?.releaseName ?? "Release"}
        description={openCandidate ? `${openCandidate.indexerName} · score ${openCandidate.score}` : undefined}
        footer={
          <DrawerFooter
            state={openCandidate?.downloadUrl ? "clean" : "error"}
            message={openCandidate?.downloadUrl ? openCandidate.summary : "This candidate has no downloadable URL yet"}
            saveType="button"
            saveLabel="Send to downloads"
            saveEnabled={Boolean(openCandidate?.downloadUrl) && busyAction === null}
            onSave={() => openCandidate && void handleGrabCandidate(openCandidate, false)}
            onCancel={() => setOpenCandidate(null)}
          />
        }
      >
        {openCandidate ? (
          <>
            <DrawerSection title="What Deluno scored" aside={candidateLabel(openCandidate)}>
              <DrawerFacts
                items={[
                  { label: "Quality", value: openCandidate.quality },
                  { label: "Score", value: String(openCandidate.score) },
                  { label: "Meets cutoff", value: openCandidate.meetsCutoff ? "Yes" : "No" },
                  { label: "Size", value: openCandidate.sizeBytes ? formatBytes(openCandidate.sizeBytes) : "Unknown" },
                  { label: "Seeders", value: openCandidate.seeders ?? "—" },
                  ...(openCandidate.estimatedBitrateMbps ? [{ label: "Estimated bitrate", value: `${openCandidate.estimatedBitrateMbps} Mbps` }] : []),
                  ...(openCandidate.releaseGroup ? [{ label: "Release group", value: openCandidate.releaseGroup }] : [])
                ]}
              />
              <p className="text-[length:var(--type-caption)] leading-snug text-muted-foreground">{openCandidate.summary}</p>
            </DrawerSection>

            {openCandidate.decisionReasons?.length ? (
              <DrawerSection title="Why Deluno likes it">
                <ul className="grid gap-1">
                  {openCandidate.decisionReasons.slice(0, 6).map((reason) => (
                    <li key={reason} className="text-[length:var(--type-body-sm)] text-muted-foreground">
                      {reason}
                    </li>
                  ))}
                </ul>
              </DrawerSection>
            ) : null}

            {openCandidate.riskFlags?.length ? (
              <DrawerSection title="Risks">
                <ul className="grid gap-1">
                  {openCandidate.riskFlags.slice(0, 6).map((risk) => (
                    <li key={risk} className="text-[length:var(--type-body-sm)] text-destructive">
                      {risk}
                    </li>
                  ))}
                </ul>
              </DrawerSection>
            ) : null}

            <DrawerSection>
              <div className="rounded-[10px] border border-warning/30 px-[var(--field-pad-x)] py-2">
                <div className="flex min-h-[52px] items-center justify-between gap-[var(--grid-gap)]">
                  <div className="min-w-0">
                    <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">Send it anyway</p>
                    <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">
                      Overrides the scorer. Your reason is stored in activity and search history.
                    </p>
                  </div>
                  {forceReason === null ? (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      disabled={busyAction !== null || !openCandidate.downloadUrl}
                      onClick={() => setForceReason(openCandidate.summary ?? "")}
                    >
                      Force
                    </Button>
                  ) : null}
                </div>
                {forceReason !== null ? (
                  <div className="mt-2 flex items-center gap-2 pb-1">
                    <Input
                      value={forceReason}
                      onChange={(event) => setForceReason(event.target.value)}
                      aria-label="Why force this release?"
                      placeholder="Why force this release?"
                      autoFocus
                    />
                    <Button type="button" variant="outline" size="sm" onClick={() => setForceReason(null)}>
                      Cancel
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      disabled={busyAction !== null || !forceReason.trim()}
                      onClick={() => {
                        void handleGrabCandidate(openCandidate, true, forceReason.trim());
                        setForceReason(null);
                      }}
                    >
                      {busyAction === "force-grab" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                      Send anyway
                    </Button>
                  </div>
                ) : null}
              </div>
            </DrawerSection>
          </>
        ) : null}
      </Drawer>

      <Drawer
        open={openSearch !== null}
        onOpenChange={(next) => {
          if (!next) setOpenSearchId(null);
        }}
        title={openSearch?.releaseName ?? "Search"}
        description={openSearch ? formatDateTime(openSearch.createdUtc) : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenSearchId(null)} />}
      >
        {openSearch ? (
          <>
            <DrawerSection title="Outcome" aside={formatSearchOutcome(openSearch.outcome)}>
              <DrawerFacts
                items={[
                  { label: "Trigger", value: formatTriggerKind(openSearch.triggerKind) },
                  { label: "Source", value: openSearch.indexerName ?? "No source yet" },
                  { label: "Release", value: openSearch.releaseName ?? "None selected" }
                ]}
              />
            </DrawerSection>

            {parseSearchCandidates(openSearch.detailsJson).length ? (
              <DrawerSection title="Release scoring" aside={`${parseSearchCandidates(openSearch.detailsJson).length} scored`}>
                <DrawerFacts
                  items={parseSearchCandidates(openSearch.detailsJson)
                    .slice(0, 6)
                    .map((candidate) => ({
                      label: candidate.releaseName,
                      value: `${candidate.quality} · ${candidate.score}`
                    }))}
                />
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>

      <MediaMetadataDrawer
        open={isMetadataOpen}
        onOpenChange={setIsMetadataOpen}
        endpointBase={`/api/movies/${movie.id}`}
        mediaType="movies"
        mediaLabel="movie"
        title={movie.title}
        year={movie.releaseYear}
        provider={movie.metadataProvider}
        providerId={movie.metadataProviderId}
        posterUrl={movie.posterUrl}
        externalUrl={movie.externalUrl}
        value={{
          originalTitle: movie.originalTitle ?? "",
          overview: movie.overview ?? "",
          posterUrl: movie.posterUrl ?? "",
          backdropUrl: movie.backdropUrl ?? "",
          rating: movie.rating !== null && movie.rating !== undefined ? String(movie.rating) : "",
          genres: movie.genres ?? "",
          externalUrl: movie.externalUrl ?? "",
          imdbId: movie.imdbId ?? ""
        }}
        onChanged={() => revalidator.revalidate()}
      />

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

/* -------------------------------------------------------------- helpers */

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
  releaseGroup?: string | null;
  estimatedBitrateMbps?: number | null;
}

function candidateLabel(candidate: SearchPlanCandidate) {
  if (candidate.decisionStatus === "rejected") return "Rejected";
  if (["preferred", "eligible"].includes(candidate.decisionStatus || "") && candidate.meetsCutoff) return "Recommended";
  return "Needs review";
}

function candidateTone(candidate: SearchPlanCandidate): "ok" | "warn" | "bad" {
  if (candidate.decisionStatus === "rejected") return "bad";
  if (["preferred", "eligible"].includes(candidate.decisionStatus || "") && candidate.meetsCutoff) return "ok";
  return "warn";
}

function parseSearchCandidates(detailsJson: string | null): SearchPlanCandidate[] {
  if (!detailsJson) return [];

  try {
    const parsed = JSON.parse(detailsJson) as { candidates?: unknown };
    if (!Array.isArray(parsed.candidates)) return [];
    return parsed.candidates
      .filter((item): item is Record<string, unknown> => typeof item === "object" && item !== null)
      .map((item) => normalizeSearchCandidate(item));
  } catch {
    return [];
  }
}

function normalizeSearchCandidate(value: Record<string, unknown>): SearchPlanCandidate {
  return {
    releaseName: String(value.releaseName ?? value.ReleaseName ?? "Unknown release"),
    indexerId: (value.indexerId ?? value.IndexerId ?? null) as string | null,
    indexerName: String(value.indexerName ?? value.IndexerName ?? "Unknown source"),
    quality: String(value.quality ?? value.Quality ?? "Unknown"),
    score: Number(value.score ?? value.Score ?? 0),
    meetsCutoff: Boolean(value.meetsCutoff ?? value.MeetsCutoff ?? false),
    summary: String(value.summary ?? value.Summary ?? ""),
    downloadUrl: (value.downloadUrl ?? value.DownloadUrl ?? null) as string | null,
    sizeBytes: (value.sizeBytes ?? value.SizeBytes ?? null) as number | null,
    seeders: (value.seeders ?? value.Seeders ?? null) as number | null,
    decisionStatus: (value.decisionStatus ?? value.DecisionStatus) as string | undefined,
    decisionReasons: normalizeStringArray(value.decisionReasons ?? value.DecisionReasons),
    riskFlags: normalizeStringArray(value.riskFlags ?? value.RiskFlags),
    releaseGroup: (value.releaseGroup ?? value.ReleaseGroup ?? null) as string | null,
    estimatedBitrateMbps: (value.estimatedBitrateMbps ?? value.EstimatedBitrateMbps ?? null) as number | null
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

function formatGrabMessage(
  best: string,
  payload: { forceOverride?: boolean; dispatchStatus?: string; dispatchMessage?: string }
) {
  const prefix = payload.forceOverride ? "Forced" : "Sent";
  switch (payload.dispatchStatus) {
    case "sent":
      return `${prefix} ${best} to the download client.`;
    case "planned":
      return `Matched ${best}, but no downloadable URL was available yet.`;
    case "failed":
      return `Matched ${best}, but the download client rejected it${payload.dispatchMessage ? `: ${payload.dispatchMessage}` : "."}`;
    default:
      return `Matched ${best}.`;
  }
}

function dispatchTone(status: string): "ok" | "warn" | "bad" | "info" {
  switch (status) {
    case "sent":
      return "ok";
    case "failed":
      return "bad";
    case "planned":
      return "warn";
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

function searchOutcomeTone(outcome: string): "ok" | "warn" | "bad" | "muted" {
  switch (outcome) {
    case "matched":
      return "ok";
    case "error":
      return "bad";
    case "blocked":
      return "warn";
    default:
      return "muted";
  }
}

function formatSearchOutcome(outcome: string) {
  switch (outcome) {
    case "matched":
      return "Matched";
    case "no_match":
      return "No match";
    case "error":
      return "Error";
    case "skipped":
      return "Skipped";
    case "pending":
      return "Pending";
    case "blocked":
      return "Blocked";
    default:
      return outcome.charAt(0).toUpperCase() + outcome.slice(1).replace(/[-_]/g, " ");
  }
}

function formatTriggerKind(value: string) {
  switch (value) {
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
