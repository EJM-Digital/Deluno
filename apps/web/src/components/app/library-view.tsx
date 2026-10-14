import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useNavigate, useNavigation, useSearchParams } from "react-router-dom";
import type { MediaItem } from "../../lib/media-types";
import { describeMetadataSearchError } from "../../lib/metadata-check";
import {
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
  type QualityProfileItem,
  type SeriesListItem,
  type UpdateLibraryViewRequest
} from "../../lib/api";
import { adaptMovieItems, adaptSeriesItems } from "../../lib/ui-adapters";
import { isMonitoringFilter, monitoringParam, QUICK_FILTER_MARK, type MonitoringFilter, type QuickFilter } from "../../lib/library-filters";
import { applyConditions, isCompleteCondition, parseConditions } from "../../lib/library-controls";
import { titleMark } from "../../lib/status-tones";
import { LibraryCreateDialog } from "./library-create-dialog";
import { LibraryResults } from "./library-results";
import { LibrarySelectionCommandBar } from "./library-selection-command-bar";
import {
  ControlRail,
  isQuickFilter,
  type SavedFilterPreset,
  type ViewMode,
} from "./library-control-rail";
import { useLibraryAddress } from "../../hooks/use-library-address";
import { useLibraryFilters } from "../../hooks/use-library-filters";
import { useLatestRequest } from "../../hooks/use-latest-request";
import { useCoalescedRevalidate } from "../../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent } from "../../lib/use-signalr";
import { useBulkEdit } from "../../hooks/use-bulk-edit";
import { parseTagList } from "./tag-picker";
import { createInitialLibraryForm, metadataCreatePayload, parseIdPrefixedSearch, readRememberedAddTags, rememberAddTags,useLibraryCreate, type CreateFormDraft } from "../../hooks/use-library-create";
import { authedFetch } from "../../lib/use-auth";
import { describeRequestFailure } from "../../lib/search-reasons";
import { toast } from "../shell/toaster";
import { BulkRemoveDialog, type BulkRemoveOptions } from "./bulk-remove-dialog";
import { LibraryBulkToolsDialog } from "./library-bulk-tools-dialog";
import { LibrarySelectAllToggle } from "./library-select-all-toggle";
import { LibraryActions } from "./library-actions";

type Variant = "movies" | "shows";

// Last-known page, surviving the per-variant remount that `key` forces. Without
// it every visit repainted a zeroed header and empty grid for ~300ms before the
// fetch landed (#265); with it a revisit renders instantly from the last
// snapshot while a silent refetch replaces it.
//
// Keyed by the *whole* query, not just the variant. Keying it by variant alone
// meant a search result was stored under "movies" and then replayed on the next
// mount as if it were the entire library — the list showed the last thing you
// searched for, presented as everything you own.
const catalogueCache = new Map<string, { items: MediaItem[]; totalCount: number; facets: CatalogueFacets | null }>();

/** Only the most recent snapshot is worth keeping; this is an anti-flash buffer, not a store. */
const CATALOGUE_CACHE_LIMIT = 8;

/**
 * The first slice is small so the shelf paints at once; the slices behind it are
 * as large as the API will bind, because they are the difference between a
 * twenty-thousand-title library arriving in forty requests and in two hundred.
 * Both are the same keyset query — nothing about the page changed, only how many
 * the client asks for and what it does with them.
 */
const FIRST_PAGE_SIZE = 100;
const FILL_PAGE_SIZE = 500;

/**
 * The snapshot is an anti-flash buffer, so it keeps the first slice and not the
 * library. Remembering twenty thousand adapted titles per query, eight queries
 * deep, would be a cache larger than the thing it is smoothing over.
 */
const CATALOGUE_CACHE_ITEMS = FIRST_PAGE_SIZE;

function rememberCatalogue(key: string, entry: { items: MediaItem[]; totalCount: number; facets: CatalogueFacets | null }) {
  catalogueCache.delete(key);
  catalogueCache.set(key, entry);
  while (catalogueCache.size > CATALOGUE_CACHE_LIMIT) {
    const oldest = catalogueCache.keys().next().value;
    if (oldest === undefined) break;
    catalogueCache.delete(oldest);
  }
}

function sameMetadataResult(left: MetadataSearchResult, right: MetadataSearchResult) {
  return left.provider === right.provider && left.providerId === right.providerId;
}

/**
 * Whether a title already on screen is known to still belong once a chip,
 * monitoring or library switch lands, judged only from what a `MediaItem`
 * already carries (#960).
 *
 * The quick filter narrows on the mark `titleMark` derives from
 * `wantedStatus` — the same field the poster's own badge reads, so this never
 * disagrees with what a reader can already see. Monitoring and library are
 * plain fields on the item. A custom condition is not: the field-registry
 * filter engine was deliberately removed from the browser (#302), so callers
 * only trust this when no condition is active — otherwise nothing on screen
 * can honestly be called "known to match" and the shelf clears outright.
 */
export function knownStructuralMatch(item: MediaItem, quickFilter: QuickFilter, monitoring: MonitoringFilter, libraryId: string | null): boolean {
  const targetMark = QUICK_FILTER_MARK[quickFilter];
  if (targetMark !== null && titleMark(item) !== targetMark) return false;
  // A status pill speaks for monitored titles only (#1048); an unmonitored title
  // lives under Unmonitored.
  if (targetMark !== null && !item.monitored) return false;
  const targetMonitored = monitoringParam(monitoring);
  if (targetMonitored !== undefined && item.monitored !== targetMonitored) return false;
  if (libraryId && item.libraryId !== libraryId) return false;
  return true;
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
  variant,
  defaultView
}: {
  isRouteLoading?: boolean;
  metadataStatus?: MetadataProviderStatus | null;
  onReload?: () => void;
  variant: Variant;
  /** Settings -> Interface's saved default, used only when this browser has never remembered a view for this variant (#736). */
  defaultView?: ViewMode;
}) {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const {
    query, setQuery, libraryId, pickLibrary, quickFilter, setQuickFilter, view, setView, sortField, setSortField,
    sortDirection, setSortDirection, cardSize, listColumnOrder, setListColumnOrder, resetListColumnOrder, displayOptions,
    savedPresets, setSavedPresets, newPresetName, setNewPresetName, isSavingPreset,
    setIsSavingPreset, changeSize, updateDisplayOptions, activeFilterCount,
    monitoring, setMonitoring, controlSet, conditions, setConditions, clearConditions
  } = useLibraryFilters(variant, searchParams.get("filter"), defaultView, searchParams.get("library"));

  const buildCatalogueParams = useCallback((pageToken?: string, pageSize = FIRST_PAGE_SIZE) => {
    const params = new URLSearchParams({ pageSize: String(pageSize), sort: sortField, direction: sortDirection });
    if (pageToken) params.set("pageToken", pageToken);
    if (query.trim()) params.set("search", query.trim());
    if (quickFilter !== "all") params.set("status", quickFilter);
    // The other axis, sent separately so the two can narrow together. It used
    // to be a `status` value, which made "missing and unmonitored" unaskable.
    const monitored = monitoringParam(monitoring);
    if (monitored !== undefined) params.set("monitored", String(monitored));
    if (libraryId) params.set("libraryId", libraryId);
    // One `f` per condition, read against the field registry this media kind
    // declares. Applied in SQL like everything else here; nothing is filtered in
    // the browser, because the browser only ever has one page of a library that
    // may hold twenty thousand.
    applyConditions(params, conditions);
    return params;
  }, [conditions, libraryId, monitoring, query, quickFilter, sortDirection, sortField]);

  // The identity of what is on screen. A snapshot may only be replayed for the
  // exact query that produced it.
  const cacheKey = `${variant}?${buildCatalogueParams()}`;
  const cacheKeyRef = useRef(cacheKey);
  cacheKeyRef.current = cacheKey;
  const seeded = catalogueCache.get(cacheKey);

  const [libraryItems, setLibraryItems] = useState<MediaItem[]>(() => seeded?.items ?? []);
  const [libraries, setLibraries] = useState<LibraryItem[]>([]);
  const [addProfiles, setAddProfiles] = useState<QualityProfileItem[]>([]);
  const [librariesLoaded, setLibrariesLoaded] = useState(false);
  const [totalCount, setTotalCount] = useState(() => seeded?.totalCount ?? 0);
  const [facets, setFacets] = useState<CatalogueFacets | null>(() => seeded?.facets ?? null);
  const [nextPageToken, setNextPageToken] = useState<string | null>(null);
  const [isCatalogueLoading, setIsCatalogueLoading] = useState(() => seeded === undefined);
  const hasLoadedOnceRef = useRef(seeded !== undefined);
  const [hasLoadedOnce, setHasLoadedOnce] = useState(() => seeded !== undefined);
  const [isLoadingMore, setIsLoadingMore] = useState(false);
  // One background fill at a time. The loop below and the shelf's own
  // end-of-list nudge both continue from the same token, and without this they
  // would continue from it twice and append the same hundred titles.
  const isFillingRef = useRef(false);
  const [refreshVersion, setRefreshVersion] = useState(0);
  const [isUpdatingMetadata, setIsUpdatingMetadata] = useState(false);
  const [isSearchingShown, setIsSearchingShown] = useState(false);

  const compatibleLibraries = libraries
    .filter((library) => library.mediaType === (variant === "movies" ? "movies" : "tv"))
    .sort((left, right) => left.name.localeCompare(right.name));
  const changeLibrary = useLibraryAddress(libraryId, pickLibrary, librariesLoaded ? compatibleLibraries.map((library) => library.id) : null);
  const [createLibraryId, setCreateLibraryId] = useState<string | null>(null);
  const addIntoLibraryId = compatibleLibraries.some((library) => library.id === createLibraryId) ? createLibraryId : compatibleLibraries[0]?.id ?? null;

  useEffect(() => {
    let cancelled = false;
    void fetchJson<LibraryItem[]>("/api/libraries")
      .then((items) => {
        if (!cancelled) { setLibraries(items); setLibrariesLoaded(true); }
      })
      .catch(() => {
        if (!cancelled) setLibraries([]);
      });
    // For the profile choice in the add dialog (#1145); without them the dialog simply leaves the choice out.
    void fetchJson<QualityProfileItem[]>("/api/quality-profiles")
      .then((items) => {
        if (!cancelled) setAddProfiles(items);
      })
      .catch(() => {
        if (!cancelled) setAddProfiles([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const {
    selectedIds, setSelectedIds, isBulkUpdating, setIsBulkUpdating, isBulkToolsOpen, setIsBulkToolsOpen,
    isRemovalConfirmationOpen, setIsRemovalConfirmationOpen, bulkOperation, setBulkOperation,
    bulkMonitored, setBulkMonitored, bulkQualityProfileId, setBulkQualityProfileId,
    bulkMinimumAvailability, setBulkMinimumAvailability,
    bulkTargetLibraryId, setBulkTargetLibraryId, bulkMoveFiles, setBulkMoveFiles,
    bulkTagsInput, setBulkTagsInput,
    bulkTagsMode, setBulkTagsMode,
    bulkRenameTemplate, setBulkRenameTemplate, bulkRenamePreview, setBulkRenamePreview,
    isApplyingRename, applyBulkRename,
    bulkConfirming, setBulkConfirming, bulkError, setBulkError, bulkLibraries, bulkQualityProfiles,
    bulkOptionsLoading, undoStack, redoStack, runUndo, runRedo, openBulkTools,
    executeBulkToolsOperation, toggleSelectedId, toggleSelectAllVisible, clearSelection
  } = useBulkEdit({ libraryItems, setLibraryItems, variant });

  const {
    showCreate, setShowCreate, isCreating, setIsCreating, createForm, setCreateForm,
    metadataResults, setMetadataResults, selectedMetadataResults, setSelectedMetadataResults,
    isSearchingMetadata, setIsSearchingMetadata, metadataSearchSequence
  } = useLibraryCreate(variant, searchParams.get("add") === "true");

  // Opened from elsewhere with a title to look for, such as a film on a media
  // server that Deluno does not manage yet (#494): start with that title.
  const addTitle = searchParams.get("add") === "true" ? searchParams.get("title") : null;
  // #1038: the tag picker starts with the tags last used for this library.
  useEffect(() => {
    if (!showCreate) return;
    const remembered = readRememberedAddTags(addIntoLibraryId);
    setCreateForm((current) => (current.tags === remembered ? current : { ...current, tags: remembered }));
  }, [showCreate, addIntoLibraryId, setCreateForm]);

  useEffect(() => {
    if (addTitle) setCreateForm((current) => (current.title ? current : { ...current, title: addTitle }));
  }, [addTitle, setCreateForm]);

  /**
   * One page of whichever catalogue this view is showing.
   *
   * Written once because it was written six times: every load path spelled out
   * the same `variant === "movies" ? fetch movies : fetch series` and adapted
   * the result itself, which is the shape every defect worth finding in this
   * codebase has had — one rule in several places that cannot check each other.
   */
  const fetchCataloguePage = useCallback(async (params: URLSearchParams) => {
    if (variant === "movies") {
      const page = await fetchJson<CataloguePage<MovieListItem>>(`/api/movies/page?${params}`);
      return { items: adaptMovieItems(page.items), page };
    }

    const page = await fetchJson<CataloguePage<SeriesListItem>>(`/api/series/page?${params}`);
    return { items: adaptSeriesItems(page.items), page };
  }, [variant]);

  // Which change this is decides how the shelf may look while the fetch is in
  // flight, and #934 was two bugs from treating every change the same way:
  // a chip switch showed the *previous* filter's titles for the whole 250ms
  // debounce (a ghost), and a chip nobody had opened yet in this session
  // showed nothing at all until the fetch landed. Neither is acceptable for a
  // click that changed what is selected on screen; both are fine for a
  // keystroke in the search box, which is why that case keeps the old
  // behaviour of holding its results steady while the next debounced fetch
  // replaces them.
  //
  // "Structural" below means every axis but the free-text query: quick
  // filter, monitoring, library, sort and conditions. A change to any of them
  // is instant and clears to a cached snapshot or a skeleton in the same
  // paint as the click that caused it; only the query keeps the debounce and
  // the "leave the old rows up" behaviour #265 was written for.
  const prevStructuralKeyRef = useRef<string | null>(null);
  const { next: nextRequest, isStale: isRequestStale } = useLatestRequest();

  useLayoutEffect(() => {
    // A snapshot of *this* query, if we have one, is shown at once; results
    // from a different query are never replayed here (#270). Cross-variant
    // rows cannot appear either — the `key` on LibraryView remounts the
    // component per variant.
    const remembered = catalogueCache.get(cacheKeyRef.current);

    const structuralParams = buildCatalogueParams();
    structuralParams.delete("search");
    const structuralKey = `${variant}?${structuralParams}`;
    const isFirstRun = prevStructuralKeyRef.current === null;
    const isStructuralChange = !isFirstRun && prevStructuralKeyRef.current !== structuralKey;
    prevStructuralKeyRef.current = structuralKey;

    if (remembered) {
      setLibraryItems(remembered.items);
      setTotalCount(remembered.totalCount);
      setFacets(remembered.facets);
      setIsCatalogueLoading(false);
    } else if (isStructuralChange) {
      // Nothing cached for a chip/monitoring/library/sort/conditions change.
      // A chip, monitoring or library switch narrows on fields already known
      // for every title on screen; keep whichever of them the new query
      // still wants and drop the rest at once, rather than blank the whole
      // shelf while the real page loads (#960). A sort change reorders
      // rather than narrows, so it always keeps everything. An active custom
      // condition cannot be judged from what the browser already holds, so
      // that still clears to the skeleton, same as before #960.
      const hasActiveCondition = conditions.some(isCompleteCondition);
      setLibraryItems((current) => (
        hasActiveCondition ? [] : current.filter((item) => knownStructuralMatch(item, quickFilter, monitoring, libraryId))
      ));
      setTotalCount(0);
      setFacets(null);
      setIsCatalogueLoading(true);
    } else if (isFirstRun) {
      setLibraryItems([]);
      setTotalCount(0);
      setFacets(null);
      setIsCatalogueLoading(true);
    }
    // Anything else — a query edit with nothing cached — falls through with
    // the prior render's rows untouched, same as before #934.

    let cancelled = false;
    const requestId = nextRequest();
    // A live reload that was still reading is for the old view; this load replaces it.
    silentReloadToken.current += 1;
    setNextPageToken(null);
    isFillingRef.current = false;
    // The delay is a debounce for typing in the search box; a structural
    // change, and the first load of a mounted view, have nothing to debounce
    // and used to eat it as pure lag (that lag was the ghost window in #934).
    const delay = hasLoadedOnceRef.current && !isStructuralChange && !isFirstRun ? 250 : 0;
    let firstSliceLanded = false;
    const timer = window.setTimeout(async () => {
      // A later click (or keystroke) retires this token before this runs, so
      // a response that lands out of order is dropped rather than rendered.
      const stale = () => cancelled || isRequestStale(requestId);
      try {
        const { items, page } = await fetchCataloguePage(buildCatalogueParams());
        if (stale()) return;

        setLibraryItems(items);
        setTotalCount(page.totalCount ?? 0);
        setFacets(page.facets);
        setNextPageToken(page.nextPageToken);
        // Keep what is still on the page selected. Clearing everything here
        // raced a person's own click: typing in search schedules this fetch,
        // so a title selected before it landed was silently unselected and
        // the bulk actions never appeared (seen in CI as a Remove button that
        // never came).
        const stillShown = new Set(items.map((item) => item.id));
        setSelectedIds((current) => current.filter((id) => stillShown.has(id)));
        firstSliceLanded = true;
        rememberCatalogue(cacheKeyRef.current, {
          items: items.slice(0, CATALOGUE_CACHE_ITEMS),
          totalCount: page.totalCount ?? 0,
          facets: page.facets
        });
        hasLoadedOnceRef.current = true;
        setHasLoadedOnce(true);
        setIsCatalogueLoading(false);

        /*
          And then the rest of it, behind the shelf the reader is already using.

          This is the whole of #312. The query is unchanged — the same keyset
          seek, so slice four hundred costs what slice one costs — and what
          changed is that the client appends instead of replacing. One list, one
          scrollbar, Ctrl+F over the library rather than over a page of it, and
          a rail that can only be drawn because the rows are all here.

          Only the DOM is bounded, by the virtualiser, which is why this is
          faster than Radarr rather than the same trade: Radarr's three to five
          seconds is twenty thousand poster elements, not twenty thousand
          objects.
        */
        isFillingRef.current = true;
        let token = page.nextPageToken;
        setIsLoadingMore(Boolean(token));
        while (!stale() && token) {
          const slice = await fetchCataloguePage(buildCatalogueParams(token, FILL_PAGE_SIZE));
          if (stale()) return;
          setLibraryItems((current) => [...current, ...slice.items]);
          token = slice.page.nextPageToken;
          setNextPageToken(token);
        }
      } catch {
        if (stale()) return;
        // A slice that fails leaves the shelf holding what did arrive rather
        // than emptying it; only a failed *first* slice means "we have nothing".
        if (firstSliceLanded) {
          toast.error("Could not load the rest of the library.");
        } else {
          setLibraryItems([]);
          setTotalCount(0);
          setFacets(null);
          setNextPageToken(null);
          catalogueCache.delete(cacheKeyRef.current);
          toast.error("Could not load the library.");
        }
      } finally {
        isFillingRef.current = false;
        if (!stale()) {
          hasLoadedOnceRef.current = true;
          setHasLoadedOnce(true);
          setIsCatalogueLoading(false);
          setIsLoadingMore(false);
        }
      }
    }, delay);
    return () => { cancelled = true; window.clearTimeout(timer); };
  }, [buildCatalogueParams, conditions, fetchCataloguePage, isRequestStale, libraryId, monitoring, nextRequest, quickFilter, refreshVersion, setSelectedIds, variant]);

  /*
    Live changes (#1179). A film or show changing from any path (an import, a metadata
    job, a search, an edit in another tab) is announced, and the shelf follows without a
    reload and without losing the filters, the selection or the scroll position:
    - a title already on the shelf is read again on its own and swapped in place;
    - anything else (a new title, a removal, a change that may move a title in or out of
      this filter, or a large burst) reads the whole shelf again behind the one on screen
      and swaps it in at once, so nothing blanks or jumps.
    Changes are gathered for a moment, so a burst is one read.
  */
  const libraryItemsRef = useRef(libraryItems);
  libraryItemsRef.current = libraryItems;
  const silentReloadToken = useRef(0);
  const changedIds = useRef(new Set<string>());
  const needsWholeShelf = useRef(false);

  const reloadShelfQuietly = useCallback(async () => {
    const token = ++silentReloadToken.current;
    try {
      const first = await fetchCataloguePage(buildCatalogueParams());
      const collected = [...first.items];
      let next = first.page.nextPageToken;
      while (next) {
        if (token !== silentReloadToken.current) return;
        const slice = await fetchCataloguePage(buildCatalogueParams(next, FILL_PAGE_SIZE));
        collected.push(...slice.items);
        next = slice.page.nextPageToken;
      }
      if (token !== silentReloadToken.current) return;
      setLibraryItems(collected);
      setTotalCount(first.page.totalCount ?? collected.length);
      setFacets(first.page.facets);
      setNextPageToken(null);
      const stillShown = new Set(collected.map((item) => item.id));
      setSelectedIds((current) => current.filter((id) => stillShown.has(id)));
      rememberCatalogue(cacheKeyRef.current, {
        items: collected.slice(0, CATALOGUE_CACHE_ITEMS),
        totalCount: first.page.totalCount ?? collected.length,
        facets: first.page.facets
      });
    } catch {
      // The shelf on screen is still right as of the last read; the next change reads again.
    }
  }, [buildCatalogueParams, fetchCataloguePage, setSelectedIds]);

  // The filter pills' counts and the total move when any title changes (unmonitoring one moves it
  // between pills), so they are read again on every change, as one small request (#1179).
  const refreshCountsQuietly = useCallback(async () => {
    const token = silentReloadToken.current;
    try {
      const first = await fetchCataloguePage(buildCatalogueParams(undefined, 1));
      if (token !== silentReloadToken.current) return;
      setFacets(first.page.facets);
      setTotalCount(first.page.totalCount ?? libraryItemsRef.current.length);
    } catch {
      // The counts on screen are still right as of the last read; the next change reads again.
    }
  }, [buildCatalogueParams, fetchCataloguePage]);

  const followLiveChanges = useCoalescedRevalidate(() => {
    // The first load is still filling the shelf and will already hold what changed.
    if (isFillingRef.current) {
      changedIds.current.clear();
      needsWholeShelf.current = false;
      return;
    }

    const ids = [...changedIds.current];
    changedIds.current.clear();
    const wholeShelf = needsWholeShelf.current;
    needsWholeShelf.current = false;
    const shown = new Set(libraryItemsRef.current.map((item) => item.id));
    if (wholeShelf || ids.length > 12 || ids.some((id) => !shown.has(id))) {
      void reloadShelfQuietly();
      return;
    }

    void refreshCountsQuietly();
    void (async () => {
      try {
        const route = variant === "movies" ? "movies" : "series";
        const fresh = await Promise.all(ids.map(async (id) => {
          const row = await fetchJson<MovieListItem | SeriesListItem>(`/api/${route}/${id}`);
          return (variant === "movies" ? adaptMovieItems([row as MovieListItem]) : adaptSeriesItems([row as SeriesListItem]))[0];
        }));
        // A title that no longer fits this view's filters needs the shelf read again to say where it goes.
        if (fresh.some((item) => item && !knownStructuralMatch(item, quickFilter, monitoring, libraryId))) {
          void reloadShelfQuietly();
          return;
        }
        const byId = new Map(fresh.filter(Boolean).map((item) => [item.id, item]));
        setLibraryItems((current) => current.map((item) => byId.get(item.id) ?? item));
      } catch {
        void reloadShelfQuietly();
      }
    })();
  }, 500);

  const noteTitleChanged = useCallback((expected: "movies" | "shows", id: string) => {
    if (variant !== expected) return;
    if (!id || id === "*") {
      needsWholeShelf.current = true;
    } else {
      changedIds.current.add(id);
    }
    followLiveChanges();
  }, [followLiveChanges, variant]);

  useSignalREvent("MovieChanged", RealtimeGroups.Dashboard, (event) => noteTitleChanged("movies", event.id));
  useSignalREvent("SeriesChanged", RealtimeGroups.Dashboard, (event) => noteTitleChanged("shows", event.id));
  // Titles added, moved or removed by a library change.
  useSignalREvent("LibraryChanged", libraryId ? [RealtimeGroups.Dashboard, RealtimeGroups.Library(libraryId)] : RealtimeGroups.Dashboard, () => {
    needsWholeShelf.current = true;
    followLiveChanges();
  });

  /**
   * The shelf reaching its end while titles are still outstanding.
   *
   * The background fill above normally gets there first, so this is the nudge
   * for when it did not — a failed slice, or a reader who scrolls faster than
   * the library arrives. It continues from the same token behind the same
   * guard, so it can only ever be the fill happening sooner, never twice.
   */
  const loadNextCataloguePage = useCallback(async () => {
    if (!nextPageToken || isFillingRef.current || isCatalogueLoading) return;
    isFillingRef.current = true;
    setIsLoadingMore(true);
    try {
      const slice = await fetchCataloguePage(buildCatalogueParams(nextPageToken, FILL_PAGE_SIZE));
      setLibraryItems((current) => [...current, ...slice.items]);
      setNextPageToken(slice.page.nextPageToken);
    } catch {
      toast.error("Could not load more titles.");
    } finally {
      isFillingRef.current = false;
      setIsLoadingMore(false);
    }
  }, [buildCatalogueParams, fetchCataloguePage, isCatalogueLoading, nextPageToken, setLibraryItems]);

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
            monitoring: isMonitoringFilter(item.monitoring ?? null) ? item.monitoring as MonitoringFilter : "any",
            sortField: item.sortField || "title",
            sortDirection: item.sortDirection === "desc" ? "desc" : "asc",
            viewMode: item.viewMode === "list" ? "list" : "grid",
            cardSize: item.cardSize === "sm" || item.cardSize === "lg" ? item.cardSize : "md",
            displayOptions: JSON.parse(item.displayOptionsJson || "{}") as Record<string, boolean>,
            automationAction: item.automationAction === "search" ? "search" : null,
            conditions: parseConditions(item.rulesJson)
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

  /**
   * Every title matching the current query is on the shelf.
   *
   * The jump rail is derived from those rows, so this is what separates "no
   * titles under W" from "W has not arrived yet" — and it is the only reason
   * the rail can be read from the shelf instead of counted a second time in
   * SQL, where the two answers would eventually disagree.
   */
  const isComplete = hasLoadedOnce && !isCatalogueLoading && nextPageToken === null;

  const selectedCount = selectedIds.length;
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
      const savedConditions = conditions.filter(isCompleteCondition);
      const payload: CreateLibraryViewRequest = {
        variant,
        libraryId,
        name,
        quickFilter,
        monitoring,
        sortField,
        sortDirection,
        viewMode: view,
        cardSize,
        displayOptionsJson: JSON.stringify(displayOptions),
        // This field waited for "a server-side rule contract". It has one now:
        // the conditions are read against a server-declared field registry and
        // applied in SQL, so what is stored here is a filter the server can
        // actually perform rather than the browser-side rule list #302 deleted.
        // Rows written before #324 hold the nine-property record and are
        // migrated on read; older ones hold `[]` and read back as no filters,
        // which is what they meant.
        rulesJson: JSON.stringify(savedConditions)
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
          monitoring: isMonitoringFilter(created.monitoring ?? null) ? created.monitoring as MonitoringFilter : "any",
          sortField: created.sortField || "title",
          sortDirection: created.sortDirection === "desc" ? "desc" : "asc",
          viewMode: created.viewMode === "list" ? "list" : "grid",
          cardSize: created.cardSize === "sm" || created.cardSize === "lg" ? created.cardSize : "md",
          displayOptions: JSON.parse(created.displayOptionsJson || "{}") as Record<string, boolean>,
          automationAction: created.automationAction === "search" ? "search" : null,
          conditions: parseConditions(created.rulesJson)
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
    changeLibrary(preset.libraryId);
    setQuickFilter(preset.quickFilter);
    setMonitoring(preset.monitoring);
    setSortField(preset.sortField);
    setSortDirection(preset.sortDirection);
    setView(preset.viewMode);
    changeSize(preset.cardSize);
    updateDisplayOptions(preset.displayOptions);
    setConditions(preset.conditions);
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

  async function updatePresetAutomation(presetId: string, action: "search" | null) {
    const preset = savedPresets.find((item) => item.id === presetId);
    if (!preset) return;

    const payload: UpdateLibraryViewRequest = {
      libraryId: preset.libraryId,
      name: preset.name,
      quickFilter: preset.quickFilter,
      monitoring: preset.monitoring,
      sortField: preset.sortField,
      sortDirection: preset.sortDirection,
      viewMode: preset.viewMode,
      cardSize: preset.cardSize,
      displayOptionsJson: JSON.stringify(preset.displayOptions),
      rulesJson: JSON.stringify(preset.conditions),
      automationAction: action
    };

    try {
      const updated = await fetchJson<LibraryViewItem>(`/api/library-views/${presetId}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });
      setSavedPresets((current) => current.map((item) => item.id === presetId
        ? { ...item, automationAction: updated.automationAction === "search" ? "search" : null }
        : item));
      toast.success(action === "search"
        ? `${preset.name} will scope the next library search cycle.`
        : `${preset.name} is view-only again.`);
    } catch {
      toast.error(`Could not update automation for ${preset.name}.`);
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

    const idSearch = parseIdPrefixedSearch(searchTitle);

    const searchSequence = metadataSearchSequence.current + 1;
    metadataSearchSequence.current = searchSequence;
    setIsSearchingMetadata(true);
    try {
      const params = new URLSearchParams({
        mediaType: variant === "movies" ? "movies" : "tv"
      });
      if (idSearch) {
        params.set("providerId", idSearch.id);
        params.set("idSource", idSearch.source);
      } else {
        params.set("query", searchTitle);
        if (createForm.year) {
          params.set("year", createForm.year);
        }
      }

      const results = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      if (searchSequence !== metadataSearchSequence.current) {
        return;
      }

      setMetadataResults(results);
      if (results.length === 0 && !options.silent) {
        toast.info(
          metadataStatus?.isConfigured === false
            ? "Title matching is temporarily unavailable."
            : idSearch
              ? `No title found for ${idSearch.source}:${idSearch.id}.`
              : "No metadata matches found."
        );
      }
    } catch (error) {
      // A service that is not answering is said in its own words, never as
      // "No metadata matches found" (#1090).
      const failure = describeMetadataSearchError(error);
      if (!options.silent) {
        if (failure.outage) {
          toast.warning(failure.message, { description: failure.description });
        } else {
          toast.error(failure.message);
        }
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
      monitorPreset: createForm.monitorPreset,
      searchNow: createForm.searchNow,
      qualityProfileId: createForm.qualityProfileId,
      tags: createForm.tags,
      metadata: createForm.metadata
    };
  }

  function createDraftFromMetadataSelection(result: MetadataSearchResult): CreateFormDraft {
    return {
      title: result.title,
      year: result.year ? String(result.year) : "",
      imdbId: result.imdbId ?? "",
      monitored: createForm.monitored,
      monitorPreset: createForm.monitorPreset,
      searchNow: createForm.searchNow,
      qualityProfileId: createForm.qualityProfileId,
      tags: createForm.tags,
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
              searchForMissingNow: draft.searchNow,
              searchForCutoffUnmetNow: draft.searchNow,
              qualityProfileId: draft.qualityProfileId || null,
              tags: parseTagList(draft.tags),
              libraryId: addIntoLibraryId,
              ...metadataCreatePayload(resolvedMetadata)
            }
          : {
              title: draft.title,
              startYear: draft.year ? Number(draft.year) : null,
              imdbId: draft.imdbId || null,
              monitored: draft.monitored,
              // #991: "all" is the same as not sending a preset at all
              // (today's fully-monitored behaviour), so it is left out.
              listMonitorMode: draft.monitored && draft.monitorPreset !== "all" ? draft.monitorPreset : null,
              searchForMissingNow: draft.searchNow,
              searchForCutoffUnmetNow: draft.searchNow,
              qualityProfileId: draft.qualityProfileId || null,
              tags: parseTagList(draft.tags),
              libraryId: addIntoLibraryId,
              ...metadataCreatePayload(resolvedMetadata)
            }
      )
    });
    if (response.ok) rememberAddTags(addIntoLibraryId, draft.tags);
    if (!response.ok) {
      const problem = await readValidationProblem(response);
      throw new Error(problem?.title ?? `Could not add ${singular}.`);
    }
  }

  /**
   * A title the catalogue already holds is a destination, not an addition.
   *
   * The server decides which those are - the library screen holds one page of
   * the catalogue, so it cannot tell "you do not have this" from "this is not
   * on this page".
   */
  function openHeldMetadataResult(result: MetadataSearchResult) {
    if (!result.libraryEntryId) return;
    closeCreate();
    navigate(variant === "movies" ? `/movies/${result.libraryEntryId}` : `/tv/${result.libraryEntryId}`);
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

  async function handleRemoveFromDeluno({ addImportListExclusion }: BulkRemoveOptions) {
    if (!selectedIds.length) return;

    setIsBulkUpdating(true);
    try {
      const response = await authedFetch(variant === "movies" ? "/api/movies/bulk" : "/api/series/bulk", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          variant === "movies"
            ? { movieIds: selectedIds, operation: "remove", addImportListExclusion }
            : { seriesIds: selectedIds, operation: "remove", addImportListExclusion }
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
   * Search exactly the titles on screen.
   *
   * This was "Hunt N missing", and it asked a different question from the one
   * the shelf was answering. It ran its own catalogue query — library and sort
   * only — so a typed search or a picked genre narrowed the shelf and the
   * count on the button, and the hunt still searched everything missing in the
   * library. It said "Hunt 5 missing" and searched ten.
   *
   * James: *"whatever is shown is what can be searched … if we create a filter
   * for something specific we can only search that specific on screen."*
   *
   * So it searches `filtered` — the rows the grid is rendering, ids already in
   * hand. That is the model, and it is also why the mismatch cannot return:
   * there is no second query left to disagree with the first. Narrowing the
   * shelf *is* choosing what to search.
   *
   * It does not filter to missing on the way out, either. Searching a title
   * that already has a file is an upgrade search, and the acquisition pipeline
   * decides what to do with it — the same decision it makes on the library's
   * own cycle. Wanting only the missing ones is what the Missing chip is for.
   */
  async function handleSearchShown() {
    const ids = filtered.map((item) => item.id);
    if (isSearchingShown || ids.length === 0) return;

    setIsSearchingShown(true);
    const loadingId = toast.loading(`Searching for ${ids.length} ${ids.length === 1 ? singular : label}…`);
    let searchResponse: Response | null = null;
    try {
      const response = await authedFetch(variant === "movies" ? "/api/movies/bulk/search" : "/api/series/bulk/search", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(variant === "movies" ? { movieIds: ids } : { seriesIds: ids })
      });
      searchResponse = response;
      if (!response.ok) throw new Error("search-shown-failed");

      const result = await response.json() as { searchesTriggered?: number; libraryCount?: number };
      const searches = result.searchesTriggered ?? 0;
      toast.success(
        searches > 0
          ? `Searching for ${searches} ${searches === 1 ? singular : label}. Watch it in Transfers.`
          : `None of these ${label} was ready to search. Check that it is wanted in a library and a search source is set up.`,
        { id: loadingId }
      );
    } catch (searchError) {
      const explained = await describeRequestFailure(searchResponse, searchError, {
        action: "start that search",
        check: { label: "Check indexers", href: "/indexers/indexers" },
      });
      toast.error(explained.title, {
        id: loadingId,
        description: explained.description,
        action: explained.action
          ? { label: explained.action.label, onClick: () => navigate(explained.action!.href) }
          : undefined,
      });
    } finally {
      setIsSearchingShown(false);
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
      <section className="space-y-(--grid-gap)">
        {/*
          The summary band is gone. It counted Missing, Monitored, Unmonitored
          and Upgradable directly above a row of chips carrying the same four
          numbers — the same fact twice, once clickable and once not — and its
          three buttons now sit in that row instead. One row: search, scope,
          display, the filters that are also the legend, and the two things you
          can do about them.
        */}
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
          onOpenHeldResult={openHeldMetadataResult}
          onCreate={handleCreate}
          libraries={compatibleLibraries}
          libraryId={addIntoLibraryId ?? undefined}
          onLibraryChange={setCreateLibraryId}
          qualityProfiles={addProfiles.filter((profile) => profile.mediaType === (variant === "movies" ? "movies" : "tv"))}
        />

        {/* ═══════ CONTROL RAIL ═══════ */}
        <ControlRail
          label={label}
          variant={variant}
          facets={facets}
          actions={
            <LibraryActions
              singular={singular}
              label={label}
              shownCount={filtered.length}
              onToggleCreate={() => showCreate ? closeCreate() : openCreate()}
              isUpdatingMetadata={isUpdatingMetadata}
              onUpdateMetadata={() => void handleUpdateAllMetadata()}
              isSearchingShown={isSearchingShown}
              onSearchShown={() => void handleSearchShown()}
              showSeasonPassLink={variant === "shows"}
            />
          }
          controls={{
            query, setQuery, quickFilter, setQuickFilter, monitoring, setMonitoring, sortField, setSortField,
            sortDirection, setSortDirection, view, setView, cardSize, changeSize,
            listColumnOrder, setListColumnOrder, resetListColumnOrder,
            displayOptions, setDisplayOptions: updateDisplayOptions,
            controlSet, conditions, setConditions, clearConditions, savedPresets,
            libraryId, setLibraryId: changeLibrary, libraries: compatibleLibraries,

            newPresetName, setNewPresetName, isSavingPreset, saveCurrentPreset,
            applyPreset, deletePreset, updatePresetAutomation, activeFilterCount
          }}
        />

        {/*
          The count line belongs to the shelf, not to the space above it.

          It used to be a third sibling in a `space-y-(--grid-gap)` stack, so one
          sentence sat in its own band with a full gap either side: the rail, a
          gap, "11 titles shown", another gap, then the posters. It reads as a
          hole. It is a caption for what is below it, so it sits close to it, and
          the one remaining gap does the real job of separating the controls from
          the results.
        */}
        <div className="space-y-[calc(var(--grid-gap)*0.4)]">
          <LibrarySelectAllToggle
            totalCount={totalCount}
            loadedCount={libraryItems.length}
            isLoadingMore={isLoadingMore}
            isQueryLoading={isCatalogueLoading}
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
          onClear={clearSelection}
        />

        <LibraryResults
          variant={variant}
          hasLoadedOnce={hasLoadedOnce}
          isQueryLoading={isCatalogueLoading}
          items={filtered}
          label={label}
          singular={singular}
          libraryCount={facets?.all ?? totalCount}
          hasActiveFilter={activeFilterCount > 0 || Boolean(query.trim())}
          view={view}
          cardSize={cardSize}
          displayOptions={displayOptions}
          listColumnOrder={listColumnOrder}
          onListColumnOrderChange={setListColumnOrder}
          selectedIds={selectedIds}
          keyBust={`${cardSize}-${libraryId ?? "all"}-${quickFilter}-${monitoring}-${query}-${sortField}-${sortDirection}-${displayOptions.showQualityBadge}-${displayOptions.showRating}`}
          sortField={sortField}
          sortDirection={sortDirection}
          onSortChange={(field, direction) => {
            setSortField(field);
            setSortDirection(direction);
          }}
          isComplete={isComplete}
          onOpenCreate={openCreate}
          onClearFilters={() => {
            setQuickFilter("all");
            changeLibrary(null);
            setMonitoring("any");
            clearConditions();
            setQuery("");
          }}
          onSelect={openWorkspace}
          onToggle={toggleSelectedId}
          onToggleAll={toggleSelectAllVisible}
          onEndReached={() => void loadNextCataloguePage()}
        />
        </div>
      </section>

      <LibraryBulkToolsDialog
        open={isBulkToolsOpen}
        selectedCount={selectedIds.length}
        variant={variant}
        isUpdating={isBulkUpdating}
        operation={bulkOperation}
        monitored={bulkMonitored}
        qualityProfileId={bulkQualityProfileId}
        minimumAvailability={bulkMinimumAvailability}
        targetLibraryId={bulkTargetLibraryId}
        moveFiles={bulkMoveFiles}
        tags={bulkTagsInput}
        tagsMode={bulkTagsMode}
        renameTemplate={bulkRenameTemplate}
        renamePreview={bulkRenamePreview}
        isApplyingRename={isApplyingRename}
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
        onMinimumAvailabilityChange={setBulkMinimumAvailability}
        onTargetLibraryChange={setBulkTargetLibraryId}
        onMoveFilesChange={setBulkMoveFiles}
        onTagsChange={setBulkTagsInput}
        onTagsModeChange={setBulkTagsMode}
        onRenameTemplateChange={setBulkRenameTemplate}
        onExecute={() => void executeBulkToolsOperation()}
        onApplyRename={() => void applyBulkRename()}
      />

      <BulkRemoveDialog
        open={isRemovalConfirmationOpen}
        onOpenChange={setIsRemovalConfirmationOpen}
        count={selectedIds.length}
        mediaLabel={singular}
        busy={isBulkUpdating}
        onConfirm={(options) => void handleRemoveFromDeluno(options)}
      />

    </>
  );
}
