/**
 * TV show detail — hero → toolbar sections → list cards → drawers.
 *
 * A detail page keeps its `h1`: the topbar names the section ("TV Shows"), not
 * the title, and the artwork is content rather than chrome. Everything below the
 * hero obeys the list → drawer grammar the rest of the app uses.
 *
 * Contracts: GET /api/series/{id}, /inventory, /removal-preview; PUT
 * /api/series/monitoring, /api/series/episodes/monitoring; POST
 * /api/series/{id}/search, /grab, /episodes/search, /seasons/{n}/search,
 * /automation/defer, /automation/skip-once, /api/series/bulk.
 */
import { Fragment, useMemo, useState } from "react";
import { Link, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { ArrowLeft, LoaderCircle, Search, Trash2 } from "lucide-react";
import {
  fetchJson,
  type ActivityEventItem,
  type DecisionExplanationItem,
  type DownloadDispatchItem,
  type LibraryItem,
  type MetadataCastMember,
  type IntakeTitleOriginItem,
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
import { Card, CardContent } from "../components/ui/card";
import { RemoveMediaDialog, type MediaRemovalPreview, type RemoveMediaOptions } from "../components/app/remove-media-dialog";
import { DecisionExplanationList } from "../components/app/decision-explanation-list";
import { MediaMetadataDrawer } from "../components/app/media-metadata-drawer";
import { RatingStrip } from "../components/app/rating-strip";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { ListGroupHeader } from "../components/ui/media-type-split";
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
import { RouteSkeleton } from "../components/shell/skeleton";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { Switch } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";

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

type DetailSection = "episodes" | "destination" | "history";
type EpisodeFilter = "all" | "missing" | "upgrade" | "monitored" | "imported";

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

  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [isRemoveConfirmationOpen, setIsRemoveConfirmationOpen] = useState(false);
  const [isMetadataOpen, setIsMetadataOpen] = useState(false);
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);
  const [openCandidate, setOpenCandidate] = useState<SearchPlanCandidate | null>(null);
  const [openEpisodeId, setOpenEpisodeId] = useState<string | null>(null);
  const [openSearchId, setOpenSearchId] = useState<string | null>(null);
  const [episodeFilter, setEpisodeFilter] = useState<EpisodeFilter>("all");
  const [query, setQuery] = useState("");
  const [section, setSection] = useState<DetailSection>("episodes");

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
  const missingCount = inventory.episodes.filter((item) => item.wantedStatus === "missing" || !item.hasFile).length;
  const upgradeCount = inventory.episodes.filter((item) => item.wantedStatus === "upgrade").length;
  const monitoredCount = inventory.episodes.filter((item) => item.monitored).length;
  const openEpisode = inventory.episodes.find((item) => item.episodeId === openEpisodeId) ?? null;
  const openSearch = seriesSearches.find((item) => item.id === openSearchId) ?? null;

  const nextStep = importCases.length
    ? {
        eyebrow: "Needs attention",
        title: `Review ${importCases.length} import issue${importCases.length === 1 ? "" : "s"}`,
        description: "Something Deluno brought in could not be filed. It needs a decision before this show is settled.",
        action: "Open import issues",
        onAction: () => setSection("history")
      }
    : releaseCandidates.length
      ? {
          eyebrow: "Release ready",
          title: "Choose a release",
          description: "Deluno scored the candidates it found. Pick the one to send to your download client.",
          action: "Review candidates",
          onAction: () => setSection("episodes")
        }
      : !series.monitored
        ? {
            eyebrow: "Monitoring paused",
            title: "Resume automatic care",
            description: "This show is not being watched for missing episodes or quality improvements.",
            action: "Resume automation",
            onAction: () => void handleSeriesMonitoring(true)
          }
        : missingCount > 0
          ? {
              eyebrow: "Episodes missing",
              title: `Find ${missingCount} missing episode${missingCount === 1 ? "" : "s"}`,
              description: "Deluno can search every indexer you have connected using this show's media plan.",
              action: "Search now",
              onAction: () => void handleSearchNow("automatic")
            }
          : null;

  async function handleEpisodeMonitoring(episodeIds: string[], monitored: boolean) {
    if (!episodeIds.length) return;
    setBusyAction(`episode-monitor:${episodeIds[0]}`);

    try {
      const response = await authedFetch("/api/series/episodes/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds, monitored })
      });
      if (!response.ok) throw new Error("episode-monitoring-failed");
      revalidator.revalidate();
    } catch {
      toast.error("Those episodes could not be updated.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSeriesMonitoring(monitored: boolean) {
    setBusyAction("series-monitor");

    try {
      const response = await authedFetch("/api/series/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seriesIds: [series.id], monitored })
      });
      if (!response.ok) throw new Error("series-monitoring-failed");
      revalidator.revalidate();
    } catch {
      toast.error("This show's monitoring could not be changed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleRemoveFromDeluno(options: RemoveMediaOptions) {
    setBusyAction("remove");
    try {
      const response = await authedFetch("/api/series/bulk", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seriesIds: [series.id], operation: "remove", ...options })
      });
      if (!response.ok) throw new Error("series-remove-failed");

      const result = (await response.json()) as { successCount?: number };
      if ((result.successCount ?? 0) !== 1) throw new Error("series-remove-failed");
      toast.success(`${series.title} removed from Deluno`);
      navigate("/tv", { replace: true });
    } catch {
      toast.error("This TV show could not be removed.");
    } finally {
      setBusyAction(null);
      setIsRemoveConfirmationOpen(false);
    }
  }

  async function handleDeferAutomation() {
    if (!wantedItem) return;
    setBusyAction("defer");
    try {
      const response = await authedFetch(`/api/series/${series.id}/automation/defer`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: wantedItem.libraryId, hours: 24 })
      });
      if (!response.ok) throw new Error("series-defer-failed");
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
      const response = await authedFetch(`/api/series/${series.id}/automation/skip-once`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: wantedItem.libraryId })
      });
      if (!response.ok) throw new Error("series-skip-once-failed");
      toast.success("The next scheduled search will be skipped.");
      revalidator.revalidate();
    } catch {
      toast.error("The next scheduled search could not be skipped.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSearchNow(mode: "automatic" | "interactive") {
    setBusyAction(`${mode}-search`);

    try {
      const response = await authedFetch(`/api/series/${series.id}/search${mode === "interactive" ? "?mode=preview" : ""}`, { method: "POST" });
      if (!response.ok) throw new Error("series-search-failed");

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

      if (mode === "interactive") {
        const found = payload.candidates?.length ?? 0;
        setSection("episodes");
        if (found) toast.success(`${found} release${found === 1 ? "" : "s"} scored. Choose one below.`);
        else toast.info(payload.summary ?? "No releases matched this show's media plan.");
      } else {
        toast.success(best ? `Deluno selected ${best} using this show's media plan.` : "Search finished with no accepted release.");
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
      if (!response.ok) throw new Error("series-grab-failed");

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

  async function handleEpisodeSearch(episodeIds: string[]) {
    if (!episodeIds.length) return;
    setBusyAction("episode-search");

    try {
      const response = await authedFetch(`/api/series/${series.id}/episodes/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds })
      });
      if (!response.ok) throw new Error("episode-search-failed");

      const payload = (await response.json()) as {
        searchedEpisodes?: number;
        matchedCount?: number;
        sentCount?: number;
        plannedCount?: number;
        failedCount?: number;
      };
      const searched = payload.searchedEpisodes ?? episodeIds.length;
      const matched = payload.matchedCount ?? 0;
      toast.success(
        matched > 0
          ? `Searched ${searched} episode${searched === 1 ? "" : "s"}, matched ${matched}. ${formatDispatchSummary(payload)}`
          : `Searched ${searched} episode${searched === 1 ? "" : "s"}. Nothing matched yet.`
      );
      revalidator.revalidate();
    } catch {
      toast.error("The episode search failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSeasonSearch(seasonNumber: number) {
    setBusyAction(`season-search-${seasonNumber}`);

    try {
      const response = await authedFetch(`/api/series/${series.id}/seasons/${seasonNumber}/search`, { method: "POST" });
      if (!response.ok) throw new Error("season-search-failed");

      const payload = (await response.json()) as {
        matchedCount?: number;
        seasonNumber?: number;
        dispatchStatus?: string | null;
        dispatchMessage?: string | null;
      };
      const resolved = payload.seasonNumber ?? seasonNumber;
      const matched = payload.matchedCount ?? 0;
      toast.success(
        matched > 0
          ? `${formatSeasonLabel(resolved)}: ${matched} episode match${matched === 1 ? "" : "es"}. ${formatDispatchSummary(payload)}`
          : `${formatSeasonLabel(resolved)}: search finished with no matches.`
      );
      revalidator.revalidate();
    } catch {
      toast.error("The season search failed.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleDismissImportCase(id: string) {
    setBusyAction(`import-${id}`);
    try {
      const response = await authedFetch(`/api/series/import-recovery/${id}`, { method: "DELETE" });
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
      {/* One toolbar: which part of the show you want, where you came from, and
          the two searches. The topbar names the section, the hero names the show. */}
      <PageToolbar
        left={
          <SegmentedControl<DetailSection>
            aria-label="Section"
            className="w-auto"
            value={section}
            onValueChange={setSection}
            options={[
              { value: "episodes", label: "Episodes" },
              { value: "destination", label: "Destination" },
              { value: "history", label: "History" }
            ]}
          />
        }
        actions={
          <>
            <Button asChild type="button" variant="outline">
              <Link to="/tv">
                <ArrowLeft className="h-4 w-4" />
                All TV
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
              title="Deluno applies the active media plan and sends the best acceptable release."
            >
              {busyAction === "automatic-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Search now
            </Button>
          </>
        }
      />

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
              <Button variant="outline" className="mt-4 w-full" onClick={() => setIsMetadataOpen(true)}>Edit metadata</Button>
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

      {nextStep ? (
        <ListCard title="Next step" count={nextStep.eyebrow}>
          <ListTable
            chevron={false}
            columns={[{ label: "What Deluno suggests" }, { label: "Action", width: "auto", align: "end", mobile: true }]}
          >
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

      {section === "episodes" ? (
        <>
          <SummaryStrip
            cells={[
              { label: "Episodes", value: inventory.episodeCount, help: `${inventory.seasonCount} season${inventory.seasonCount === 1 ? "" : "s"}` },
              { label: "On disk", value: inventory.importedEpisodeCount, help: "imported and verified" },
              { label: "Missing", value: missingCount, tone: missingCount > 0 ? "warning" : undefined, help: "no file yet" },
              { label: "Upgrades", value: upgradeCount, tone: upgradeCount > 0 ? "warning" : undefined, help: "better release wanted" },
              { label: "Monitored", value: monitoredCount, help: `of ${inventory.episodeCount} watched` }
            ]}
          />

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

          <ListCard
            title="Episodes"
            count={`${visibleEpisodes.length} of ${inventory.episodeCount} shown`}
            filter={{ value: query, onChange: setQuery, placeholder: "Filter by code or title" }}
            actions={
              <>
                <Select
                  aria-label="Filter episodes"
                  className="h-[var(--control-height-sm)] w-40 py-0 text-[length:var(--type-caption)]"
                  value={episodeFilter}
                  onChange={(event) => setEpisodeFilter(event.target.value as EpisodeFilter)}
                  options={[
                    { value: "all", label: "All episodes" },
                    { value: "missing", label: "Missing" },
                    { value: "upgrade", label: "Upgrade" },
                    { value: "monitored", label: "Monitored" },
                    { value: "imported", label: "On disk" }
                  ]}
                />
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={busyAction !== null || !visibleEpisodes.length}
                  onClick={() => void handleEpisodeSearch(visibleEpisodes.map((item) => item.episodeId))}
                >
                  {busyAction === "episode-search" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
                  Search these
                </Button>
              </>
            }
          >
            {visibleSeasons.length === 0 ? (
              <ListEmpty
                title={inventory.episodeCount ? "No episodes match" : "No episodes tracked yet"}
                description={
                  inventory.episodeCount
                    ? "Try a different filter — missing, upgrade, monitored, or on disk."
                    : "Episodes appear once Deluno has scanned your library or pulled the season list from the metadata provider."
                }
              />
            ) : (
              <ListTable
                columns={[
                  { label: "Episode" },
                  { label: "Aired" },
                  { label: "File" },
                  { label: "Last search" },
                  { label: "Status", width: LIST_TRACK.status },
                  { label: "On", width: LIST_TRACK.toggle, mobile: true }
                ]}
              >
                {visibleSeasons.map((season) => (
                  <Fragment key={season.seasonNumber}>
                    <ListGroupHeader
                      label={formatSeasonLabel(season.seasonNumber)}
                      detail={`${season.episodes.length} episodes · ${season.importedCount} on disk · ${season.missingCount} missing`}
                      actions={
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          onClick={() => void handleSeasonSearch(season.seasonNumber)}
                          disabled={busyAction !== null}
                        >
                          {busyAction === `season-search-${season.seasonNumber}` ? (
                            <LoaderCircle className="h-3.5 w-3.5 animate-spin" />
                          ) : (
                            <Search className="h-3.5 w-3.5" />
                          )}
                          Search season
                        </Button>
                      }
                    />
                    {season.episodes.map((episode) => (
                      <ListRow
                        key={episode.episodeId}
                        onClick={() => setOpenEpisodeId(episode.episodeId)}
                        selected={openEpisodeId === episode.episodeId}
                      >
                        <ListNameCell name={formatEpisodeCode(episode)} sub={episode.title ?? "Episode title pending"} />
                        <ListCell primary={episode.airDateUtc ? formatDate(episode.airDateUtc) : "—"} />
                        <ListCell primary={episode.hasFile ? "On disk" : "Not imported"} />
                        <ListCell primary={episode.lastSearchUtc ? formatDateTime(episode.lastSearchUtc) : "Never"} />
                        <ListCell>
                          <Chip tone={wantedTone(episode.wantedStatus)}>{formatWantedStatus(episode.wantedStatus)}</Chip>
                        </ListCell>
                        <div role="cell" className="flex justify-start">
                          <Switch
                            aria-label={`Monitor ${formatEpisodeCode(episode)}`}
                            checked={episode.monitored}
                            disabled={busyAction !== null}
                            onCheckedChange={(checked) => void handleEpisodeMonitoring([episode.episodeId], checked)}
                          />
                        </div>
                      </ListRow>
                    ))}
                  </Fragment>
                ))}
              </ListTable>
            )}
          </ListCard>
        </>
      ) : null}

      {section === "destination" ? (
        <>
          <ListCard title="Routing and destination" count={library?.name ?? "No library linked"}>
            <ListTable chevron={false} columns={[{ label: "Setting" }, { label: "Value", width: "minmax(0,2fr)", mobile: true }]}>
              {[
                ["Library", library?.name ?? "Not linked"],
                ["Root folder", library?.rootPath || "No root configured"],
                ["Downloads folder", library?.downloadsPath || "Download client default"],
                ["Import workflow", library?.importWorkflow === "refine-before-import" ? "Refine before import" : "Standard import"],
                ["Current quality", wantedItem?.currentQuality ?? "Unknown"],
                ["Target quality", wantedItem?.targetQuality ?? "WEB 1080p"]
              ].map(([label, value]) => (
                <ListRow key={label}>
                  <ListNameCell name={label} />
                  <ListCell primary={value} mobile />
                </ListRow>
              ))}
            </ListTable>
          </ListCard>

          <ListCard title="Automation" count={series.monitored ? "Watching this show" : "Paused"}>
            <ListTable chevron={false} columns={[{ label: "Control" }, { label: "Action", width: "auto", align: "end", mobile: true }]}>
              <ListRow>
                <ListNameCell
                  name="Background automation"
                  sub="Deluno searches for missing episodes and quality upgrades on its own schedule."
                />
                <div role="cell" className="flex justify-end">
                  <Switch
                    aria-label="Background automation"
                    checked={series.monitored}
                    disabled={busyAction !== null}
                    onCheckedChange={(checked) => void handleSeriesMonitoring(checked)}
                  />
                </div>
              </ListRow>
              <ListRow>
                <ListNameCell
                  name="Defer for 24 hours"
                  sub={
                    wantedItem
                      ? "Pause scheduled searches for a day. Manual searches still work."
                      : "Available once this show belongs to a library."
                  }
                />
                <div role="cell" className="flex justify-end">
                  <Button type="button" size="sm" variant="outline" onClick={() => void handleDeferAutomation()} disabled={busyAction !== null || !wantedItem}>
                    {busyAction === "defer" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                    Defer
                  </Button>
                </div>
              </ListRow>
              <ListRow>
                <ListNameCell
                  name="Skip the next search"
                  sub={
                    wantedItem
                      ? "Let one scheduled cycle pass without searching this show."
                      : "Available once this show belongs to a library."
                  }
                />
                <div role="cell" className="flex justify-end">
                  <Button type="button" size="sm" variant="outline" onClick={() => void handleSkipNextAutomationSearch()} disabled={busyAction !== null || !wantedItem}>
                    {busyAction === "skip-once" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                    Skip once
                  </Button>
                </div>
              </ListRow>
            </ListTable>
          </ListCard>

          {origins.length ? (
            <ListCard title="How this show was added" count={`${origins.length} import list${origins.length === 1 ? "" : "s"}`}>
              <ListTable chevron={false} columns={[{ label: "Source" }, { label: "Provider", mobile: true }, { label: "First seen" }]}>
                {origins.map((origin) => (
                  <ListRow key={origin.id}>
                    <ListNameCell name={origin.sourceName} sub="Removing the list never removes this show or its files." />
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

          <ListCard title="Searches" count={seriesSearches.length ? `Latest ${Math.min(seriesSearches.length, 12)} of ${seriesSearches.length}` : undefined}>
            {seriesSearches.length === 0 ? (
              <ListEmpty
                title="No searches yet"
                description="Manual and scheduled searches for this show — series, season and episode alike — appear here with what they scored."
              />
            ) : (
              <ListTable
                columns={[
                  { label: "Release" },
                  { label: "Scope", mobile: true },
                  { label: "Trigger" },
                  { label: "When" },
                  { label: "Outcome", width: LIST_TRACK.status }
                ]}
              >
                {seriesSearches.slice(0, 12).map((item) => (
                  <ListRow key={item.id} onClick={() => setOpenSearchId(item.id)} selected={openSearchId === item.id}>
                    <ListNameCell name={item.releaseName ?? "No release selected"} sub={item.indexerName ?? "No source yet"} />
                    <ListCell primary={formatSearchScope(item)} mobile />
                    <ListCell primary={formatTriggerKind(item.triggerKind)} />
                    <ListCell primary={formatDateTime(item.createdUtc)} />
                    <ListCell>
                      <Chip tone={searchOutcomeTone(item.outcome)}>
                        {formatSearchOutcome(item.outcome)}
                      </Chip>
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            )}
          </ListCard>

          <ListCard
            title="Sent to downloads"
            count={seriesDispatches.length ? `${seriesDispatches.length} dispatch${seriesDispatches.length === 1 ? "" : "es"}` : undefined}
            actions={
              seriesDispatches.length ? (
                <Button asChild type="button" size="sm" variant="outline">
                  <Link to="/queue">Open Transfers</Link>
                </Button>
              ) : null
            }
          >
            {seriesDispatches.length === 0 ? (
              <ListEmpty
                title="Nothing sent yet"
                description="Releases Deluno hands to a download client are listed here, with what the client said back."
              />
            ) : (
              <ListTable chevron={false} columns={[{ label: "Release" }, { label: "Client", mobile: true }, { label: "When" }, { label: "Status", width: LIST_TRACK.status }]}>
                {seriesDispatches.slice(0, 8).map((item) => (
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
              <ListEmpty title="Nothing has happened yet" description="Every event Deluno records against this show shows up here." />
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
        open={openEpisode !== null}
        onOpenChange={(next) => {
          if (!next) setOpenEpisodeId(null);
        }}
        title={openEpisode ? `${formatEpisodeCode(openEpisode)} · ${openEpisode.title ?? "Episode title pending"}` : "Episode"}
        description={openEpisode ? formatSeasonLabel(openEpisode.seasonNumber) : undefined}
        footer={
          <DrawerFooter
            state="clean"
            message={openEpisode?.wantedReason}
            readOnly
            saveLabel="Close"
            onCancel={() => setOpenEpisodeId(null)}
          />
        }
      >
        {openEpisode ? (
          <>
            <DrawerSection title="Basics">
              <DrawerFacts
                items={[
                  { label: "Season", value: formatSeasonLabel(openEpisode.seasonNumber) },
                  { label: "Episode", value: `#${openEpisode.episodeNumber}` },
                  { label: "First aired", value: openEpisode.airDateUtc ? formatDate(openEpisode.airDateUtc) : "Not announced" },
                  { label: "File", value: openEpisode.hasFile ? "On disk" : "Not imported" }
                ]}
              />
            </DrawerSection>

            <DrawerSection title="Automation" aside={formatWantedStatus(openEpisode.wantedStatus)}>
              <div className="flex min-h-[var(--control-height)] items-center justify-between gap-[var(--grid-gap)]">
                <span className="min-w-0">
                  <span className="block text-[length:var(--type-body-sm)] font-medium text-foreground">Monitor this episode</span>
                  <span className="mt-0.5 block text-[length:var(--type-caption)] text-muted-foreground">{openEpisode.wantedReason}</span>
                </span>
                <Switch
                  aria-label="Monitor this episode"
                  checked={openEpisode.monitored}
                  disabled={busyAction !== null}
                  onCheckedChange={(checked) => void handleEpisodeMonitoring([openEpisode.episodeId], checked)}
                />
              </div>
              <DrawerFacts
                items={[
                  { label: "Quality cutoff met", value: openEpisode.qualityCutoffMet ? "Yes" : "No" },
                  { label: "Last searched", value: openEpisode.lastSearchUtc ? formatDateTime(openEpisode.lastSearchUtc) : "Never" },
                  {
                    label: "Next eligible search",
                    value: openEpisode.nextEligibleSearchUtc ? formatDateTime(openEpisode.nextEligibleSearchUtc) : "As soon as a cycle runs"
                  }
                ]}
              />
            </DrawerSection>

            <DrawerSection title="Search">
              <div>
              <Button
                type="button"
                variant="outline"
                onClick={() => void handleEpisodeSearch([openEpisode.episodeId])}
                disabled={busyAction !== null}
              >
                {busyAction === "episode-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                Search for this episode
              </Button>
              </div>
            </DrawerSection>
          </>
        ) : null}
      </Drawer>

      <Drawer
        open={openCandidate !== null}
        onOpenChange={(next) => {
          if (!next) setOpenCandidate(null);
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
              <div className="flex min-h-[52px] items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border border-warning/30 px-[var(--field-pad-x)] py-2">
                <div className="min-w-0">
                  <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">Send it anyway</p>
                  <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">
                    Overrides the scorer. Your reason is stored in activity and search history.
                  </p>
                </div>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={busyAction !== null || !openCandidate.downloadUrl}
                  onClick={() => {
                    const reason = window.prompt("Why force this release?", openCandidate.summary);
                    if (reason !== null && reason.trim()) void handleGrabCandidate(openCandidate, true, reason.trim());
                  }}
                >
                  {busyAction === "force-grab" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                  Force
                </Button>
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
        description={openSearch ? `${formatSearchScope(openSearch)} · ${formatDateTime(openSearch.createdUtc)}` : undefined}
        footer={
          <DrawerFooter
            state="clean"
            readOnly
            saveLabel="Close"
            onCancel={() => setOpenSearchId(null)}
          />
        }
      >
        {openSearch ? (
          <>
            <DrawerSection title="Outcome" aside={formatSearchOutcome(openSearch.outcome)}>
              <DrawerFacts
                items={[
                  { label: "Trigger", value: formatTriggerKind(openSearch.triggerKind) },
                  { label: "Scope", value: formatSearchScope(openSearch) },
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
        endpointBase={`/api/series/${series.id}`}
        mediaType="tv"
        mediaLabel="series"
        title={series.title}
        year={series.startYear}
        provider={series.metadataProvider}
        providerId={series.metadataProviderId}
        posterUrl={series.posterUrl}
        externalUrl={series.externalUrl}
        value={{
          originalTitle: series.originalTitle ?? "",
          overview: series.overview ?? "",
          posterUrl: series.posterUrl ?? "",
          backdropUrl: series.backdropUrl ?? "",
          rating: series.rating !== null && series.rating !== undefined ? String(series.rating) : "",
          genres: series.genres ?? "",
          externalUrl: series.externalUrl ?? "",
          imdbId: series.imdbId ?? ""
        }}
        onChanged={() => revalidator.revalidate()}
      />

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

/* -------------------------------------------------------------- helpers */

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

function matchesEpisodeFilter(episode: SeriesEpisodeInventoryItem, filter: EpisodeFilter, query: string) {
  const haystack = `${formatEpisodeCode(episode)} ${episode.title ?? ""}`.toLowerCase();
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
  const seasons = new Map<number, SeriesEpisodeInventoryItem[]>();

  for (const episode of episodes) {
    const current = seasons.get(episode.seasonNumber) ?? [];
    current.push(episode);
    seasons.set(episode.seasonNumber, current);
  }

  return [...seasons.entries()]
    .sort(([left], [right]) => left - right)
    .map(([seasonNumber, seasonEpisodes]) => {
      const sorted = [...seasonEpisodes].sort((left, right) => left.episodeNumber - right.episodeNumber);
      return {
        seasonNumber,
        episodes: sorted,
        importedCount: sorted.filter((item) => item.hasFile).length,
        missingCount: sorted.filter((item) => item.wantedStatus === "missing" || !item.hasFile).length,
        monitoredCount: sorted.filter((item) => item.monitored).length
      };
    });
}

function formatEpisodeCode(episode: SeriesEpisodeInventoryItem) {
  return `S${String(episode.seasonNumber).padStart(2, "0")}E${String(episode.episodeNumber).padStart(2, "0")}`;
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

function wantedTone(value: string): "ok" | "warn" | "info" | "muted" {
  switch (value) {
    case "covered":
      return "ok";
    case "missing":
      return "warn";
    case "upgrade":
      return "info";
    default:
      return "muted";
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

    return parts.length ? `Dispatch: ${parts.join(", ")}.` : "Nothing was dispatched.";
  }

  switch (payload.dispatchStatus) {
    case "sent":
      return "Sent to the download client.";
    case "planned":
      return "Matched, but no downloadable URL was available yet.";
    case "failed":
      return `The download client rejected it${payload.dispatchMessage ? `: ${payload.dispatchMessage}` : "."}`;
    default:
      return "Dispatch recorded.";
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

function formatSearchScope(item: SeriesSearchHistoryItem) {
  if (item.seasonNumber !== null && item.episodeNumber !== null) {
    return `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`;
  }
  if (item.seasonNumber !== null) return formatSeasonLabel(item.seasonNumber);
  return "Whole show";
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

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", year: "numeric" }).format(new Date(value));
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
