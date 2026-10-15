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
import { importWorkflowLabel } from "../lib/import-workflow";
import { getDispatchStatusLabel as formatDispatchStatus } from "../lib/job-status-constants";
import { ArtworkImage } from "../components/ui/artwork-image";
import { eventLabel } from "../lib/activity-format";
import { activityLinkForTitle } from "../lib/activity-history";
import type { Tone } from "../lib/status-tones";
import { Fragment, useEffect, useMemo, useState } from "react";
import { Link, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { ArrowLeft, LoaderCircle, Pencil, RefreshCw, Search, Trash2, ShieldCheck, ShieldOff
} from "lucide-react";
import {
  fetchJson, fetchPageItems,
  type ActivityEventItem,
  type DecisionExplanationItem,
  type DownloadClientItem,
  type DownloadDispatchItem,
  type IndexerItem,
  type IntegrationFailure,
  type LibraryItem,
  type LibraryRoutingSnapshot,
  type IntakeTitleOriginItem,
  type SeriesEpisodeInventoryItem,
  type SeriesImportRecoverySummary,
  type SeriesInventoryDetail,
  type SeriesListItem,
  type MetadataProviderIssue,
  type SeriesSearchHistoryItem,
  type AcquisitionBlockersResponse,
  type MediaSubtitleRow,
  type MediaSubtitleAttemptHistoryEntry,
  type SubtitleLanguageOption
} from "../lib/api";
import { describeTitleRouting } from "../lib/title-routing";
import { subtitleTypeLabel, subtitleSourceLabel, timingStatusLabel } from "../lib/subtitle-display";
import { SubtitleTimingActions } from "../components/app/subtitle-timing-actions";
import { SubtitleOverrideCard } from "../components/app/subtitle-override-card";
import { SubtitleManualSearchDialog } from "../components/app/subtitle-manual-search-dialog";
import { authedFetch } from "../lib/use-auth";
import { cn, formatBytes } from "../lib/utils";
import { isEpisodeMissing, isEpisodeUpcoming, isEpisodeUpgradable, summariseEpisodes } from "../lib/episode-progress";
import { SearchResultNote, type SearchNote } from "../components/app/search-result-note";
import { describeSearchNow, describeSearchReason, describeRequestFailure, formatGrabMessage, formatSearchFailureNotice } from "../lib/search-reasons";
import { candidateExplanation, candidateLabel, candidateTone, canWinSearch, isTypedCandidate, likesCandidate, plainCandidateReason, plainSearchResult } from "../lib/release-candidate-status";
import {
  candidateSortValue,
  filterCandidates,
  formatGrabs,
  formatProtocol,
  formatReleaseAge,
  formatSeedersAndLeechers
} from "../lib/release-candidate-columns";
import { toneSortValue } from "../lib/status-tones";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent } from "../components/ui/card";
import { AcquisitionBlockersCard } from "../components/app/acquisition-blockers-card";
import { LibraryMembershipCard, type LibraryMembership } from "../components/app/library-membership-card";
import { RetentionTitleCard } from "../components/app/retention-title-card";
import { RemoveMediaDialog, type MediaRemovalPreview, type RemoveMediaOptions } from "../components/app/remove-media-dialog";
import { showRemovalToast } from "../components/app/removal-toast";
import { CountryReleaseRow } from "../components/app/country-release-row";
import { CreditsRow, readStoredCredits } from "../components/app/credits-row";
import { DownloadDispatchDrawer } from "../components/app/download-dispatch-drawer";
import { BlockReleaseAction } from "../components/app/block-release-action";
import {
  AlreadyDownloadingDialog,
  readAlreadyDownloading,
  type AlreadyDownloadingPrompt,
  type DuplicateSendAction
} from "../components/app/already-downloading-prompt";
import { RemoveDownloadAction } from "../components/app/remove-download-action";
import { TitleSynopsis } from "../components/app/title-synopsis";
import { TitleTagsEditor } from "../components/app/title-tags-editor";
import { TitleFolderActions } from "../components/app/title-folder-actions";
import { TitleActionGrid } from "../components/app/title-action-grid";
import { DecisionExplanationList } from "../components/app/decision-explanation-list";
import { MediaMetadataDrawer } from "../components/app/media-metadata-drawer";
import { HeroBackdrop } from "../components/app/hero-backdrop";
import { MetadataProviderIssueNotice } from "../components/app/metadata-provider-issue-notice";
import { SourceMark } from "../components/app/source-mark";
import { TmdbAndTvdbAttribution, TmdbAttribution } from "../components/app/tmdb-attribution";
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
import { FILTER_MENU_TRIGGER, FilterChips, type FilterChipOption } from "../components/ui/filter-chips";
import { MenuSelect } from "../components/ui/menu-select";
import { ReleaseFilterMenus } from "../components/app/release-filter-menus";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { Switch } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { wantedStatusPresentation } from "../lib/status-tones";
import { TitleMarkLabel } from "../components/ui/title-mark";
import { formatAirDateTime, formatDateTime as formatPreferenceDateTime, formatRuntime, formatShortDate, useDisplayPreferences } from "../lib/display-preferences";
import { readAlternateTitles, readArtworkExtras, readReleaseFactsByCountry, selectCountryFacts } from "../lib/ui-adapters";
import { seasonMonitorState } from "./season-pass-page";
import { parseSearchCandidates, searchCandidateCount } from "../lib/search-history";
import { useTitleLive, type TitleEventGroups, type TitleGroups } from "../lib/use-title-live";

interface ShowDetailLoaderData {
  acquisitionBlockers: AcquisitionBlockersResponse | null;
  activity: ActivityEventItem[];
  decisions: DecisionExplanationItem[];
  dispatches: DownloadDispatchItem[];
  importRecovery: SeriesImportRecoverySummary;
  inventory: SeriesInventoryDetail;
  libraries: LibraryItem[];
  memberships: LibraryMembership[];
  origins: IntakeTitleOriginItem[];
  removalPreview: MediaRemovalPreview;
  searchHistory: SeriesSearchHistoryItem[];
  series: SeriesListItem;
  metadataIssue: MetadataProviderIssue | null;
  subtitleLanguageOptions: SubtitleLanguageOption[];
  /** #990: which indexers/clients this title's library routes it to, and why — null when the title has no linked library yet. */
  titleRouting: TitleRoutingSnapshot | null;
}

/** #990: everything the "Will use" section needs to say which indexers/clients this title uses, and why. */
interface TitleRoutingSnapshot {
  routing: LibraryRoutingSnapshot;
  indexers: IndexerItem[];
  downloadClients: DownloadClientItem[];
  titleTags: string[];
}

type DetailSection = "episodes" | "destination" | "history";
type EpisodeFilter = "all" | "missing" | "upgrade" | "monitored" | "imported";

interface NumberingDraft {
  seriesType: string;
  numberingScheme: string;
  numberingSource: string;
  /** The orderings this title can actually be picked from (#863) - always includes the currently-saved scheme, even if a later refresh dropped it. */
  availableSchemes: string[];
  /** Every other ordering TMDb publishes that Deluno does not resolve episodes by yet - shown for information only. */
  otherSchemeNames: string[];
  mappings: Record<string, {
    absoluteNumber: string;
    sceneSeasonNumber: string;
    sceneEpisodeNumber: string;
    airDate: string;
  }>;
}

/**
 * The page is read in groups, so a live change re-reads only the groups it can
 * move (#1179) while the loader reads them all.
 */
function showGroups(id: string): TitleGroups<ShowDetailLoaderData> {
  return {
    // The show itself, its libraries, tags and routing.
    title: async () => {
      const [series, metadataIssue, removalPreview, memberships] = await Promise.all([
        fetchJson<SeriesListItem>(`/api/series/${id}`),
        fetchJson<MetadataProviderIssue | null>(`/api/series/${id}/metadata/issue`).catch(() => null),
        fetchJson<MediaRemovalPreview>(`/api/series/${id}/removal-preview`).catch(() => ({ filePaths: [], folderPaths: [], warnings: [] })),
        // The library this show is in, as a person chose it (#530).
        fetchJson<LibraryMembership[]>(`/api/series/${id}/libraries/`).catch(() => [] as LibraryMembership[])
      ]);
      // #990: which indexers/clients this title uses, and why — needs the
      // library only known once `series` itself is back, so it is fetched after.
      const titleRouting = await loadTitleRoutingAsync(series.libraryId, id);
      return { series, metadataIssue, removalPreview, memberships, titleRouting };
    },
    // Every season and episode, with what is held and what is wanted.
    inventory: async () => ({ inventory: await fetchJson<SeriesInventoryDetail>(`/api/series/${id}/inventory`) }),
    // What was searched, sent, decided and noted for this show.
    history: async () => {
      const [searchHistory, dispatches, activity, decisions, origins, acquisitionBlockers] = await Promise.all([
        fetchJson<SeriesSearchHistoryItem[]>(`/api/series/search-history?seriesId=${encodeURIComponent(id)}&take=50`),
        // Archived rows included on purpose: an import archives its dispatch the
        // moment it succeeds, so a history that hides them shows nothing was ever
        // sent for the very shows Deluno did download. Asked for by title, because
        // the newest twenty rows across the whole library are not this show's.
        fetchPageItems<DownloadDispatchItem>(`/api/download-dispatches?mediaType=tv&pageSize=20&includeArchived=true&entityId=${encodeURIComponent(id)}`),
        fetchPageItems<ActivityEventItem>(`/api/activity?relatedEntityType=series&relatedEntityId=${id}&pageSize=20`),
        fetchPageItems<DecisionExplanationItem>(`/api/decisions?relatedEntityType=series&relatedEntityId=${id}&pageSize=40`),
        fetchJson<IntakeTitleOriginItem[]>(`/api/intake-title-origins?mediaType=tv&entityId=${encodeURIComponent(id)}`).catch(() => []),
        // Caught rather than awaited into the failure: a page that will not open
        // because it could not find out why a download is stuck is worse than a
        // page that simply does not mention it.
        fetchJson<AcquisitionBlockersResponse>(`/api/series/${id}/acquisition-blockers`).catch(() => null)
      ]);
      return { searchHistory, dispatches, activity, decisions, origins, acquisitionBlockers };
    },
    recovery: async () => ({
      importRecovery: await fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery?limit=500")
    }),
    // Things that change on their own screens, not with this show.
    shared: async () => {
      const [libraries, subtitleLanguageOptions] = await Promise.all([
        fetchJson<LibraryItem[]>("/api/libraries"),
        // For the per-title override picker (#843).
        fetchJson<SubtitleLanguageOption[]>("/api/subtitle-languages").catch(() => [] as SubtitleLanguageOption[])
      ]);
      return { libraries, subtitleLanguageOptions };
    }
  };
}

/** What a change to the show moves (#1179). */
const SHOW_EVENT_GROUPS: TitleEventGroups = {
  SeriesChanged: ["title", "inventory", "recovery", "history"],
  ImportStateChanged: ["inventory", "history", "recovery"],
  JourneyStageChanged: ["history", "inventory"],
  RecoveryCaseChanged: ["recovery", "inventory"],
  ReleasesTurnedDown: ["history"]
};

export async function showDetailLoader({
  params
}: {
  params: { id?: string };
}): Promise<ShowDetailLoaderData> {
  const groups = showGroups(params.id!);
  const parts = await Promise.all(Object.values(groups).map((read) => read()));
  return Object.assign({}, ...parts) as ShowDetailLoaderData;
}

/**
 * #990: the title page says which indexers/download clients a title will
 * use, and why — the same tag-routing rule the search itself follows
 * (#808), reused from `describeTitleRouting` rather than judged twice.
 * Null when the title has no linked library, since there is nothing routed
 * yet to explain.
 */
async function loadTitleRoutingAsync(libraryId: string | null | undefined, titleId: string): Promise<TitleRoutingSnapshot | null> {
  if (!libraryId) {
    return null;
  }

  const [routing, indexers, downloadClients, titleTagAssignments] = await Promise.all([
    fetchJson<LibraryRoutingSnapshot>(`/api/libraries/${libraryId}/routing`).catch(() => null),
    fetchJson<IndexerItem[]>("/api/indexers").catch(() => [] as IndexerItem[]),
    fetchJson<DownloadClientItem[]>("/api/download-clients").catch(() => [] as DownloadClientItem[]),
    fetchJson<Array<{ tagId: string; name: string }>>(`/api/series/${titleId}/tags`).catch(() => [] as Array<{ tagId: string; name: string }>)
  ]);

  if (!routing) {
    return null;
  }

  return { routing, indexers, downloadClients, titleTags: titleTagAssignments.map((assignment) => assignment.name) };
}

export function ShowDetailPage() {
  const routeData = useLoaderData() as ShowDetailLoaderData;
  // Reads again only what a change to this show can move, as the server announces it (#1179).
  const loaderData = useTitleLive(routeData.series.id, routeData, useMemo(() => showGroups(routeData.series.id), [routeData.series.id]), SHOW_EVENT_GROUPS);
  const { acquisitionBlockers, activity, decisions, dispatches, importRecovery, inventory, libraries, memberships, metadataIssue, origins, removalPreview, searchHistory, series, subtitleLanguageOptions, titleRouting } = loaderData;
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const { preferences, certificationCountry } = useDisplayPreferences();
  const showAbsolute = preferences.showAbsoluteEpisodeNumbers && (inventory.numbering?.seriesType ?? series.seriesType) === "anime";

  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [searchNote, setSearchNote] = useState<SearchNote | null>(null);
  const [isRemoveConfirmationOpen, setIsRemoveConfirmationOpen] = useState(false);
  const [isMetadataOpen, setIsMetadataOpen] = useState(false);
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);
  // #753: sort/filter state for the candidates table, reset each fresh search.
  const [candidateProtocolFilter, setCandidateProtocolFilter] = useState("");
  const [candidateIndexerFilter, setCandidateIndexerFilter] = useState("");
  // Which episode the candidates below belong to, when they came from an
  // episode search rather than a show-wide one. Without it the card would say
  // "Choose a release" for a show with 71 episodes and name none of them.
  const [candidatesFor, setCandidatesFor] = useState<string | null>(null);
  const [openCandidate, setOpenCandidate] = useState<SearchPlanCandidate | null>(null);
  const [duplicateSend, setDuplicateSend] = useState<
    (AlreadyDownloadingPrompt & { candidate: SearchPlanCandidate; force: boolean; overrideReason?: string }) | null
  >(null);
  const [forceReason, setForceReason] = useState<string | null>(null);
  const [openEpisodeId, setOpenEpisodeId] = useState<string | null>(null);
  /** The episode whose file is waiting on a second click to be moved to the recycle bin (#1188). */
  const [confirmingEpisodeFileDelete, setConfirmingEpisodeFileDelete] = useState<string | null>(null);
  // Which language's manual search dialog (#841) is open, or null for none.
  const [manualSearchLanguage, setManualSearchLanguage] = useState<string | null>(null);
  // Which of the three types (plain / hearing-impaired / forced, #923) each
  // subtitle this episode's file has is — cached per episode so reopening the
  // same row does not refetch, the same way the movie page's loader gives it
  // for free but an episode drawer opens on demand.
  const [episodeSubtitles, setEpisodeSubtitles] = useState<Record<string, MediaSubtitleRow[]>>({});
  // #840: what has already been tried for this episode's subtitles.
  // What the drawer shows about the file an episode holds (#1338), read when the drawer opens on an episode with a file.
  const [episodeFiles, setEpisodeFiles] = useState<Record<string, EpisodeFileFacts | null>>({});
  const [episodeSubtitleHistory, setEpisodeSubtitleHistory] = useState<Record<string, MediaSubtitleAttemptHistoryEntry[]>>({});
  const [openSearchId, setOpenSearchId] = useState<string | null>(null);
  const [openDispatchId, setOpenDispatchId] = useState<string | null>(null);
  const [episodeFilter, setEpisodeFilter] = useState<EpisodeFilter>("all");
  const [openSeasons, setOpenSeasons] = useState<number[] | null>(null);
  /** Scopes the History tab down to one episode's own searches (#795). Cleared, it shows the whole series. */
  const [historyEpisodeId, setHistoryEpisodeId] = useState<string | null>(null);
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
  // Which of its libraries the page's actions go through (#530). A show made
  // wanted everywhere by the old add may still be in more than one.
  const [chosenLibraryId, setChosenLibraryId] = useState<string | null>(null);
  const actingLibraryId = memberships.some((item) => item.libraryId === chosenLibraryId)
    ? chosenLibraryId
    : memberships.find((item) => item.libraryId === wantedItem?.libraryId)?.libraryId ?? memberships[0]?.libraryId ?? wantedItem?.libraryId ?? null;
  const libraryQuery = memberships.length > 1 && actingLibraryId ? `libraryId=${encodeURIComponent(actingLibraryId)}` : "";
  const library = actingLibraryId ? libraries.find((item) => item.id === actingLibraryId) ?? null : null;
  // #990: which of this title's library's indexers/download clients Deluno
  // will actually use for it, and why — the tag rule already applied to
  // search and grab (#808), read here instead of judged again.
  const usedIndexers = useMemo(() => {
    if (!titleRouting) return [];
    const indexerTags = new Map(titleRouting.indexers.map((item) => [item.id, item.tags]));
    return describeTitleRouting(
      titleRouting.routing.sources.map((source) => ({
        id: source.indexerId,
        name: source.indexerName,
        tags: indexerTags.get(source.indexerId)
      })),
      titleRouting.titleTags
    );
  }, [titleRouting]);
  const usedDownloadClients = useMemo(() => {
    if (!titleRouting) return [];
    const clientTags = new Map(titleRouting.downloadClients.map((item) => [item.id, item.tags]));
    const clientGroups = new Map(
      titleRouting.downloadClients.map((item) => [
        item.id,
        ["sabnzbd", "nzbget", "usenet"].includes(item.protocol?.trim().toLowerCase() ?? "") ? "usenet" : "torrent"
      ])
    );
    return describeTitleRouting(
      titleRouting.routing.downloadClients.map((client) => ({
        id: client.downloadClientId,
        name: client.downloadClientName,
        tags: clientTags.get(client.downloadClientId),
        group: clientGroups.get(client.downloadClientId) ?? "torrent"
      })),
      titleRouting.titleTags
    );
  }, [titleRouting]);
  const seriesSearches = searchHistory.filter((item) => item.seriesId === series.id);
  // The History tab's own episode filter (#795): narrows the same list down
  // to one episode's searches, rather than asking the server a new question.
  const historyEpisode = historyEpisodeId
    ? inventory.episodes.find((episode) => episode.episodeId === historyEpisodeId) ?? null
    : null;
  const historyFilteredSearches = filterSearchHistoryByEpisode(seriesSearches, historyEpisode?.episodeId ?? null);
  const seriesDispatches = dispatches.filter((item) => item.entityId === series.id);
  const importCases = importRecovery.recentCases.filter(
    (item) => item.title.trim().toLowerCase() === series.title.trim().toLowerCase()
  );
  const { cast, crew } = readStoredCredits(series.metadataJson);
  // #861: region-specific spellings TMDb's alternative_titles endpoint
  // reported, beyond the original title already shown above.
  const alternateTitles = readAlternateTitles(series.metadataJson).filter(
    (candidate) => candidate !== series.title && candidate !== series.originalTitle
  );
  // #864: a transparent logo/wordmark and extra backdrop stills, beyond the
  // poster and backdrop already shown. No banner: TMDb has never reported one.
  const { logoUrl, screenshots } = readArtworkExtras(series.metadataJson);
  // #862: every country TMDb answered for, unreduced, so the certification
  // country setting can pick between them without a re-fetch.
  const releaseFactsByCountry = readReleaseFactsByCountry(series.metadataJson);
  const selectedCountryFacts = selectCountryFacts(releaseFactsByCountry, certificationCountry);
  const otherCountryFacts = releaseFactsByCountry.filter((entry) => entry !== selectedCountryFacts);
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
  // The chips and the summary strip count a list through the same helpers, so one list has one answer (#1338).
  const episodeFilterOptions = useMemo<FilterChipOption<EpisodeFilter>[]>(() => {
    const countOf = (filter: EpisodeFilter) => inventory.episodes.filter((episode) => matchesEpisodeFilter(episode, filter, "")).length;
    return [
      { value: "all", label: "All", count: inventory.episodes.length },
      { value: "missing", label: "Missing", count: countOf("missing") },
      { value: "upgrade", label: "Upgrade", count: countOf("upgrade") },
      { value: "monitored", label: "Monitored", count: countOf("monitored") },
      { value: "imported", label: "On disk", count: countOf("imported") }
    ];
  }, [inventory.episodes]);
  const monitoredCount = inventory.episodes.filter((item) => item.monitored).length;
  // The season switch answers for the whole season, not for the rows a filter leaves
  // showing: with the list filtered to Monitored, 1 of 74 Specials read as all of them.
  const seasonTotals = useMemo(() => {
    const totals = new Map<number, { episodes: number; monitored: number }>();
    for (const item of inventory.episodes) {
      const total = totals.get(item.seasonNumber) ?? { episodes: 0, monitored: 0 };
      total.episodes += 1;
      if (item.monitored) total.monitored += 1;
      totals.set(item.seasonNumber, total);
    }
    return totals;
  }, [inventory.episodes]);
  const openEpisode = inventory.episodes.find((item) => item.episodeId === openEpisodeId) ?? null;

  // Subtitles belong to the episode's own file, not the series (a show's
  // wanted-state bar rolls episodes up, but "which type is this one" is a
  // per-file question) — so the drawer fetches on open rather than the
  // series loader pulling every episode's rows up front.
  useEffect(() => {
    if (!openEpisodeId || episodeSubtitles[openEpisodeId]) {
      return;
    }
    let cancelled = false;
    fetchJson<MediaSubtitleRow[]>(`/api/series/episodes/${openEpisodeId}/subtitles`)
      .then((rows) => {
        if (!cancelled) {
          setEpisodeSubtitles((current) => ({ ...current, [openEpisodeId]: rows }));
        }
      })
      .catch(() => {
        if (!cancelled) {
          setEpisodeSubtitles((current) => ({ ...current, [openEpisodeId]: [] }));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [openEpisodeId, episodeSubtitles]);

  const openEpisodeHasFile = openEpisode?.hasFile ?? false;
  useEffect(() => {
    if (!openEpisodeId || !openEpisodeHasFile || openEpisodeId in episodeFiles) {
      return;
    }
    let cancelled = false;
    fetchJson<EpisodeFileFacts>(`/api/series/episodes/${openEpisodeId}/file`)
      .then((facts) => {
        if (!cancelled) setEpisodeFiles((current) => ({ ...current, [openEpisodeId]: facts }));
      })
      .catch(() => {
        if (!cancelled) setEpisodeFiles((current) => ({ ...current, [openEpisodeId]: null }));
      });
    return () => {
      cancelled = true;
    };
  }, [openEpisodeId, openEpisodeHasFile, episodeFiles]);

  useEffect(() => {
    if (!openEpisodeId || episodeSubtitleHistory[openEpisodeId]) {
      return;
    }
    let cancelled = false;
    fetchJson<MediaSubtitleAttemptHistoryEntry[]>(`/api/series/episodes/${openEpisodeId}/subtitles/history`)
      .then((rows) => {
        if (!cancelled) {
          setEpisodeSubtitleHistory((current) => ({ ...current, [openEpisodeId]: rows }));
        }
      })
      .catch(() => {
        if (!cancelled) {
          setEpisodeSubtitleHistory((current) => ({ ...current, [openEpisodeId]: [] }));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [openEpisodeId, episodeSubtitleHistory]);

  // Re-reads one episode's subtitle rows after a resync or an out-of-sync
  // action changes them — the cache above only fills on first open, so an
  // action taken from an already-open drawer needs its own refresh.
  function refetchEpisodeSubtitles(episodeId: string) {
    fetchJson<MediaSubtitleRow[]>(`/api/series/episodes/${episodeId}/subtitles`)
      .then((rows) => setEpisodeSubtitles((current) => ({ ...current, [episodeId]: rows })))
      .catch(() => {});
    fetchJson<MediaSubtitleAttemptHistoryEntry[]>(`/api/series/episodes/${episodeId}/subtitles/history`)
      .then((rows) => setEpisodeSubtitleHistory((current) => ({ ...current, [episodeId]: rows })))
      .catch(() => {});
  }

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
  /*
    #612: the grab endpoint re-derives this exact decision server-side
    (`AcquisitionDecisionPipeline.IsSafeForAutomaticDispatch`) and refuses
    anything that is not Deluno's own preferred, cutoff-met, non-downgrade
    pick with a 400 unless `force` is set — so an Eligible, Acceptable or
    Equivalent candidate's blue "Send to downloads" was a button that could
    never work, and the person only learned that after clicking it. The
    preview response already carries `decisionStatus`, `meetsCutoff` and
    `qualityDelta` per candidate, so the drawer can know this before the
    click and open with Force already in view instead.
  */
  const openCandidateNeedsForce = openCandidate ? candidateRequiresForce(openCandidate) : false;
  const forceRequiredNotice = openCandidate && openCandidateNeedsForce
    ? `Deluno did not pick this release automatically (${candidateLabel(openCandidate)}). Force it below to send anyway.`
    : null;
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

  // #1188: the file goes to the recycle bin, where it can be restored.
  async function handleEpisodeFileDelete(episodeId: string) {
    setBusyAction(`episode-file-delete:${episodeId}`);

    try {
      const response = await authedFetch(`/api/series/episodes/${episodeId}/file`, { method: "DELETE" });
      if (!response.ok) throw new Error("episode-file-delete-failed");
      toast.success("File moved to the recycle bin. This episode now shows as missing and Deluno will look for it again. You can restore it from the recycle bin.");
      setConfirmingEpisodeFileDelete(null);
      revalidator.revalidate();
    } catch {
      toast.error("Could not delete this episode's file.");
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

  /**
   * A whole season's monitor switch, right on the show page (#991). This
   * calls the exact same Season Pass endpoint the dedicated bulk screen uses
   * (`PUT /api/series/season-pass/monitoring`), so unmonitoring a season here
   * cancels its in-flight downloads through the same path Season Pass does
   * (#1028) — there is only one place that logic lives.
   */
  async function handleSeasonMonitoring(seasonNumber: number, monitored: boolean) {
    setBusyAction(`season-monitor-${seasonNumber}`);

    try {
      const response = await authedFetch("/api/series/season-pass/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ scopes: [{ seriesId: series.id, seasonNumber }], monitored })
      });
      if (!response.ok) throw new Error("season-monitoring-failed");
      revalidator.revalidate();
    } catch {
      toast.error(`${formatSeasonLabel(seasonNumber)} could not be updated.`);
    } finally {
      setBusyAction(null);
    }
  }

  async function handleMonitorNewEpisodesPreset(monitorNewEpisodes: boolean) {
    setBusyAction("monitor-new-episodes");

    try {
      const response = await authedFetch(`/api/series/${series.id}/monitor-new-episodes`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ monitorNewEpisodes })
      });
      if (!response.ok) throw new Error("series-monitor-new-episodes-failed");
      revalidator.revalidate();
    } catch {
      toast.error("This show's new-episode preset could not be changed.");
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

      const result = (await response.json()) as { successCount?: number; results?: Array<{ metadata?: Record<string, string | null> | null }> };
      if ((result.successCount ?? 0) !== 1) throw new Error("series-remove-failed");
      showRemovalToast(series.title, result.results?.[0]?.metadata, (to) => navigate(to));
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
        body: JSON.stringify({ libraryId: actingLibraryId ?? wantedItem.libraryId, hours: 24 })
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
        body: JSON.stringify({ libraryId: actingLibraryId ?? wantedItem.libraryId })
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

    const numberingScheme = detail?.numberingScheme ?? series.numberingScheme ?? "standard";
    const availableSchemes = detail?.availableNumberingSchemes?.length
      ? detail.availableNumberingSchemes
      : ["standard"];

    setNumberingDraft({
      seriesType: detail?.seriesType ?? series.seriesType ?? "standard",
      numberingScheme,
      numberingSource: detail?.numberingSource ?? series.numberingSource ?? "provider",
      // The currently-saved scheme is always offered, even if a later
      // provider refresh dropped it (a DVD-order group TMDb removed, say) -
      // a saved choice must stay visible and explained, never vanish from
      // its own dropdown.
      availableSchemes: availableSchemes.includes(numberingScheme)
        ? availableSchemes
        : [...availableSchemes, numberingScheme],
      otherSchemeNames: detail?.otherNumberingSchemeNames ?? [],
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
    // Every fresh search starts from every candidate, not the few a past filter left (#1338).
    setCandidateProtocolFilter("");
    setCandidateIndexerFilter("");
    setBusyAction(`${mode}-search`);
    setSearchNote({ state: "searching", label: mode === "interactive" ? "Comparing releases from your indexers..." : "Searching your indexers..." });
    let searchResponse: Response | null = null;

    try {
      const query = [mode === "interactive" ? "mode=preview" : "", libraryQuery].filter(Boolean).join("&");
      searchResponse = await authedFetch(`/api/series/${series.id}/search${query ? `?${query}` : ""}`, { method: "POST" });
      if (!searchResponse.ok) throw new Error("series-search-failed");
      const response = searchResponse;

      const payload = (await response.json()) as {
        outcome?: string;
        summary?: string;
        releaseName?: string | null;
        indexerName?: string | null;
        dispatchStatus?: string | null;
        dispatchMessage?: string | null;
        plannedEpisodes?: number;
        reason?: string;
        candidates?: SearchPlanCandidate[];
        failures?: IntegrationFailure[];
      };
      const best = payload.releaseName ? `${payload.releaseName}${payload.indexerName ? ` via ${payload.indexerName}` : ""}` : null;
      const failureNotice = formatSearchFailureNotice(payload.failures);
      setReleaseCandidates(mode === "interactive" ? payload.candidates ?? [] : []);
      setCandidatesFor(null);

      if (mode === "interactive") {
        const found = payload.candidates?.length ?? 0;
        setSection("episodes");
        if (found) {
          toast.success(`${found} release${found === 1 ? "" : "s"} compared. Choose one below.`, failureNotice ? { description: failureNotice } : undefined);
          setSearchNote({ state: "done", tone: "success", title: `${found} release${found === 1 ? "" : "s"} compared. Choose one below.`, description: failureNotice ?? undefined });
        } else {
          const explained = describeSearchReason(payload.reason, payload.summary ?? "No releases matched this show's Library Profile.");
          const action = explained.action;
          toast.info(explained.title, {
            description: [explained.description, failureNotice].filter(Boolean).join(" "),
            action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
          });
          setSearchNote({ state: "done", tone: "info", title: explained.title, description: [explained.description, failureNotice].filter(Boolean).join(" ") || undefined });
        }
      } else {
        const notice = describeSearchNow(payload);
        if (notice) {
          const description = [notice.description, failureNotice].filter(Boolean).join(" ");
          toast[notice.tone](notice.title, description ? { description } : undefined);
          setSearchNote({ state: "done", tone: notice.tone, title: notice.title, description: description || undefined });
        } else {
          const explained = describeSearchReason(payload.reason, "Search finished with no accepted release.");
          const action = explained.action;
          toast.info(explained.title, {
            description: [explained.description, failureNotice].filter(Boolean).join(" "),
            action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
          });
          setSearchNote({ state: "done", tone: "info", title: explained.title, description: [explained.description, failureNotice].filter(Boolean).join(" ") || undefined });
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
      setSearchNote({ state: "done", tone: "error", title: explained.title, description: explained.description });
    } finally {
      setBusyAction(null);
    }
  }

  async function handleGrabCandidate(
    candidate: SearchPlanCandidate,
    force = false,
    overrideReason?: string,
    duplicateAction?: DuplicateSendAction
  ) {
    setBusyAction(force ? "force-grab" : "grab");
    let grabResponse: Response | null = null;

    try {
      const response = await authedFetch(`/api/series/${series.id}/grab${libraryQuery ? `?${libraryQuery}` : ""}`, {
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
          overrideReason: force ? overrideReason || `User forced this release despite Deluno's decision: ${candidate.summary}` : null,
          duplicateAction: duplicateAction ?? null
        })
      });
      grabResponse = response;
      const alreadyDownloading = await readAlreadyDownloading(response);
      if (alreadyDownloading) {
        setDuplicateSend({ ...alreadyDownloading, candidate, force, overrideReason });
        return;
      }
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
      setCandidatesFor(null);
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

  // One thing per button (#1175): "send" searches and sends the best release, "choose" sends nothing and lists what it found.
  async function handleEpisodeSearch(episodeIds: string[], mode: "send" | "choose" = "send") {
    if (!episodeIds.length) return;
    setBusyAction(mode === "choose" ? "episode-choose" : "episode-search");
    setCandidateProtocolFilter("");
    setCandidateIndexerFilter("");
    let searchResponse: Response | null = null;

    try {
      const response = await authedFetch(`/api/series/${series.id}/episodes/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds, mode })
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
        alreadyDownloading?: string[];
        episodes?: Array<{
          episodeId: string;
          seasonNumber: number;
          episodeNumber: number;
          title?: string | null;
          summary?: string | null;
          releaseName?: string | null;
          comparedCount?: number;
          candidates?: SearchPlanCandidate[];
        }>;
        failures?: IntegrationFailure[];
      };
      const searched = payload.searchedEpisodes ?? episodeIds.length;
      const matched = payload.matchedCount ?? 0;

      // Show what the search actually compared. Counts alone left a person
      // with no way to see which release Deluno chose, or to choose another.
      const withCandidates = (payload.episodes ?? []).filter((item) => (item.candidates?.length ?? 0) > 0);
      if (mode === "send") {
        // Sending lists nothing to choose from: the toast says what was sent, or why nothing was.
        setReleaseCandidates([]);
        setCandidatesFor(null);
      } else if (withCandidates.length === 1) {
        const only = withCandidates[0]!;
        setReleaseCandidates(only.candidates ?? []);
        setCandidatesFor(`S${String(only.seasonNumber).padStart(2, "0")}E${String(only.episodeNumber).padStart(2, "0")}${only.title ? ` · ${only.title}` : ""}`);
        setOpenEpisodeId(null);
        setSection("episodes");
      } else if (withCandidates.length === 0) {
        setReleaseCandidates([]);
        setCandidatesFor(null);
      }
      const failureNotice = formatSearchFailureNotice(payload.failures);
      const compared = withCandidates.reduce((total, item) => total + (item.candidates?.length ?? 0), 0);
      // Candidates on the page beat any reason code. A search that produced
      // releases to choose from succeeded, whatever it chose to call itself -
      // and telling a person nothing was found while the list sits under the
      // toast sends them off to fix something that works.
      if (payload.alreadyDownloading?.length) {
        toast.info("Already downloading, so nothing more was sent.", { description: payload.alreadyDownloading.join(" ") });
      } else if (compared > 0) {
        toast.success(
          `Compared ${compared} release${compared === 1 ? "" : "s"}. Choose one below.`,
          failureNotice ? { description: failureNotice } : undefined
        );
      } else if (payload.reason && payload.reason !== "ok") {
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

  // #753: filter the candidates table without touching the fetched order the
  // server returned. The column headers sort it (#1119); the server's ranking
  // is what shows until one is chosen.
  const candidateIndexerOptions = [...new Set(releaseCandidates.map((candidate) => candidate.indexerName))];
  const candidateProtocolOptions = [...new Set(releaseCandidates.map((candidate) => candidate.indexerProtocol).filter((value): value is string => Boolean(value)))];
  const visibleCandidates = filterCandidates(releaseCandidates, { protocol: candidateProtocolFilter, indexerName: candidateIndexerFilter });

  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-(--page-gap)">
      {/* One toolbar: which part of the show you want, where you came from, and
          the two searches. The topbar names the section, the hero names the show. */}
      <PageToolbar
        sections={{
          label: "Section",
          value: section,
          onValueChange: (next) => setSection(next as DetailSection),
          options: [
            { value: "episodes", label: "Episodes" },
            { value: "destination", label: "Destination" },
            { value: "history", label: "History" }
          ]
        }}
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

      <SearchResultNote note={searchNote} />

      <MetadataProviderIssueNotice
        issue={metadataIssue}
        subjectLabel="show"
        acknowledgeUrl={`/api/series/${series.id}/metadata/issue/acknowledge`}
        onAcknowledged={() => revalidator.revalidate()}
        onFindAnother={() => setIsMetadataOpen(true)}
        onRetry={() => void handleMetadataRefresh()}
      />

      <Card className="relative isolate min-w-0 min-h-76 overflow-hidden border-primary/25 bg-card">
        <HeroBackdrop url={series.backdropUrl} />
        <CardContent className="relative p-(--tile-pad) sm:p-[calc(var(--tile-pad)*1.15)]">
          <div className="grid items-start gap-(--grid-gap) md:grid-cols-[16rem_minmax(0,1fr)] xl:grid-cols-[16rem_minmax(0,1fr)_22rem]">
            {series.posterUrl ? (
              <ArtworkImage src={series.posterUrl} alt={`${series.title} poster`} className="h-96 w-64 justify-self-center rounded-2xl border border-white/15 bg-surface-1 object-cover shadow-2xl md:justify-self-start" />
            ) : (
              <div className="flex h-96 w-64 justify-self-center items-center justify-center rounded-2xl border border-hairline bg-surface-1 px-3 text-center text-xs text-muted-foreground md:justify-self-start">No artwork yet</div>
            )}
            <div className="min-w-0 self-start xl:flex xl:min-h-96 xl:flex-col">
              <p className="text-(length:--section-eyebrow-size) font-bold uppercase tracking-[0.18em] text-primary">TV series</p>
              {/* #864: a transparent logo/wordmark, where TMDb has one, above the title it labels. */}
              {logoUrl ? <ArtworkImage fallback={null} src={logoUrl} alt="" className="mt-1 h-12 w-auto max-w-[18rem] object-contain object-left" /> : null}
              <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 xl:flex-nowrap">
                <h1 className="font-display text-4xl font-semibold tracking-tight text-foreground sm:text-5xl xl:min-w-0 xl:truncate xl:text-3xl" title={metaText("LocalizedTitle") ?? series.title}>{metaText("LocalizedTitle") ?? series.title}</h1>
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
              {metaText("LocalizedTitle") ? (
                <p className="mt-1 text-sm text-muted-foreground">
                  Original title {series.originalTitle ?? series.title}
                  {series.originalTitle && series.originalTitle !== series.title ? ` · Deluno name ${series.title}` : ""}
                </p>
              ) : series.originalTitle && series.originalTitle !== series.title ? <p className="mt-1 text-sm text-muted-foreground">Also known as {series.originalTitle}</p> : null}
              {alternateTitles.length ? (
                <p className="mt-1 text-sm text-muted-foreground" title="Used to match a release filed under a region-specific spelling">
                  Alternate titles: {alternateTitles.join(", ")}
                </p>
              ) : null}
              <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
                {metaText("Certification") ? (
                  <span className="rounded-sm border border-hairline px-1.5 py-px text-xs font-bold uppercase tracking-wide" title="Classification">
                    {metaText("Certification")}
                  </span>
                ) : null}
                {series.startYear ? <span>{series.startYear}</span> : null}
                {series.runtimeMinutes ? (
                  <span>{formatRuntime(series.runtimeMinutes, preferences)}</span>
                ) : null}
              </div>
              <div className="mt-4 flex flex-wrap gap-2 xl:mt-2 xl:max-h-7 xl:shrink-0 xl:overflow-hidden" title={series.genres ?? undefined}>
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
              <dl className="mt-2 grid grid-cols-2 gap-x-6 gap-y-1.5 sm:grid-cols-3 xl:shrink-0 2xl:grid-cols-4">
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
                    <dt className="text-(length:--type-micro) font-semibold uppercase tracking-widest text-muted-foreground">{fact.label}</dt>
                    <dd className="truncate max-md:whitespace-normal max-md:break-words text-sm text-foreground" title={String(fact.value)}>{fact.value}</dd>
                  </div>
                ))}
              </dl>
              <TitleSynopsis text={series.overview ?? "No overview has been stored yet. Refresh metadata when you want Deluno to enrich this series."} />
            </div>
            <aside className="w-full self-start rounded-xl border border-white/10 bg-card/80 p-3 backdrop-blur-xs xl:min-h-96">
              <p className="text-(length:--type-micro) font-bold uppercase tracking-[0.18em] text-muted-foreground">Ratings &amp; IDs</p>
              <p className="mt-0.5 text-xs text-muted-foreground">The metadata Deluno is using</p>
              <div className="mt-2"><RatingStrip ratings={series.ratings} fallbackRating={series.rating} /></div>
              {/* Source, IMDb and TheTVDB on one line (#1058): the panel may not outgrow the poster. */}
              <div className="mt-3 flex min-w-0 items-center gap-x-2 overflow-hidden whitespace-nowrap border-t border-hairline pt-3 text-xs">
                {series.metadataProvider ? <SourceMark source={series.metadataProvider.toLowerCase()} label={series.metadataProvider.toUpperCase()} /> : <span className="font-medium text-foreground">Not linked</span>}
                <span aria-hidden className="text-muted-foreground">·</span>
                <span className="flex min-w-0 items-center gap-1.5"><SourceMark source="imdb" label="IMDb" /><span className="truncate font-mono font-medium text-foreground">{series.imdbId ?? "—"}</span></span>
                {(series.tvDbId ?? metaText("TvDbId")) ? (
                  <>
                    <span aria-hidden className="text-muted-foreground">·</span>
                    <span className="flex min-w-0 items-center gap-1.5"><span className="text-muted-foreground">TheTVDB</span><span className="truncate font-mono font-medium text-foreground">{series.tvDbId ?? metaText("TvDbId")}</span></span>
                  </>
                ) : null}
              </div>
              {/* One short credit line for both sources, so the panel still fits beside the poster. */}
              {metaText("TvDbId")
                ? <TmdbAndTvdbAttribution className="mt-1.5 text-(length:--type-micro) leading-tight" />
                : <TmdbAttribution className="mt-1.5 text-(length:--type-micro) leading-tight" />}
              <TitleActionGrid>
              <Button variant="outline" className="w-full" onClick={() => void handleMetadataRefresh()} disabled={busyAction !== null}>
                {busyAction === "metadata-refresh" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                Refresh metadata
              </Button>
              <TitleFolderActions
                variant="series"
                titleId={series.id}
                titleName={series.title}
                onChanged={() => revalidator.revalidate()}
                disabled={busyAction !== null}
              />
              <Button variant="outline" className="w-full" onClick={() => setIsMetadataOpen(true)}>
                <Pencil className="h-4 w-4" />
                Edit metadata
              </Button>
              <Button variant="destructive" className="w-full" disabled={busyAction !== null} onClick={() => setIsRemoveConfirmationOpen(true)}>
                <Trash2 className="hidden h-4 w-4 min-[420px]:block" />
                Remove from Deluno
              </Button>
              </TitleActionGrid>
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
      {/* #864: extra backdrop stills beyond the single hero backdrop already shown above. */}
      {screenshots.length ? (
        <Card>
          <CardContent className="p-4">
            <p className="text-(length:--type-micro) font-bold uppercase tracking-[0.18em] text-muted-foreground">Screenshots</p>
            <div className="mt-2 flex gap-3 overflow-x-auto pb-1">
              {screenshots.map((url) => (
                <ArtworkImage key={url} fallback={null} src={url} alt="" className="h-32 w-56 shrink-0 rounded-lg border border-hairline object-cover" />
              ))}
            </div>
          </CardContent>
        </Card>
      ) : null}
      {/*
        #862: a show's certification is per country (release dates are a
        movie-only TMDb concept, so only Certification is populated here).
        The certification country setting's pick leads; the rest stay
        reachable underneath.
      */}
      {releaseFactsByCountry.length ? (
        <Card>
          <CardContent className="p-4">
            <p className="text-(length:--type-micro) font-bold uppercase tracking-[0.18em] text-muted-foreground">Certification by country</p>
            <div className="mt-2 space-y-2 text-sm">
              {selectedCountryFacts ? <CountryReleaseRow facts={selectedCountryFacts} preferences={preferences} highlighted /> : null}
              {otherCountryFacts.map((facts) => (
                <CountryReleaseRow key={facts.country} facts={facts} preferences={preferences} />
              ))}
            </div>
          </CardContent>
        </Card>
      ) : null}

      {/*
        Above "Next step" on purpose. Advice about what to do next is worth
        nothing to somebody whose last three attempts went nowhere for a reason
        the screen never mentioned.
      */}
      <AcquisitionBlockersCard
        blockers={acquisitionBlockers}
        libraryId={libraryQuery ? actingLibraryId : null}
        route="/api/series"
        mediaId={series.id}
        disabled={busyAction !== null}
        onForced={() => revalidator.revalidate()}
      />

      <RetentionTitleCard entityId={series.id} onChanged={() => revalidator.revalidate()} />

      {nextStep ? (
        <ListCard title="Next step" count={nextStep.eyebrow}>
          <ListTable
            chevron={false}
            columns={[{ label: "What Deluno suggests" }, { label: "Action", width: LIST_TRACK.action, align: "end", mobile: true }]}
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
            <ListCard
              title={candidatesFor ? `Choose a release for ${candidatesFor}` : "Choose a release"}
              count={`${visibleCandidates.length} of ${releaseCandidates.length} candidate${releaseCandidates.length === 1 ? "" : "s"}`}
              actions={<ReleaseFilterMenus protocols={candidateProtocolOptions} protocol={candidateProtocolFilter} onProtocolChange={setCandidateProtocolFilter} indexers={candidateIndexerOptions} indexer={candidateIndexerFilter} onIndexerChange={setCandidateIndexerFilter} />}
            >
              <ListTable
                // The server's ranking is the order until a header is clicked.
                listId="show-release-candidates"
                items={visibleCandidates}
                columns={[
                  { id: "release", label: "Release", sort: (candidate) => candidate.releaseName.toLowerCase() },
                  { id: "quality", label: "Quality", mobile: true, sort: (candidate) => candidate.quality.toLowerCase() },
                  { id: "protocol", label: "Protocol", sort: (candidate) => candidateSortValue(candidate, "protocol") },
                  { id: "age", label: "Age", sort: (candidate) => candidateSortValue(candidate, "age") },
                  { id: "seeders", label: "Seeders/Leechers", sort: (candidate) => candidateSortValue(candidate, "seeders") },
                  { id: "grabs", label: "Grabs", sort: (candidate) => candidateSortValue(candidate, "grabs") },
                  { id: "size", label: "Size", sort: (candidate) => candidateSortValue(candidate, "size") },
                  { id: "decision", label: "Decision", width: LIST_TRACK.status, mobile: true, sort: (candidate) => toneSortValue(candidateTone(candidate), candidateLabel(candidate)) }
                ]}
              >
                {(rows) => rows.map((candidate) => (
                  <ListRow
                    key={`${candidate.indexerName}:${candidate.releaseName}`}
                    onClick={() => {
                      setOpenCandidate(candidate);
                      // Open with Force already expanded when the plain send
                      // cannot work, the same default reason the Force
                      // button itself fills in — never a collapsed toggle
                      // the person has to find after a failed click first.
                      setForceReason(candidateRequiresForce(candidate) && candidate.downloadUrl ? (candidate.summary ?? "") : null);
                    }}
                    selected={openCandidate?.releaseName === candidate.releaseName}
                  >
                    <ListNameCell
                      name={candidate.releaseName}
                      sub={`${candidate === visibleCandidates[0] && canWinSearch(candidate) ? "Best match · " : ""}${candidate.indexerName}`}
                    />
                    <ListCell primary={candidate.quality} mobile />
                    <ListCell primary={formatProtocol(candidate.indexerProtocol)} />
                    <ListCell primary={formatReleaseAge(candidate.releaseAgeHours)} />
                    <ListCell primary={formatSeedersAndLeechers(candidate.seeders, candidate.leechers)} />
                    <ListCell primary={formatGrabs(candidate.grabs)} />
                    <ListCell primary={candidate.sizeBytes ? formatBytes(candidate.sizeBytes) : "—"} />
                    <ListCell mobile>
                      <Chip tone={candidateTone(candidate)}>{candidateLabel(candidate)}</Chip>
                      {candidate.decisionStatus?.toLowerCase() === "rejected" && candidate.decisionReasons?.[0] ? (
                        <span className="mt-1 block text-(length:--type-caption) text-muted-foreground">{plainCandidateReason(candidate.decisionReasons[0])}</span>
                      ) : null}
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            </ListCard>
          ) : null}

          <ListCard
            title={`${visibleSeasons.filter((season) => expanded.includes(season.seasonNumber)).reduce((total, season) => total + season.episodes.length, 0)} shown`}
            titleCase={false}
            filter={{ value: query, onChange: setQuery, placeholder: "Filter by code or title" }}
            actions={
              <>
                <FilterChips<EpisodeFilter>
                  label="Filter episodes"
                  size="sm"
                  value={episodeFilter}
                  onValueChange={setEpisodeFilter}
                  options={episodeFilterOptions}
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
                  Search and send these
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
                // Grouped by season, so the columns move but the rows are not re-sorted.
                listId="show-episodes"
                columns={[
                  { id: "episode", label: "Episode" },
                  { id: "aired", label: "Aired" },
                  { id: "lastSearch", label: "Last search" },
                  { id: "status", label: "Status", width: LIST_TRACK.status },
                  { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true }
                ]}
              >
                {visibleSeasons.map((season) => (
                  <Fragment key={season.seasonNumber}>
                    <ListGroupHeader
                      label={formatSeasonLabel(season.seasonNumber)}
                      detail={`${season.episodes.length} episodes · ${season.importedCount} on disk · ${season.missingCount} missing${season.upcomingCount ? ` · ${season.upcomingCount} upcoming` : ""}`}
                      actions={
                        <>
                        <label className="inline-flex select-none items-center gap-2 text-xs text-muted-foreground">
                          Monitor season
                          <Switch
                            aria-label={`Monitor ${formatSeasonLabel(season.seasonNumber)}`}
                            checked={seasonMonitorState(seasonTotals.get(season.seasonNumber)?.episodes ?? season.episodes.length, seasonTotals.get(season.seasonNumber)?.monitored ?? season.monitoredCount) === "all"}
                            disabled={busyAction !== null}
                            onCheckedChange={(checked) => void handleSeasonMonitoring(season.seasonNumber, checked)}
                          />
                          {busyAction === `season-monitor-${season.seasonNumber}` ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                        </label>
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
                        <ListNameCell name={formatEpisodeCode(episode, showAbsolute)} sub={episode.title ?? "Episode title pending"} />
                        <ListCell primary={episode.airDateUtc ? formatAirDateTime(episode.airDateUtc, preferences) : "—"} />
                        <ListCell
                          primary={episode.lastSearchUtc ? formatPreferenceDateTime(episode.lastSearchUtc, preferences) : "Never"}
                          secondary={episode.lastSearchResult ? plainSearchResult(episode.lastSearchResult) : undefined}
                        />
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

          {memberships.length > 0 ? (
            <LibraryMembershipCard
              route="/api/series"
              mediaId={series.id}
              noun="show"
              libraries={libraries.filter((item) => item.mediaType === "tv")}
              memberships={memberships}
              selectedLibraryId={actingLibraryId}
              onSelect={setChosenLibraryId}
              onChanged={() => revalidator.revalidate()}
            />
          ) : null}

          <ListCard title="Routing and destination" count={library?.name ?? "No library linked"}>
            <ListTable chevron={false} columns={[{ label: "Setting" }, { label: "Value", width: "minmax(0,2fr)", mobile: true }]}>
              {[
                ["Library", library?.name ?? "Not linked"],
                ["Root folder", library?.rootPath || "No root configured"],
                ["Downloads folder", library?.downloadsPath || "Download client default"],
                ["Import workflow", importWorkflowLabel(library?.importWorkflow)],
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

          {usedIndexers.length > 0 || usedDownloadClients.length > 0 ? (
            <ListCard
              title="Indexers and download clients"
              count={`${[...usedIndexers, ...usedDownloadClients].filter((entry) => entry.used).length} of ${usedIndexers.length + usedDownloadClients.length} used`}
            >
              <ListTable
                chevron={false}
                listId="show-connections"
                items={[...usedIndexers, ...usedDownloadClients]}
                columns={[
                  { id: "connection", label: "Connection", sort: (entry) => entry.name.toLowerCase() },
                  { id: "why", label: "Why", width: "minmax(0,2fr)", mobile: true, sort: (entry) => `${entry.used ? 0 : 1}-${entry.reason.toLowerCase()}` }
                ]}
              >
                {(rows) => rows.map((entry) => (
                  <ListRow key={entry.id}>
                    <ListNameCell name={entry.name} />
                    <ListCell mobile>
                      <Chip tone={entry.used ? "ok" : "idle"}>{entry.used ? "Used" : "Not used"}</Chip>
                      <span className="mt-1 block text-(length:--type-caption) text-muted-foreground">{entry.reason}</span>
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            </ListCard>
          ) : null}

          <SubtitleOverrideCard
            basePath={`/api/series/${series.id}`}
            languages={series.subtitleLanguagesOverride}
            mode={series.subtitleModeOverride}
            libraryLanguages={library?.subtitleLanguages ?? []}
            options={subtitleLanguageOptions}
            onChanged={() => revalidator.revalidate()}
          />

          <ListCard title="Automation" count={series.monitored ? "Monitored" : "Unmonitored"}>
            <ListTable chevron={false} columns={[{ label: "Control" }, { label: "Action", width: LIST_TRACK.action, align: "end", mobile: true }]}>
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
                  name="Monitor new episodes"
                  sub="When a refresh discovers an episode this show has never listed before, monitor it automatically."
                />
                <div role="cell" className="flex justify-end">
                  <Switch
                    aria-label="Monitor new episodes"
                    checked={series.monitorNewEpisodes ?? true}
                    disabled={busyAction !== null}
                    onCheckedChange={(checked) => void handleMonitorNewEpisodesPreset(checked)}
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
              <ListTable
                chevron={false}
                listId="show-origins"
                items={origins}
                columns={[
                  { id: "source", label: "Source", sort: (origin) => origin.sourceName.toLowerCase() },
                  { id: "provider", label: "Provider", mobile: true, sort: (origin) => origin.provider.toLowerCase() },
                  { id: "firstSeen", label: "First seen", sort: (origin) => new Date(origin.firstSeenUtc) }
                ]}
              >
                {(rows) => rows.map((origin) => (
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
              <ListTable
                chevron={false}
                listId="show-import-issues"
                items={importCases}
                columns={[
                  { id: "issue", label: "Issue", sort: (item) => formatFailureKind(item.failureKind).toLowerCase() },
                  { id: "whatToDo", label: "What to do", width: "minmax(0,1.4fr)", sort: (item) => (item.recommendedAction ?? "").toLowerCase() },
                  { id: "action", label: "Action", width: LIST_TRACK.action, align: "end", mobile: true, movable: false, sortable: false }
                ]}
              >
                {(rows) => rows.map((item) => {
                  // #930, #957: a file missing or unmatched from a processor's
                  // output whose own name resolved to a canonical episode
                  // carries that episode's id, so Search can act on it
                  // directly rather than sending a person to look it up
                  // themselves.
                  const isProcessorOutputCase = item.failureKind === "processor-output-missing" || item.failureKind === "processor-unmatched-output";
                  const missingEpisodeId = isProcessorOutputCase ? episodeIdIn(item.detailsJson) : null;
                  return (
                    <ListRow key={item.id}>
                      <ListNameCell name={formatFailureKind(item.failureKind)} sub={item.summary} />
                      <ListCell primary={item.recommendedAction} />
                      <div role="cell" className="flex justify-end gap-2">
                        {missingEpisodeId ? (
                          <Button type="button" size="sm" variant="outline" onClick={() => void handleEpisodeSearch([missingEpisodeId])} disabled={busyAction !== null}>
                            {busyAction === "episode-search" ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                            Search
                          </Button>
                        ) : null}
                        {isProcessorOutputCase ? (
                          <Button type="button" size="sm" variant="outline" onClick={() => navigate("/queue?manualImportMediaType=tv")}>
                            Import manually
                          </Button>
                        ) : null}
                        <Button type="button" size="sm" variant="outline" onClick={() => void handleDismissImportCase(item.id)} disabled={busyAction === `import-${item.id}`}>
                          {busyAction === `import-${item.id}` ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                          Dismiss
                        </Button>
                      </div>
                    </ListRow>
                  );
                })}
              </ListTable>
            </ListCard>
          ) : null}

          <DecisionExplanationList decisions={decisions} />

          <ListCard
            title="Searches"
            count={historyFilteredSearches.length ? `Latest ${Math.min(historyFilteredSearches.length, 12)} of ${historyFilteredSearches.length}` : undefined}
            actions={
              <MenuSelect
                label="Filter history to episode"
                value={historyEpisodeId ?? ""}
                onChange={(next) => setHistoryEpisodeId(next || null)}
                options={[
                  { value: "", label: "Every episode" },
                  ...inventory.episodes.map((episode) => ({
                    value: episode.episodeId,
                    label: `${formatEpisodeCode(episode)} · ${episode.title ?? "Episode title pending"}`
                  }))
                ]}
                menuWidth="max-h-[min(22rem,60vh)] overflow-y-auto"
                align="end"
                triggerClassName={FILTER_MENU_TRIGGER}
              />
            }
          >
            {historyFilteredSearches.length === 0 ? (
              <ListEmpty
                title={historyEpisode ? `No searches yet for ${formatEpisodeCode(historyEpisode)}` : "No searches yet"}
                description={
                  historyEpisode
                    ? "Manual and scheduled searches for this episode appear here with their outcomes and explanations."
                    : "Manual and scheduled searches for this show — series, season and episode alike — appear here with their outcomes and explanations."
                }
              />
            ) : (
              <ListTable
                // Sorts the latest 12 shown, not the whole history.
                listId="show-searches"
                items={historyFilteredSearches.slice(0, 12)}
                columns={[
                  { id: "release", label: "Release", sort: (item) => (item.releaseName ?? "").toLowerCase() },
                  { id: "scope", label: "Scope", mobile: true, sort: (item) => formatSearchScope(item).toLowerCase() },
                  { id: "trigger", label: "Trigger", sort: (item) => formatTriggerKind(item.triggerKind).toLowerCase() },
                  { id: "when", label: "When", sort: (item) => new Date(item.createdUtc) },
                  { id: "outcome", label: "Outcome", width: LIST_TRACK.status, sort: (item) => toneSortValue(searchOutcomeTone(item.outcome), formatSearchOutcome(item.outcome)) }
                ]}
              >
                {(rows) => rows.map((item) => (
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
              <ListTable
                listId="show-dispatches"
                items={seriesDispatches.slice(0, 8)}
                columns={[
                  { id: "release", label: "Release", sort: (item) => item.releaseName.toLowerCase() },
                  { id: "client", label: "Client", mobile: true, sort: (item) => item.downloadClientName.toLowerCase() },
                  { id: "when", label: "When", sort: (item) => new Date(item.createdUtc) },
                  { id: "status", label: "Status", width: LIST_TRACK.status, sort: (item) => toneSortValue(dispatchTone(item.status), formatDispatchStatus(item.status)) }
                ]}
              >
                {(rows) => rows.map((item) => (
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

          <ListCard
            title="Activity"
            count={activity.length ? `Latest ${Math.min(activity.length, 10)} of ${activity.length}` : undefined}
            actions={
              <Button asChild variant="ghost" size="sm" className="text-primary">
                <Link to={activityLinkForTitle(series.title, "series", series.id)}>Everything about this show</Link>
              </Button>
            }
          >
            {activity.length === 0 ? (
              <ListEmpty title="Nothing has happened yet" description="Every event Deluno records against this show shows up here." />
            ) : (
              <ListTable
                chevron={false}
                listId="show-activity"
                items={activity.slice(0, 10)}
                columns={[
                  { id: "event", label: "Event", sort: (item) => item.message.toLowerCase() },
                  { id: "category", label: "Category", mobile: true, sort: (item) => eventLabel(item).toLowerCase() },
                  { id: "when", label: "When", sort: (item) => new Date(item.createdUtc) }
                ]}
              >
                {(rows) => rows.map((item) => (
                  <ListRow key={item.id}>
                    <ListNameCell name={item.message} />
                    <ListCell primary={eventLabel(item)} mobile />
                    <ListCell primary={formatPreferenceDateTime(item.createdUtc, preferences)} />
                  </ListRow>
                ))}
              </ListTable>
            )}
          </ListCard>
        </>
      ) : null}

      {/* ------------------------------------------------------------ drawers */}

      <DownloadDispatchDrawer
        dispatch={openDispatch}
        onClose={() => setOpenDispatchId(null)}
        onBlocked={() => revalidator.revalidate()}
      />

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
                    options={ALL_NUMBERING_SCHEME_OPTIONS.filter((option) => numberingDraft.availableSchemes.includes(option.value))}
                  />
                  <p className="text-(length:--type-caption) leading-snug text-muted-foreground">
                    {explainNumberingScheme(numberingDraft.numberingScheme)}
                  </p>
                  {hasNoOtherOrderings(numberingDraft) ? (
                    <p className="text-(length:--type-caption) leading-snug text-muted-foreground">
                      {OTHER_ORDERINGS_UNAVAILABLE_NOTE}
                    </p>
                  ) : null}
                  {numberingDraft.otherSchemeNames.length > 0 ? (
                    <p className="text-(length:--type-caption) leading-snug text-muted-foreground">
                      TMDb also publishes {numberingDraft.otherSchemeNames.join(", ")} for this title, which Deluno does not match episodes by yet.
                    </p>
                  ) : null}
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
              <p className="mt-3 text-(length:--type-caption) leading-snug text-muted-foreground">
                Deluno matches only an exact, unique key. If a filename cannot be matched safely it stays in import recovery for review.
              </p>
            </DrawerSection>

            <DrawerSection title="Episode mappings" aside={`${Object.keys(numberingDraft.mappings).length} episodes`}>
              <div className="max-h-112 space-y-2 overflow-y-auto pr-1">
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
        title={openEpisode ? `${formatEpisodeCode(openEpisode, showAbsolute)} · ${openEpisode.title ?? "Episode title pending"}` : "Episode"}
        description={openEpisode ? formatSeasonLabel(openEpisode.seasonNumber) : undefined}
        footer={
          <DrawerFooter
            state="clean"
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
                  { label: "First aired", value: openEpisode.airDateUtc ? formatAirDateTime(openEpisode.airDateUtc, preferences) : "Not announced" },
                  ...(openEpisode.absoluteNumber !== null && openEpisode.absoluteNumber !== undefined ? [{ label: "Absolute", value: `#${openEpisode.absoluteNumber}` }] : []),
                  ...(openEpisode.sceneSeasonNumber !== null && openEpisode.sceneSeasonNumber !== undefined && openEpisode.sceneEpisodeNumber !== null && openEpisode.sceneEpisodeNumber !== undefined
                    ? [{ label: "Scene", value: `S${String(openEpisode.sceneSeasonNumber).padStart(2, "0")}E${String(openEpisode.sceneEpisodeNumber).padStart(2, "0")}` }]
                    : []),
                  ...(openEpisode.airDate ? [{ label: "Air-date key", value: openEpisode.airDate }] : []),
                  // Where the numbers above came from, once there is one worth
                  // naming: an untouched provider default is not news, but an
                  // owner correction or an automatic scene-mapping fetch is
                  // exactly the kind of decision the North Star asks Deluno to
                  // explain rather than leave silent.
                  ...(openEpisode.numberingSource && openEpisode.numberingSource !== "provider"
                    ? [{ label: "Numbering source", value: formatNumberingSource(openEpisode.numberingSource) }]
                    : [])
                ]}
              />
            </DrawerSection>

            {(episodeSubtitles[openEpisode.episodeId]?.length ?? 0) > 0 ? (
              <DrawerSection title="Subtitles" aside={`${episodeSubtitles[openEpisode.episodeId]!.length} on disk`}>
                <ListTable
                  chevron={false}
                  listId="show-episode-subtitles"
                  items={episodeSubtitles[openEpisode.episodeId]!}
                  columns={[
                    { id: "language", label: "Language", sort: (subtitle) => subtitle.language.toLowerCase() },
                    { id: "type", label: "Type", sort: (subtitle) => subtitleTypeLabel(subtitle).toLowerCase() },
                    { id: "source", label: "Source", mobile: true, sort: (subtitle) => (subtitle.provider ?? subtitleSourceLabel(subtitle.source)).toLowerCase() },
                    { id: "timing", label: "Timing", mobile: true, sort: (subtitle) => timingStatusLabel(subtitle).toLowerCase() },
                    { id: "action", label: "Action", width: LIST_TRACK.action, align: "end", mobile: true, movable: false, sortable: false }
                  ]}
                >
                  {(rows) => rows.map((subtitle, index) => (
                    <ListRow key={`${subtitle.language}-${subtitle.forced}-${subtitle.hearingImpaired}-${index}`}>
                      <ListNameCell name={subtitle.language.toUpperCase()} />
                      <ListCell primary={subtitleTypeLabel(subtitle)} />
                      <ListCell primary={subtitle.provider ?? subtitleSourceLabel(subtitle.source)} mobile />
                      <ListCell primary={timingStatusLabel(subtitle)} secondary={subtitle.timingReason ?? undefined} mobile />
                      <div role="cell" className="flex items-center justify-end gap-1.5">
                        {!subtitle.forced ? (
                          <>
                            <Button
                              type="button"
                              variant="ghost"
                              size="sm"
                              aria-label={`Search ${subtitle.language.toUpperCase()} subtitles by hand`}
                              onClick={() => setManualSearchLanguage(subtitle.language)}
                            >
                              <Search className="h-3.5 w-3.5" />
                              Manual search
                            </Button>
                            <SubtitleTimingActions
                              basePath={`/api/series/episodes/${openEpisode.episodeId}`}
                              language={subtitle.language}
                              timingStatus={subtitle.timingStatus}
                              onChanged={() => refetchEpisodeSubtitles(openEpisode.episodeId)}
                            />
                          </>
                        ) : null}
                      </div>
                    </ListRow>
                  ))}
                </ListTable>
              </DrawerSection>
            ) : null}

            {(episodeSubtitleHistory[openEpisode.episodeId]?.length ?? 0) > 0 ? (
              <DrawerSection title="Subtitle search history" aside={`Last ${episodeSubtitleHistory[openEpisode.episodeId]!.length}`}>
                <ListTable
                  chevron={false}
                  listId="show-episode-subtitle-history"
                  items={episodeSubtitleHistory[openEpisode.episodeId]!}
                  columns={[
                    { id: "language", label: "Language", sort: (entry) => entry.language.toLowerCase() },
                    { id: "provider", label: "Provider", mobile: true, sort: (entry) => (entry.provider ?? "").toLowerCase() },
                    { id: "whatHappened", label: "What happened", sort: (entry) => entry.outcome.toLowerCase() },
                    { id: "score", label: "Score", mobile: true, sort: (entry) => entry.score }
                  ]}
                >
                  {(rows) => rows.map((entry, index) => (
                    <ListRow key={`${entry.language}-${entry.attemptedUtc}-${index}`}>
                      <ListNameCell name={entry.language.toUpperCase()} sub={new Date(entry.attemptedUtc).toLocaleString()} />
                      <ListCell primary={entry.provider ?? "—"} mobile />
                      <ListCell primary={entry.outcome} />
                      <ListCell primary={subtitleScoreLabel(entry.score)} mobile />
                    </ListRow>
                  ))}
                </ListTable>
              </DrawerSection>
            ) : null}

            {manualSearchLanguage && openEpisode ? (
              <SubtitleManualSearchDialog
                open={manualSearchLanguage !== null}
                onOpenChange={(next) => { if (!next) setManualSearchLanguage(null); }}
                basePath={`/api/series/episodes/${openEpisode.episodeId}`}
                language={manualSearchLanguage}
                onChanged={() => refetchEpisodeSubtitles(openEpisode.episodeId)}
              />
            ) : null}

            <DrawerSection title="Automation" aside={wantedStatusPresentation(openEpisode.wantedStatus).label}>
              <div className="flex min-h-(--control-height) items-center justify-between gap-(--grid-gap)">
                <span className="min-w-0">
                  <span className="block text-(length:--type-body-sm) font-medium text-foreground">Monitor this episode</span>
                  <span className="mt-0.5 block text-(length:--type-caption) text-muted-foreground">{openEpisode.wantedReason}</span>
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
                  ...(openEpisode.lastSearchResult && plainSearchResult(openEpisode.lastSearchResult).trim() !== (openEpisode.wantedReason ?? "").trim() ? [{ label: "What happened", value: plainSearchResult(openEpisode.lastSearchResult) }] : []),
                  {
                    label: "Next eligible search",
                    value: openEpisode.nextEligibleSearchUtc ? formatPreferenceDateTime(openEpisode.nextEligibleSearchUtc, preferences) : "As soon as a cycle runs"
                  }
                ]}
              />
            </DrawerSection>

            {openEpisode.hasFile ? (
              <DrawerSection title="File">
                <DrawerFacts
                  items={[
                    { label: "Path", value: episodeFiles[openEpisode.episodeId]?.filePath ?? "Not recorded", mono: true },
                    { label: "Size", value: episodeFiles[openEpisode.episodeId]?.fileSizeBytes ? formatBytes(episodeFiles[openEpisode.episodeId]!.fileSizeBytes!) : "Not recorded" },
                    { label: "Quality", value: episodeFiles[openEpisode.episodeId]?.quality ?? openEpisode.currentQuality ?? "Not recorded" },
                    { label: "Release group", value: episodeFiles[openEpisode.episodeId]?.releaseGroup ?? "Not recorded" },
                    { label: "Indexer", value: episodeFiles[openEpisode.episodeId]?.indexerName ?? "Not recorded" },
                    { label: "Download client", value: episodeFiles[openEpisode.episodeId]?.downloadClientName ?? "Not recorded" }
                  ]}
                />
                <div className="flex flex-wrap items-center gap-(--grid-gap)">
                  {confirmingEpisodeFileDelete === openEpisode.episodeId ? (
                    <>
                      <span className="text-(length:--type-caption) text-muted-foreground">Goes to the recycle bin. You can restore it. Deluno will look for it again.</span>
                      <Button
                        type="button"
                        variant="destructive"
                        onClick={() => void handleEpisodeFileDelete(openEpisode.episodeId)}
                        disabled={busyAction !== null}
                      >
                        {busyAction === `episode-file-delete:${openEpisode.episodeId}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : "Confirm delete"}
                      </Button>
                      <Button type="button" variant="ghost" onClick={() => setConfirmingEpisodeFileDelete(null)} disabled={busyAction !== null}>
                        Cancel
                      </Button>
                    </>
                  ) : (
                    <Button
                      type="button"
                      variant="outline"
                      className="text-destructive hover:bg-destructive/10 hover:text-destructive"
                      onClick={() => setConfirmingEpisodeFileDelete(openEpisode.episodeId)}
                      disabled={busyAction !== null}
                      title="Moves this episode's file to the recycle bin, where it can be restored."
                    >
                      <Trash2 className="h-4 w-4" />
                      Delete file
                    </Button>
                  )}
                </div>
              </DrawerSection>
            ) : null}

            <DrawerSection title="Search">
              <div className="flex flex-wrap gap-(--grid-gap)">
              <Button
                type="button"
                variant="outline"
                onClick={() => void handleEpisodeSearch([openEpisode.episodeId], "send")}
                disabled={busyAction !== null}
                title="Deluno applies the Library Profile and sends the best acceptable release."
              >
                {busyAction === "episode-search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                Search and send the best release
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={() => void handleEpisodeSearch([openEpisode.episodeId], "choose")}
                disabled={busyAction !== null}
                title="Lists the releases it finds and sends nothing until you choose one."
              >
                {busyAction === "episode-choose" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                Choose a release for this episode
              </Button>
              </div>
            </DrawerSection>
          </>
        ) : null}
      </Drawer>

      <AlreadyDownloadingDialog
        prompt={duplicateSend}
        busy={busyAction !== null}
        onCancel={() => setDuplicateSend(null)}
        onChoose={(action) => {
          const pending = duplicateSend;
          setDuplicateSend(null);
          if (pending) void handleGrabCandidate(pending.candidate, pending.force, pending.overrideReason, action);
        }}
      />

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
            state={openCandidate?.downloadUrl ? (forceRequiredNotice ? "error" : "clean") : "error"}
            message={
              openCandidate?.downloadUrl
                ? (forceRequiredNotice ?? "Ready to send.")
                : "This candidate has no downloadable URL yet"
            }
            saveType="button"
            saveLabel="Send to downloads"
            saveEnabled={Boolean(openCandidate?.downloadUrl) && busyAction === null && !openCandidateNeedsForce}
            onSave={() => openCandidate && void handleGrabCandidate(openCandidate, false)}
            onCancel={() => setOpenCandidate(null)}
            extraAction={openCandidate ? (
              <BlockReleaseAction
                releaseName={openCandidate.releaseName}
                indexerName={openCandidate.indexerName}
                mediaType="tv"
                entityId={series.id}
                title={series.title}
                onBlocked={() => {
                  setOpenCandidate(null);
                  revalidator.revalidate();
                }}
              />
            ) : null}
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
                    ? [{ label: "Judged by", value: "Your quality profile" }]
                    : [{ label: "Judged by", value: "Your older rules" }]),
                  { label: "Meets cutoff", value: openCandidate.meetsCutoff ? "Yes" : "No" },
                  { label: "Size", value: openCandidate.sizeBytes ? formatBytes(openCandidate.sizeBytes) : "Unknown" },
                  { label: "Seeders", value: openCandidate.seeders ?? "—" },
                  ...(openCandidate.estimatedBitrateMbps ? [{ label: "Estimated bitrate", value: `${openCandidate.estimatedBitrateMbps} Mbps` }] : []),
                  ...(openCandidate.releaseGroup ? [{ label: "Release group", value: openCandidate.releaseGroup }] : [])
                ]}
              />
              <p className="text-(length:--type-caption) leading-snug text-muted-foreground">{candidateExplanation(openCandidate).summary}</p>
            </DrawerSection>

            {candidateExplanation(openCandidate).reasons.length ? (
              <DrawerSection title={likesCandidate(openCandidate) ? "Why Deluno likes it" : "How Deluno reached this"}>
                <ul className="grid gap-1">
                  {candidateExplanation(openCandidate).reasons.slice(0, 6).map((reason) => (
                    <li key={reason} className="text-(length:--type-body-sm) text-muted-foreground">
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
                    <li key={risk} className="text-(length:--type-body-sm) text-destructive">
                      {risk}
                    </li>
                  ))}
                </ul>
              </DrawerSection>
            ) : null}

            {/* A release Deluno already prefers needs no override (#1138). */}
            {openCandidateNeedsForce ? (
            <DrawerSection>
              <div className="rounded-[10px] border border-warning/30 px-(--field-pad-x) py-2">
                <div className="flex min-h-[52px] items-center justify-between gap-(--grid-gap)">
                  <div className="min-w-0">
                    <p className="text-(length:--type-body-sm) font-medium text-foreground">Send it anyway</p>
                    <p className="mt-0.5 text-(length:--type-caption) text-muted-foreground">
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
            ) : null}
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
            extraAction={openSearch?.releaseName ? (
              <>
                <RemoveDownloadAction
                  releaseName={openSearch.releaseName}
                  indexerName={openSearch.indexerName}
                  mediaType="tv"
                  entityId={series.id}
                  title={series.title}
                  onRemoved={() => {
                    setOpenSearchId(null);
                    revalidator.revalidate();
                  }}
                />
                <BlockReleaseAction
                  releaseName={openSearch.releaseName}
                  indexerName={openSearch.indexerName}
                  mediaType="tv"
                  entityId={series.id}
                  title={series.title}
                  onBlocked={() => {
                    setOpenSearchId(null);
                    revalidator.revalidate();
                  }}
                />
              </>
            ) : null}
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

            {parseSearchCandidates(openSearch.detailsJson, normalizeSearchCandidate).length ? (
              <DrawerSection title="Release outcomes" aside={`${searchCandidateCount(openSearch.detailsJson)} considered`}>
                <DrawerFacts
                  items={parseSearchCandidates(openSearch.detailsJson, normalizeSearchCandidate)
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
    case "dvd": return "DVD order";
    default: return "Season / episode";
  }
}

/** Plain words about what actually changes when this scheme is picked (#863) - shown under the switcher so a choice is never made blind. */
export function explainNumberingScheme(value: string | null | undefined) {
  switch (value?.toLowerCase()) {
    case "airdate":
      return "Matches and imports episodes by the date they aired, for shows with no fixed season structure.";
    case "absolute":
      return "Matches and imports episodes by one running count instead of season breaks - the numbering most anime releases use.";
    case "scene":
      return "Matches and imports using the numbering release groups actually used, mapped automatically from TheXEM.";
    case "dvd":
      return "Matches and imports using the season/episode order TMDb's DVD release used, when it differs from the order episodes aired.";
    default:
      return "Matches and imports episodes by the season and episode number TMDb aired them under.";
  }
}

/**
 * Deluno stores nothing when the metadata service gives no alternate orderings,
 * whether the show has none or the service could not answer, so the two cannot
 * be told apart on screen. Say so in words instead of leaving a list that looks
 * complete.
 */
export const OTHER_ORDERINGS_UNAVAILABLE_NOTE =
  "Other orderings (DVD order, absolute numbering) are unavailable right now. Either this show has none, or the metadata service could not be reached; Deluno checks again on its next refresh.";

export function hasNoOtherOrderings(draft: { availableSchemes: string[]; otherSchemeNames: string[] }) {
  return draft.otherSchemeNames.length === 0
    && !draft.availableSchemes.some((scheme) => scheme === "dvd" || scheme === "absolute");
}

/** Every scheme the show page's numbering switcher may offer, in a fixed display order. Only <see cref="detail.availableNumberingSchemes"/> narrows this down to what a title actually has. */
export const ALL_NUMBERING_SCHEME_OPTIONS = [
  { value: "standard", label: "Season / episode (aired)" },
  { value: "dvd", label: "DVD order" },
  { value: "absolute", label: "Absolute episode" },
  { value: "airdate", label: "Air date" },
  { value: "scene", label: "Scene season / episode" }
];

function formatNumberingSource(value: string | null | undefined) {
  switch (value?.toLowerCase()) {
    case "owner": return "Owner mapping";
    case "scene-map": return "Scene mapping (auto)";
    default: return "Provider metadata";
  }
}

function matchesEpisodeFilter(episode: SeriesEpisodeInventoryItem, filter: EpisodeFilter, query: string) {
  const haystack = `${formatEpisodeCode(episode)} ${episode.title ?? ""}`.toLowerCase();
  const matchesQuery = !query.trim() || haystack.includes(query.trim().toLowerCase());

  const matchesFilter =
    filter === "all" ||
    // The Missing and Upgrade chips count with the same helpers as the summary strip (#1338), so one list never has two answers.
    (filter === "missing" && isEpisodeMissing(episode)) ||
    (filter === "upgrade" && isEpisodeUpgradable(episode)) ||
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

/**
 * Scopes a show's search history down to one episode (#795), or returns it
 * unchanged when no episode is selected - clearing the filter is just
 * passing `null` back in.
 */
/** What the server knows about the file an episode holds, from GET /api/series/episodes/{id}/file. */
interface EpisodeFileFacts {
  filePath: string;
  fileSizeBytes: number | null;
  quality: string | null;
  releaseGroup: string | null;
  indexerName: string | null;
  downloadClientName: string | null;
}

export function filterSearchHistoryByEpisode(
  searches: SeriesSearchHistoryItem[],
  episodeId: string | null
): SeriesSearchHistoryItem[] {
  return episodeId ? searches.filter((item) => item.episodeId === episodeId) : searches;
}

/**
 * S01E05, or S01E05 (105) for an anime episode when the person chose absolute
 * numbers in Interface settings (#1024). An episode with no absolute number
 * keeps the plain code.
 */
export function formatEpisodeCode(episode: SeriesEpisodeInventoryItem, showAbsolute = false) {
  const code = `S${String(episode.seasonNumber).padStart(2, "0")}E${String(episode.episodeNumber).padStart(2, "0")}`;
  return showAbsolute && episode.absoluteNumber ? `${code} (${episode.absoluteNumber})` : code;
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
  // #753: enriched results columns, alongside the existing seeders and
  // decisionReasons (a rejection's own reason).
  leechers?: number | null;
  // #969: blank when the indexer never reports a grab count.
  grabs?: number | null;
  indexerProtocol?: string | null;
  releaseAgeHours?: number | null;
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

/**
 * Whether the grab endpoint will refuse this candidate with a 400 unless
 * `force` is set. Mirrors `IsSafeForAutomaticDispatch` in
 * `AcquisitionDecisionPipeline.cs` exactly: only a release that is Deluno's
 * own preferred pick, meets the cutoff, and is not a quality downgrade can
 * be sent with a plain "Send to downloads". Everything else — Acceptable,
 * Eligible, Equivalent, held, a downgrade — runs into the same decision
 * again on the server and comes back "requires force override" (#612).
 */
function candidateRequiresForce(candidate: SearchPlanCandidate): boolean {
  const isPreferred = (candidate.decisionStatus ?? "").toLowerCase() === "preferred";
  const safe = isPreferred && candidate.meetsCutoff && (candidate.qualityDelta ?? 0) >= 0;
  return !safe;
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
    case "processor-output-missing":
      return "Missing from processor output";
    case "processor-unmatched-output":
      return "Unmatched in processor output";
    default:
      return "Needs review";
  }
}

/** The episode a "missing" or "unmatched" processor-output case named, if its file name resolved to one (#930, #957). */
function episodeIdIn(detailsJson: string | null | undefined): string | null {
  if (!detailsJson) return null;
  try {
    const details = JSON.parse(detailsJson) as Record<string, unknown>;
    const id = details.episodeId;
    return typeof id === "string" && id.length > 0 ? id : null;
  } catch {
    return null;
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

function formatSearchScope(item: SeriesSearchHistoryItem) {
  if (item.seasonNumber !== null && item.episodeNumber !== null) {
    return `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`;
  }
  if (item.seasonNumber !== null) return formatSeasonLabel(item.seasonNumber);
  return "Whole show";
}

/**
 * The rung a subtitle attempt scored (#982) — the same ladder a held
 * subtitle's own match carries. "—" for an attempt that found nothing, so
 * there was no candidate to score.
 */
function subtitleScoreLabel(score: number | null) {
  switch (score) {
    case 2:
      return "Made for this file";
    case 1:
      return "Same source";
    case 0:
      return "Any release";
    default:
      return "—";
  }
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

