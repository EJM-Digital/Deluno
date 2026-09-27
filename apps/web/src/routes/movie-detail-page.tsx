/**
 * Movie detail — hero → toolbar sections → list cards → drawers.
 *
 * Same shape as the TV show page, minus episodes: a detail page keeps its `h1`
 * because the topbar names the section ("Movies") rather than the movie, and the
 * hero artwork is content. Everything below it obeys the list → drawer grammar.
 *
 * Contracts: GET /api/movies/{id}, /workflow-status, /removal-preview; PUT
 * /api/movies/monitoring, /api/movies/{id}/replacement-protection; POST
 * /api/movies/{id}/search, /grab, /automation/defer, /automation/skip-once,
 * /api/movies/bulk.
 */
import { getDispatchStatusLabel as formatDispatchStatus } from "../lib/job-status-constants";
import type { Tone } from "../lib/status-tones";
import { useMemo, useState } from "react";
import { Link, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { ArrowLeft, ArrowUpDown, LoaderCircle, RefreshCw, Search, Trash2, ShieldCheck, ShieldOff
} from "lucide-react";
import {
  fetchJson, fetchPageItems,
  type ActivityEventItem,
  type DecisionExplanationItem,
  type DownloadDispatchItem,
  type IntegrationFailure,
  type LibraryItem,
  type IntakeTitleOriginItem,
  type MovieImportRecoverySummary,
  type MovieListItem,
  type MetadataProviderIssue,
  type MovieSearchHistoryItem,
  type AcquisitionBlockersResponse,
  type MediaSubtitleRow,
  type SubtitleLanguageOption
} from "../lib/api";
import { subtitleTypeLabel, subtitleSourceLabel, timingStatusLabel } from "../lib/subtitle-display";
import { SubtitleTimingActions } from "../components/app/subtitle-timing-actions";
import { SubtitleOverrideCard } from "../components/app/subtitle-override-card";
import { SubtitleManualSearchDialog } from "../components/app/subtitle-manual-search-dialog";
import { AcquisitionBlockersCard } from "../components/app/acquisition-blockers-card";
import { LibraryMembershipCard, type LibraryMembership } from "../components/app/library-membership-card";
import { RetentionTitleCard } from "../components/app/retention-title-card";
import { CountryReleaseRow } from "../components/app/country-release-row";
import { CreditsRow, readStoredCredits } from "../components/app/credits-row";
import { DownloadDispatchDrawer } from "../components/app/download-dispatch-drawer";
import { BlockReleaseAction } from "../components/app/block-release-action";
import { RemoveDownloadAction } from "../components/app/remove-download-action";
import { TitleTagsEditor } from "../components/app/title-tags-editor";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";
import { friendlyRuleName } from "../lib/guide-names";
import { describeSearchNow, describeSearchReason, describeRequestFailure, formatGrabMessage, formatSearchFailureNotice } from "../lib/search-reasons";
import { candidateLabel, candidateTone, canWinSearch, isTypedCandidate, likesCandidate } from "../lib/release-candidate-status";
import {
  type CandidateSortKey,
  defaultSortDirection,
  filterCandidates,
  formatProtocol,
  formatReleaseAge,
  formatSeedersAndLeechers,
  sortCandidates,
  type SortDirection
} from "../lib/release-candidate-columns";
import { Badge } from "../components/ui/badge";
import { Select } from "../components/ui/select";
import { Button } from "../components/ui/button";
import { Card, CardContent } from "../components/ui/card";
import { RemoveMediaDialog, type MediaRemovalPreview, type RemoveMediaOptions } from "../components/app/remove-media-dialog";
import { DecisionExplanationList } from "../components/app/decision-explanation-list";
import { MediaMetadataDrawer } from "../components/app/media-metadata-drawer";
import { MovieEditDrawer } from "../components/app/movie-edit-drawer";
import { MovieFilesCard } from "../components/app/movie-files-card";
import { HeroBackdrop } from "../components/app/hero-backdrop";
import { MetadataProviderIssueNotice } from "../components/app/metadata-provider-issue-notice";
import { SourceMark } from "../components/app/source-mark";
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
import { TitleMarkLabel } from "../components/ui/title-mark";
import { formatDateTime as formatPreferenceDateTime, formatRuntime, formatShortDate, useDisplayPreferences } from "../lib/display-preferences";
import { readAlternateTitles, readArtworkExtras, readReleaseFactsByCountry, selectCountryFacts } from "../lib/ui-adapters";

/**
 * What Deluno recorded about the copy it holds: the release it came from, what
 * that release matched, and where it landed. Deluno keeps this whether or not
 * the owner's naming puts any of it in the file name.
 */
interface MovieCopyRecord {
  filePath?: string | null;
  fileSizeBytes?: number | null;
  quality?: string | null;
  releaseName?: string | null;
  releaseGroup?: string | null;
  indexerName?: string | null;
  downloadClientName?: string | null;
  grabbedUtc?: string | null;
  importedUtc?: string | null;
  source?: string | null;
  formats: Array<{ id: string; name: string; trashId?: string | null }>;
  planVersion?: string | null;
  planHash?: string | null;
}

interface MovieDetailLoaderData {
  acquisitionBlockers: AcquisitionBlockersResponse | null;
  activity: ActivityEventItem[];
  copy: MovieCopyRecord | null;
  decisions: DecisionExplanationItem[];
  dispatches: DownloadDispatchItem[];
  importRecovery: MovieImportRecoverySummary;
  libraries: LibraryItem[];
  memberships: LibraryMembership[];
  movie: MovieListItem;
  metadataIssue: MetadataProviderIssue | null;
  origins: IntakeTitleOriginItem[];
  removalPreview: MediaRemovalPreview;
  searchHistory: MovieSearchHistoryItem[];
  subtitles: MediaSubtitleRow[];
  subtitleLanguageOptions: SubtitleLanguageOption[];
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
  const [movie, metadataIssue, searchHistory, dispatches, importRecovery, activity, decisions, libraries, workflowStatus, origins, removalPreview, acquisitionBlockers, copy, memberships, subtitles, subtitleLanguageOptions] = await Promise.all([
    fetchJson<MovieListItem>(`/api/movies/${id}`),
    fetchJson<MetadataProviderIssue | null>(`/api/movies/${id}/metadata/issue`).catch(() => null),
    fetchJson<MovieSearchHistoryItem[]>("/api/movies/search-history"),
    // Archived rows included on purpose: an import archives its dispatch the
    // moment it succeeds, so a history that hides them shows nothing was ever
    // sent for the very titles Deluno did download. Asked for by title, because
    // the newest twenty rows across the whole library are not this title's.
    fetchPageItems<DownloadDispatchItem>(`/api/download-dispatches?mediaType=movies&pageSize=20&includeArchived=true&entityId=${encodeURIComponent(id)}`),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchPageItems<ActivityEventItem>(`/api/activity?relatedEntityType=movie&relatedEntityId=${id}&pageSize=20`),
    fetchPageItems<DecisionExplanationItem>(`/api/decisions?relatedEntityType=movie&relatedEntityId=${id}&pageSize=40`),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<MovieWorkflowStatus>(`/api/movies/${id}/workflow-status`).catch(() => null),
    fetchJson<IntakeTitleOriginItem[]>(`/api/intake-title-origins?mediaType=movies&entityId=${encodeURIComponent(id)}`).catch(() => []),
    fetchJson<MediaRemovalPreview>(`/api/movies/${id}/removal-preview`).catch(() => ({ filePaths: [], folderPaths: [], warnings: [] })),
    // Caught rather than awaited into the failure: a page that will not open
    // because it could not find out why a download is stuck is worse than a
    // page that simply does not mention it.
    fetchJson<AcquisitionBlockersResponse>(`/api/movies/${id}/acquisition-blockers`).catch(() => null),
    // What the copy on disk is, in Deluno's own words. The file name is a
    // preference and may say none of it.
    fetchJson<MovieCopyRecord>(`/api/movies/${id}/copy`).catch(() => null),
    // The libraries this film is in, as a person chose them (#530).
    fetchJson<LibraryMembership[]>(`/api/movies/${id}/libraries/`).catch(() => [] as LibraryMembership[]),
    // Which type each held subtitle is — plain, hearing-impaired or forced
    // (#923). Caught rather than awaited into the failure: a page that
    // cannot show its subtitle types is not a page that should not open.
    fetchJson<MediaSubtitleRow[]>(`/api/movies/${id}/subtitles`).catch(() => [] as MediaSubtitleRow[]),
    // For the per-title override picker (#843) — the same names the
    // library's own subtitle-languages screen uses.
    fetchJson<SubtitleLanguageOption[]>("/api/subtitle-languages").catch(() => [] as SubtitleLanguageOption[])
  ]);

  return { acquisitionBlockers, activity, copy, decisions, dispatches, importRecovery, libraries, memberships, metadataIssue, movie, origins, removalPreview, searchHistory, subtitles, subtitleLanguageOptions, workflowStatus };
}

export function MovieDetailPage() {
  const loaderData = useLoaderData() as MovieDetailLoaderData;
  const { acquisitionBlockers, activity, copy, decisions, dispatches, importRecovery, libraries, memberships, metadataIssue, movie, origins, removalPreview, searchHistory, subtitles, subtitleLanguageOptions, workflowStatus } = loaderData;
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const { preferences, certificationCountry } = useDisplayPreferences();

  const [busyAction, setBusyAction] = useState<string | null>(null);
  // Which language's manual search dialog (#841) is open, or null for none.
  const [manualSearchLanguage, setManualSearchLanguage] = useState<string | null>(null);
  const [isRemoveConfirmationOpen, setIsRemoveConfirmationOpen] = useState(false);
  const [isMetadataOpen, setIsMetadataOpen] = useState(false);
  const [isEditOpen, setIsEditOpen] = useState(false);
  const [releaseCandidates, setReleaseCandidates] = useState<SearchPlanCandidate[]>([]);
  // #753: sort/filter state for the candidates table, reset each fresh search.
  const [candidateSort, setCandidateSort] = useState<{ key: CandidateSortKey; direction: SortDirection } | null>(null);
  const [candidateProtocolFilter, setCandidateProtocolFilter] = useState("");
  const [candidateIndexerFilter, setCandidateIndexerFilter] = useState("");
  const [openCandidate, setOpenCandidate] = useState<SearchPlanCandidate | null>(null);
  const [forceReason, setForceReason] = useState<string | null>(null);
  const [openSearchId, setOpenSearchId] = useState<string | null>(null);
  const [openDispatchId, setOpenDispatchId] = useState<string | null>(null);
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
  // Which of its libraries the page's actions go through. With one library that
  // is the answer; with several, the Libraries card lets a person point them.
  const [chosenLibraryId, setChosenLibraryId] = useState<string | null>(null);
  const actingLibraryId = memberships.some((item) => item.libraryId === chosenLibraryId)
    ? chosenLibraryId
    : memberships.find((item) => item.libraryId === wantedItem?.libraryId)?.libraryId ?? memberships[0]?.libraryId ?? wantedItem?.libraryId ?? null;
  const libraryQuery = memberships.length > 1 && actingLibraryId ? `libraryId=${encodeURIComponent(actingLibraryId)}` : "";
  const library = actingLibraryId ? libraries.find((item) => item.id === actingLibraryId) ?? null : null;
  const movieSearches = searchHistory.filter((item) => item.movieId === movie.id);
  const movieDispatches = dispatches.filter((item) => item.entityId === movie.id);
  const importCases = importRecovery.recentCases.filter(
    (item) => item.title.trim().toLowerCase() === movie.title.trim().toLowerCase()
  );
  const { cast, crew } = readStoredCredits(movie.metadataJson);
  // #861: region-specific spellings TMDb's alternative_titles endpoint
  // reported, beyond the original title already shown above. A release filed
  // under one of these has nothing else on the page saying it is a match.
  const alternateTitles = readAlternateTitles(movie.metadataJson).filter(
    (candidate) => candidate !== movie.title && candidate !== movie.originalTitle
  );
  // #864: a transparent logo/wordmark and extra backdrop stills, beyond the
  // poster and backdrop already shown above. No banner: TMDb has never
  // reported one, so there is nothing here to show for it.
  const { logoUrl, screenshots } = readArtworkExtras(movie.metadataJson);
  // #862: every country TMDb answered for, unreduced, so the certification
  // country setting can pick between them without a re-fetch.
  const releaseFactsByCountry = readReleaseFactsByCountry(movie.metadataJson);
  const selectedCountryFacts = selectCountryFacts(releaseFactsByCountry, certificationCountry);
  const otherCountryFacts = releaseFactsByCountry.filter((entry) => entry !== selectedCountryFacts);
  const openSearch = movieSearches.find((item) => item.id === openSearchId) ?? null;
  const openDispatch = movieDispatches.find((item) => item.id === openDispatchId) ?? null;
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

  /*
    The metadata Deluno already stores, and nothing was reading.

    `metadataJson` is delivered with the detail item and carries Studio,
    Certification, Original language, Collection, Director, Tagline and **Cast** —
    everything Radarr's header shows. James: *"where are the bigger poster and
    actors"*. They were never missing from the data, only from the page.

    Parsed here rather than added to `MovieListItem`, because the fields are
    already on the wire: widening the contract to re-deliver what is already
    being delivered would be the same fact travelling twice.
  */
  const meta = useMemo<Record<string, unknown> | null>(() => {
    if (!movie.metadataJson) return null;
    try { return JSON.parse(movie.metadataJson) as Record<string, unknown>; } catch { return null; }
  }, [movie.metadataJson]);
  // `cast` and `crew` are already read above by `readStoredCredits`, which handles both the
  // camelCase and PascalCase shapes the store has used. A second parse here
  // would be the same rule written twice.
  const metaText = (key: string) => {
    const value = meta?.[key];
    return typeof value === "string" && value.trim() ? value.trim() : null;
  };

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
        description: "Something Deluno brought in could not be filed. It needs a decision before this movie is settled.",
        action: "Open import issues",
        onAction: () => setSection("history")
      }
    : releaseCandidates.length
      ? {
          eyebrow: "Release ready",
          title: "Choose a release",
          description: "Deluno compared the candidates it found. Pick the one to send to your download client.",
          action: "Review candidates",
          onAction: () => setSection("destination")
        }
      : !movie.monitored
        ? {
            eyebrow: "Unmonitored",
            title: "Resume automatic care",
            description: "This movie is not being watched for a missing file or a quality improvement.",
            action: "Resume automation",
            onAction: () => void handleMonitoring(true)
          }
        : !movie.hasFile
          ? {
              eyebrow: "File missing",
              title: "Find this movie",
              description: "Deluno can search every indexer you have connected using this movie's Library Profile.",
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
      toast.error("This movie's monitoring could not be changed.");
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
        body: JSON.stringify({ libraryId: actingLibraryId ?? wantedItem.libraryId, hours: 24 })
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
        body: JSON.stringify({ libraryId: actingLibraryId ?? wantedItem.libraryId })
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
    let refreshResponse: Response | null = null;

    try {
      const response = await authedFetch(`/api/movies/${movie.id}/metadata/refresh`, { method: "POST" });
      refreshResponse = response;
      if (response.status === 409) {
        revalidator.revalidate();
        toast.info("The TMDb record is no longer available. Your movie and files were kept.");
        return;
      }
      if (!response.ok) throw new Error("movie-metadata-refresh-failed");
      toast.success(`${movie.title} metadata refreshed.`);
      revalidator.revalidate();
    } catch (refreshError) {
      const explained = await describeRequestFailure(refreshResponse, refreshError, {
        action: "refresh this movie's metadata",
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

  async function handleSearchNow(mode: "automatic" | "interactive") {
    setBusyAction(`${mode}-search`);
    let searchResponse: Response | null = null;

    try {
      const query = [mode === "interactive" ? "mode=preview" : "", libraryQuery].filter(Boolean).join("&");
      searchResponse = await authedFetch(`/api/movies/${movie.id}/search${query ? `?${query}` : ""}`, { method: "POST" });
      if (!searchResponse.ok) throw new Error("movie-search-failed");
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
        setSection("destination");
        if (found) toast.success(`${found} release${found === 1 ? "" : "s"} compared. Choose one below.`, failureNotice ? { description: failureNotice } : undefined);
        else {
          const explained = describeSearchReason(payload.reason, payload.summary ?? "No releases matched this movie's Library Profile.");
          const action = explained.action;
          toast.info(explained.title, {
            description: [explained.description, failureNotice].filter(Boolean).join(" "),
            action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
          });
        }
      } else {
        const notice = describeSearchNow(payload);
        if (notice) {
          const description = [notice.description, failureNotice].filter(Boolean).join(" ");
          toast[notice.tone](notice.title, description ? { description } : undefined);
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
      const response = await authedFetch(`/api/movies/${movie.id}/grab${libraryQuery ? `?${libraryQuery}` : ""}`, {
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

  // #753: sort/filter the candidates table without touching the fetched
  // order the server returned (still the default when nothing is chosen).
  function toggleCandidateSort(key: CandidateSortKey) {
    setCandidateSort((current) =>
      current?.key === key
        ? { key, direction: current.direction === "asc" ? "desc" : "asc" }
        : { key, direction: defaultSortDirection(key) }
    );
  }
  const candidateIndexerOptions = [...new Set(releaseCandidates.map((candidate) => candidate.indexerName))];
  const candidateProtocolOptions = [...new Set(releaseCandidates.map((candidate) => candidate.indexerProtocol).filter((value): value is string => Boolean(value)))];
  const visibleCandidates = (() => {
    const filtered = filterCandidates(releaseCandidates, { protocol: candidateProtocolFilter, indexerName: candidateIndexerFilter });
    return candidateSort ? sortCandidates(filtered, candidateSort.key, candidateSort.direction) : filtered;
  })();

  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-[var(--page-gap)]">
      {/* One toolbar: which part of the movie you want, where you came from, and
          the two searches. The topbar names the section, the hero names the movie. */}
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
            {/*
              The only place a single title's monitoring can be changed.

              James: "There isnt an unmonitor button or a way to unmonitor
              titles without selecting it for bulk." Right — the page could
              *resume* monitoring, from a prompt that appears only once it is
              already paused, and offered no way to pause it. So turning one
              film off meant going back to the shelf, selecting it, and using a
              bulk action on a selection of one.

              It sits beside the search actions because it belongs to the same
              question they answer: whether Deluno is working on this title.
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
        subjectLabel="movie"
        acknowledgeUrl={`/api/movies/${movie.id}/metadata/issue/acknowledge`}
        onAcknowledged={() => revalidator.revalidate()}
        onFindAnother={() => setIsMetadataOpen(true)}
        onRetry={() => void handleMetadataRefresh()}
      />

      <Card className="relative isolate min-w-0 min-h-[19rem] overflow-hidden border-primary/25 bg-card">
        <HeroBackdrop url={movie.backdropUrl} />
        <CardContent className="relative p-[var(--tile-pad)] sm:p-[calc(var(--tile-pad)*1.15)]">
          <div className="grid items-start gap-[var(--grid-gap)] md:grid-cols-[16rem_minmax(0,1fr)] xl:grid-cols-[16rem_minmax(0,1fr)_14rem]">
            {movie.posterUrl ? (
              <img src={movie.posterUrl} alt={`${movie.title} poster`} className="h-96 w-64 justify-self-center rounded-2xl border border-white/15 bg-surface-1 object-cover shadow-2xl md:justify-self-start" />
            ) : (
              <div className="flex h-96 w-64 justify-self-center items-center justify-center rounded-2xl border border-hairline bg-surface-1 px-3 text-center text-xs text-muted-foreground md:justify-self-start">No artwork yet</div>
            )}
            <div className="min-w-0 self-start">
              <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-primary">Movie</p>
              {/* #864: a transparent logo/wordmark, where TMDb has one, above the title it labels. */}
              {logoUrl ? <img src={logoUrl} alt="" className="mt-1 h-12 w-auto max-w-[18rem] object-contain object-left" /> : null}
              {/*
                **Centre, not baseline.** A button whose only child is an SVG has
                its baseline at the BOTTOM of its box, so aligning that to the
                title's baseline lifts the icon well above the text — James: *"the
                shield isnt on the same line as the title its too high"*.

                Baseline was right when the year shared this row, because the year
                is text. The year moved to the meta line, so this row holds a
                heading and an icon, and two things of different heights line up
                on their centres.
              */}
              <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1">
                <h1 className="font-display text-4xl font-semibold tracking-tight text-foreground sm:text-5xl">{movie.title}</h1>
                {/*
                  Monitoring: the shield, beside the title, and nothing else.
                  James: *"instead of having a big button for monitor/unmonitored
                  why cant we just have a shield here with hover over test kiss"*.

                  It was a button in the toolbar, then a labelled chip in the
                  row below — both bigger than the fact deserves. The glyph
                  already carries the state (shield against slashed shield) and
                  the tooltip carries the state AND the action, which is the
                  variant chosen from the four rendered. Radarr puts its bookmark
                  in exactly this spot for exactly this reason.
                */}
                <button
                  type="button"
                  onClick={() => void handleMonitoring(!movie.monitored)}
                  disabled={busyAction !== null}
                  aria-label={movie.monitored ? "Monitored — click to unmonitor" : "Unmonitored — click to monitor"}
                  title={movie.monitored ? "Monitored — click to unmonitor" : "Unmonitored — click to monitor"}
                  className={cn(
                    "rounded-lg p-1.5 transition-colors",
                    movie.monitored
                      ? "text-foreground hover:bg-surface-2"
                      : "text-muted-foreground hover:bg-surface-2 hover:text-foreground"
                  )}
                >
                  {busyAction === "monitor"
                    ? <LoaderCircle className="h-7 w-7 animate-spin" />
                    : movie.monitored ? <ShieldCheck className="h-7 w-7" /> : <ShieldOff className="h-7 w-7" />}
                </button>
              </div>
              {movie.originalTitle && movie.originalTitle !== movie.title ? <p className="mt-1 text-sm text-muted-foreground">Also known as {movie.originalTitle}</p> : null}
              {alternateTitles.length ? (
                <p className="mt-1 text-sm text-muted-foreground" title="Used to match a release filed under a region-specific spelling">
                  Alternate titles: {alternateTitles.join(", ")}
                </p>
              ) : null}
              <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
                {metaText("Certification") ? (
                  <span
                    className="rounded border border-hairline px-1.5 py-px text-xs font-bold uppercase tracking-wide"
                    title="Classification"
                  >
                    {metaText("Certification")}
                  </span>
                ) : null}
                {movie.releaseYear ? <span>{movie.releaseYear}</span> : null}
                {movie.runtimeMinutes ? (
                  <span>{formatRuntime(movie.runtimeMinutes, preferences)}</span>
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
                  item={{ monitored: movie.monitored, wantedStatus: wantedItem?.wantedStatus }}
                  type="movie"
                />
                {importCases.length ? <Badge variant="warning">{importCases.length} import issue{importCases.length === 1 ? "" : "s"}</Badge> : null}
                {movie.genres?.split(",").map((genre) => <span key={genre} className="rounded-full border border-primary/20 bg-primary/10 px-2.5 py-1 text-xs font-medium text-primary">{genre.trim()}</span>)}
              </div>
              <TitleTagsEditor id={movie.id} mediaType="movies" metadataJson={movie.metadataJson} onSaved={() => revalidator.revalidate()} />
              {/*
                The facts about this title, in one dense row.

                James, with a Radarr header beside ours: *"bigger poster, a lot
                more information... I like what we have, I think it just can be
                better similar to what radarr is doing"*, and earlier *"we should
                have a small list of things that show the title"*.

                **Everything, unconditionally — this is not the poster options.**
                The shelf and this page have opposite jobs: on a shelf you choose
                what each card carries, because you are scanning a wall and space
                is scarce. Here you have stopped and gone looking, so mirroring
                the toggles would make a fact you switched off for scanning
                unfindable at exactly the moment you want it.

                Label above value, Radarr's shape, because it packs eight facts
                into the space two rows of chips would take — and every one is
                skippable by eye until you want it.
              */}
              <dl className="mt-4 grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3 lg:grid-cols-4">
                {[
                  { label: "Path", value: movie.filePath || null, mono: true },
                  // Quality and Target are NOT here: the summary strip below says
                  // both, with a tone and the reason beside them, which a flat
                  // row cannot. Repeating them here is what made "WEB 2160p"
                  // appear three times on one page.
                  { label: "Size", value: movie.fileSizeBytes ? formatBytes(movie.fileSizeBytes) : null },
                  // Runtime is on the meta line under the title, with the
                  // certification and the year, where Radarr keeps it.
                  { label: "Studio", value: metaText("Studio") },
                  // Certification is NOT here: it is the badge on the title,
                  // where Radarr puts it. Saying it twice is the duplication
                  // this page has already been circled for.
                  { label: "Language", value: metaText("OriginalLanguage") },
                  { label: "Collection", value: metaText("Collection") },
                  { label: "Director", value: metaText("Director") },
                  // Studio, Certification and Original language are in the
                  // database and sortable, but `MovieListItem` does not project
                  // them — Radarr shows all three in this row and they belong
                  // here. Adding them is a contract change, so it is deliberate
                  // rather than smuggled in with a layout commit.
                  { label: "Codec", value: [movie.videoCodec, movie.audioCodec].filter(Boolean).join(" · ") || null },
                  { label: "Release group", value: movie.releaseGroup || null },
                  { label: "Added", value: movie.createdUtc ? formatShortDate(movie.createdUtc, preferences) : null },
                  { label: "Import issues", value: importCases.length ? String(importCases.length) : null }
                ].filter((f) => f.value).map((f) => (
                  <div key={f.label} className="min-w-0">
                    <dt className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">{f.label}</dt>
                    <dd className={cn("truncate text-sm text-foreground", f.mono && "font-mono text-xs")} title={String(f.value)}>{f.value}</dd>
                  </div>
                ))}
              </dl>

              <p className="mt-4 max-w-4xl text-sm leading-relaxed text-muted-foreground">
                {movie.overview ?? "No overview has been stored yet. Refresh metadata when you want Deluno to enrich this title."}
              </p>
            </div>
            <aside className="w-full self-start rounded-xl border border-white/10 bg-card/80 p-3 backdrop-blur-sm xl:min-h-96">
              <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.18em] text-muted-foreground">Ratings &amp; IDs</p>
              <p className="mt-0.5 text-xs text-muted-foreground">The metadata Deluno is using</p>
              <div className="mt-2"><RatingStrip ratings={movie.ratings} fallbackRating={movie.rating} /></div>
              <div className="mt-3 space-y-2 border-t border-hairline pt-3 text-sm">
                <div className="flex items-center justify-between gap-3"><span className="text-muted-foreground">Source</span>{movie.metadataProvider ? <SourceMark source={movie.metadataProvider.toLowerCase()} label={movie.metadataProvider.toUpperCase()} /> : <span className="font-medium text-foreground">Not linked</span>}</div>
                <div className="flex items-center justify-between gap-3"><SourceMark source="imdb" label="IMDb" /><span className="font-mono text-xs font-medium text-foreground">{movie.imdbId ?? "—"}</span></div>
              </div>
              <Button variant="outline" className="mt-3 w-full" onClick={() => void handleMetadataRefresh()} disabled={busyAction !== null}>
                {busyAction === "metadata-refresh" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                Refresh metadata
              </Button>
              <Button variant="outline" className="mt-1 w-full" onClick={() => setIsMetadataOpen(true)}>Edit metadata</Button>
              {/*
                Separate from Edit metadata (#784): that drawer covers only
                descriptive fields, and monitored state, quality profile and
                path had no in-page edit surface at all — only the separate
                bulk tools, which meant a single-title change needed the
                multi-select flow.
              */}
              <Button variant="outline" className="mt-1 w-full" onClick={() => setIsEditOpen(true)}>Edit movie</Button>
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

      {/*
        Cast and crew, out of the bubble.

        They lived inside the header card, which made that card a mile tall and
        put a wall of thirty faces in the same box as the runtime and the
        overview — a header that has to be scrolled is not a header. Radarr and
        Sonarr both break them out as their own full-width blocks below, and
        James, looking at the two side by side: *"lets move the cast and the
        crew out of the main bubble like radarr and sonarr do"*.

        Each is its own card, so a title with no crew simply has no crew card
        rather than an empty band under the cast.
      */}
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
            <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.18em] text-muted-foreground">Screenshots</p>
            <div className="mt-2 flex gap-3 overflow-x-auto pb-1">
              {screenshots.map((url) => (
                <img key={url} src={url} alt="" className="h-32 w-56 shrink-0 rounded-lg border border-hairline object-cover" />
              ))}
            </div>
          </CardContent>
        </Card>
      ) : null}
      {/*
        #862: TMDb reports a release date and certification per country;
        Deluno used to keep only the single earliest date and a single
        certification-country choice, discarding the rest. The selected
        country (Settings -> Media management -> Certification country)
        leads; every other country TMDb had something for stays reachable
        underneath rather than being thrown away.
      */}
      {releaseFactsByCountry.length ? (
        <Card>
          <CardContent className="p-4">
            <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.18em] text-muted-foreground">Release dates &amp; certification</p>
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
        Three cells, and none of them the mark.

        This was five. **File** said "On disk" or "Missing" — the mark in the
        header, restated in words and in a *second* colour, and the missing case
        was amber, the one signal reserved for "a person is needed". Nobody is
        needed: Deluno searches for it on its schedule. **Cutoff** said "Met" or
        "Below target", which is the difference between Quality met and
        Upgradable — the mark again — and on a movie with no file at all it read
        "Below target", claiming a comparison against a file that does not exist.

        What is left is what the mark cannot say: which quality is actually
        there, whether automation is on, and whether anything is stuck. See
        DESIGN-001 and #302.
      */}
      <SummaryStrip
        cells={[
          {
            label: "Quality",
            value: currentQuality ?? (movie.hasFile ? "Unknown" : "Nothing yet"),
            tone: cutoffMet === true ? "success" : undefined,
            // "last delta 0" told the user nothing they could act on (#259).
            // Say what the comparison meant instead of printing its number.
            help: !movie.hasFile
              ? `waiting for ${targetQuality}`
              // "meets X" restated the rung, which the chip beside the title
              // already says: two things saying one thing on one page. The tile
              // says what the FILE is and what the plan asked for; the chip says
              // the verdict. James, circling both: "this conflicts with what is
              // shown above as in quality met".
              : cutoffMet === true
                ? `plan asked for ${targetQuality}`
                : cutoffMet === false
                  ? `plan asks for ${targetQuality}`
                  : lastDelta === null
                    ? `plan asks for ${targetQuality}`
                    : lastDelta > 0
                      ? "last release was better"
                      : lastDelta < 0
                        ? "last release was worse"
                        : "last release was equivalent"
          },
          // **Monitoring is not a tile.** The shield in the header says the state
          // and changes it in one control, so a tile beside it is the same fact
          // read-only — James: "montoring, I dont think we need this here".
          //
          // The Next Step card still surfaces it when it is the thing standing in
          // the way, which is a different job: that is advice, not a readout.
          {
            label: "Import issues",
            value: importCases.length,
            tone: importCases.length ? "warning" : undefined,
            help: importCases.length ? "need a decision" : "nothing stuck"
          }
        ]}
      />

      {/*
        Above "Next step" on purpose. Advice about what to do next is worth
        nothing to somebody whose last three attempts went nowhere for a reason
        the screen never mentioned.
      */}
      <AcquisitionBlockersCard
        blockers={acquisitionBlockers} libraryId={libraryQuery ? actingLibraryId : null}
        route="/api/movies"
        mediaId={movie.id}
        disabled={busyAction !== null}
        onForced={() => revalidator.revalidate()}
      />

      <RetentionTitleCard entityId={movie.id} onChanged={() => revalidator.revalidate()} />

      {nextStep ? (
        <ListCard title="Next step" count={nextStep.eyebrow}>
          <ListTable chevron={false} columns={[{ label: "What Deluno suggests" }, { label: "Action", width: LIST_TRACK.action, align: "end", mobile: true }]}>
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
            <ListCard title="Choose a release" count={`${visibleCandidates.length} of ${releaseCandidates.length} candidate${releaseCandidates.length === 1 ? "" : "s"}`}>
              {candidateProtocolOptions.length > 1 || candidateIndexerOptions.length > 1 ? (
                <div className="flex flex-wrap items-center gap-2 border-b border-hairline p-[var(--card-pad-x)]">
                  <Select
                    value={candidateProtocolFilter}
                    onChange={(event) => setCandidateProtocolFilter(event.target.value)}
                    options={[{ value: "", label: "Every protocol" }, ...candidateProtocolOptions.map((protocol) => ({ value: protocol, label: formatProtocol(protocol) }))]}
                    aria-label="Filter by protocol"
                    className="max-w-[10rem]"
                  />
                  <Select
                    value={candidateIndexerFilter}
                    onChange={(event) => setCandidateIndexerFilter(event.target.value)}
                    options={[{ value: "", label: "Every indexer" }, ...candidateIndexerOptions.map((name) => ({ value: name, label: name }))]}
                    aria-label="Filter by indexer"
                    className="max-w-[12rem]"
                  />
                </div>
              ) : null}
              <ListTable
                columns={[
                  { label: "Release" },
                  { label: "Quality", mobile: true },
                  { label: <SortHeader label="Protocol" active={candidateSort?.key === "protocol"} direction={candidateSort?.direction} onClick={() => toggleCandidateSort("protocol")} /> },
                  { label: <SortHeader label="Age" active={candidateSort?.key === "age"} direction={candidateSort?.direction} onClick={() => toggleCandidateSort("age")} /> },
                  { label: <SortHeader label="Seeders/Leechers" active={candidateSort?.key === "seeders"} direction={candidateSort?.direction} onClick={() => toggleCandidateSort("seeders")} /> },
                  { label: <SortHeader label="Size" active={candidateSort?.key === "size"} direction={candidateSort?.direction} onClick={() => toggleCandidateSort("size")} /> },
                  { label: "Decision", width: LIST_TRACK.status, mobile: true }
                ]}
              >
                {visibleCandidates.map((candidate, index) => (
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
                      sub={`${index === 0 && canWinSearch(candidate) ? "Best match · " : ""}${candidate.indexerName}`}
                    />
                    <ListCell primary={candidate.quality} mobile />
                    <ListCell primary={formatProtocol(candidate.indexerProtocol)} />
                    <ListCell primary={formatReleaseAge(candidate.releaseAgeHours)} />
                    <ListCell primary={formatSeedersAndLeechers(candidate.seeders, candidate.leechers)} />
                    <ListCell primary={candidate.sizeBytes ? formatBytes(candidate.sizeBytes) : "—"} />
                    <ListCell mobile>
                      <Chip tone={candidateTone(candidate)}>{candidateLabel(candidate)}</Chip>
                      {candidate.decisionStatus?.toLowerCase() === "rejected" && candidate.decisionReasons?.[0] ? (
                        <span className="mt-1 block text-[length:var(--type-caption)] text-muted-foreground">{candidate.decisionReasons[0]}</span>
                      ) : null}
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            </ListCard>
          ) : null}

          {memberships.length > 0 ? (
            <LibraryMembershipCard
              route="/api/movies"
              mediaId={movie.id}
              noun="film"
              libraries={libraries.filter((item) => item.mediaType === "movies")}
              memberships={memberships}
              selectedLibraryId={actingLibraryId}
              onSelect={setChosenLibraryId}
              onChanged={() => revalidator.revalidate()}
            />
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
                // What this copy is, kept by Deluno rather than read back out
                // of the file name - which says only what the owner's naming
                // pattern asks for, and may say none of this.
                ...(copy?.releaseName
                  ? [{ label: "Release taken", value: copy.releaseName, path: true }]
                  : []),
                ...(copy?.formats?.length
                  ? [{ label: "Matched", value: copy.formats.map((format) => friendlyRuleName(format.name)).join(", ") }]
                  : []),
                ...(copy?.indexerName || copy?.downloadClientName
                  ? [{ label: "Came from", value: [copy.indexerName, copy.downloadClientName].filter(Boolean).join(" · ") }]
                  : [])
                // Current and Target quality are NOT here. This table is where a
                // title is ROUTED — library, folders, workflow — and quality is
                // not routing. The summary strip says both, with a tone and a
                // reason; saying them again here is the third copy that made
                // James circle it: "we have 2 different things saying the same
                // thing".
              ] as Array<{ label: string; value: string; path?: boolean }>).map(({ label, value, path }) => (
                <ListRow key={label}>
                  <ListNameCell name={label} />
                  <ListCell primary={path ? <span className="font-mono text-[length:var(--type-caption)]">{value}</span> : value} mobile />
                </ListRow>
              ))}
            </ListTable>
          </ListCard>

          <SubtitleOverrideCard
            basePath={`/api/movies/${movie.id}`}
            languages={movie.subtitleLanguagesOverride}
            mode={movie.subtitleModeOverride}
            libraryLanguages={library?.subtitleLanguages ?? []}
            options={subtitleLanguageOptions}
            onChanged={() => revalidator.revalidate()}
          />

          {subtitles.length > 0 ? (
            <ListCard title="Subtitles" count={`${subtitles.length} on disk`}>
              <ListTable
                chevron={false}
                columns={[
                  { label: "Language" },
                  { label: "Type" },
                  { label: "Source", mobile: true },
                  { label: "Timing", mobile: true },
                  { label: "Action", width: LIST_TRACK.action, align: "end", mobile: true }
                ]}
              >
                {subtitles.map((subtitle, index) => (
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
                            basePath={`/api/movies/${movie.id}`}
                            language={subtitle.language}
                            timingStatus={subtitle.timingStatus}
                            onChanged={() => revalidator.revalidate()}
                          />
                        </>
                      ) : null}
                    </div>
                  </ListRow>
                ))}
              </ListTable>
            </ListCard>
          ) : null}

          {manualSearchLanguage ? (
            <SubtitleManualSearchDialog
              open={manualSearchLanguage !== null}
              onOpenChange={(next) => { if (!next) setManualSearchLanguage(null); }}
              basePath={`/api/movies/${movie.id}`}
              language={manualSearchLanguage}
              onChanged={() => revalidator.revalidate()}
            />
          ) : null}

          <ListCard title="Automation" count={movie.monitored ? "Monitored" : "Unmonitored"}>
            <ListTable chevron={false} columns={[{ label: "Control" }, { label: "Action", width: LIST_TRACK.action, align: "end", mobile: true }]}>
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
                      : "Nothing to defer — Deluno is not searching for this movie."
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
                      ? "Let one scheduled cycle pass without searching this movie."
                      : "Nothing to skip — Deluno is not searching for this movie."
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
            <ListCard title="How this movie was added" count={`${origins.length} import list${origins.length === 1 ? "" : "s"}`}>
              <ListTable chevron={false} columns={[{ label: "Source" }, { label: "Provider", mobile: true }, { label: "First seen" }]}>
                {origins.map((origin) => (
                  <ListRow key={origin.id}>
                    <ListNameCell name={origin.sourceName} sub="Removing the list never removes this movie or its files." />
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
              <ListTable chevron={false} columns={[{ label: "Issue" }, { label: "What to do", width: "minmax(0,1.4fr)" }, { label: "Action", width: LIST_TRACK.action, align: "end", mobile: true }]}>
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

          <MovieFilesCard movieId={movie.id} />

          <ListCard title="Searches" count={movieSearches.length ? `Latest ${Math.min(movieSearches.length, 12)} of ${movieSearches.length}` : undefined}>
            {movieSearches.length === 0 ? (
              <ListEmpty
                title="No searches yet"
                description="Manual and scheduled searches for this movie appear here with their outcomes and explanations."
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
                    <ListCell primary={formatPreferenceDateTime(item.createdUtc, preferences)} />
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
              <ListTable columns={[{ label: "Release" }, { label: "Client", mobile: true }, { label: "When" }, { label: "Status", width: LIST_TRACK.status }]}>
                {movieDispatches.slice(0, 8).map((item) => (
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
              <ListEmpty title="Nothing has happened yet" description="Every event Deluno records against this movie shows up here." />
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

      <DownloadDispatchDrawer
        dispatch={openDispatch}
        onClose={() => setOpenDispatchId(null)}
        onBlocked={() => revalidator.revalidate()}
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
                ? (forceRequiredNotice ?? openCandidate.summary)
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
                mediaType="movie"
                entityId={movie.id}
                title={movie.title}
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
        description={openSearch ? formatPreferenceDateTime(openSearch.createdUtc, preferences) : undefined}
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
                  mediaType="movie"
                  entityId={movie.id}
                  title={movie.title}
                  onRemoved={() => {
                    setOpenSearchId(null);
                    revalidator.revalidate();
                  }}
                />
                <BlockReleaseAction
                  releaseName={openSearch.releaseName}
                  indexerName={openSearch.indexerName}
                  mediaType="movie"
                  entityId={movie.id}
                  title={movie.title}
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

      <MovieEditDrawer
        open={isEditOpen}
        onOpenChange={setIsEditOpen}
        movieId={movie.id}
        monitored={movie.monitored}
        qualityProfileId={movie.qualityProfileId ?? null}
        libraryId={movie.libraryId ?? null}
        filePath={movie.filePath ?? null}
        metadataJson={movie.metadataJson}
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
  indexerProtocol?: string | null;
  releaseAgeHours?: number | null;
  decisionStatus?: string;
  decisionReasons?: string[];
  riskFlags?: string[];
  qualityDelta?: number;
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

/** #753: a clickable column header for the candidates table, showing which column (if any) currently sorts it and which way. */
function SortHeader({ label, active, direction, onClick }: { label: string; active: boolean; direction?: SortDirection; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={(event) => { event.stopPropagation(); onClick(); }}
      className="flex items-center gap-1 uppercase tracking-[0.1em] text-muted-foreground hover:text-foreground"
      aria-label={`Sort by ${label}`}
    >
      {label}
      <ArrowUpDown aria-hidden className={cn("h-3 w-3", active && (direction === "asc" ? "rotate-180" : ""))} />
    </button>
  );
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
