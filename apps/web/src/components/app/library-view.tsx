import React, { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate, useNavigation, useSearchParams } from "react-router-dom";
import type { MediaItem } from "../../lib/media-types";
import {
  ApiRequestError,
  fetchJson,
  readValidationProblem,
  type CatalogueFacets,
  type CataloguePage,
  type CreateLibraryViewRequest,
  type LibraryItem,
  type LibraryViewItem,
  type MetadataRefreshJobsResponse,
  type MetadataProviderStatus,
  type MetadataSearchResult,
  type MovieListItem,
  type MovieWantedSummary,
  type SeriesListItem,
  type SeriesWantedSummary
} from "../../lib/api";
import { adaptMovieItems, adaptSeriesItems } from "../../lib/ui-adapters";
import { parseDisplayOptions } from "../../lib/library-filters";
import { LibraryCreateDialog } from "./library-create-dialog";
import { LibraryResults } from "./library-results";
import { LibrarySelectionCommandBar } from "./library-selection-command-bar";
import {
  ControlRail,
  isQuickFilter,
  isSortField,
  type SavedFilterPreset,
} from "./library-control-rail";
import { useDensity } from "../../lib/use-density";
import { useLibraryFilters } from "../../hooks/use-library-filters";
import { useBulkEdit } from "../../hooks/use-bulk-edit";
import { createInitialLibraryForm, metadataCreatePayload, useLibraryCreate, type CreateFormDraft } from "../../hooks/use-library-create";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { LibraryBulkToolsDialog } from "./library-bulk-tools-dialog";
import { LibrarySelectAllToggle } from "./library-select-all-toggle";
import { LibrarySummaryHeader } from "./library-summary-header";

type Variant = "movies" | "shows";

// Last-known page per variant, surviving the per-variant remount that `key`
// forces. Without it every visit repainted a zeroed header and empty grid for
// ~300ms before the fetch landed (#265); with it a revisit renders instantly
// from the last snapshot while a silent refetch replaces it.
const catalogueCache = new Map<Variant, { items: MediaItem[]; totalCount: number; facets: CatalogueFacets | null }>();

function sameMetadataResult(left: MetadataSearchResult, right: MetadataSearchResult) {
  return left.provider === right.provider && left.providerId === right.providerId;
}

interface BulkRemovalResponse {
  successCount?: number;
  failureCount?: number;
  results?: Array<{
    movieId?: string;
    seriesId?: string;
    succeeded?: boolean;
    errorMessage?: string | null;
  }>;
}

export function LibraryView({
  isRouteLoading = false,
  metadataStatus,
  onReload,
  variant
}: {
  isRouteLoading?: boolean;
  metadataStatus?: MetadataProviderStatus | null;
  onReload?: () => void;
  variant: Variant;
}) {
  const navigate = useNavigate();
  const navigation = useNavigation();
  const [searchParams, setSearchParams] = useSearchParams();
  const { density } = useDensity();
  const [libraryItems, setLibraryItems] = useState<MediaItem[]>(() => catalogueCache.get(variant)?.items ?? []);
  const [libraries, setLibraries] = useState<LibraryItem[]>([]);
  const [totalCount, setTotalCount] = useState(() => catalogueCache.get(variant)?.totalCount ?? 0);
  const [facets, setFacets] = useState<CatalogueFacets | null>(() => catalogueCache.get(variant)?.facets ?? null);
  const [nextPageToken, setNextPageToken] = useState<string | null>(null);
  const [currentPageToken, setCurrentPageToken] = useState<string | null>(null);
  const [previousPageTokens, setPreviousPageTokens] = useState<Array<string | null>>([]);
  const [isCatalogueLoading, setIsCatalogueLoading] = useState(() => !catalogueCache.has(variant));
  const hasLoadedOnceRef = useRef(catalogueCache.has(variant));
  const [hasLoadedOnce, setHasLoadedOnce] = useState(() => catalogueCache.has(variant));
  const [isLoadingMore, setIsLoadingMore] = useState(false);
  const [refreshVersion, setRefreshVersion] = useState(0);
  const [isUpdatingMetadata, setIsUpdatingMetadata] = useState(false);
  const [isHuntingMissing, setIsHuntingMissing] = useState(false);
  const {
    query, setQuery, libraryId, setLibraryId, quickFilter, setQuickFilter, view, setView, sortField, setSortField,
    sortDirection, setSortDirection, cardSize, displayOptions,
    savedPresets, setSavedPresets, newPresetName, setNewPresetName, isSavingPreset,
    setIsSavingPreset, changeSize, updateDisplayOptions, activeFilterCount
  } = useLibraryFilters(variant, searchParams.get("filter"));

  const compatibleLibraries = libraries
    .filter((library) => library.mediaType === (variant === "movies" ? "movies" : "tv"))
    .sort((left, right) => left.name.localeCompare(right.name));

  const buildCatalogueParams = useCallback((pageToken?: string) => {
    const params = new URLSearchParams({ pageSize: "100", sort: sortField, direction: sortDirection });
    if (pageToken) params.set("pageToken", pageToken);
    if (query.trim()) params.set("search", query.trim());
    if (quickFilter !== "all") params.set("status", quickFilter);
    if (libraryId) params.set("libraryId", libraryId);
    return params;
  }, [libraryId, query, quickFilter, sortDirection, sortField]);

  useEffect(() => {
    let cancelled = false;
    void fetchJson<LibraryItem[]>("/api/libraries")
      .then((items) => {
        if (!cancelled) setLibraries(items);
      })
      .catch(() => {
        if (!cancelled) setLibraries([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const {
    selectedIds, setSelectedIds, isBulkUpdating, setIsBulkUpdating, isBulkToolsOpen, setIsBulkToolsOpen,
    isRemovalConfirmationOpen, setIsRemovalConfirmationOpen, bulkOperation, setBulkOperation,
    bulkMonitored, setBulkMonitored, bulkQualityProfileId, setBulkQualityProfileId,
    bulkTargetLibraryId, setBulkTargetLibraryId, bulkTagsInput, setBulkTagsInput,
    bulkRenameTemplate, setBulkRenameTemplate, bulkRenamePreview, setBulkRenamePreview,
    bulkConfirming, setBulkConfirming, bulkError, setBulkError, bulkLibraries, bulkQualityProfiles,
    bulkOptionsLoading, undoStack, redoStack, runUndo, runRedo, openBulkTools,
    executeBulkToolsOperation, toggleSelectedId, toggleSelectAllVisible
  } = useBulkEdit({ libraryItems, setLibraryItems, variant });

  const {
    showCreate, setShowCreate, isCreating, setIsCreating, createForm, setCreateForm,
    metadataResults, setMetadataResults, selectedMetadataResults, setSelectedMetadataResults,
    isSearchingMetadata, setIsSearchingMetadata, metadataSearchSequence
  } = useLibraryCreate(variant, searchParams.get("add") === "true");

  useEffect(() => {
    let cancelled = false;
    // Keep the previous page visible while the next one loads (#265): zeroing
    // here flashed a "0 total" header and empty grid on every visit. Stale
    // cross-variant rows cannot appear — the `key` on LibraryView remounts the
    // component per variant. Only pagination is reset so "load more" cannot
    // run against a stale token mid-reload.
    setNextPageToken(null);
    setCurrentPageToken(null);
    setPreviousPageTokens([]);
    setIsCatalogueLoading(true);
    // The delay is a debounce for typing in the search box; the first load of
    // a mounted view has nothing to debounce and used to eat it as pure lag.
    const delay = hasLoadedOnceRef.current ? 250 : 0;
    const timer = window.setTimeout(async () => {
      const params = buildCatalogueParams();
      try {
        if (variant === "movies") {
          const [page, wanted] = await Promise.all([
            fetchJson<CataloguePage<MovieListItem>>(`/api/movies/page?${params}`),
            fetchJson<MovieWantedSummary>("/api/movies/wanted")
          ]);
          if (cancelled) return;
          const items = adaptMovieItems(page.items, wanted);
          setLibraryItems(items);
          setTotalCount(page.totalCount ?? 0);
          setFacets(page.facets);
          setNextPageToken(page.nextPageToken);
          setCurrentPageToken(null);
          setPreviousPageTokens([]);
          catalogueCache.set(variant, { items, totalCount: page.totalCount ?? 0, facets: page.facets });
        } else {
          const [page, wanted] = await Promise.all([
            fetchJson<CataloguePage<SeriesListItem>>(`/api/series/page?${params}`),
            fetchJson<SeriesWantedSummary>("/api/series/wanted")
          ]);
          if (cancelled) return;
          const items = adaptSeriesItems(page.items, wanted);
          setLibraryItems(items);
          setTotalCount(page.totalCount ?? 0);
          setFacets(page.facets);
          setNextPageToken(page.nextPageToken);
          setCurrentPageToken(null);
          setPreviousPageTokens([]);
          catalogueCache.set(variant, { items, totalCount: page.totalCount ?? 0, facets: page.facets });
        }
        setSelectedIds([]);
      } catch {
        if (!cancelled) {
          setLibraryItems([]);
          setTotalCount(0);
          setFacets(null);
          setNextPageToken(null);
          catalogueCache.delete(variant);
          toast.error("Could not load the library.");
        }
      } finally {
        if (!cancelled) {
          hasLoadedOnceRef.current = true;
          setHasLoadedOnce(true);
          setIsCatalogueLoading(false);
        }
      }
    }, delay);
    return () => { cancelled = true; window.clearTimeout(timer); };
  }, [buildCatalogueParams, refreshVersion, setSelectedIds, variant]);

  async function loadNextCataloguePage() {
    if (!nextPageToken || isLoadingMore || isCatalogueLoading) return;
    setIsLoadingMore(true);
    const params = buildCatalogueParams(nextPageToken);
    try {
      if (variant === "movies") {
        const [page, wanted] = await Promise.all([
          fetchJson<CataloguePage<MovieListItem>>(`/api/movies/page?${params}`),
          fetchJson<MovieWantedSummary>("/api/movies/wanted")
        ]);
        setLibraryItems(adaptMovieItems(page.items, wanted));
        setNextPageToken(page.nextPageToken);
        setPreviousPageTokens((current) => [...current, currentPageToken]);
        setCurrentPageToken(nextPageToken);
      } else {
        const [page, wanted] = await Promise.all([
          fetchJson<CataloguePage<SeriesListItem>>(`/api/series/page?${params}`),
          fetchJson<SeriesWantedSummary>("/api/series/wanted")
        ]);
        setLibraryItems(adaptSeriesItems(page.items, wanted));
        setNextPageToken(page.nextPageToken);
        setPreviousPageTokens((current) => [...current, currentPageToken]);
        setCurrentPageToken(nextPageToken);
      }
    } catch {
      toast.error("Could not load more titles.");
    } finally {
      setIsLoadingMore(false);
    }
  }

  async function loadPreviousCataloguePage() {
    if (previousPageTokens.length === 0 || isLoadingMore) return;
    const previousToken = previousPageTokens[previousPageTokens.length - 1] ?? null;

    setIsLoadingMore(true);
    const params = buildCatalogueParams(previousToken ?? undefined);
    try {
      if (variant === "movies") {
        const [page, wanted] = await Promise.all([
          fetchJson<CataloguePage<MovieListItem>>(`/api/movies/page?${params}`),
          fetchJson<MovieWantedSummary>("/api/movies/wanted")
        ]);
        setLibraryItems(adaptMovieItems(page.items, wanted));
        setNextPageToken(page.nextPageToken);
      } else {
        const [page, wanted] = await Promise.all([
          fetchJson<CataloguePage<SeriesListItem>>(`/api/series/page?${params}`),
          fetchJson<SeriesWantedSummary>("/api/series/wanted")
        ]);
        setLibraryItems(adaptSeriesItems(page.items, wanted));
        setNextPageToken(page.nextPageToken);
      }
      setCurrentPageToken(previousToken);
      setPreviousPageTokens((current) => current.slice(0, -1));
    } catch {
      toast.error("Could not load the previous titles.");
    } finally {
      setIsLoadingMore(false);
    }
  }

  useEffect(() => {
    const query = createForm.title.trim();
    const selectedTitle = createForm.metadata?.title.trim().toLocaleLowerCase();
    if (
      !showCreate ||
      query.length < 2 ||
      metadataStatus?.isConfigured === false ||
      selectedTitle === query.toLocaleLowerCase()
    ) {
      return;
    }

    const timer = window.setTimeout(() => {
      void handleMetadataSearch({ silent: true });
    }, 350);

    return () => window.clearTimeout(timer);
  }, [
    createForm.metadata?.title,
    createForm.title,
    createForm.year,
    metadataStatus?.isConfigured,
    showCreate,
    variant
  ]);

  useEffect(() => {
    const primarySelection = selectedMetadataResults[0] ?? null;
    setCreateForm((current) => {
      if (!primarySelection) {
        if (current.metadata === null) return current;
        return { ...current, metadata: null };
      }

      if (current.metadata?.provider === primarySelection.provider && current.metadata.providerId === primarySelection.providerId) {
        return current;
      }

      return {
        ...current,
        title: primarySelection.title,
        year: primarySelection.year ? String(primarySelection.year) : current.year,
        imdbId: primarySelection.imdbId ?? current.imdbId,
        metadata: primarySelection
      };
    });
  }, [selectedMetadataResults]);


  useEffect(() => {
    let cancelled = false;

    async function loadLibraryViews() {
      try {
        const items = await fetchJson<LibraryViewItem[]>(`/api/library-views?variant=${variant}`);
        if (cancelled) {
          return;
        }

        setSavedPresets(
          items.map((item) => ({
            id: item.id,
            name: item.name,
            libraryId: item.libraryId ?? null,
            quickFilter: isQuickFilter(item.quickFilter) ? item.quickFilter : "all",
            sortField: isSortField(item.sortField) ? item.sortField : "title",
            sortDirection: item.sortDirection === "desc" ? "desc" : "asc",
            viewMode: item.viewMode === "list" ? "list" : "grid",
            cardSize: item.cardSize === "sm" || item.cardSize === "lg" ? item.cardSize : "md",
            displayOptions: parseDisplayOptions(item.displayOptionsJson)
          }))
        );
      } catch {
        if (!cancelled) {
          setSavedPresets([]);
        }
      }
    }

    void loadLibraryViews();

    return () => {
      cancelled = true;
    };
  }, [variant]);

  // The catalogue endpoint owns filtering and ordering. A browser-side pass
  // here would quietly make the answer incomplete after the first page.
  const filtered = libraryItems;

  const selectedCount = selectedIds.length;
  const monitoredCount = facets?.monitored ?? 0;
  const missingCount = facets?.missing ?? 0;
  const downloadingCount = 0;
  const downloadedCount = facets?.downloaded ?? 0;
  const label = variant === "movies" ? "movies" : "TV shows";
  const singular = variant === "movies" ? "movie" : "TV show";

  function openWorkspace(item: MediaItem) {
    navigate(item.type === "movie" ? `/movies/${item.id}` : `/tv/${item.id}`);
  }

  async function handleUpdateAllMetadata() {
    if (isUpdatingMetadata) return;
    setIsUpdatingMetadata(true);
    try {
      const endpoint = variant === "movies" ? "/api/movies/metadata/jobs" : "/api/series/metadata/jobs";
      const result = await fetchJson<MetadataRefreshJobsResponse>(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ forceAll: true, take: 500 })
      });
      toast.success(result.message);
      setRefreshVersion((current) => current + 1);
      onReload?.();
    } catch {
      toast.error(`Could not queue a metadata update for the ${variant === "movies" ? "movie" : "TV show"} library.`);
    } finally {
      setIsUpdatingMetadata(false);
    }
  }

  async function saveCurrentPreset() {
    const name = newPresetName.trim();
    if (!name) {
      toast.info("Give the custom filter a name first.");
      return;
    }

    setIsSavingPreset(true);
    try {
      const payload: CreateLibraryViewRequest = {
        variant,
        libraryId,
        name,
        quickFilter,
        sortField,
        sortDirection,
        viewMode: view,
        cardSize,
        displayOptionsJson: JSON.stringify(displayOptions),
        // Rules were a browser-only filter over an incomplete page. Persist an
        // empty legacy value until the API exposes a server-side rule contract.
        rulesJson: "[]"
      };

      const created = await fetchJson<LibraryViewItem>("/api/library-views", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });

      setSavedPresets((current) => [
        ...current,
        {
          id: created.id,
          name: created.name,
          libraryId: created.libraryId ?? null,
          quickFilter: isQuickFilter(created.quickFilter) ? created.quickFilter : "all",
          sortField: isSortField(created.sortField) ? created.sortField : "title",
          sortDirection: created.sortDirection === "desc" ? "desc" : "asc",
          viewMode: created.viewMode === "list" ? "list" : "grid",
          cardSize: created.cardSize === "sm" || created.cardSize === "lg" ? created.cardSize : "md",
          displayOptions: parseDisplayOptions(created.displayOptionsJson)
        }
      ]);
      setNewPresetName("");
      toast.success("Custom filter saved");
    } catch {
      toast.error("Could not save this custom filter.");
    } finally {
      setIsSavingPreset(false);
    }
  }

  function applyPreset(preset: SavedFilterPreset) {
    setLibraryId(preset.libraryId);
    setQuickFilter(preset.quickFilter);
    setSortField(preset.sortField);
    setSortDirection(preset.sortDirection);
    setView(preset.viewMode);
    changeSize(preset.cardSize);
    updateDisplayOptions(preset.displayOptions);
    toast.success(`Applied ${preset.name}`);
  }

  async function deletePreset(presetId: string) {
    try {
      const response = await authedFetch(`/api/library-views/${presetId}`, {
        method: "DELETE"
      });
      if (!response.ok) {
        throw new Error("delete-failed");
      }
      setSavedPresets((current) => current.filter((preset) => preset.id !== presetId));
    } catch {
      toast.error("Could not remove this custom filter.");
    }
  }

  async function handleMetadataSearch(options: { silent?: boolean } = {}) {
    const searchTitle = createForm.title.trim();
    if (!searchTitle) {
      toast.info(`Type a ${singular} name first.`);
      return;
    }

    if (metadataStatus && !metadataStatus.isConfigured) {
      toast.warning("Title matching is not available yet.", {
        description: "You can add the title manually now. Live matching will return when the Deluno server's metadata connection is available."
      });
      return;
    }

    const searchSequence = metadataSearchSequence.current + 1;
    metadataSearchSequence.current = searchSequence;
    setIsSearchingMetadata(true);
    try {
      const params = new URLSearchParams({
        mediaType: variant === "movies" ? "movies" : "tv",
        query: searchTitle
      });
      if (createForm.year) {
        params.set("year", createForm.year);
      }

      const results = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      if (searchSequence !== metadataSearchSequence.current) {
        return;
      }

      setMetadataResults(results);
      if (results.length === 0 && !options.silent) {
        toast.info(metadataStatus?.isConfigured === false ? "Title matching is temporarily unavailable." : "No metadata matches found.");
      }
    } catch (error) {
      const message =
        error instanceof ApiRequestError
          ? error.message
          : "Metadata search failed.";
      if (!options.silent) {
        toast.error(message);
      }
    } finally {
      if (searchSequence === metadataSearchSequence.current) {
        setIsSearchingMetadata(false);
      }
    }
  }

  async function enrichMetadataResult(result: MetadataSearchResult) {
    try {
      const params = new URLSearchParams({
        mediaType: variant === "movies" ? "movies" : "tv",
        query: result.title,
        providerId: result.providerId
      });
      if (result.year) {
        params.set("year", String(result.year));
      }

      const details = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      return details.find((item) => item.providerId === result.providerId) ?? result;
    } catch {
      // Keep card-level metadata available even if details fail.
      return result;
    }
  }

  function createDraftFromCurrentForm(): CreateFormDraft {
    return {
      title: createForm.title,
      year: createForm.year,
      imdbId: createForm.imdbId,
      monitored: createForm.monitored,
      metadata: createForm.metadata
    };
  }

  function createDraftFromMetadataSelection(result: MetadataSearchResult): CreateFormDraft {
    return {
      title: result.title,
      year: result.year ? String(result.year) : "",
      imdbId: result.imdbId ?? "",
      monitored: createForm.monitored,
      metadata: result
    };
  }

  async function submitCreateDraft(draft: CreateFormDraft) {
    const resolvedMetadata = draft.metadata ? await enrichMetadataResult(draft.metadata) : null;
    const response = await authedFetch(variant === "movies" ? "/api/movies" : "/api/series", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? {
              title: draft.title,
              releaseYear: draft.year ? Number(draft.year) : null,
              imdbId: draft.imdbId || null,
              monitored: draft.monitored,
              ...metadataCreatePayload(resolvedMetadata)
            }
          : {
              title: draft.title,
              startYear: draft.year ? Number(draft.year) : null,
              imdbId: draft.imdbId || null,
              monitored: draft.monitored,
              ...metadataCreatePayload(resolvedMetadata)
            }
      )
    });
    if (!response.ok) {
      const problem = await readValidationProblem(response);
      throw new Error(problem?.title ?? `Could not add ${singular}.`);
    }
  }

  function applyMetadataResult(result: MetadataSearchResult) {
    setSelectedMetadataResults((currentSelection) => {
      const isSelected = currentSelection.some((item) => sameMetadataResult(item, result));
      if (isSelected) {
        return currentSelection.filter((item) => !sameMetadataResult(item, result));
      }

      void enrichMetadataResult(result).then((resolvedMetadata) => {
        setSelectedMetadataResults((current) =>
          current.some((item) => sameMetadataResult(item, result))
            ? current.map((item) => (sameMetadataResult(item, result) ? resolvedMetadata : item))
            : current
        );
      });

      return [...currentSelection, result];
    });
  }

  async function handleRemoveFromDeluno() {
    if (!selectedIds.length) return;

    setIsBulkUpdating(true);
    try {
      const response = await authedFetch(variant === "movies" ? "/api/movies/bulk" : "/api/series/bulk", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          variant === "movies"
            ? { movieIds: selectedIds, operation: "remove" }
            : { seriesIds: selectedIds, operation: "remove" }
        )
      });
      if (!response.ok) throw new Error("remove-from-deluno-failed");

      const result = await response.json() as BulkRemovalResponse;
      const removedIds = (result.results ?? [])
        .filter((item) => item.succeeded)
        .map((item) => item.movieId ?? item.seriesId)
        .filter((id): id is string => Boolean(id));
      const idsToRemove = removedIds.length ? removedIds : selectedIds;
      const failedCount = result.failureCount ?? Math.max(0, selectedIds.length - idsToRemove.length);

      setLibraryItems((current) => current.filter((item) => !idsToRemove.includes(item.id)));
      setSelectedIds((current) => current.filter((id) => !idsToRemove.includes(id)));
      setIsRemovalConfirmationOpen(false);
      toast.success(
        `${idsToRemove.length} ${singular}${idsToRemove.length === 1 ? "" : "s"} removed from Deluno. Imported files and download-client items were left alone.`
      );
      if (failedCount > 0) {
        toast.error(`${failedCount} title${failedCount === 1 ? "" : "s"} could not be removed.`);
      }
      // The rows are already gone optimistically; this resyncs the header
      // total and the filter chip counts, which the optimistic edit cannot.
      refreshCatalogue();
    } catch {
      toast.error("Could not remove the selected titles from Deluno.");
    } finally {
      setIsBulkUpdating(false);
    }
  }

  function openCreate() {
    setShowCreate(true);
  }

  function closeCreate() {
    metadataSearchSequence.current += 1;
    setIsSearchingMetadata(false);
    setSelectedMetadataResults([]);
    setShowCreate(false);
    if (searchParams.has("add")) {
      setSearchParams((current) => {
        const next = new URLSearchParams(current);
        next.delete("add");
        return next;
      }, { replace: true });
    }
  }

  async function handleCreate(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const selectedDrafts = selectedMetadataResults.map(createDraftFromMetadataSelection);
    if (!createForm.title.trim() && selectedDrafts.length === 0) {
      toast.info(`Type a ${singular} name first.`);
      return;
    }

    setIsCreating(true);
    try {
      if (selectedDrafts.length > 0) {
        const settled = await Promise.allSettled(selectedDrafts.map((draft) => submitCreateDraft(draft)));
        const successCount = settled.filter((result) => result.status === "fulfilled").length;
        const failureCount = settled.length - successCount;

        if (successCount > 0) {
          toast.success(`${successCount} ${successCount === 1 ? singular : label} added`);
        }

        if (failureCount === 0) {
          setCreateForm(createInitialLibraryForm());
          setMetadataResults([]);
          setSelectedMetadataResults([]);
          closeCreate();
          refreshCatalogue();
          return;
        }

        setSelectedMetadataResults((current) =>
          current.filter((_, index) => settled[index]?.status === "rejected")
        );
        toast.error(`${failureCount} ${failureCount === 1 ? "title" : "titles"} could not be added.`);
        if (successCount > 0) {
          refreshCatalogue();
        }
        return;
      }

      await submitCreateDraft(createDraftFromCurrentForm());
      toast.success(variant === "movies" ? "Movie added" : "TV show added");
      setCreateForm(createInitialLibraryForm());
      setMetadataResults([]);
      setSelectedMetadataResults([]);
      closeCreate();
      refreshCatalogue();
    } catch (error) {
      const msg = error instanceof Error ? error.message : "Create failed.";
      toast.error(msg);
    } finally {
      setIsCreating(false);
    }
  }

  /**
   * "Hunt N missing" used to render with no handler at all — a headline
   * control on both media pages that did nothing (#252). It now queues the
   * missing-search cycle for the libraries those titles belong to, which is
   * what the bulk-search endpoint does with an explicit selection.
   */
  async function handleHuntMissing() {
    if (isHuntingMissing || missingCount === 0) return;
    setIsHuntingMissing(true);
    const loadingId = toast.loading(`Hunting ${missingCount} missing ${missingCount === 1 ? singular : label}…`);
    try {
      // Ask the server which titles are missing rather than relying on the
      // loaded page: the header count is a whole-library facet, so the rows it
      // counts may not all be on screen.
      const params = new URLSearchParams({ pageSize: "100", status: "missing", sort: sortField, direction: sortDirection });
      if (libraryId) params.set("libraryId", libraryId);
      const page = await fetchJson<CataloguePage<{ id: string }>>(
        `/api/${variant === "movies" ? "movies" : "series"}/page?${params}`
      );
      const ids = page.items.map((item) => item.id);
      if (ids.length === 0) {
        toast.info("Nothing is missing right now.", { id: loadingId });
        return;
      }

      const response = await authedFetch(variant === "movies" ? "/api/movies/bulk/search" : "/api/series/bulk/search", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(variant === "movies" ? { movieIds: ids } : { seriesIds: ids })
      });
      if (!response.ok) throw new Error("hunt-missing-failed");

      const result = await response.json() as { searchesTriggered?: number; libraryCount?: number };
      const searches = result.searchesTriggered ?? 0;
      toast.success(
        searches > 0
          ? `Searching for missing titles in ${searches} ${searches === 1 ? "library" : "libraries"}. Watch it in Transfers.`
          : "No library was ready to search. Check that automation and a search source are configured.",
        { id: loadingId }
      );
    } catch {
      toast.error("Could not start the hunt for missing titles.", { id: loadingId });
    } finally {
      setIsHuntingMissing(false);
    }
  }

  /**
   * Reload the catalogue itself, not just the route loader. `onReload` only
   * revalidates the router data (metadata status); the catalogue is fetched by
   * the effect keyed on refreshVersion, so a create that called only onReload
   * left the list showing its pre-create state until a manual reload (#251).
   */
  function refreshCatalogue() {
    setRefreshVersion((current) => current + 1);
    onReload?.();
  }

  function closeBulkTools() {
    setIsBulkToolsOpen(false);
    setBulkConfirming(false);
    setBulkError(null);
    setBulkRenamePreview([]);
  }

  return (
    <>
      <section className="space-y-[var(--grid-gap)]">
        <LibrarySummaryHeader
          label={label}
          singular={singular}
          isLoading={isCatalogueLoading && !hasLoadedOnce}
          totalCount={totalCount}
          downloadedCount={downloadedCount}
          monitoredCount={monitoredCount}
          missingCount={missingCount}
          downloadingCount={downloadingCount}
          onToggleCreate={() => showCreate ? closeCreate() : openCreate()}
          isUpdatingMetadata={isUpdatingMetadata}
          onUpdateMetadata={() => void handleUpdateAllMetadata()}
          isHuntingMissing={isHuntingMissing}
          onHuntMissing={() => void handleHuntMissing()}
        />
        <LibraryCreateDialog
          open={showCreate}
          onOpenChange={(open) => (open ? openCreate() : closeCreate())}
          variant={variant}
          label={label}
          singular={singular}
          metadataStatus={metadataStatus}
          isCreating={isCreating}
          createForm={createForm}
          setCreateForm={setCreateForm}
          metadataResults={metadataResults}
          setMetadataResults={setMetadataResults}
          selectedMetadataResults={selectedMetadataResults}
          setSelectedMetadataResults={setSelectedMetadataResults}
          isSearchingMetadata={isSearchingMetadata}
          metadataSearchSequence={metadataSearchSequence}
          onSearch={() => void handleMetadataSearch()}
          onSelectResult={applyMetadataResult}
          onCreate={handleCreate}
        />

        {/* ═══════ CONTROL RAIL ═══════ */}
        <ControlRail
          label={label}
          facets={facets}
          controls={{
            query, setQuery, quickFilter, setQuickFilter, sortField, setSortField,
            sortDirection, setSortDirection, view, setView, cardSize, changeSize,
            displayOptions, setDisplayOptions: updateDisplayOptions, savedPresets,
            libraryId, setLibraryId, libraries: compatibleLibraries,
            newPresetName, setNewPresetName, isSavingPreset, saveCurrentPreset,
            applyPreset, deletePreset, activeFilterCount
          }}
        />

        <LibrarySelectAllToggle
          totalCount={totalCount}
          loadedCount={libraryItems.length}
          filteredCount={filtered.length}
          selectedCount={selectedCount}
          allVisibleSelected={filtered.length > 0 && filtered.every((item) => selectedIds.includes(item.id))}
          onToggle={toggleSelectAllVisible}
          view={view}
        />

        {/* Action messages now surface through the global Toaster */}

        <LibrarySelectionCommandBar
          count={selectedCount}
          isUpdating={isBulkUpdating}
          canUndo={undoStack.length > 0}
          canRedo={redoStack.length > 0}
          onUndo={() => void runUndo()}
          onRedo={() => void runRedo()}
          onOpenBulkTools={openBulkTools}
          onRemove={() => setIsRemovalConfirmationOpen(true)}
          onClear={() => setSelectedIds([])}
        />

        <LibraryResults
          isLoading={isRouteLoading || navigation.state !== "idle" || isCatalogueLoading}
          items={filtered}
          label={label}
          singular={singular}
          libraryCount={facets?.all ?? totalCount}
          hasActiveFilter={Boolean(query.trim()) || libraryId !== null || quickFilter !== "all"}
          view={view}
          cardSize={cardSize}
          density={density}
          displayOptions={displayOptions}
          selectedIds={selectedIds}
          keyBust={`${cardSize}-${libraryId ?? "all"}-${quickFilter}-${query}-${sortField}-${sortDirection}-${displayOptions.showMeta}-${displayOptions.showStatusPill}-${displayOptions.showQualityBadge}-${displayOptions.showRating}`}
          isLoadingMore={isLoadingMore}
          hasPreviousPage={previousPageTokens.length > 0}
          hasNextPage={Boolean(nextPageToken)}
          onOpenCreate={openCreate}
          onClearFilters={() => {
            setQuickFilter("all");
            setLibraryId(null);
            setQuery("");
          }}
          onSelect={openWorkspace}
          onToggle={toggleSelectedId}
          onToggleAll={toggleSelectAllVisible}
          onPreviousPage={() => void loadPreviousCataloguePage()}
          onNextPage={() => void loadNextCataloguePage()}
        />
      </section>

      <LibraryBulkToolsDialog
        open={isBulkToolsOpen}
        selectedCount={selectedIds.length}
        variant={variant}
        isUpdating={isBulkUpdating}
        operation={bulkOperation}
        monitored={bulkMonitored}
        qualityProfileId={bulkQualityProfileId}
        targetLibraryId={bulkTargetLibraryId}
        tags={bulkTagsInput}
        renameTemplate={bulkRenameTemplate}
        renamePreview={bulkRenamePreview}
        confirming={bulkConfirming}
        error={bulkError}
        libraries={bulkLibraries}
        qualityProfiles={bulkQualityProfiles}
        isOptionsLoading={bulkOptionsLoading}
        undoCount={undoStack.length}
        redoCount={redoStack.length}
        onClose={closeBulkTools}
        onOperationChange={(operation) => {
          setBulkOperation(operation);
          setBulkConfirming(false);
          setBulkError(null);
          setBulkRenamePreview([]);
        }}
        onMonitoredChange={setBulkMonitored}
        onQualityProfileChange={setBulkQualityProfileId}
        onTargetLibraryChange={setBulkTargetLibraryId}
        onTagsChange={setBulkTagsInput}
        onRenameTemplateChange={setBulkRenameTemplate}
        onExecute={() => void executeBulkToolsOperation()}
      />

      <ConfirmDialog
        open={isRemovalConfirmationOpen}
        onOpenChange={setIsRemovalConfirmationOpen}
        title={`Remove ${selectedIds.length} ${singular}${selectedIds.length === 1 ? "" : "s"} from Deluno?`}
        description="This removes the selected catalogue record and stops Deluno managing it. It does not delete imported media files or remove anything from your download client."
        confirmLabel="Remove from Deluno"
        busy={isBulkUpdating}
        onConfirm={() => void handleRemoveFromDeluno()}
      />

    </>
  );
}
