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
import type { Tone } from "../lib/status-tones";
import { Fragment, useMemo, useState } from "react";
import { Link, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { ArrowLeft, LoaderCircle, RefreshCw, Search, Trash2, ShieldCheck, ShieldOff
} from "lucide-react";
import {
  fetchJson, fetchPageItems,
  type ActivityEventItem,
  type DecisionExplanationItem,
  type DownloadDispatchItem,
  type IntegrationFailure,
  type LibraryItem,
  type IntakeTitleOriginItem,
  type SeriesEpisodeInventoryItem,
  type SeriesImportRecoverySummary,
  type SeriesInventoryDetail,
  type SeriesListItem,
  type MetadataProviderIssue,
  type SeriesSearchHistoryItem
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";
import { isEpisodeMissing, isEpisodeUpcoming, summariseEpisodes } from "../lib/episode-progress";
import { describeSearchReason, describeRequestFailure, formatSearchFailureNotice } from "../lib/search-reasons";
import { candidateLabel, candidateTone, canWinSearch, isTypedCandidate, likesCandidate } from "../lib/release-candidate-status";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent } from "../components/ui/card";
import { RemoveMediaDialog, type MediaRemovalPreview, type RemoveMediaOptions } from "../components/app/remove-media-dialog";
import { CreditsRow, readStoredCredits } from "../components/app/credits-row";
import { DownloadDispatchDrawer } from "../components/app/download-dispatch-drawer";
import { TitleTagsEditor } from "../components/app/title-tags-editor";
import { DecisionExplanationList } from "../components/app/decision-explanation-list";
import { MediaMetadataDrawer } from "../components/app/media-metadata-drawer";
import { HeroBackdrop } from "../components/app/hero-backdrop";
import { MetadataProviderIssueNotice } from "../components/app/metadata-provider-issue-notice";
import { SourceMark } from "../components/app/source-mark";
import { RatingStrip } from "../components/app/rating-strip";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { Input } from "../components/ui/input";
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
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { Switch } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { wantedStatusPresentation } from "../lib/status-tones";
import { TitleMarkLabel } from "../components/ui/title-mark";
import { formatDateTime as formatPreferenceDateTime, formatRuntime, formatShortDate, useDisplayPreferences } from "../lib/display-preferences";

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
  metadataIssue: MetadataProviderIssue | null;
}

type DetailSection = "episodes" | "destination" | "history";
type EpisodeFilter = "all" | "missing" | "upgrade" | "monitored" | "imported";

interface NumberingDraft {
  seriesType: string;
  numberingScheme: string;
  numberingSource: string;
  mappings: Record<string, {
    absoluteNumber: string;
    sceneSeasonNumber: string;
    sceneEpisodeNumber: string;
    airDate: string;
  }>;
}

export async function showDetailLoader({
  params
}: {
  params: { id?: string };
}): Promise<ShowDetailLoaderData> {
  const id = params.id!;
  const [series, metadataIssue, searchHistory, dispatches, importRecovery, inventory, activity, decisions, libraries, origins, removalPreview] =
    await Promise.all([
      fetchJson<SeriesListItem>(`/api/series/${id}`),
      fetchJson<MetadataProviderIssue | null>(`/api/series/${id}/metadata/issue`).catch(() => null),
      fetchJson<SeriesSearchHistoryItem[]>("/api/series/search-history"),
      fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?mediaType=tv&pageSize=20"),
      fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery"),
      fetchJson<SeriesInventoryDetail>(`/api/series/${id}/inventory`),
      fetchPageItems<ActivityEventItem>(
        `/api/activity?relatedEntityType=series&relatedEntityId=${id}&pageSize=20`
      ),
      fetchPageItems<DecisionExplanationItem>(`/api/decisions?relatedEntityType=series&relatedEntityId=${id}&pageSize=40`),
      fetchJson<LibraryItem[]>("/api/libraries"),
      fetchJson<IntakeTitleOriginItem[]>(`/api/intake-title-origins?mediaType=tv&entityId=${encodeURIComponent(id)}`).catch(() => []),
      fetchJson<MediaRemovalPreview>(`/api/series/${id}/removal-preview`).catch(() => ({ filePaths: [], folderPaths: [], warnings: [] }))
    ]);

  return { activity, decisions, importRecovery, inventory, libraries, metadataIssue, origins, removalPreview, searchHistory, series, dispatches };
}

export function ShowDetailPage() {
  const loaderData = useLoaderData() as ShowDetailLoaderData;
  const { activity, decisions, dispatches, importRecovery, inventory, libraries, metadataIssue, origins, removalPreview, searchHistory, series } = loaderData;
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const { preferences } = useDisplayPreferences();

  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [isRemoveConfirmationOpen, setIsRemoveConfirmationOpen] = useState(false);
  const [isMetadataOpen, setIsMetadataOpen] = useState(false);
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);
  const [openCandidate, setOpenCandidate] = useState<SearchPlanCandidate | null>(null);
  const [forceReason, setForceReason] = useState<string | null>(null);
  const [openEpisodeId, setOpenEpisodeId] = useState<string | null>(null);
  const [openSearchId, setOpenSearchId] = useState<string | null>(null);
  const [openDispatchId, setOpenDispatchId] = useState<string | null>(null);
  const [episodeFilter, setEpisodeFilter] = useState<EpisodeFilter>("all");
  const [openSeasons, setOpenSeasons] = useState<number[] | null>(null);
  const [query, setQuery] = useState("");
  const [section, setSection] = useState<DetailSection>("episodes");
  const [numberingDraft, setNumberingDraft] = useState<NumberingDraft | null>(null);

  /*
    The title's own record carries its search state.

    This used to search the wanted summary — a list of the 25 most recently
    updated titles — for the one title the page was already showing. Open the
    26th and the lookup missed: no library, no target quality, no cutoff, and a
    Defer button that could only 404. The same defect the grid had, on the
    screen that shows a single title, found by asking where else that shape
    lived.
  */
  const wantedItem = series.wantedStatus
    ? {
        libraryId: series.libraryId ?? "",
        wantedStatus: series.wantedStatus,
        wantedReason: series.wantedReason ?? "",
        currentQuality: series.currentQuality ?? null,
        targetQuality: series.targetQuality ?? null,
        qualityCutoffMet: series.qualityCutoffMet ?? false
      }
    : null;
  const library = wantedItem ? libraries.find((item) => item.id === wantedItem.libraryId) ?? null : null;
  const seriesSearches = searchHistory.filter((item) => item.seriesId === series.id);
  const seriesDispatches = dispatches.filter((item) => item.entityId === series.id);
  const importCases = importRecovery.recentCases.filter(
    (item) => item.title.trim().toLowerCase() === series.title.trim().toLowerCase()
  );
  const { cast, crew } = readStoredCredits(series.metadataJson);
  const meta = useMemo<Record<string, unknown> | null>(() => {
    if (!series.metadataJson) return null;
    try { return JSON.parse(series.metadataJson) as Record<string, unknown>; } catch { return null; }
  }, [series.metadataJson]);
  const metaText = (key: string) => {
    const value = meta?.[key] ?? meta?.[key.charAt(0).toLowerCase() + key.slice(1)];
    return typeof value === "string" && value.trim() ? value.trim() : null;
  };

  const visibleEpisodes = useMemo(
    () => inventory.episodes.filter((episode) => matchesEpisodeFilter(episode, episodeFilter, query)),
    [episodeFilter, inventory.episodes, query]
  );
  const visibleSeasons = useMemo(() => buildSeasonGroups(visibleEpisodes), [visibleEpisodes]);
  // Counted over what has aired. `|| !item.hasFile` used to pull in every
  // episode still to come, so a show mid-season offered to "find" episodes that
  // did not exist yet — Slow Horses read 36 missing when 30 had aired.
  const progress = useMemo(() => summariseEpisodes(inventory.episodes), [inventory.episodes]);
  const missingCount = progress.missing;
  const upcomingCount = progress.upcoming;
  const upgradeCount = progress.upgradable;
  const monitoredCount = inventory.episodes.filter((item) => item.monitored).length;
  const openEpisode = inventory.episodes.find((item) => item.episodeId === openEpisodeId) ?? null;

  // A full catalogue can be hundreds of episodes — The Simpsons is 885 — so
  // seasons collapse. The ones with something missing open by default, capped,
  // because those are the ones worth looking at. A collapsed season still
  // states its own span in its header.
  const defaultOpenSeasons = useMemo(() => {
    const withMissing = visibleSeasons.filter((season) => season.missingCount > 0).map((season) => season.seasonNumber);
    const chosen = withMissing.length ? withMissing : visibleSeasons.map((season) => season.seasonNumber);
    return chosen.slice(0, 2);
  }, [visibleSeasons]);
  const expanded = openSeasons ?? defaultOpenSeasons;
  const allExpanded = visibleSeasons.length > 0 && expanded.length === visibleSeasons.length;
  const openSearch = seriesSearches.find((item) => item.id === openSearchId) ?? null;
  const openDispatch = seriesDispatches.find((item) => item.id === openDispatchId) ?? null;
  // Deferring only touches a wanted state that is actually being searched for, so
  // offering it on a settled title produced an enabled button and a 404.
  const isBeingSearchedFor = wantedItem?.wantedStatus === "missing" || wantedItem?.wantedStatus === "upgrade";

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
          description: "Deluno compared the candidates it found. Pick the one to send to your download client.",
          action: "Review candidates",
          onAction: () => setSection("episodes")
        }
      : !series.monitored
        ? {
            eyebrow: "Unmonitored",
            title: "Resume automatic care",
            description: "This show is not being watched for missing episodes or quality improvements.",
            action: "Resume automation",
            onAction: () => void handleSeriesMonitoring(true)
          }
        : missingCount > 0
          ? {
              eyebrow: "Episodes missing",
              // Aired episodes only. Offering to find one that has not aired is
              // offering to fail, and it used to offer for every one of them.
              title: `Find ${missingCount} missing episode${missingCount === 1 ? "" : "s"}`,
              description: "Deluno can search every indexer you have connected using this show's Library Profile.",
              action: "Search now",
              onAction: () => void handleSearchNow("automatic")
            }
          : upcomingCount > 0
            ? {
                // The same word the shelf's mark uses, which is Sonarr's.
                eyebrow: "Continuing",
                title: `Every aired episode is here`,
                description: `${upcomingCount} episode${upcomingCount === 1 ? " has" : "s have"} not aired yet. Deluno will look as each one does.`,
                action: null,
                onAction: null
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

  async function handleMetadataRefresh() {
    setBusyAction("metadata-refresh");
    let refreshResponse: Response | null = null;

    try {
      const response = await authedFetch(`/api/series/${series.id}/metadata/refresh`, { method: "POST" });
      refreshResponse = response;
      if (response.status === 409) {
        revalidator.revalidate();
        toast.info("The TMDb record is no longer available. Your show and files were kept.");
        return;
      }
      if (!response.ok) throw new Error("series-metadata-refresh-failed");
      toast.success(`${series.title} metadata refreshed.`);
      revalidator.revalidate();
    } catch (refreshError) {
      const explained = await describeRequestFailure(refreshResponse, refreshError, {
        action: "refresh this show's metadata",
        check: { label: "Check metadata settings", href: "/settings/metadata" },
      });
      toast.error(explained.title, {
        description: explained.description,
        action: explained.action
          ? { label: explained.action.label, onClick: () => navigate(explained.action!.href) }
          : undefined,
      });
    } finally {
      setBusyAction(null);
    }
  }

  function openNumberingEditor() {
    const detail = inventory.numbering;
    const episodes = detail?.episodes ?? inventory.episodes.map((episode) => ({
      episodeId: episode.episodeId,
      absoluteNumber: episode.absoluteNumber ?? null,
      sceneSeasonNumber: episode.sceneSeasonNumber ?? null,
      sceneEpisodeNumber: episode.sceneEpisodeNumber ?? null,
      airDate: episode.airDate ?? null
    }));

    setNumberingDraft({
      seriesType: detail?.seriesType ?? series.seriesType ?? "standard",
      numberingScheme: detail?.numberingScheme ?? series.numberingScheme ?? "standard",
      numberingSource: detail?.numberingSource ?? series.numberingSource ?? "provider",
      mappings: Object.fromEntries(episodes.map((episode) => [episode.episodeId, {
        absoluteNumber: episode.absoluteNumber === null || episode.absoluteNumber === undefined ? "" : String(episode.absoluteNumber),
        sceneSeasonNumber: episode.sceneSeasonNumber === null || episode.sceneSeasonNumber === undefined ? "" : String(episode.sceneSeasonNumber),
        sceneEpisodeNumber: episode.sceneEpisodeNumber === null || episode.sceneEpisodeNumber === undefined ? "" : String(episode.sceneEpisodeNumber),
        airDate: episode.airDate ?? ""
      }]))
    });
  }

  async function handleNumberingSave() {
    if (!numberingDraft) return;
    setBusyAction("numbering-save");

    const mappings = Object.entries(numberingDraft.mappings)
      .map(([episodeId, mapping]) => ({
        episodeId,
        absoluteNumber: mapping.absoluteNumber.trim() ? Number(mapping.absoluteNumber) : null,
        sceneSeasonNumber: mapping.sceneSeasonNumber.trim() ? Number(mapping.sceneSeasonNumber) : null,
        sceneEpisodeNumber: mapping.sceneEpisodeNumber.trim() ? Number(mapping.sceneEpisodeNumber) : null,
        airDate: mapping.airDate.trim() || null
      }))
      .filter((mapping) => mapping.absoluteNumber !== null || mapping.sceneSeasonNumber !== null || mapping.sceneEpisodeNumber !== null || mapping.airDate !== null);

    try {
      const response = await authedFetch(`/api/series/${series.id}/numbering`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          seriesType: numberingDraft.seriesType,
          numberingScheme: numberingDraft.numberingScheme,
          numberingSource: numberingDraft.numberingSource,
          mappings: numberingDraft.numberingSource === "provider" ? [] : mappings
        })
      });
      if (!response.ok) throw new Error("series-numbering-failed");
      toast.success("TV numbering updated.");
      setNumberingDraft(null);
      revalidator.revalidate();
    } catch {
      toast.error("TV numbering could not be updated.");
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSearchNow(mode: "automatic" | "interactive") {
    setBusyAction(`${mode}-search`);
    let searchResponse: Response | null = null;

    try {
      searchResponse = await authedFetch(`/api/series/${series.id}/search${mode === "interactive" ? "?mode=preview" : ""}`, { method: "POST" });
      if (!searchResponse.ok) throw new Error("series-search-failed");
      const response = searchResponse;

      const payload = (await response.json()) as {
        outcome?: string;
        summary?: string;
        releaseName?: string | null;
        indexerName?: string | null;
        dispatchStatus?: string | null;
        dispatchMessage?: string | null;
        reason?: string;
        candidates?: SearchPlanCandidate[];
        failures?: IntegrationFailure[];
      };
      const best = payload.releaseName ? `${payload.releaseName}${payload.indexerName ? ` via ${payload.indexerName}` : ""}` : null;
      const failureNotice = formatSearchFailureNotice(payload.failures);
      setReleaseCandidates(mode === "interactive" ? payload.candidates ?? [] : []);

      if (mode === "interactive") {
        const found = payload.candidates?.length ?? 0;
        setSection("episodes");
        if (found) toast.success(`${found} release${found === 1 ? "" : "s"} compared. Choose one below.`, failureNotice ? { description: failureNotice } : undefined);
        else {
          const explained = describeSearchReason(payload.reason, payload.summary ?? "No releases matched this show's Library Profile.");
          const action = explained.action;
          toast.info(explained.title, {
            description: [explained.description, failureNotice].filter(Boolean).join(" "),
            action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
          });
        }
      } else {
        if (best) {
          toast.success(`Deluno selected ${best} using this show's Library Profile.`, failureNotice ? { description: failureNotice } : undefined);
        } else {
          const explained = describeSearchReason(payload.reason, "Search finished with no accepted release.");
          const action = explained.action;
          toast.info(explained.title, {
            description: [explained.description, failureNotice].filter(Boolean).join(" "),
            action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
          });
        }
      }
      revalidator.revalidate();
    } catch (searchError) {
      const explained = await describeRequestFailure(searchResponse, searchError, {
        action: "search for this title",
        check: { label: "Check indexers", href: "/indexers/indexers" },
      });
      toast.error(explained.title, {
        description: explained.description,
        action: explained.action
          ? { label: explained.action.label, onClick: () => navigate(explained.action!.href) }
          : undefined,
      });
    } finally {
      setBusyAction(null);
    }
  }

  async function handleGrabCandidate(candidate: SearchPlanCandidate, force = false, overrideReason?: string) {
    setBusyAction(force ? "force-grab" : "grab");
    let grabResponse: Response | null = null;

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
          overrideReason: force ? overrideReason || `User forced this release despite Deluno's decision: ${candidate.summary}` : null
        })
      });
      grabResponse = response;
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
    } catch (grabError) {
      const explained = await describeRequestFailure(grabResponse, grabError, {
        action: "send that release to the download client",
        check: { label: "Check download clients", href: "/indexers/download-clients" },
      });
      toast.error(explained.title, {
        description: explained.description,
        action: explained.action
          ? { label: explained.action.label, onClick: () => navigate(explained.action!.href) }
          : undefined,
      });
    } finally {
      setBusyAction(null);
    }
  }

  async function handleEpisodeSearch(episodeIds: string[]) {
    if (!episodeIds.length) return;
    setBusyAction("episode-search");
    let searchResponse: Response | null = null;

    try {
      const response = await authedFetch(`/api/series/${series.id}/episodes/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds })
      });
      searchResponse = response;
      if (!response.ok) throw new Error("episode-search-failed");

      const payload = (await response.json()) as {
        searchedEpisodes?: number;
        matchedCount?: number;
        sentCount?: number;
        plannedCount?: number;
        failedCount?: number;
        reason?: string;
        failures?: IntegrationFailure[];
      };
      const searched = payload.searchedEpisodes ?? episodeIds.length;
      const matched = payload.matchedCount ?? 0;
      const failureNotice = formatSearchFailureNotice(payload.failures);
      if (payload.reason && payload.reason !== "ok") {
        const explained = describeSearchReason(payload.reason, `Searched ${searched} episode${searched === 1 ? "" : "s"}. Nothing matched yet.`);
        const action = explained.action;
        toast.info(explained.title, {
          description: [explained.description, failureNotice].filter(Boolean).join(" "),
          action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
        });
      } else {
        toast.success(
          matched > 0
            ? `Searched ${searched} episode${searched === 1 ? "" : "s"}, matched ${matched}. ${formatDispatchSummary(payload)}`
            : `Searched ${searched} episode${searched === 1 ? "" : "s"}. Nothing matched yet.`,
          failureNotice ? { description: failureNotice } : undefined
        );
      }
      revalidator.revalidate();
    } catch (searchError) {
      const explained = await describeRequestFailure(searchResponse, searchError, {
        action: "search for those episodes",
        check: { label: "Check indexers", href: "/indexers/indexers" },
      });
      toast.error(explained.title, {
        description: explained.description,
        action: explained.action
          ? { label: explained.action.label, onClick: () => navigate(explained.action!.href) }
          : undefined,
      });
    } finally {
      setBusyAction(null);
    }
  }

  async function handleSeasonSearch(seasonNumber: number) {
    setBusyAction(`season-search-${seasonNumber}`);
    let searchResponse: Response | null = null;

    try {
      const response = await authedFetch(`/api/series/${series.id}/seasons/${seasonNumber}/search`, { method: "POST" });
      searchResponse = response;
      if (!response.ok) throw new Error("season-search-failed");

      const payload = (await response.json()) as {
        matchedCount?: number;
        seasonNumber?: number;
        dispatchStatus?: string | null;
        dispatchMessage?: string | null;
        reason?: string;
        failures?: IntegrationFailure[];
      };
      const resolved = payload.seasonNumber ?? seasonNumber;
      const matched = payload.matchedCount ?? 0;
      const failureNotice = formatSearchFailureNotice(payload.failures);
      if (payload.reason && payload.reason !== "ok") {
        const explained = describeSearchReason(payload.reason, `${formatSeasonLabel(resolved)}: search finished with no matches.`);
        const action = explained.action;
        toast.info(explained.title, {
          description: [explained.description, failureNotice].filter(Boolean).join(" "),
          action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
        });
      } else {
        toast.success(
          matched > 0
            ? `${formatSeasonLabel(resolved)}: ${matched} episode match${matched === 1 ? "" : "es"}. ${formatDispatchSummary(payload)}`
            : `${formatSeasonLabel(resolved)}: search finished with no matches.`,
          failureNotice ? { description: failureNotice } : undefined
        );
      }
      revalidator.revalidate();
    } catch (searchError) {
      const explained = await describeRequestFailure(searchResponse, searchError, {
        action: "search for that season",
        check: { label: "Check indexers", href: "/indexers/indexers" },
      });
      toast.error(explained.title, {
        description: explained.description,
        action: explained.action
          ? { label: explained.action.label, onClick: () => navigate(explained.action!.href) }
          : undefined,
      });
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
    <div className="grid grid-cols-[minmax(0,1fr)] gap-[var(--page-gap)]">
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
            {/*
              The only place a show's monitoring can be changed on its own page.

              James: "There isnt an unmonitor button or a way to unmonitor
              titles without selecting it for bulk." The page could resume it,
              from a prompt that only appears once it is already paused, and
              offered no way to pause it — so turning one show off meant going
              back to the shelf and using a bulk action on a selection of one.

              Episode-level monitoring is a separate control further down, and
              stays that way: a show you stop watching is not the same as an
              episode you never want.
            */}

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

      <MetadataProviderIssueNotice
        issue={metadataIssue}
        subjectLabel="show"
        acknowledgeUrl={`/api/series/${series.id}/metadata/issue/acknowledge`}
        onAcknowledged={() => revalidator.revalidate()}
        onFindAnother={() => setIsMetadataOpen(true)}
        onRetry={() => void handleMetadataRefresh()}
      />

      <Card className="relative isolate min-w-0 min-h-[19rem] overflow-hidden border-primary/25 bg-card">
        <HeroBackdrop url={series.backdropUrl} />
        <CardContent className="relative p-[var(--tile-pad)] sm:p-[calc(var(--tile-pad)*1.15)]">
          <div className="grid items-start gap-[var(--grid-gap)] md:grid-cols-[16rem_minmax(0,1fr)] xl:grid-cols-[16rem_minmax(0,1fr)_14rem]">
            {series.posterUrl ? (
              <img src={series.posterUrl} alt={`${series.title} poster`} className="h-96 w-64 justify-self-center rounded-2xl border border-white/15 bg-surface-1 object-cover shadow-2xl md:justify-self-start" />
            ) : (
              <div className="flex h-96 w-64 justify-self-center items-center justify-center rounded-2xl border border-hairline bg-surface-1 px-3 text-center text-xs text-muted-foreground md:justify-self-start">Artwork is being refreshed</div>
            )}
            <div className="min-w-0 self-start">
              <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-primary">TV series</p>
              <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1">
                <h1 className="font-display text-4xl font-semibold tracking-tight text-foreground sm:text-5xl">{series.title}</h1>
                {/* The shield, beside the title — one control, both shelves. */}
                <button
                  type="button"
                  onClick={() => void handleSeriesMonitoring(!series.monitored)}
                  disabled={busyAction !== null}
                  aria-label={series.monitored ? "Monitored — click to unmonitor" : "Unmonitored — click to monitor"}
                  title={series.monitored ? "Monitored — click to unmonitor" : "Unmonitored — click to monitor"}
                  className={cn(
                    "self-center rounded-lg p-1.5 transition-colors",
                    series.monitored
                      ? "text-foreground hover:bg-surface-2"
                      : "text-muted-foreground hover:bg-surface-2 hover:text-foreground"
                  )}
                >
                  {busyAction === "series-monitor"
                    ? <LoaderCircle className="h-5 w-5 animate-spin" />
                    : series.monitored ? <ShieldCheck className="h-5 w-5" /> : <ShieldOff className="h-5 w-5" />}
                </button>
              </div>
              {series.originalTitle && series.originalTitle !== series.title ? <p className="mt-1 text-sm text-muted-foreground">Also known as {series.originalTitle}</p> : null}
              <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
                {metaText("Certification") ? (
                  <span className="rounded border border-hairline px-1.5 py-px text-xs font-bold uppercase tracking-wide" title="Classification">
                    {metaText("Certification")}
                  </span>
                ) : null}
                {series.startYear ? <span>{series.startYear}</span> : null}
                {series.runtimeMinutes ? (
                  <span>{formatRuntime(series.runtimeMinutes, preferences)}</span>
                ) : null}
              </div>
              <div className="mt-4 flex flex-wrap gap-2">
                {/*
                  The mark, and nothing beside it about monitoring.

                  This was two badges: "Monitored" in words, and a status badge
                  that chose its own colour — amber for Missing and Upgradable,
                  blue for the rest. Amber is the signal that means a person is
                  needed, and neither of those needs one (#302); the poster's own
                  mark had already called them red and green. The halved dot says
                  monitoring, which is what it is for.
                */}
                <TitleMarkLabel
                  className="rounded-full border border-hairline bg-surface-2 px-2.5 py-1 text-xs font-medium"
                  item={{
                    monitored: series.monitored,
                    wantedStatus: wantedItem?.wantedStatus,
                    // A show is judged on its episodes, not on its own row —
                    // the lowest rung any aired episode is on, so the header
                    // never says more than the season list underneath it.
                    airedEpisodeCount: progress.aired,
                    airedWithFileCount: progress.held,
                    airedUpgradableCount: progress.upgradable
                  }}
                />
                {importCases.length ? <Badge variant="warning">{importCases.length} import issue{importCases.length === 1 ? "" : "s"}</Badge> : null}
                {series.genres?.split(",").map((genre) => <span key={genre} className="rounded-full border border-primary/20 bg-primary/10 px-2.5 py-1 text-xs font-medium text-primary">{genre.trim()}</span>)}
              </div>
              <TitleTagsEditor id={series.id} mediaType="series" metadataJson={series.metadataJson} onSaved={() => revalidator.revalidate()} />
              <dl className="mt-4 grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3 lg:grid-cols-4">
                {[
                  { label: "Episodes", value: progress.aired ? `${progress.held}/${progress.aired} aired held` : null },
                  { label: "Network", value: metaText("Network") },
                  { label: "Studio", value: metaText("Studio") },
                  { label: "Language", value: metaText("OriginalLanguage") },
                  { label: "Collection", value: metaText("Collection") },
                  { label: "Numbering", value: `${formatSeriesType(series.seriesType)} · ${formatNumberingScheme(series.numberingScheme)}` },
                  { label: "Director", value: metaText("Director") },
                  { label: "Status", value: metaText("Status") },
                  { label: "Added", value: series.createdUtc ? formatShortDate(series.createdUtc, preferences) : null },
                  { label: "Import issues", value: importCases.length ? String(importCases.length) : null }
                ].filter((fact) => fact.value).map((fact) => (
                  <div key={fact.label} className="min-w-0">
                    <dt className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">{fact.label}</dt>
                    <dd className="truncate text-sm text-foreground" title={String(fact.value)}>{fact.value}</dd>
                  </div>
                ))}
              </dl>
              <p className="mt-4 max-w-4xl text-sm leading-relaxed text-muted-foreground">
                {series.overview ?? "No overview has been stored yet. Refresh metadata when you want Deluno to enrich this series."}
              </p>
            </div>
            <aside className="w-full self-start rounded-xl border border-white/10 bg-card/80 p-3 backdrop-blur-sm xl:min-h-96">
              <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.18em] text-muted-foreground">Ratings &amp; IDs</p>
              <p className="mt-0.5 text-xs text-muted-foreground">The metadata Deluno is using</p>
              <div className="mt-2"><RatingStrip ratings={series.ratings} fallbackRating={series.rating} /></div>
              <div className="mt-3 space-y-2 border-t border-hairline pt-3 text-sm">
                <div className="flex items-center justify-between gap-3"><span className="text-muted-foreground">Source</span>{series.metadataProvider ? <SourceMark source={series.metadataProvider.toLowerCase()} label={series.metadataProvider.toUpperCase()} /> : <span className="font-medium text-foreground">Not linked</span>}</div>
                <div className="flex items-center justify-between gap-3"><SourceMark source="imdb" label="IMDb" /><span className="font-mono text-xs font-medium text-foreground">{series.imdbId ?? "—"}</span></div>
              </div>
              <Button variant="outline" className="mt-3 w-full" onClick={() => void handleMetadataRefresh()} disabled={busyAction !== null}>
                {busyAction === "metadata-refresh" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                Refresh metadata
              </Button>
              <Button variant="outline" className="mt-1 w-full" onClick={() => setIsMetadataOpen(true)}>Edit metadata</Button>
              {/* Destructive, so it sits with the other "manage this title" controls
                  rather than beside the two searches in the toolbar. */}
              <Button
                variant="ghost"
                className="mt-1 w-full text-destructive hover:bg-destructive/10 hover:text-destructive"
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

      {/* Out of the bubble and into their own blocks, the same as a film's page
          and for the same reason — a header you have to scroll is not a header. */}
      {cast.length ? (
        <Card>
          <CardContent className="p-4">
            <CreditsRow heading="Cast" people={cast} className="border-t-0 pt-0" />
          </CardContent>
        </Card>
      ) : null}
      {crew.length ? (
        <Card>
          <CardContent className="p-4">
            <CreditsRow heading="Crew" people={crew} className="border-t-0 pt-0" />
          </CardContent>
        </Card>
      ) : null}

      {nextStep ? (
        <ListCard title="Next step" count={nextStep.eyebrow}>
          <ListTable
            chevron={false}
            columns={[{ label: "What Deluno suggests" }, { label: "Action", width: "auto", align: "end", mobile: true }]}
          >
            <ListRow>
              <ListNameCell name={nextStep.title} sub={nextStep.description} />
              <div role="cell" className="flex justify-end">
                {/* Some next steps are a statement rather than an offer — there
                    is nothing to do about an episode that has not aired. */}
                {nextStep.action && nextStep.onAction ? (
                  <Button type="button" size="sm" onClick={nextStep.onAction} disabled={busyAction !== null}>
                    {nextStep.action}
                  </Button>
                ) : null}
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
              // The mark's own red, not amber. These two are counts of work
              // Deluno is already doing on its schedule — amber claims a person
              // has to act, and spending it here is what teaches people to stop
              // reading it (#302). Red is Missing and green is Upgradable, the
              // same two colours as the dots on the episodes below.
              { label: "Missing", value: missingCount, tone: missingCount > 0 ? "danger" : undefined, help: "aired, no file yet" },
              { label: "Upcoming", value: upcomingCount, help: upcomingCount ? "not aired yet" : "nothing scheduled" },
              { label: "Upgrades", value: upgradeCount, tone: upgradeCount > 0 ? "success" : undefined, help: "better release wanted" },
              { label: "Monitored", value: monitoredCount, help: `of ${inventory.episodeCount} watched` }
            ]}
          />

          {releaseCandidates.length ? (
            <ListCard title="Choose a release" count={`${releaseCandidates.length} candidate${releaseCandidates.length === 1 ? "" : "s"}`}>
              <ListTable
                columns={[
                  { label: "Release" },
                  { label: "Quality", mobile: true },
                  { label: "Size" },
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
                      sub={`${index === 0 && canWinSearch(candidate) ? "Best match · " : ""}${candidate.indexerName}`}
                    />
                    <ListCell primary={candidate.quality} mobile />
                    <ListCell primary={candidate.sizeBytes ? formatBytes(candidate.sizeBytes) : "—"} />
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
            count={`${visibleSeasons.filter((season) => expanded.includes(season.seasonNumber)).reduce((total, season) => total + season.episodes.length, 0)} of ${inventory.episodeCount} shown`}
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
                  disabled={!visibleSeasons.length}
                  onClick={() =>
                    setOpenSeasons(allExpanded ? [] : visibleSeasons.map((season) => season.seasonNumber))
                  }
                >
                  {allExpanded ? "Collapse all" : "Expand all"}
                </Button>
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
                // No File column. It could only ever say "On disk" or "Not
                // imported" — the Status cell two columns along, in different
                // words, on every row. Status says which of the four rungs the
                // episode is on, which is strictly more.
                columns={[
                  { label: "Episode" },
                  { label: "Aired" },
                  { label: "Last search" },
                  { label: "Status", width: LIST_TRACK.status },
                  { label: "On", width: LIST_TRACK.toggle, mobile: true }
                ]}
              >
                {visibleSeasons.map((season) => (
                  <Fragment key={season.seasonNumber}>
                    <ListGroupHeader
                      label={formatSeasonLabel(season.seasonNumber)}
                      detail={`${season.episodes.length} episodes · ${season.importedCount} on disk · ${season.missingCount} missing${season.upcomingCount ? ` · ${season.upcomingCount} upcoming` : ""}`}
                      actions={
                        <>
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          aria-expanded={expanded.includes(season.seasonNumber)}
                          onClick={() =>
                            setOpenSeasons(
                              expanded.includes(season.seasonNumber)
                                ? expanded.filter((value) => value !== season.seasonNumber)
                                : [...expanded, season.seasonNumber]
                            )
                          }
                        >
                          {expanded.includes(season.seasonNumber) ? "Hide" : "Show"}
                        </Button>
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
                        </>
                      }
                    />
                    {(expanded.includes(season.seasonNumber) ? season.episodes : []).map((episode) => (
                      <ListRow
                        key={episode.episodeId}
                        onClick={() => setOpenEpisodeId(episode.episodeId)}
                        selected={openEpisodeId === episode.episodeId}
                      >
                        <ListNameCell name={formatEpisodeCode(episode)} sub={episode.title ?? "Episode title pending"} />
                        <ListCell primary={episode.airDateUtc ? formatShortDate(episode.airDateUtc, { ...preferences, showRelativeDates: false }) : "—"} />
                        <ListCell primary={episode.lastSearchUtc ? formatPreferenceDateTime(episode.lastSearchUtc, preferences) : "Never"} />
                        <ListCell>
                          {/* An episode is a title. Same five marks (DESIGN-001). */}
                          <TitleMarkLabel item={{ monitored: episode.monitored, wantedStatus: episode.wantedStatus }} type="show" />
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
          <ListCard
            title="TV numbering"
            count={`${formatSeriesType(inventory.numbering?.seriesType ?? series.seriesType)} · ${formatNumberingScheme(inventory.numbering?.numberingScheme ?? series.numberingScheme)}`}
            actions={<Button type="button" size="sm" variant="outline" onClick={openNumberingEditor}>Edit numbering</Button>}
          >
            <ListTable chevron={false} columns={[{ label: "Setting" }, { label: "Value", width: "minmax(0,2fr)", mobile: true }]}>
              <ListRow>
                <ListNameCell name="Series type" sub="Standard, daily, and anime use different identity clues." />
                <ListCell primary={formatSeriesType(inventory.numbering?.seriesType ?? series.seriesType)} mobile />
              </ListRow>
              <ListRow>
                <ListNameCell name="Numbering scheme" sub="The key Deluno uses before it will match a file to an episode." />
                <ListCell primary={formatNumberingScheme(inventory.numbering?.numberingScheme ?? series.numberingScheme)} mobile />
              </ListRow>
              <ListRow>
                <ListNameCell name="Mapping source" sub="Owner mappings are protected from provider refreshes." />
                <ListCell primary={formatNumberingSource(inventory.numbering?.numberingSource ?? series.numberingSource)} mobile />
              </ListRow>
            </ListTable>
          </ListCard>

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

          <ListCard title="Automation" count={series.monitored ? "Monitored" : "Unmonitored"}>
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
                    isBeingSearchedFor
                      ? "Pause scheduled searches for a day. Manual searches still work."
                      : "Nothing to defer — Deluno is not searching for this show."
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
                      ? "Let one scheduled cycle pass without searching this show."
                      : "Nothing to skip — Deluno is not searching for this show."
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
            <ListCard title="How this show was added" count={`${origins.length} import list${origins.length === 1 ? "" : "s"}`}>
              <ListTable chevron={false} columns={[{ label: "Source" }, { label: "Provider", mobile: true }, { label: "First seen" }]}>
                {origins.map((origin) => (
                  <ListRow key={origin.id}>
                    <ListNameCell name={origin.sourceName} sub="Removing the list never removes this show or its files." />
                    <ListCell primary={origin.provider} mobile />
                    <ListCell primary={formatPreferenceDateTime(origin.firstSeenUtc, preferences)} />
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
                description="Manual and scheduled searches for this show — series, season and episode alike — appear here with their outcomes and explanations."
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
                    <ListCell primary={formatPreferenceDateTime(item.createdUtc, preferences)} />
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
              <ListTable columns={[{ label: "Release" }, { label: "Client", mobile: true }, { label: "When" }, { label: "Status", width: LIST_TRACK.status }]}>
                {seriesDispatches.slice(0, 8).map((item) => (
                  <ListRow key={item.id} onClick={() => setOpenDispatchId(item.id)} selected={openDispatchId === item.id}>
                    <ListNameCell name={item.releaseName} sub={item.indexerName} />
                    <ListCell primary={item.downloadClientName} mobile />
                    <ListCell primary={formatPreferenceDateTime(item.createdUtc, preferences)} />
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
                    <ListCell primary={formatPreferenceDateTime(item.createdUtc, preferences)} />
                  </ListRow>
                ))}
              </ListTable>
            )}
          </ListCard>
        </>
      ) : null}

      {/* ------------------------------------------------------------ drawers */}

      <DownloadDispatchDrawer dispatch={openDispatch} onClose={() => setOpenDispatchId(null)} />

      <Drawer
        open={numberingDraft !== null}
        onOpenChange={(next) => {
          if (!next) setNumberingDraft(null);
        }}
        title="TV numbering"
        description="Choose how Deluno identifies episodes. Unmatched files remain recoverable instead of being guessed."
        footer={
          <DrawerFooter
            state="clean"
            saveType="button"
            saveLabel="Save numbering"
            saveEnabled={numberingDraft !== null && busyAction === null}
            onSave={() => void handleNumberingSave()}
            onCancel={() => setNumberingDraft(null)}
          />
        }
      >
        {numberingDraft ? (
          <>
            <DrawerSection title="Series model">
              <div className="grid gap-3 sm:grid-cols-2">
                <label className="grid gap-1.5 text-sm">
                  <span className="font-medium text-foreground">Series type</span>
                  <Select
                    aria-label="Series type"
                    value={numberingDraft.seriesType}
                    onChange={(event) => setNumberingDraft({ ...numberingDraft, seriesType: event.target.value })}
                    options={[
                      { value: "standard", label: "Standard" },
                      { value: "daily", label: "Daily" },
                      { value: "anime", label: "Anime" }
                    ]}
                  />
                </label>
                <label className="grid gap-1.5 text-sm">
                  <span className="font-medium text-foreground">Numbering scheme</span>
                  <Select
                    aria-label="Numbering scheme"
                    value={numberingDraft.numberingScheme}
                    onChange={(event) => setNumberingDraft({ ...numberingDraft, numberingScheme: event.target.value })}
                    options={[
                      { value: "standard", label: "Season / episode" },
                      { value: "airdate", label: "Air date" },
                      { value: "absolute", label: "Absolute episode" },
                      { value: "scene", label: "Scene season / episode" }
                    ]}
                  />
                </label>
                <label className="grid gap-1.5 text-sm sm:col-span-2">
                  <span className="font-medium text-foreground">Mapping source</span>
                  <Select
                    aria-label="Mapping source"
                    value={numberingDraft.numberingSource}
                    onChange={(event) => setNumberingDraft({ ...numberingDraft, numberingSource: event.target.value })}
                    options={[
                      { value: "provider", label: "Provider metadata (clear owner mappings)" },
                      { value: "owner", label: "Owner mappings (protected from refresh)" }
                    ]}
                  />
                </label>
              </div>
              <p className="mt-3 text-[length:var(--type-caption)] leading-snug text-muted-foreground">
                Deluno matches only an exact, unique key. If a filename cannot be matched safely it stays in import recovery for review.
              </p>
            </DrawerSection>

            <DrawerSection title="Episode mappings" aside={`${Object.keys(numberingDraft.mappings).length} episodes`}>
              <div className="max-h-[28rem] space-y-2 overflow-y-auto pr-1">
                {inventory.episodes.map((episode) => {
                  const mapping = numberingDraft.mappings[episode.episodeId] ?? { absoluteNumber: "", sceneSeasonNumber: "", sceneEpisodeNumber: "", airDate: "" };
                  const updateMapping = (field: keyof NumberingDraft["mappings"][string], value: string) => setNumberingDraft({
                    ...numberingDraft,
                    mappings: { ...numberingDraft.mappings, [episode.episodeId]: { ...mapping, [field]: value } }
                  });
                  return (
                    <div key={episode.episodeId} className="rounded-lg border border-hairline p-2.5">
                      <p className="mb-2 text-sm font-medium text-foreground">{formatEpisodeCode(episode)} · {episode.title ?? "Episode"}</p>
                      <div className="grid gap-2 sm:grid-cols-4">
                        <Input aria-label={`${formatEpisodeCode(episode)} absolute number`} type="number" min={1} placeholder="Absolute" value={mapping.absoluteNumber} onChange={(event) => updateMapping("absoluteNumber", event.target.value)} />
                        <Input aria-label={`${formatEpisodeCode(episode)} scene season`} type="number" min={0} placeholder="Scene season" value={mapping.sceneSeasonNumber} onChange={(event) => updateMapping("sceneSeasonNumber", event.target.value)} />
                        <Input aria-label={`${formatEpisodeCode(episode)} scene episode`} type="number" min={1} placeholder="Scene episode" value={mapping.sceneEpisodeNumber} onChange={(event) => updateMapping("sceneEpisodeNumber", event.target.value)} />
                        <Input aria-label={`${formatEpisodeCode(episode)} air date`} type="date" value={mapping.airDate} onChange={(event) => updateMapping("airDate", event.target.value)} />
                      </div>
                    </div>
                  );
                })}
              </div>
            </DrawerSection>
          </>
        ) : null}
      </Drawer>

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
                  { label: "First aired", value: openEpisode.airDateUtc ? formatShortDate(openEpisode.airDateUtc, { ...preferences, showRelativeDates: false }) : "Not announced" },
                  ...(openEpisode.absoluteNumber !== null && openEpisode.absoluteNumber !== undefined ? [{ label: "Absolute", value: `#${openEpisode.absoluteNumber}` }] : []),
                  ...(openEpisode.sceneSeasonNumber !== null && openEpisode.sceneSeasonNumber !== undefined && openEpisode.sceneEpisodeNumber !== null && openEpisode.sceneEpisodeNumber !== undefined
                    ? [{ label: "Scene", value: `S${String(openEpisode.sceneSeasonNumber).padStart(2, "0")}E${String(openEpisode.sceneEpisodeNumber).padStart(2, "0")}` }]
                    : []),
                  ...(openEpisode.airDate ? [{ label: "Air-date key", value: openEpisode.airDate }] : [])
                ]}
              />
            </DrawerSection>

            <DrawerSection title="Automation" aside={wantedStatusPresentation(openEpisode.wantedStatus).label}>
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
                  { label: "Last searched", value: openEpisode.lastSearchUtc ? formatPreferenceDateTime(openEpisode.lastSearchUtc, preferences) : "Never" },
                  {
                    label: "Next eligible search",
                    value: openEpisode.nextEligibleSearchUtc ? formatPreferenceDateTime(openEpisode.nextEligibleSearchUtc, preferences) : "As soon as a cycle runs"
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
          if (!next) {
            setOpenCandidate(null);
            setForceReason(null);
          }
        }}
        title={openCandidate?.releaseName ?? "Release"}
        description={openCandidate ? `${openCandidate.indexerName} · ${candidateLabel(openCandidate)}` : undefined}
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
            <DrawerSection title="How Deluno evaluated it" aside={candidateLabel(openCandidate)}>
              <DrawerFacts
                items={[
                  { label: "Quality", value: openCandidate.quality },
                  ...(isTypedCandidate(openCandidate)
                    ? [{ label: "Policy", value: "Typed release plan" }]
                    : [{ label: "Evaluation", value: "Legacy compatibility rules" }]),
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
              <DrawerSection title={likesCandidate(openCandidate) ? "Why Deluno likes it" : "How Deluno reached this"}>
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
                      Overrides this decision. Your reason is stored in activity and search history.
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
        description={openSearch ? `${formatSearchScope(openSearch)} · ${formatPreferenceDateTime(openSearch.createdUtc, preferences)}` : undefined}
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
              <DrawerSection title="Release outcomes" aside={`${parseSearchCandidates(openSearch.detailsJson).length} considered`}>
                <DrawerFacts
                  items={parseSearchCandidates(openSearch.detailsJson)
                    .slice(0, 6)
                    .map((candidate) => ({
                      label: candidate.releaseName,
                      value: isTypedCandidate(candidate)
                        ? `${candidate.quality} · ${candidateLabel(candidate)}`
                        : `${candidate.quality} · legacy compatibility rules`
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

function formatSeriesType(value: string | null | undefined) {
  switch (value?.toLowerCase()) {
    case "daily": return "Daily";
    case "anime": return "Anime";
    default: return "Standard";
  }
}

function formatNumberingScheme(value: string | null | undefined) {
  switch (value?.toLowerCase()) {
    case "airdate": return "Air date";
    case "absolute": return "Absolute episode";
    case "scene": return "Scene numbering";
    default: return "Season / episode";
  }
}

function formatNumberingSource(value: string | null | undefined) {
  return value?.toLowerCase() === "owner" ? "Owner mapping" : "Provider metadata";
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
        missingCount: sorted.filter((item) => isEpisodeMissing(item)).length,
        upcomingCount: sorted.filter((item) => isEpisodeUpcoming(item)).length,
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
  preferenceEvaluation?: unknown;
  preferenceComparison?: unknown;
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
    estimatedBitrateMbps: (value.estimatedBitrateMbps ?? value.EstimatedBitrateMbps ?? null) as number | null,
    preferenceEvaluation: value.preferenceEvaluation ?? value.PreferenceEvaluation,
    preferenceComparison: value.preferenceComparison ?? value.PreferenceComparison
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

function searchOutcomeTone(outcome: string): Tone {
  switch (outcome) {
    case "matched":
      return "ok";
    case "error":
      return "bad";
    case "blocked":
      return "warn";
    default:
      return "idle";
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

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}
