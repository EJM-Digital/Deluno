import {
  ArrowDownAZ,
  ArrowUpDown,
  ChevronDown,
  CircleOff,
  Eye,
  Filter,
  FolderTree,
  LayoutTemplate,
  LayoutGrid,
  List,
  LoaderCircle,
  Play,
  Plus,
  Redo2,
  Search,
  ShieldCheck,
  SlidersHorizontal,
  Star,
  Trash2,
  Undo2,
  X,
  Zap,
} from "lucide-react";
import React, { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import * as Dialog from "@radix-ui/react-dialog";
import { Link, useNavigate, useNavigation, useSearchParams } from "react-router-dom";
import type { MediaItem, MediaStatus } from "../../lib/media-types";
import { MEDIA_STATUS_PRESENTATION, librarySummaryTone, mediaStatusIsActive } from "../../lib/media-status-presentation";
import {
  ApiRequestError,
  fetchJson,
  readValidationProblem,
  type CatalogueFacets,
  type CataloguePage,
  type CreateLibraryViewRequest,
  type LibraryItem,
  type LibraryViewItem,
  type MetadataProviderStatus,
  type MetadataSearchResult,
  type MovieListItem,
  type MovieWantedSummary,
  type QualityProfileItem,
  type SeriesListItem,
  type SeriesWantedSummary
} from "../../lib/api";
import { adaptMovieItems, adaptSeriesItems } from "../../lib/ui-adapters";
import { useDensity, type Density } from "../../lib/use-density";
import { authedFetch } from "../../lib/use-auth";
import { cn, formatBytesFromGb } from "../../lib/utils";
import { GlassTile, PageHero, StatChip } from "../shell/page-hero";
import { EmptyState } from "../shell/empty-state";
import { LibraryGridSkeleton } from "../shell/skeleton";
import { toast } from "../shell/toaster";
import { Badge } from "../ui/badge";
import { Button } from "../ui/button";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { Input } from "../ui/input";

type Variant = "movies" | "shows";
type QuickFilter =
  | "all"
  | "monitored"
  | "unmonitored"
  | "downloaded"
  | "missing"
  | "upgrades";
type ViewMode = "grid" | "list";
type SortField =
  | "title"
  | "year"
  | "rating"
  | "added";
type SortDirection = "asc" | "desc";
type CardSize = "sm" | "md" | "lg";
type CreateFormDraft = {
  title: string;
  year: string;
  imdbId: string;
  monitored: boolean;
  metadata: MetadataSearchResult | null;
};
type FilterField =
  | "title"
  | "status"
  | "monitored"
  | "quality"
  | "genre"
  | "year"
  | "rating"
  | "sizeGb"
  | "bitrateMbps"
  | "network"
  | "releaseGroup"
  | "tags"
  | "source"
  | "codec"
  | "audioCodec"
  | "audioChannels"
  | "language"
  | "hdrFormat"
  | "releaseStatus"
  | "certification"
  | "collection"
  | "minimumAvailability"
  | "consideredAvailable"
  | "digitalRelease"
  | "physicalRelease"
  | "releaseDate"
  | "inCinemas"
  | "originalLanguage"
  | "originalTitle"
  | "path"
  | "qualityProfile"
  | "runtimeMinutes"
  | "studio"
  | "tmdbRating"
  | "tmdbVotes"
  | "imdbRating"
  | "imdbVotes"
  | "traktRating"
  | "traktVotes"
  | "tomatoRating"
  | "tomatoVotes"
  | "popularity"
  | "keywords"
  | "wantedReason"
  | "currentQuality"
  | "targetQuality"
  | "type";
type FilterComparator = "contains" | "equals" | "notEquals" | "gt" | "gte" | "lt" | "lte";

interface CustomFilterRule {
  id: string;
  field: FilterField;
  comparator: FilterComparator;
  value: string;
}

interface SavedFilterPreset {
  id: string;
  name: string;
  quickFilter: QuickFilter;
  sortField: SortField;
  sortDirection: SortDirection;
  viewMode: ViewMode;
  cardSize: CardSize;
  displayOptions: DisplayOptions;
}

interface DisplayOptions {
  showTitle: boolean;
  showMeta: boolean;
  showStatusPill: boolean;
  showQualityBadge: boolean;
  showRating: boolean;
}

function sameMetadataResult(left: MetadataSearchResult, right: MetadataSearchResult) {
  return left.provider === right.provider && left.providerId === right.providerId;
}

type BulkWorkflowOperation =
  | "monitoring"
  | "quality"
  | "reassignLibrary"
  | "tags"
  | "search"
  | "renamePreview";

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

interface BulkRenamePreviewItem {
  itemId: string;
  title: string;
  year: number | null;
  template: string;
  proposedName: string;
}

interface BulkHistoryEntry {
  label: string;
  undoLabel: string;
  redoLabel: string;
  undo: () => Promise<void>;
  redo: () => Promise<void>;
}

const SIZE_STORAGE_KEY = (v: Variant) => `deluno-card-size-${v}`;
const DISPLAY_STORAGE_KEY = (v: Variant) => `deluno-display-options-${v}`;

function resolveInitialSize(variant: Variant): CardSize {
  try {
    const stored = localStorage.getItem(SIZE_STORAGE_KEY(variant)) as CardSize | null;
    if (stored === "sm" || stored === "md" || stored === "lg") return stored;
  } catch { /* ignore */ }
  return "md";
}

function resolveInitialDisplayOptions(variant: Variant): DisplayOptions {
  try {
    const raw = localStorage.getItem(DISPLAY_STORAGE_KEY(variant));
    if (!raw) return defaultDisplayOptions();
    const parsed = JSON.parse(raw) as Partial<DisplayOptions>;
    return {
      showTitle: parsed.showTitle ?? true,
      showMeta: parsed.showMeta ?? true,
      showStatusPill: parsed.showStatusPill ?? true,
      showQualityBadge: parsed.showQualityBadge ?? true,
      showRating: parsed.showRating ?? true
    };
  } catch {
    return defaultDisplayOptions();
  }
}

const GRID_MIN_BY_DENSITY: Record<Density, Record<CardSize, string>> = {
  compact: { sm: "var(--library-card-sm)", md: "var(--library-card-md)", lg: "var(--library-card-lg)" },
  comfortable: { sm: "var(--library-card-sm)", md: "var(--library-card-md)", lg: "var(--library-card-lg)" },
  spacious: { sm: "var(--library-card-sm)", md: "var(--library-card-md)", lg: "var(--library-card-lg)" },
  expanded: { sm: "var(--library-card-sm)", md: "var(--library-card-md)", lg: "var(--library-card-lg)" }
};

const TITLE_CLASS_BY_DENSITY: Record<Density, Record<CardSize, string>> = {
  compact: { sm: "text-[length:var(--library-title-sm)]", md: "text-[length:var(--library-title-md)]", lg: "text-[length:var(--library-title-lg)]" },
  comfortable: { sm: "text-[length:var(--library-title-sm)]", md: "text-[length:var(--library-title-md)]", lg: "text-[length:var(--library-title-lg)]" },
  spacious: { sm: "text-[length:var(--library-title-sm)]", md: "text-[length:var(--library-title-md)]", lg: "text-[length:var(--library-title-lg)]" },
  expanded: { sm: "text-[length:var(--library-title-sm)]", md: "text-[length:var(--library-title-md)]", lg: "text-[length:var(--library-title-lg)]" }
};

/** Whether to show sub-metadata (year / genre) per size */
const SHOW_META: Record<CardSize, boolean> = {
  sm: false,
  md: true,
  lg: true,
};

const quickFilterConfig: Array<{ key: QuickFilter; label: string }> = [
  { key: "all", label: "All" },
  { key: "monitored", label: "Monitored" },
  { key: "unmonitored", label: "Unmonitored" },
  { key: "downloaded", label: "Downloaded" },
  { key: "missing", label: "Missing" },
  { key: "upgrades", label: "Upgrades" }
];

function isQuickFilter(value: string | null): value is QuickFilter {
  return quickFilterConfig.some((filter) => filter.key === value);
}

function isSortField(value: string | null): value is SortField {
  return sortFieldOptions.some((sort) => sort.value === value);
}

function isUpgradeCandidate(item: MediaItem) {
  return item.wantedReason?.toLowerCase().includes("upgrade") === true ||
    (item.status === "downloaded" &&
      Boolean(item.currentQuality) &&
      Boolean(item.targetQuality) &&
      item.currentQuality !== item.targetQuality);
}

function isAttentionCandidate(item: MediaItem) {
  return item.status === "importFailed" || item.status === "processingFailed";
}

const sortFieldOptions: Array<{ value: SortField; label: string }> = [
  { value: "title", label: "Title" },
  { value: "year", label: "Year" },
  { value: "rating", label: "Rating" },
  { value: "added", label: "Added" }
];

const filterFieldOptions: Array<{ value: FilterField; label: string; kind: "text" | "number" | "boolean" | "enum" }> = [
  { value: "title", label: "Title", kind: "text" },
  { value: "status", label: "Status", kind: "enum" },
  { value: "monitored", label: "Monitored", kind: "boolean" },
  { value: "quality", label: "Quality", kind: "text" },
  { value: "genre", label: "Genre", kind: "text" },
  { value: "year", label: "Year", kind: "number" },
  { value: "rating", label: "Rating", kind: "number" },
  { value: "sizeGb", label: "Size on disk", kind: "number" },
  { value: "bitrateMbps", label: "Bitrate", kind: "number" },
  { value: "network", label: "Network", kind: "text" },
  { value: "releaseGroup", label: "Release group", kind: "text" },
  { value: "tags", label: "Tags", kind: "text" },
  { value: "source", label: "Source", kind: "enum" },
  { value: "codec", label: "Codec", kind: "enum" },
  { value: "audioCodec", label: "Audio codec", kind: "enum" },
  { value: "audioChannels", label: "Audio channels", kind: "enum" },
  { value: "language", label: "Language", kind: "enum" },
  { value: "hdrFormat", label: "HDR format", kind: "enum" },
  { value: "releaseStatus", label: "Release status", kind: "enum" },
  { value: "certification", label: "Certification", kind: "enum" },
  { value: "collection", label: "Collection", kind: "text" },
  { value: "minimumAvailability", label: "Minimum availability", kind: "enum" },
  { value: "consideredAvailable", label: "Considered available", kind: "boolean" },
  { value: "digitalRelease", label: "Digital release", kind: "text" },
  { value: "physicalRelease", label: "Physical release", kind: "text" },
  { value: "releaseDate", label: "Release date", kind: "text" },
  { value: "inCinemas", label: "In cinemas", kind: "text" },
  { value: "originalLanguage", label: "Original language", kind: "enum" },
  { value: "originalTitle", label: "Original title", kind: "text" },
  { value: "path", label: "Path", kind: "text" },
  { value: "qualityProfile", label: "Quality profile", kind: "enum" },
  { value: "runtimeMinutes", label: "Runtime", kind: "number" },
  { value: "studio", label: "Studio", kind: "text" },
  { value: "tmdbRating", label: "TMDb rating", kind: "number" },
  { value: "tmdbVotes", label: "TMDb votes", kind: "number" },
  { value: "imdbRating", label: "IMDb rating", kind: "number" },
  { value: "imdbVotes", label: "IMDb votes", kind: "number" },
  { value: "traktRating", label: "Trakt rating", kind: "number" },
  { value: "traktVotes", label: "Trakt votes", kind: "number" },
  { value: "tomatoRating", label: "Tomato rating", kind: "number" },
  { value: "tomatoVotes", label: "Tomato votes", kind: "number" },
  { value: "popularity", label: "Popularity", kind: "number" },
  { value: "keywords", label: "Keywords", kind: "text" },
  { value: "wantedReason", label: "Wanted reason", kind: "text" },
  { value: "currentQuality", label: "Current quality", kind: "text" },
  { value: "targetQuality", label: "Target quality", kind: "text" },
  { value: "type", label: "Media type", kind: "enum" }
];

const enumOptions: Partial<Record<FilterField, Array<{ value: string; label: string }>>> = {
  status: [
    { value: "downloaded", label: "Downloaded" },
    { value: "downloading", label: "Downloading" },
    { value: "missing", label: "Missing" },
    { value: "processing", label: "Processing" },
    { value: "importReady", label: "Import ready" },
    { value: "importQueued", label: "Import queued" },
    { value: "importFailed", label: "Import failed" },
    { value: "processingFailed", label: "Processing failed" }
  ],
  monitored: [
    { value: "true", label: "Yes" },
    { value: "false", label: "No" }
  ],
  consideredAvailable: [
    { value: "true", label: "Yes" },
    { value: "false", label: "No" }
  ],
  source: [
    { value: "WEB-DL", label: "WEB-DL" },
    { value: "Bluray", label: "Bluray" },
    { value: "Remux", label: "Remux" },
    { value: "HDTV", label: "HDTV" }
  ],
  codec: [
    { value: "H.264", label: "H.264" },
    { value: "H.265", label: "H.265" },
    { value: "AV1", label: "AV1" }
  ],
  audioCodec: [
    { value: "AAC", label: "AAC" },
    { value: "DD+", label: "DD+" },
    { value: "DTS-HD MA", label: "DTS-HD MA" },
    { value: "TrueHD Atmos", label: "TrueHD Atmos" }
  ],
  audioChannels: [
    { value: "2.0", label: "2.0" },
    { value: "5.1", label: "5.1" },
    { value: "7.1", label: "7.1" }
  ],
  language: [
    { value: "English", label: "English" },
    { value: "Japanese", label: "Japanese" },
    { value: "Korean", label: "Korean" },
    { value: "Spanish", label: "Spanish" }
  ],
  hdrFormat: [
    { value: "SDR", label: "SDR" },
    { value: "HDR10", label: "HDR10" },
    { value: "HDR10+", label: "HDR10+" },
    { value: "Dolby Vision", label: "Dolby Vision" }
  ],
  releaseStatus: [
    { value: "Available", label: "Available" },
    { value: "Downloading", label: "Downloading" },
    { value: "missing", label: "Missing" },
    { value: "upgrade", label: "Upgrade" },
    { value: "Monitored", label: "Monitored" },
    { value: "Not monitored", label: "Not monitored" }
  ],
  certification: [
    { value: "G", label: "G" },
    { value: "PG", label: "PG" },
    { value: "PG-13", label: "PG-13" },
    { value: "R", label: "R" },
    { value: "TV-14", label: "TV-14" },
    { value: "TV-MA", label: "TV-MA" }
  ],
  minimumAvailability: [
    { value: "Announced", label: "Announced" },
    { value: "In cinemas", label: "In cinemas" },
    { value: "Released", label: "Released" },
    { value: "Digital", label: "Digital" },
    { value: "Physical", label: "Physical" }
  ],
  originalLanguage: [
    { value: "English", label: "English" },
    { value: "Japanese", label: "Japanese" },
    { value: "Korean", label: "Korean" },
    { value: "Spanish", label: "Spanish" },
    { value: "French", label: "French" }
  ],
  qualityProfile: [
    { value: "HD 1080p", label: "HD 1080p" },
    { value: "Ultra HD", label: "Ultra HD" },
    { value: "Remux", label: "Remux" },
    { value: "Kids", label: "Kids" },
    { value: "Anime", label: "Anime" }
  ],
  type: [
    { value: "movie", label: "Movies" },
    { value: "show", label: "TV shows" }
  ]
};

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
  const [libraryItems, setLibraryItems] = useState<MediaItem[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [facets, setFacets] = useState<CatalogueFacets | null>(null);
  const [nextPageToken, setNextPageToken] = useState<string | null>(null);
  const [currentPageToken, setCurrentPageToken] = useState<string | null>(null);
  const [previousPageTokens, setPreviousPageTokens] = useState<Array<string | null>>([]);
  const [isCatalogueLoading, setIsCatalogueLoading] = useState(true);
  const [isLoadingMore, setIsLoadingMore] = useState(false);
  const [refreshVersion, setRefreshVersion] = useState(0);
  const [query, setQuery] = useState("");
  const [quickFilter, setQuickFilter] = useState<QuickFilter>("all");
  const [view, setView] = useState<ViewMode>("grid");
  const [sortField, setSortField] = useState<SortField>("title");
  const [sortDirection, setSortDirection] = useState<SortDirection>("asc");
  const [cardSize, setCardSize] = useState<CardSize>(() => resolveInitialSize(variant));
  const [displayOptions, setDisplayOptions] = useState<DisplayOptions>(() => resolveInitialDisplayOptions(variant));
  const [savedPresets, setSavedPresets] = useState<SavedFilterPreset[]>([]);
  const [newPresetName, setNewPresetName] = useState("");
  const [isSavingPreset, setIsSavingPreset] = useState(false);

  function changeSize(size: CardSize) {
    setCardSize(size);
    try { localStorage.setItem(SIZE_STORAGE_KEY(variant), size); } catch { /* ignore */ }
  }
  function updateDisplayOptions(nextOptions: DisplayOptions) {
    setDisplayOptions(nextOptions);
    try { localStorage.setItem(DISPLAY_STORAGE_KEY(variant), JSON.stringify(nextOptions)); } catch { /* ignore */ }
  }
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [isBulkUpdating, setIsBulkUpdating] = useState(false);
  const [isBulkToolsOpen, setIsBulkToolsOpen] = useState(false);
  const [isRemovalConfirmationOpen, setIsRemovalConfirmationOpen] = useState(false);
  const [bulkOperation, setBulkOperation] = useState<BulkWorkflowOperation>("monitoring");
  const [bulkMonitored, setBulkMonitored] = useState(true);
  const [bulkQualityProfileId, setBulkQualityProfileId] = useState("");
  const [bulkTargetLibraryId, setBulkTargetLibraryId] = useState("");
  const [bulkTagsInput, setBulkTagsInput] = useState("");
  const [bulkRenameTemplate, setBulkRenameTemplate] = useState("");
  const [bulkRenamePreview, setBulkRenamePreview] = useState<BulkRenamePreviewItem[]>([]);
  const [bulkConfirming, setBulkConfirming] = useState(false);
  const [bulkError, setBulkError] = useState<string | null>(null);
  const [bulkLibraries, setBulkLibraries] = useState<LibraryItem[]>([]);
  const [bulkQualityProfiles, setBulkQualityProfiles] = useState<QualityProfileItem[]>([]);
  const [bulkOptionsLoading, setBulkOptionsLoading] = useState(false);
  const [undoStack, setUndoStack] = useState<BulkHistoryEntry[]>([]);
  const [redoStack, setRedoStack] = useState<BulkHistoryEntry[]>([]);
  const [showCreate, setShowCreate] = useState(() => searchParams.get("add") === "true");
  const [isCreating, setIsCreating] = useState(false);
  const [createForm, setCreateForm] = useState(() => createInitialForm());
  const [metadataResults, setMetadataResults] = useState<MetadataSearchResult[]>([]);
  const [selectedMetadataResults, setSelectedMetadataResults] = useState<MetadataSearchResult[]>([]);
  const [isSearchingMetadata, setIsSearchingMetadata] = useState(false);
  const metadataSearchSequence = useRef(0);

  useEffect(() => {
    let cancelled = false;
    const timer = window.setTimeout(async () => {
      setIsCatalogueLoading(true);
      const params = new URLSearchParams({ pageSize: "100", sort: sortField, direction: sortDirection });
      if (query.trim()) params.set("search", query.trim());
      if (quickFilter !== "all") params.set("status", quickFilter);
      try {
        if (variant === "movies") {
          const [page, wanted] = await Promise.all([
            fetchJson<CataloguePage<MovieListItem>>(`/api/movies/page?${params}`),
            fetchJson<MovieWantedSummary>("/api/movies/wanted")
          ]);
          if (cancelled) return;
          setLibraryItems(adaptMovieItems(page.items, wanted));
          setTotalCount(page.totalCount ?? 0);
          setFacets(page.facets);
          setNextPageToken(page.nextPageToken);
          setCurrentPageToken(null);
          setPreviousPageTokens([]);
        } else {
          const [page, wanted] = await Promise.all([
            fetchJson<CataloguePage<SeriesListItem>>(`/api/series/page?${params}`),
            fetchJson<SeriesWantedSummary>("/api/series/wanted")
          ]);
          if (cancelled) return;
          setLibraryItems(adaptSeriesItems(page.items, wanted));
          setTotalCount(page.totalCount ?? 0);
          setFacets(page.facets);
          setNextPageToken(page.nextPageToken);
          setCurrentPageToken(null);
          setPreviousPageTokens([]);
        }
        setSelectedIds([]);
      } catch {
        if (!cancelled) {
          setLibraryItems([]);
          setTotalCount(0);
          setFacets(null);
          setNextPageToken(null);
          toast.error("Could not load the library.");
        }
      } finally {
        if (!cancelled) setIsCatalogueLoading(false);
      }
    }, 250);
    return () => { cancelled = true; window.clearTimeout(timer); };
  }, [query, quickFilter, refreshVersion, sortDirection, sortField, variant]);

  async function loadNextCataloguePage() {
    if (!nextPageToken || isLoadingMore) return;
    setIsLoadingMore(true);
    const params = new URLSearchParams({ pageSize: "100", sort: sortField, direction: sortDirection, pageToken: nextPageToken });
    if (query.trim()) params.set("search", query.trim());
    if (quickFilter !== "all") params.set("status", quickFilter);
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
    const params = new URLSearchParams({ pageSize: "100", sort: sortField, direction: sortDirection });
    if (previousToken) params.set("pageToken", previousToken);
    if (query.trim()) params.set("search", query.trim());
    if (quickFilter !== "all") params.set("status", quickFilter);
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
    setCreateForm(createInitialForm());
  }, [variant]);

  useEffect(() => {
    if (searchParams.get("add") === "true") {
      setShowCreate(true);
    }
    const filter = searchParams.get("filter");
    if (isQuickFilter(filter)) {
      setQuickFilter(filter);
    }
  }, [searchParams]);

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
    setSavedPresets([]);
    setQuickFilter("all");
    setSortField("title");
    setSortDirection("asc");
    setDisplayOptions(resolveInitialDisplayOptions(variant));
  }, [variant]);

  useEffect(() => {
    if (!isBulkToolsOpen) {
      return;
    }

    let cancelled = false;
    setBulkOptionsLoading(true);
    setBulkError(null);

    Promise.all([
      fetchJson<LibraryItem[]>("/api/libraries"),
      fetchJson<QualityProfileItem[]>("/api/quality-profiles")
    ])
      .then(([libraries, profiles]) => {
        if (cancelled) {
          return;
        }

        const mediaType = variant === "movies" ? "movies" : "tv";
        const filteredLibraries = libraries.filter((item) =>
          item.mediaType.toLowerCase() === mediaType
        );
        const filteredProfiles = profiles.filter((item) =>
          item.mediaType.toLowerCase() === mediaType
        );

        setBulkLibraries(filteredLibraries);
        setBulkQualityProfiles(filteredProfiles);
        setBulkTargetLibraryId((current) =>
          current || filteredLibraries[0]?.id || ""
        );
      })
      .catch((error) => {
        if (!cancelled) {
          const message = error instanceof Error ? error.message : "Could not load bulk operation options.";
          setBulkError(message);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setBulkOptionsLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [isBulkToolsOpen, variant]);

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

  /* Escape clears selection */
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape" && selectedIds.length > 0) {
        e.preventDefault();
        setSelectedIds([]);
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [selectedIds.length]);

  // The catalogue endpoint owns filtering and ordering. A browser-side pass
  // here would quietly make the answer incomplete after the first page.
  const filtered = libraryItems;

  const selectedCount = selectedIds.length;
  const monitoredCount = facets?.monitored ?? 0;
  const missingCount = facets?.missing ?? 0;
  const downloadingCount = 0;
  const downloadedCount = facets?.downloaded ?? 0;
  const totalSizeTb = (
    libraryItems.reduce((sum, item) => sum + (item.sizeGb ?? 0), 0) / 1024
  ).toFixed(1);

  const label = variant === "movies" ? "movies" : "TV shows";
  const singular = variant === "movies" ? "movie" : "TV show";
  const selectedMetadataCount = selectedMetadataResults.length;

  async function applyMonitoring(ids: string[], monitored: boolean) {
    const response = await authedFetch(
      variant === "movies" ? "/api/movies/monitoring" : "/api/series/monitoring",
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          variant === "movies"
            ? { movieIds: ids, monitored }
            : { seriesIds: ids, monitored }
        )
      }
    );

    if (!response.ok) {
      throw new Error("bulk-monitoring-failed");
    }
  }

  async function applyQualityProfile(ids: string[], qualityProfileId: string) {
    const endpoint = variant === "movies"
      ? "/api/movies/bulk/quality-profile"
      : "/api/series/bulk/quality-profile";

    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? { movieIds: ids, qualityProfileId }
          : { seriesIds: ids, qualityProfileId }
      )
    });

    if (!response.ok) {
      throw new Error("bulk-quality-failed");
    }
  }

  async function applyTags(ids: string[], tags: string[]) {
    const endpoint = variant === "movies"
      ? "/api/movies/bulk/tags"
      : "/api/series/bulk/tags";

    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? { movieIds: ids, tags: tags.join(", ") }
          : { seriesIds: ids, tags: tags.join(", ") }
      )
    });

    if (!response.ok) {
      throw new Error("bulk-tags-failed");
    }
  }

  async function applyReassignLibrary(ids: string[], fromLibraryId: string, toLibraryId: string) {
    const endpoint = variant === "movies"
      ? "/api/movies/bulk/reassign-library"
      : "/api/series/bulk/reassign-library";

    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(
        variant === "movies"
          ? { movieIds: ids, fromLibraryId, toLibraryId }
          : { seriesIds: ids, fromLibraryId, toLibraryId }
      )
    });

    if (!response.ok) {
      throw new Error("bulk-reassign-failed");
    }
  }

  async function applySearchNow(ids: string[]) {
    const endpoint = variant === "movies" ? "/api/movies/bulk/search" : "/api/series/bulk/search";
    const response = await authedFetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(variant === "movies" ? { movieIds: ids } : { seriesIds: ids })
    });

    if (!response.ok) {
      throw new Error("bulk-search-failed");
    }
  }

  async function loadQualityProfileByIdMap(ids: string[]): Promise<Map<string, string | null>> {
    const endpointRoot = variant === "movies" ? "/api/movies" : "/api/series";
    const entries = await Promise.all(
      ids.map(async (id) => {
        const response = await authedFetch(`${endpointRoot}/${id}`);
        if (!response.ok) {
          return [id, null] as const;
        }

        const detail = await response.json() as { qualityProfileId?: string | null };
        return [id, detail.qualityProfileId ?? null] as const;
      })
    );

    return new Map(entries);
  }

  function normalizeBulkTags(rawTags: string): string[] {
    return rawTags
      .split(/[,\n;]/g)
      .map((tag) => tag.trim())
      .filter((tag) => tag.length > 0)
      .filter((tag, index, arr) =>
        arr.findIndex((candidate) => candidate.toLowerCase() === tag.toLowerCase()) === index
      );
  }

  function pushHistory(entry: BulkHistoryEntry) {
    setUndoStack((current) => [...current, entry]);
    setRedoStack([]);
  }

  async function handleBulkMonitoring(monitored: boolean) {
    if (!selectedIds.length) return;
    setIsBulkUpdating(true);
    try {
      const response = await authedFetch(
        variant === "movies" ? "/api/movies/monitoring" : "/api/series/monitoring",
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(
            variant === "movies"
              ? { movieIds: selectedIds, monitored }
              : { seriesIds: selectedIds, monitored }
          )
        }
      );
      if (!response.ok) throw new Error("bulk-monitoring-failed");
      setLibraryItems((current) =>
        current.map((item) =>
          selectedIds.includes(item.id)
            ? {
                ...item,
                // Monitoring is a policy choice; availability is a separate fact.
                // Never turn an un-downloaded title into "downloaded" just because
                // someone starts monitoring it (or vice versa).
                monitored
              }
            : item
        )
      );
      toast.success(
        monitored
          ? `${selectedIds.length} title${selectedIds.length === 1 ? "" : "s"} now monitored`
          : `${selectedIds.length} title${selectedIds.length === 1 ? "" : "s"} unmonitored`
      );
      setSelectedIds([]);
    } catch {
      toast.error("Bulk update failed", {
        description: "Something went wrong while reaching the Deluno API."
      });
    } finally {
      setIsBulkUpdating(false);
    }
  }

  async function handleBulkSearchNow() {
    const selectedItems = filtered.filter((item) => selectedIds.includes(item.id));
    if (!selectedItems.length) {
      toast.info("No titles selected for search.");
      return;
    }
    setIsBulkUpdating(true);
    const loadingId = toast.loading(
      `Searching ${selectedItems.length} title${selectedItems.length === 1 ? "" : "s"}…`
    );
    try {
      await applySearchNow(selectedIds);
      toast.success(
        `Manual search dispatched for ${selectedItems.length} title${selectedItems.length === 1 ? "" : "s"}`,
        { id: loadingId }
      );
      setSelectedIds([]);
    } catch {
      toast.error("Bulk search could not be completed.", { id: loadingId });
    } finally {
      setIsBulkUpdating(false);
    }
  }

  async function runUndo() {
    const entry = undoStack[undoStack.length - 1];
    if (!entry || isBulkUpdating) {
      return;
    }

    setIsBulkUpdating(true);
    try {
      await entry.undo();
      setUndoStack((current) => current.slice(0, -1));
      setRedoStack((current) => [...current, entry]);
      toast.success(entry.undoLabel);
    } catch {
      toast.error("Undo failed.");
    } finally {
      setIsBulkUpdating(false);
    }
  }

  async function runRedo() {
    const entry = redoStack[redoStack.length - 1];
    if (!entry || isBulkUpdating) {
      return;
    }

    setIsBulkUpdating(true);
    try {
      await entry.redo();
      setRedoStack((current) => current.slice(0, -1));
      setUndoStack((current) => [...current, entry]);
      toast.success(entry.redoLabel);
    } catch {
      toast.error("Redo failed.");
    } finally {
      setIsBulkUpdating(false);
    }
  }

  function openBulkTools(operation: BulkWorkflowOperation = "monitoring", monitored = true) {
    if (selectedIds.length === 0) {
      toast.info("Select at least one title first.");
      return;
    }

    setBulkOperation(operation);
    setBulkMonitored(monitored);
    setBulkConfirming(false);
    setBulkError(null);
    setBulkRenamePreview([]);
    setIsBulkToolsOpen(true);
  }

  async function executeBulkToolsOperation() {
    if (selectedIds.length === 0) {
      setBulkError("Select at least one title.");
      return;
    }

    if (!bulkConfirming && bulkOperation !== "renamePreview") {
      setBulkConfirming(true);
      return;
    }

    setBulkError(null);
    const selectedItems = libraryItems.filter((item) => selectedIds.includes(item.id));

    try {
      setIsBulkUpdating(true);
      if (bulkOperation === "monitoring") {
        const previousTrue = selectedItems.filter((item) => item.monitored).map((item) => item.id);
        const previousFalse = selectedItems.filter((item) => !item.monitored).map((item) => item.id);
        const label = bulkMonitored ? "Monitor selected titles" : "Unmonitor selected titles";

        await applyMonitoring(selectedIds, bulkMonitored);
        setLibraryItems((current) =>
          current.map((item) => selectedIds.includes(item.id) ? { ...item, monitored: bulkMonitored } : item)
        );
        pushHistory({
          label,
          undoLabel: "Monitoring change reverted",
          redoLabel: "Monitoring change re-applied",
          undo: async () => {
            if (previousTrue.length > 0) {
              await applyMonitoring(previousTrue, true);
            }
            if (previousFalse.length > 0) {
              await applyMonitoring(previousFalse, false);
            }
            setLibraryItems((current) =>
              current.map((item) =>
                previousTrue.includes(item.id) ? { ...item, monitored: true }
                : previousFalse.includes(item.id) ? { ...item, monitored: false }
                : item
              )
            );
          },
          redo: async () => {
            await applyMonitoring(selectedIds, bulkMonitored);
            setLibraryItems((current) =>
              current.map((item) => selectedIds.includes(item.id) ? { ...item, monitored: bulkMonitored } : item)
            );
          }
        });
        toast.success(label);
      }
      else if (bulkOperation === "quality") {
        if (!bulkQualityProfileId.trim()) {
          setBulkError("Choose a quality profile first.");
          return;
        }

        const previousProfiles = await loadQualityProfileByIdMap(selectedIds);
        await applyQualityProfile(selectedIds, bulkQualityProfileId.trim());
        const profileName = bulkQualityProfiles.find((item) => item.id === bulkQualityProfileId)?.name ?? bulkQualityProfileId;
        setLibraryItems((current) =>
          current.map((item) =>
            selectedIds.includes(item.id) ? { ...item, qualityProfile: profileName } : item
          )
        );

        pushHistory({
          label: "Apply quality profile",
          undoLabel: "Quality profile change reverted",
          redoLabel: "Quality profile change re-applied",
          undo: async () => {
            const groups = new Map<string, string[]>();
            previousProfiles.forEach((value, id) => {
              if (!value) return;
              const current = groups.get(value) ?? [];
              current.push(id);
              groups.set(value, current);
            });
            for (const [profileId, ids] of groups) {
              await applyQualityProfile(ids, profileId);
            }
          },
          redo: async () => {
            await applyQualityProfile(selectedIds, bulkQualityProfileId.trim());
          }
        });
        toast.success("Quality profile updated.");
      }
      else if (bulkOperation === "reassignLibrary") {
        if (!bulkTargetLibraryId.trim()) {
          setBulkError("Choose a destination library first.");
          return;
        }

        const previousByLibrary = new Map<string, string[]>();
        selectedItems.forEach((item) => {
          if (!item.libraryId) return;
          const current = previousByLibrary.get(item.libraryId) ?? [];
          current.push(item.id);
          previousByLibrary.set(item.libraryId, current);
        });

        for (const [fromLibraryId, ids] of previousByLibrary) {
          if (fromLibraryId === bulkTargetLibraryId) {
            continue;
          }
          await applyReassignLibrary(ids, fromLibraryId, bulkTargetLibraryId);
        }

        setLibraryItems((current) =>
          current.map((item) =>
            selectedIds.includes(item.id) ? { ...item, libraryId: bulkTargetLibraryId } : item
          )
        );

        pushHistory({
          label: "Reassign library",
          undoLabel: "Library reassignment reverted",
          redoLabel: "Library reassignment re-applied",
          undo: async () => {
            for (const [oldLibraryId, ids] of previousByLibrary) {
              await applyReassignLibrary(ids, bulkTargetLibraryId, oldLibraryId);
            }
            setLibraryItems((current) =>
              current.map((item) => {
                for (const [oldLibraryId, ids] of previousByLibrary) {
                  if (ids.includes(item.id)) {
                    return { ...item, libraryId: oldLibraryId };
                  }
                }
                return item;
              })
            );
          },
          redo: async () => {
            for (const [fromLibraryId, ids] of previousByLibrary) {
              await applyReassignLibrary(ids, fromLibraryId, bulkTargetLibraryId);
            }
            setLibraryItems((current) =>
              current.map((item) => selectedIds.includes(item.id) ? { ...item, libraryId: bulkTargetLibraryId } : item)
            );
          }
        });
        toast.success("Library assignment updated.");
      }
      else if (bulkOperation === "tags") {
        const normalizedTags = normalizeBulkTags(bulkTagsInput);
        const previousTags = new Map(selectedItems.map((item) => [item.id, item.tags ?? []] as const));
        await applyTags(selectedIds, normalizedTags);
        setLibraryItems((current) =>
          current.map((item) =>
            selectedIds.includes(item.id) ? { ...item, tags: normalizedTags } : item
          )
        );
        pushHistory({
          label: "Apply tags",
          undoLabel: "Tags reverted",
          redoLabel: "Tags re-applied",
          undo: async () => {
            const groups = new Map<string, string[]>();
            previousTags.forEach((tags, id) => {
              const key = tags.join("||");
              const current = groups.get(key) ?? [];
              current.push(id);
              groups.set(key, current);
            });
            for (const [key, ids] of groups) {
              const tags = key ? key.split("||").filter(Boolean) : [];
              await applyTags(ids, tags);
            }
            setLibraryItems((current) =>
              current.map((item) =>
                selectedIds.includes(item.id) ? { ...item, tags: previousTags.get(item.id) ?? [] } : item
              )
            );
          },
          redo: async () => {
            await applyTags(selectedIds, normalizedTags);
            setLibraryItems((current) =>
              current.map((item) => selectedIds.includes(item.id) ? { ...item, tags: normalizedTags } : item)
            );
          }
        });
        toast.success("Tags updated.");
      }
      else if (bulkOperation === "search") {
        await applySearchNow(selectedIds);
        toast.success("Manual search dispatched.");
      }
      else if (bulkOperation === "renamePreview") {
        const endpoint = variant === "movies" ? "/api/movies/bulk/rename-preview" : "/api/series/bulk/rename-preview";
        const response = await authedFetch(endpoint, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(
            variant === "movies"
              ? { movieIds: selectedIds, template: bulkRenameTemplate.trim() || undefined }
              : { seriesIds: selectedIds, template: bulkRenameTemplate.trim() || undefined }
          )
        });

        if (!response.ok) {
          throw new Error("rename-preview-failed");
        }

        const payload = await response.json() as { previews?: Array<Record<string, unknown>> };
        const preview = (payload.previews ?? []).map((item) => ({
          itemId: String(item.movieId ?? item.seriesId ?? ""),
          title: String(item.title ?? ""),
          year: item.releaseYear === null || item.startYear === null
            ? null
            : Number(item.releaseYear ?? item.startYear ?? 0),
          template: String(item.template ?? ""),
          proposedName: String(item.proposedName ?? "")
        }));
        setBulkRenamePreview(preview);
        return;
      }

      setBulkConfirming(false);
      setIsBulkToolsOpen(false);
      setSelectedIds([]);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Bulk operation failed.";
      setBulkError(message);
      toast.error("Bulk operation failed", { description: message });
    } finally {
      setIsBulkUpdating(false);
    }
  }

  function toggleSelectedId(id: string) {
    setSelectedIds((current) =>
      current.includes(id) ? current.filter((entry) => entry !== id) : [...current, id]
    );
  }

  function toggleSelectAllVisible() {
    const visibleIds = filtered.map((item) => item.id);
    const allVisibleSelected = visibleIds.every((id) => selectedIds.includes(id));
    setSelectedIds(allVisibleSelected ? [] : visibleIds);
  }

  function openWorkspace(item: MediaItem) {
    navigate(item.type === "movie" ? `/movies/${item.id}` : `/tv/${item.id}`);
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

  const activeFilterCount = quickFilter !== "all" ? 1 : 0;

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
      onReload?.();
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
          setCreateForm(createInitialForm());
          setMetadataResults([]);
          setSelectedMetadataResults([]);
          closeCreate();
          onReload?.();
          return;
        }

        setSelectedMetadataResults((current) =>
          current.filter((_, index) => settled[index]?.status === "rejected")
        );
        toast.error(`${failureCount} ${failureCount === 1 ? "title" : "titles"} could not be added.`);
        if (successCount > 0) {
          onReload?.();
        }
        return;
      }

      await submitCreateDraft(createDraftFromCurrentForm());
      toast.success(variant === "movies" ? "Movie added" : "TV show added");
      setCreateForm(createInitialForm());
      setMetadataResults([]);
      setSelectedMetadataResults([]);
      closeCreate();
      onReload?.();
    } catch (error) {
      const msg = error instanceof Error ? error.message : "Create failed.";
      toast.error(msg);
    } finally {
      setIsCreating(false);
    }
  }

  return (
    <>
      <section className="space-y-[var(--grid-gap)]">
        <div className="relative overflow-hidden rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card dark:border-white/[0.06]">
          <span
            aria-hidden
            className="pointer-events-none absolute inset-x-5 top-0 h-px rounded-full"
            style={{ background: "linear-gradient(90deg, transparent, hsl(var(--primary)/0.45), hsl(var(--primary-2)/0.28), transparent)" }}
          />
          <span aria-hidden className="pointer-events-none absolute -right-20 -top-28 h-64 w-64 rounded-full bg-primary/10 blur-3xl" />
          <div className="relative flex flex-col gap-[var(--grid-gap)] lg:flex-row lg:items-center lg:justify-between">
            <div className="min-w-0">
              <h2 className="font-display text-[length:var(--type-title-md)] font-semibold tracking-tight text-foreground">
                Browse and manage your {label}
              </h2>
              <p className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-[length:var(--type-body-sm)] text-muted-foreground">
                <span><span className="tabular font-semibold text-foreground">{totalCount.toLocaleString()}</span> total</span>
                <span className="text-muted-foreground/45">·</span>
                <span><span className={cn("tabular font-semibold", librarySummaryTone("availability", downloadedCount))}>{downloadedCount}</span> downloaded</span>
                <span className="text-muted-foreground/45">·</span>
                <span><span className="tabular font-semibold text-muted-foreground">{monitoredCount}</span> monitored</span>
                {missingCount > 0 ? (
                  <>
                    <span className="text-muted-foreground/45">·</span>
                    <span><span className="tabular font-semibold text-warning">{missingCount}</span> missing</span>
                  </>
                ) : null}
                {downloadingCount > 0 ? (
                  <>
                    <span className="text-muted-foreground/45">·</span>
                    <span><span className="tabular font-semibold text-info">{downloadingCount}</span> downloading</span>
                  </>
                ) : null}
              </p>
            </div>
            <div className="flex shrink-0 flex-wrap items-center gap-2">
              <Button className="gap-2" onClick={() => showCreate ? closeCreate() : openCreate()}>
                <Plus className="h-4 w-4" strokeWidth={2.5} />
                Add {singular}
              </Button>
              {missingCount > 0 ? (
                <Button variant="secondary" className="gap-2">
                  <Zap className="h-4 w-4" />
                  Hunt {missingCount} missing
                </Button>
              ) : null}
            </div>
          </div>
        </div>
        {/* ═══════ CINEMATIC HERO ═══════ */}
        <div className="hidden">
        <PageHero
          eyebrow={variant === "movies" ? "Movie library" : "TV library"}
          eyebrowIcon={
            <Star className="h-3 w-3 text-primary" />
          }
          title={
            <>
              Browse and manage your{" "}
              <span className="bg-gradient-to-r from-primary via-primary to-[hsl(var(--primary-2))] bg-clip-text text-transparent">
                {label}
              </span>
            </>
          }
          subtitle={
            <>
              <span className="font-semibold text-foreground">{totalCount.toLocaleString()} total titles</span>
              {" · "}
              <span className={cn("font-semibold", librarySummaryTone("availability", downloadedCount))}>{downloadedCount} downloaded</span>
              {missingCount > 0 ? (
                <>
                  {" · "}
                  <span className="font-semibold text-warning">{missingCount} missing</span>
                </>
              ) : null}
              {downloadingCount > 0 ? (
                <>
                  {" · "}
                  <span className="font-semibold text-info">{downloadingCount} downloading</span>
                </>
              ) : null}
            </>
          }
          size="sm"
          stats={[
            { label: "Total", value: totalCount.toString(), tone: "neutral" },
            { label: "Monitored", value: monitoredCount.toString(), tone: "primary" },
            { label: "Missing", value: missingCount.toString(), tone: missingCount > 0 ? "warn" : "neutral" },
            { label: "Library", value: `${totalSizeTb}TB`, tone: "neutral" }
          ]}
          actions={
            <>
              <Button className="gap-2" onClick={() => showCreate ? closeCreate() : openCreate()}>
                <Plus className="h-4 w-4" strokeWidth={2.5} />
                Add {singular}
              </Button>
              {missingCount > 0 ? (
                <Button variant="secondary" className="gap-2">
                  <Zap className="h-4 w-4" />
                  Hunt {missingCount} missing
                </Button>
              ) : null}
            </>
          }
        />
        </div>

        <Dialog.Root open={showCreate} onOpenChange={(open) => (open ? openCreate() : closeCreate())}>
          <Dialog.Portal>
            <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[3px]" />
            <Dialog.Content className="fixed left-1/2 top-1/2 z-50 flex max-h-[min(88dvh,760px)] w-[calc(100%-2rem)] max-w-5xl -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl">
              <div className="flex items-start justify-between gap-[var(--grid-gap)] border-b border-hairline px-6 py-5">
                <div>
                  <div className="flex flex-wrap items-center gap-2">
                    <Dialog.Title className="font-display text-xl font-semibold tracking-tight text-foreground">Add {singular}</Dialog.Title>
                    <Badge variant={metadataStatus?.isConfigured ? "success" : "warning"}>
                      {metadataStatus?.isConfigured ? "Title matching ready" : "Manual entry"}
                    </Badge>
                  </div>
                  <Dialog.Description className="mt-1 text-sm text-muted-foreground">
                    Start typing, then pick matches to prefill details. Use Add at the bottom to create what you selected.
                  </Dialog.Description>
                </div>
                <Dialog.Close asChild>
                  <Button variant="ghost" size="icon" aria-label={`Close add ${singular}`} disabled={isCreating}>
                    <X className="h-4 w-4" />
                  </Button>
                </Dialog.Close>
              </div>

              <div className="min-h-0 flex-1 overflow-y-auto p-6">
                <form onSubmit={(event) => { event.preventDefault(); void handleMetadataSearch(); }}>
                  <label className="text-sm font-semibold text-foreground" htmlFor={`add-${variant}-title`}>What do you want to add?</label>
                  <div className="mt-2">
                    <Input
                      id={`add-${variant}-title`}
                      autoFocus
                      value={createForm.title}
                      onChange={(event) => {
                        metadataSearchSequence.current += 1;
                        setMetadataResults([]);
                        setSelectedMetadataResults([]);
                        setCreateForm((current) => ({ ...current, title: event.target.value, metadata: null }));
                      }}
                      placeholder={variant === "movies" ? "Search movies, for example Top Gun" : "Search TV shows, for example Severance"}
                    />
                  </div>
                  <p className="mt-2 text-xs text-muted-foreground">
                    <span className="inline-flex items-center gap-2">
                      {isSearchingMetadata ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
                      {metadataStatus?.isConfigured === false
                        ? "Metadata matching is currently unavailable."
                        : isSearchingMetadata
                          ? "Searching metadata..."
                          : "Matches auto-refresh as you type, or press Enter to refresh now."}
                    </span>
                  </p>
                </form>

                  {metadataStatus?.isConfigured === false ? (
                    <p className="mt-3 rounded-xl border border-warning/25 bg-warning/10 p-3 text-sm text-warning">
                      Title matching is temporarily unavailable. You can still add this title manually below.
                    </p>
                  ) : null}
                  {metadataResults.length > 0 ? (
                    <div className="mt-5">
                      <p className="text-sm font-semibold text-foreground">
                        Choose one or more matches to prefill (this does not add yet)
                      </p>
                      <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                    {metadataResults.slice(0, 6).map((result) => {
                      const isSelected = selectedMetadataResults.some((selected) => sameMetadataResult(selected, result));
                      return (
                            <button
                              key={`${result.provider}:${result.providerId}`}
                              type="button"
                              onClick={() => applyMetadataResult(result)}
                              className={cn(
                                "flex min-w-0 gap-3 rounded-xl border bg-surface-1 p-3 text-left transition hover:border-primary/45 hover:bg-primary/5",
                                isSelected ? "border-primary/70 bg-primary/10 ring-1 ring-primary/25" : "border-hairline"
                              )}
                              title={`Select ${result.title}`}
                        >
                              {result.posterUrl ? (
                                <img src={result.posterUrl} alt="" className="h-24 w-16 shrink-0 rounded-lg bg-muted object-cover" />
                              ) : (
                                <div className="flex h-24 w-16 shrink-0 items-center justify-center rounded-lg bg-muted text-[11px] text-muted-foreground">No art</div>
                              )}
                              <span className="min-w-0 self-center">
                                <span className="block truncate text-sm font-semibold text-foreground">{result.title}</span>
                                <span className="mt-1 block text-xs text-muted-foreground">{result.year ?? "Unknown year"} · TMDb</span>
                                {result.rating ? <span className="mt-2 block font-mono text-xs text-primary">{result.rating.toFixed(1)} rating</span> : null}
                                {isSelected ? (
                                  <span className="mt-1 inline-flex rounded-full bg-primary/15 px-2 py-0.5 text-[10px] font-semibold text-primary">Selected</span>
                                ) : (
                                  <span className="mt-1 block text-[10px] text-muted-foreground">Click to select</span>
                                )}
                              </span>
                            </button>
                          );
                        })}
                      </div>
                    </div>
                  ) : null}

                <details className="mt-5 rounded-xl border border-hairline bg-surface-1 px-4 py-3">
                  <summary className="cursor-pointer text-sm font-medium text-muted-foreground">Can’t find it? Add it manually</summary>
                  <div className="mt-3 grid gap-3 sm:grid-cols-2">
                    <Input
                      type="number"
                      value={createForm.year}
                      onChange={(event) => setCreateForm((current) => ({ ...current, year: event.target.value }))}
                      placeholder={variant === "movies" ? "Year (optional)" : "Start year (optional)"}
                    />
                    <Input
                      value={createForm.imdbId}
                      onChange={(event) => setCreateForm((current) => ({ ...current, imdbId: event.target.value }))}
                      placeholder="IMDb ID (optional)"
                    />
                  </div>
                </details>
              </div>

              <form onSubmit={handleCreate} className="flex flex-col gap-3 border-t border-hairline bg-surface-1/70 px-6 py-4 sm:flex-row sm:items-center sm:justify-between">
                <div className="inline-flex min-h-[1.25rem] items-center gap-2 text-sm text-muted-foreground">
                  <label className="inline-flex select-none items-center gap-2">
                    <input
                      type="checkbox"
                      checked={createForm.monitored}
                      onChange={(event) => setCreateForm((current) => ({ ...current, monitored: event.target.checked }))}
                      className="accent-primary"
                    />
                    Monitor and search automatically
                  </label>
                  {selectedMetadataCount > 0 ? (
                    <span className="inline-flex items-center gap-2 text-xs font-semibold text-primary">
                      <span className="h-1.5 w-1.5 rounded-full bg-primary" />
                      {selectedMetadataCount} {selectedMetadataCount === 1 ? singular : label} selected
                    </span>
                  ) : null}
                </div>
                <div className="flex gap-2">
                  <Dialog.Close asChild>
                    <Button type="button" variant="ghost" disabled={isCreating}>Cancel</Button>
                  </Dialog.Close>
                  <Button type="submit" disabled={isCreating || (!createForm.title.trim() && selectedMetadataCount === 0)} className="gap-2">
                    {isCreating ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />}
                    {selectedMetadataCount > 0
                      ? selectedMetadataCount === 1
                        ? `Add selected ${singular}`
                        : `Add ${selectedMetadataCount} ${label}`
                      : `Add ${singular} manually`}
                  </Button>
                </div>
              </form>
            </Dialog.Content>
          </Dialog.Portal>
        </Dialog.Root>

        {/* ═══════ CONTROL RAIL ═══════ */}
        <ControlRail
          label={label}
          query={query}
          setQuery={setQuery}
          quickFilter={quickFilter}
          setQuickFilter={setQuickFilter}
          sortField={sortField}
          setSortField={setSortField}
          sortDirection={sortDirection}
          setSortDirection={setSortDirection}
          view={view}
          setView={setView}
          cardSize={cardSize}
          changeSize={changeSize}
          displayOptions={displayOptions}
          setDisplayOptions={updateDisplayOptions}
          facets={facets}
          savedPresets={savedPresets}
          newPresetName={newPresetName}
          setNewPresetName={setNewPresetName}
          isSavingPreset={isSavingPreset}
          saveCurrentPreset={saveCurrentPreset}
          applyPreset={applyPreset}
          deletePreset={deletePreset}
          activeFilterCount={activeFilterCount}
        />

        {/* Results only occupy space when there is something to report. */}
        {totalCount > libraryItems.length ? (
          <div className="flex items-center justify-between gap-3">
            {totalCount > libraryItems.length ? (
              <p className="text-[length:var(--library-toolbar-size)] font-medium text-muted-foreground">
                Showing <span className="font-bold tabular text-foreground">{filtered.length}</span> loaded of {totalCount.toLocaleString()}
              </p>
          ) : (
            <span />
          )}

          {/* Right — premium select-all toggle */}
          <button
            type="button"
            onClick={toggleSelectAllVisible}
            className={cn(
              "group flex min-h-[var(--library-toolbar-height)] items-center gap-2 rounded-xl px-3 py-1.5 text-[length:var(--library-toolbar-size)] font-medium transition-all duration-200 select-none",
              selectedCount > 0
                ? "bg-primary/10 text-primary ring-1 ring-inset ring-primary/20 hover:bg-primary/15"
                : "text-muted-foreground hover:bg-muted/60 hover:text-foreground dark:hover:bg-white/[0.05]"
            )}
          >
            {/* Custom checkbox */}
            <span className={cn(
              "flex h-4 w-4 shrink-0 items-center justify-center rounded-[4px] border transition-all duration-200",
              filtered.length > 0 && filtered.every((i) => selectedIds.includes(i.id))
                ? "border-primary bg-primary text-primary-foreground shadow-[0_0_8px_hsl(var(--primary)/0.5)]"
                : selectedCount > 0
                  ? "border-primary/60 bg-primary/15"
                  : "border-hairline bg-background group-hover:border-primary/40 dark:bg-white/[0.04]"
            )}>
              {filtered.length > 0 && filtered.every((i) => selectedIds.includes(i.id)) ? (
                <svg width="9" height="7" viewBox="0 0 9 7" fill="none">
                  <path d="M1 3.5L3.5 6L8 1" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round"/>
                </svg>
              ) : selectedCount > 0 ? (
                /* Indeterminate dash */
                <span className="h-0.5 w-2 rounded-full bg-primary" />
              ) : null}
            </span>
            {selectedCount > 0 ? `${selectedCount} selected` : "Select all"}
          </button>
        </div>
        ) : null}

        {/* Action messages now surface through the global Toaster */}

        {/* ═══════ FLOATING SELECTION COMMAND BAR ═══════ */}
        {selectedCount > 0 ? (
          <div
            className={cn(
              "fixed z-50 mx-auto",
              "bottom-[calc(var(--mobile-tabbar-height)+16px)] md:bottom-8",
              "left-1/2 -translate-x-1/2",
              "animate-fade-in"
            )}
          >
            <div className={cn(
              "flex items-center overflow-hidden rounded-2xl",
              "border border-white/[0.1] dark:border-white/[0.08]",
              "bg-[hsl(226_24%_10%/0.97)] dark:bg-[hsl(226_24%_8%/0.98)]",
              "shadow-[0_24px_60px_hsl(0_0%_0%/0.45),0_8px_20px_hsl(0_0%_0%/0.3),inset_0_1px_0_hsl(0_0%_100%/0.06)]",
              "backdrop-blur-2xl"
            )}>
              <div className="flex items-center gap-2.5 border-r border-white/[0.07] px-4 py-3">
                <span className={cn(
                  "flex h-6 min-w-6 items-center justify-center rounded-full px-2",
                  "bg-gradient-to-br from-primary to-[hsl(var(--primary-2))]",
                  "text-[length:var(--library-badge-size)] font-bold text-primary-foreground",
                  "shadow-[0_2px_8px_hsl(var(--primary-deep)/0.5),inset_0_1px_0_hsl(0_0%_100%/0.2)]"
                )}>
                  {selectedCount}
                </span>
                <span className="whitespace-nowrap text-[length:var(--library-toolbar-size)] font-medium text-[hsl(var(--media-muted-foreground))]">
                  {selectedCount === 1 ? "item" : "items"} selected
                </span>
              </div>

              <div className="flex items-center gap-0.5 px-1.5 py-1.5">
                <BulkAction
                  label="Undo"
                  icon={<Undo2 className="h-3.5 w-3.5" />}
                  onClick={() => void runUndo()}
                  disabled={isBulkUpdating || undoStack.length === 0}
                />
                <BulkAction
                  label="Redo"
                  icon={<Redo2 className="h-3.5 w-3.5" />}
                  onClick={() => void runRedo()}
                  disabled={isBulkUpdating || redoStack.length === 0}
                />
                <BulkAction
                  label="Monitor"
                  icon={<Eye className="h-3.5 w-3.5" />}
                  onClick={() => openBulkTools("monitoring", true)}
                  disabled={isBulkUpdating}
                  loading={isBulkUpdating}
                  variant="primary"
                />
                <BulkAction
                  label="Search now"
                  icon={<Zap className="h-3.5 w-3.5" />}
                  onClick={() => openBulkTools("search")}
                  disabled={isBulkUpdating}
                />
                <BulkAction
                  label="Unmonitor"
                  icon={<CircleOff className="h-3.5 w-3.5" />}
                  onClick={() => openBulkTools("monitoring", false)}
                  disabled={isBulkUpdating}
                />
                <BulkAction
                  label="Remove"
                  icon={<Trash2 className="h-3.5 w-3.5" />}
                  onClick={() => setIsRemovalConfirmationOpen(true)}
                  disabled={isBulkUpdating}
                  variant="danger"
                />
                <BulkAction
                  label="Bulk tools"
                  icon={<FolderTree className="h-3.5 w-3.5" />}
                  onClick={() => openBulkTools("quality")}
                  disabled={isBulkUpdating}
                />
              </div>

              <div className="border-l border-white/[0.07] px-1.5 py-1.5">
                <button
                  type="button"
                  onClick={() => setSelectedIds([])}
                  className="flex min-h-[var(--library-toolbar-height)] items-center gap-1.5 rounded-xl px-3 text-[length:var(--library-toolbar-size)] font-medium text-[hsl(var(--media-muted-foreground)/0.65)] transition hover:bg-white/[0.06] hover:text-[hsl(var(--media-foreground))]"
                  aria-label="Clear selection"
                >
                  Clear
                  <kbd className="rounded border border-white/10 bg-white/[0.05] px-1 font-mono text-[length:var(--library-badge-size)] text-[hsl(var(--media-muted-foreground)/0.5)]">Esc</kbd>
                </button>
              </div>
            </div>
          </div>
        ) : null}

        {/* ═══════ POSTER GRID or LIST ═══════ */}
        {(isRouteLoading || navigation.state !== "idle") && libraryItems.length === 0 ? (
          <GlassTile className="p-[var(--tile-pad)]">
            <LibraryGridSkeleton count={20} />
          </GlassTile>
        ) : filtered.length === 0 ? (
          libraryItems.length === 0 ? (
            <EmptyState
              variant="library"
              title={`Your ${label} library is empty`}
              description={`Add your first ${singular} to start monitoring releases, running search, and building out your collection.`}
              action={
                <Button onClick={openCreate} className="gap-1.5">
                  <Plus className="h-4 w-4" strokeWidth={2.5} />
                  Add {singular}
                </Button>
              }
              learnMore={`Deluno will track up to 100,000 ${label} without breaking a sweat.`}
            />
          ) : (
            <EmptyState
              variant="search"
              title="Nothing matches"
              description={`Try clearing filters or broadening your search. Your library has ${libraryItems.length} total title${libraryItems.length === 1 ? "" : "s"}.`}
              action={
                <Button
                  variant="secondary"
                  onClick={() => {
                    setQuickFilter("all");
                    setQuery("");
                  }}
                >
                  Clear filters
                </Button>
              }
            />
          )
        ) : view === "grid" ? (
            <ProgressiveGrid
              items={filtered}
              cardSize={cardSize}
              density={density}
              displayOptions={displayOptions}
              selectedIds={selectedIds}
              keyBust={`${cardSize}-${quickFilter}-${query}-${sortField}-${sortDirection}-${displayOptions.showMeta}-${displayOptions.showStatusPill}-${displayOptions.showQualityBadge}-${displayOptions.showRating}`}
              onSelect={openWorkspace}
              onToggle={toggleSelectedId}
              onEndReached={() => undefined}
            />
        ) : (
          <GlassTile className="p-0">
            <LibraryTable
              items={filtered}
              selectedIds={selectedIds}
              onSelect={openWorkspace}
              onToggle={toggleSelectedId}
              onToggleAll={toggleSelectAllVisible}
              allSelected={filtered.length > 0 && filtered.every((item) => selectedIds.includes(item.id))}
              someSelected={selectedCount > 0 && !filtered.every((item) => selectedIds.includes(item.id))}
              onEndReached={() => undefined}
            />
          </GlassTile>
        )}
        {libraryItems.length > 0 && (previousPageTokens.length > 0 || nextPageToken) ? (
          <div className="flex items-center justify-between gap-3 border-t border-hairline pt-3">
            <Button
              type="button"
              variant="outline"
              disabled={previousPageTokens.length === 0 || isLoadingMore}
              onClick={() => void loadPreviousCataloguePage()}
            >
              Previous 100
            </Button>
            <p className="text-sm text-muted-foreground">Only this page is kept in memory.</p>
            <Button
              type="button"
              variant="outline"
              disabled={!nextPageToken || isLoadingMore}
              onClick={() => void loadNextCataloguePage()}
            >
              Next 100
            </Button>
          </div>
        ) : null}
      </section>

      {isBulkToolsOpen ? (
        <div className="fixed inset-0 z-[70] flex items-center justify-center bg-black/60 px-4 py-6 backdrop-blur-sm">
          <div className="w-full max-w-2xl space-y-[var(--page-gap)] rounded-2xl border border-hairline bg-card p-5 shadow-2xl">
            <div className="flex items-start justify-between gap-3">
              <div>
                <p className="text-[length:var(--type-caption)] font-semibold uppercase tracking-[0.16em] text-muted-foreground">
                  Bulk workflow
                </p>
                <h3 className="font-display text-xl font-semibold text-foreground">
                  {selectedIds.length} title{selectedIds.length === 1 ? "" : "s"} selected
                </h3>
              </div>
              <Button
                type="button"
                variant="ghost"
                onClick={() => {
                  setIsBulkToolsOpen(false);
                  setBulkConfirming(false);
                  setBulkError(null);
                  setBulkRenamePreview([]);
                }}
                disabled={isBulkUpdating}
              >
                Close
              </Button>
            </div>

            <div className="grid gap-[var(--grid-gap)] md:grid-cols-2">
              <BulkField label="Operation" description="Choose the bulk action to run.">
                <select
                  value={bulkOperation}
                  onChange={(event) => {
                    setBulkOperation(event.target.value as BulkWorkflowOperation);
                    setBulkConfirming(false);
                    setBulkError(null);
                    setBulkRenamePreview([]);
                  }}
                  className="density-control-text h-[var(--control-height)] w-full rounded-[10px] border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                >
                  <option value="monitoring">Monitor or unmonitor</option>
                  <option value="quality">Set quality profile</option>
                  <option value="reassignLibrary">Assign library/root</option>
                  <option value="tags">Apply tags</option>
                  <option value="search">Search now</option>
                  <option value="renamePreview">Rename preview</option>
                </select>
              </BulkField>

              {bulkOperation === "monitoring" ? (
                <BulkField label="Monitoring state" description="Apply monitored or unmonitored to the selection.">
                  <select
                    value={bulkMonitored ? "true" : "false"}
                    onChange={(event) => setBulkMonitored(event.target.value === "true")}
                    className="density-control-text h-[var(--control-height)] w-full rounded-[10px] border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="true">Monitored</option>
                    <option value="false">Unmonitored</option>
                  </select>
                </BulkField>
              ) : null}

              {bulkOperation === "quality" ? (
                <BulkField label="Quality profile" description="Set one quality profile for all selected titles.">
                  <select
                    value={bulkQualityProfileId}
                    onChange={(event) => setBulkQualityProfileId(event.target.value)}
                    className="density-control-text h-[var(--control-height)] w-full rounded-[10px] border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="">Choose profile</option>
                    {bulkQualityProfiles.map((item) => (
                      <option key={item.id} value={item.id}>{item.name}</option>
                    ))}
                  </select>
                </BulkField>
              ) : null}

              {bulkOperation === "reassignLibrary" ? (
                <BulkField label="Destination library" description="Reassign selected titles to a different library/root.">
                  <select
                    value={bulkTargetLibraryId}
                    onChange={(event) => setBulkTargetLibraryId(event.target.value)}
                    className="density-control-text h-[var(--control-height)] w-full rounded-[10px] border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    <option value="">Choose library</option>
                    {bulkLibraries.map((item) => (
                      <option key={item.id} value={item.id}>{item.name}</option>
                    ))}
                  </select>
                </BulkField>
              ) : null}

              {bulkOperation === "tags" ? (
                <BulkField label="Tags" description="Comma-separated tags to apply to all selected titles.">
                  <Input
                    value={bulkTagsInput}
                    onChange={(event) => setBulkTagsInput(event.target.value)}
                    placeholder="e.g. favorites, weekend, 4k"
                  />
                </BulkField>
              ) : null}

              {bulkOperation === "renamePreview" ? (
                <BulkField label="Template (optional)" description="Preview generated folder names before rename workflows.">
                  <Input
                    value={bulkRenameTemplate}
                    onChange={(event) => setBulkRenameTemplate(event.target.value)}
                    placeholder={variant === "movies" ? "{Movie Title} ({Release Year})" : "{Series Title} ({Series Year})"}
                  />
                </BulkField>
              ) : null}
            </div>

            {bulkConfirming && bulkOperation !== "renamePreview" ? (
              <div className="rounded-xl border border-amber-400/40 bg-amber-500/10 px-4 py-3 text-sm text-amber-100">
                Confirming will run this operation across {selectedIds.length} selected title{selectedIds.length === 1 ? "" : "s"}.
              </div>
            ) : null}

            {bulkError ? (
              <div className="rounded-xl border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
                {bulkError}
              </div>
            ) : null}

            {bulkOperation === "renamePreview" && bulkRenamePreview.length > 0 ? (
              <div className="max-h-72 overflow-auto rounded-xl border border-hairline bg-surface-1">
                <table className="min-w-full text-sm">
                  <thead className="sticky top-0 bg-surface-2 text-left">
                    <tr>
                      <th className="px-3 py-2">Title</th>
                      <th className="px-3 py-2">Proposed name</th>
                    </tr>
                  </thead>
                  <tbody>
                    {bulkRenamePreview.map((item) => (
                      <tr key={item.itemId} className="border-t border-hairline/70">
                        <td className="px-3 py-2 text-foreground">{item.title}</td>
                        <td className="px-3 py-2 font-mono text-xs text-muted-foreground">{item.proposedName}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : null}

            <div className="flex items-center justify-between gap-3">
              <div className="text-xs text-muted-foreground">
                {bulkOptionsLoading ? "Loading options..." : `Undo stack: ${undoStack.length} · Redo stack: ${redoStack.length}`}
              </div>
              <div className="flex items-center gap-2">
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => {
                    setIsBulkToolsOpen(false);
                    setBulkConfirming(false);
                    setBulkError(null);
                    setBulkRenamePreview([]);
                  }}
                  disabled={isBulkUpdating}
                >
                  Cancel
                </Button>
                <Button
                  type="button"
                  onClick={() => void executeBulkToolsOperation()}
                  disabled={isBulkUpdating || bulkOptionsLoading}
                >
                  {isBulkUpdating
                    ? "Running..."
                    : bulkOperation === "renamePreview"
                      ? "Run preview"
                      : bulkConfirming
                        ? "Confirm and run"
                        : "Review and continue"}
                </Button>
              </div>
            </div>
          </div>
        </div>
      ) : null}

      <ConfirmDialog
        open={isRemovalConfirmationOpen}
        onOpenChange={setIsRemovalConfirmationOpen}
        title={`Remove ${selectedIds.length} ${singular}${selectedIds.length === 1 ? "" : "s"} from Deluno?`}
        description="This removes the selected catalog record and stops Deluno managing it. It does not delete imported media files or remove anything from your download client."
        confirmLabel="Remove from Deluno"
        busy={isBulkUpdating}
        onConfirm={() => void handleRemoveFromDeluno()}
      />

    </>
  );
}

/* ═══════════════ PRIMITIVES ═══════════════ */

/**
 * Poster grid with progressive hydration. Renders an initial batch of
 * cards synchronously and then reveals subsequent batches as an
 * intersection sentinel scrolls into view. Keeps first paint cheap
 * when a library has 10k+ titles while still feeling instantaneous.
 */
function ProgressiveGrid({
  items,
  cardSize,
  density,
  displayOptions,
  selectedIds,
  keyBust,
  onSelect,
  onToggle,
  onEndReached
}: {
  items: MediaItem[];
  cardSize: CardSize;
  density: Density;
  displayOptions: DisplayOptions;
  selectedIds: string[];
  keyBust: string;
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onEndReached: () => void;
}) {
  const parentRef = useRef<HTMLDivElement | null>(null);
  const [columns, setColumns] = useState(4);
  const gridMin = GRID_MIN_BY_DENSITY[density][cardSize];

  useLayoutEffect(() => {
    const container = parentRef.current;
    if (!container) return;

    const updateColumns = () => {
      // Resolve the density-aware CSS values in the same element that owns the
      // grid. `getComputedStyle` exposes custom properties as expressions, so a
      // hidden probe is the reliable way to obtain their computed pixel values.
      const probe = document.createElement("div");
      probe.style.cssText = `position:absolute;visibility:hidden;pointer-events:none;min-width:${gridMin};margin-left:var(--library-grid-gap);`;
      container.appendChild(probe);
      const minimumCardWidth = probe.getBoundingClientRect().width;
      const gap = Number.parseFloat(getComputedStyle(probe).marginLeft) || 0;
      probe.remove();

      const nextColumns = Math.max(1, Math.floor((container.clientWidth + gap) / (minimumCardWidth + gap)));
      setColumns((current) => current === nextColumns ? current : nextColumns);
    };

    updateColumns();
    const observer = new ResizeObserver(updateColumns);
    observer.observe(container);
    return () => observer.disconnect();
  }, [gridMin]);
  const rowCount = Math.ceil(items.length / columns);
  const virtualizer = useVirtualizer({ count: rowCount, getScrollElement: () => parentRef.current, estimateSize: () => cardSize === "lg" ? 440 : cardSize === "sm" ? 245 : 340, overscan: 3 });
  const virtualRows = virtualizer.getVirtualItems();

  useEffect(() => {
    const lastRow = virtualRows.at(-1);
    if (lastRow && lastRow.index >= rowCount - 2) onEndReached();
  }, [onEndReached, rowCount, virtualRows]);

  return (
    <>
      <div ref={parentRef} className="max-h-[calc(100dvh-260px)] overflow-auto" key={keyBust}>
        <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
          {virtualRows.map((row) => (
            <div key={row.key} ref={virtualizer.measureElement} data-index={row.index} className="absolute left-0 top-0 w-full" style={{ transform: `translateY(${row.start}px)` }}>
              <div className="stagger grid gap-[var(--library-grid-gap)] pb-[var(--library-grid-gap)]" style={{ gridTemplateColumns: `repeat(${columns}, minmax(${gridMin}, 1fr))` }}>
                {items.slice(row.index * columns, (row.index + 1) * columns).map((item) => (
                  <PosterCard key={item.id} item={item} size={cardSize} density={density} displayOptions={displayOptions} selected={selectedIds.includes(item.id)} onSelect={() => onSelect(item)} onToggle={() => onToggle(item.id)} />
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>
    </>
  );
}

function PosterCard({
  item,
  size = "md",
  density,
  displayOptions,
  selected,
  onSelect,
  onToggle
}: {
  item: MediaItem;
  size?: CardSize;
  density: Density;
  displayOptions: DisplayOptions;
  selected: boolean;
  onSelect: () => void;
  onToggle: () => void;
}) {
  const workspaceHref = item.type === "movie" ? `/movies/${item.id}` : `/tv/${item.id}`;
  const showMeta = SHOW_META[size] && displayOptions.showMeta;
  const titleCls = TITLE_CLASS_BY_DENSITY[density][size];

  return (
    <div className="group relative">
      {/* Premium circular selection toggle */}
      <button
        type="button"
        onClick={(e) => { e.stopPropagation(); onToggle(); }}
        aria-label={selected ? "Deselect" : "Select"}
        className={cn(
          "absolute left-2 top-2 z-10 flex shrink-0 items-center justify-center rounded-full transition-all duration-200",
          size === "sm" ? "h-5 w-5" : "h-6 w-6",
          selected
            ? [
                "bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] text-primary-foreground",
                "opacity-100 scale-100",
                "shadow-[0_0_0_2px_hsl(0_0%_0%/0.4),0_0_12px_hsl(var(--primary)/0.6),inset_0_1px_0_hsl(0_0%_100%/0.25)]"
              ].join(" ")
            : [
                "border border-white/25 bg-black/50 text-white/0 backdrop-blur-md",
                "opacity-0 scale-90 group-hover:opacity-100 group-hover:scale-100"
              ].join(" ")
        )}
      >
        {selected ? (
          /* Custom clean checkmark */
          <svg width="10" height="8" viewBox="0 0 10 8" fill="none" className="shrink-0">
            <path d="M1.5 4L4 6.5L8.5 1.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        ) : (
          <svg width="10" height="8" viewBox="0 0 10 8" fill="none" className="shrink-0 opacity-60">
            <path d="M1.5 4L4 6.5L8.5 1.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        )}
      </button>

      <button
        type="button"
        onClick={onSelect}
        className="block w-full text-left"
      >
        <div
          className={cn(
            "relative aspect-[2/3] overflow-hidden rounded-xl bg-muted transition-all duration-300",
            "shadow-card group-hover:-translate-y-1 group-hover:shadow-lg",
            selected
              ? "ring-2 ring-primary/80 shadow-[0_0_0_3px_hsl(var(--primary)/0.15),0_0_28px_hsl(var(--primary)/0.35)]"
              : "ring-0"
          )}
        >
          {/* Selected scrim overlay */}
          {selected && (
            <div className="pointer-events-none absolute inset-0 z-[5] bg-gradient-to-b from-primary/15 to-transparent" />
          )}
          <PosterArtwork
            src={item.poster}
            title={item.title}
            className="h-full w-full transition-transform duration-500 group-hover:scale-[1.04]"
          />

          {/* The poster marker always represents lifecycle state. Monitoring is
              shown only as quiet supporting text below, never as a colour. */}
          {displayOptions.showStatusPill && size !== "sm" ? (
            <div className="absolute right-1.5 top-1.5 z-10">
              <StatusPill status={item.status} />
            </div>
          ) : displayOptions.showStatusPill ? (
            <span
              role="img"
              aria-label={MEDIA_STATUS_PRESENTATION[item.status].label}
              title={MEDIA_STATUS_PRESENTATION[item.status].label}
              className={cn(
                "absolute right-1.5 top-1.5 z-10 inline-flex h-2 w-2 items-center justify-center rounded-full ring-1",
                MEDIA_STATUS_PRESENTATION[item.status].dot,
                "ring-background/90",
                mediaStatusIsActive(item.status) && "animate-pulse"
              )}
            />
          ) : null}

          {/* Gradient overlay — condenses on small */}
          <div className={cn(
            "absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/95 via-black/55 to-transparent",
            size === "sm" ? "px-2 pb-2 pt-8" : "px-2.5 pb-2.5 pt-14"
          )}>
            {displayOptions.showTitle ? (
              <p className={cn("line-clamp-2 font-semibold leading-tight text-[hsl(var(--media-foreground))] drop-shadow", titleCls)}>
                {item.title}
              </p>
            ) : null}
            {showMeta ? (
              <div className="mt-0.5 flex items-center gap-1.5 text-[length:var(--library-meta-size)] text-[hsl(var(--media-muted-foreground))]">
                <span className="tabular">{item.year}</span>
                <span className="text-[hsl(var(--media-muted-foreground)/0.45)]">·</span>
                <span className="inline-flex items-center gap-1" title={item.monitored ? "Deluno will keep looking for this title." : "Deluno will not search for this title automatically."}>
                  <ShieldCheck className="h-3 w-3 text-[hsl(var(--media-muted-foreground))]" />
                  {item.monitored ? "Monitored" : "Not monitored"}
                </span>
              </div>
            ) : null}
            {showMeta && (displayOptions.showRating || displayOptions.showQualityBadge) ? (
              <div className="mt-1">
                <div className="flex items-center justify-between gap-2 text-[length:var(--library-meta-size)]">
                  {displayOptions.showRating && item.rating !== null ? (
                    <span className="tabular inline-flex items-center gap-0.5 font-bold text-[hsl(var(--media-foreground))]">
                      <Star className="h-2.5 w-2.5 fill-warning text-warning" />
                      {item.rating.toFixed(1)}
                    </span>
                  ) : <span />}
                  {displayOptions.showQualityBadge && item.quality ? (
                    <Badge className="bg-white/15 px-1.5 py-0 text-[length:var(--library-badge-size)] font-bold text-[hsl(var(--media-foreground))] backdrop-blur-sm">
                      {shortQuality(item.quality)}
                    </Badge>
                  ) : null}
                </div>
              </div>
            ) : null}
          </div>

          {/* Hover-reveal action row */}
          <div className="absolute inset-x-0 bottom-0 flex items-center gap-1 bg-gradient-to-t from-black to-transparent px-2 pb-2 pt-6 opacity-0 transition-opacity duration-300 group-hover:opacity-100">
            <Link
              to={workspaceHref}
              onClick={(e) => e.stopPropagation()}
              className="flex flex-1 items-center justify-center gap-1 rounded-lg bg-primary px-2 py-1.5 text-[length:var(--library-badge-size)] font-bold text-primary-foreground shadow-md transition hover:brightness-110"
            >
              <Play className="h-2.5 w-2.5" fill="currentColor" />
              Open
            </Link>
          </div>
        </div>
      </button>

      {/* Below-poster metadata — adapts per size */}
      <div className="hidden">
        {displayOptions.showTitle ? (
          <p className={cn("line-clamp-1 font-semibold text-foreground", titleCls)}>
            {item.title}
          </p>
        ) : null}
        {showMeta ? (
          <div className="flex items-center gap-1.5 text-[length:var(--library-meta-size)] text-muted-foreground">
            <span className="tabular">{item.year}</span>
            <span className="text-foreground/20">·</span>
            <span className="inline-flex items-center gap-1" title={item.monitored ? "Deluno will keep looking for this title." : "Deluno will not search for this title automatically."}>
              <ShieldCheck className="h-3 w-3" />
              {item.monitored ? "Monitored" : "Not monitored"}
            </span>
          </div>
        ) : null}
      </div>
    </div>
  );
}

function StatusPill({ status }: { status: MediaStatus }) {
  const config = MEDIA_STATUS_PRESENTATION[status];

  return (
    <div
      role="img"
      aria-label={config.label}
      title={config.label}
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-1.5 py-0.5 text-[length:var(--library-badge-size)] font-bold uppercase tracking-wider backdrop-blur-md",
        config.tone
      )}
    >
      <span className={cn("h-1.5 w-1.5 rounded-full", config.dot, mediaStatusIsActive(status) && "animate-pulse")} />
      {config.compactLabel}
    </div>
  );
}

function StatusDot({ status }: { status: MediaStatus }) {
  return <span className={cn("h-2 w-2 shrink-0 rounded-full", MEDIA_STATUS_PRESENTATION[status].dot, mediaStatusIsActive(status) && "animate-pulse")} />;
}

function StatusBadge({ status }: { status: MediaStatus }) {
  const config = MEDIA_STATUS_PRESENTATION[status];
  return <Badge variant={config.variant}>{config.label}</Badge>;
}

function PosterArtwork({
  src,
  title,
  className,
  compact = false
}: {
  src: string | null;
  title: string;
  className?: string;
  compact?: boolean;
}) {
  if (src) {
    return <img src={src} alt={title} className={cn("object-cover", className)} loading="lazy" />;
  }

  return (
    <div
      className={cn(
        "flex items-center justify-center bg-gradient-to-br from-surface-2 to-surface-3 text-center text-muted-foreground",
        className
      )}
      aria-label={`${title} artwork unavailable`}
    >
      <span className={cn("px-2 font-display font-semibold tracking-tight", compact ? "text-[10px]" : "text-sm")}>
        {title.slice(0, 2).toUpperCase()}
      </span>
    </div>
  );
}

function shortQuality(value: string) {
  if (value.includes("2160")) return "4K";
  if (value.includes("1080")) return "1080p";
  if (value.includes("720")) return "720p";
  return value;
}

function createInitialForm(): CreateFormDraft {
  return { title: "", year: "", imdbId: "", monitored: true, metadata: null as MetadataSearchResult | null };
}

function metadataCreatePayload(metadata: MetadataSearchResult | null) {
  if (!metadata) {
    return {};
  }

  return {
    metadataProvider: metadata.provider,
    metadataProviderId: metadata.providerId,
    originalTitle: metadata.originalTitle,
    overview: metadata.overview,
    posterUrl: metadata.posterUrl,
    backdropUrl: metadata.backdropUrl,
    rating: metadata.rating,
    genres: metadata.genres.join(", "),
    externalUrl: metadata.externalUrl,
    metadataJson: JSON.stringify(metadata)
  };
}

/* Premium bulk action button inside the inline selection bar */
function BulkAction({
  label,
  icon,
  onClick,
  disabled,
  loading,
  variant = "ghost"
}: {
  label: string;
  icon: React.ReactNode;
  onClick: () => void;
  disabled?: boolean;
  loading?: boolean;
  variant?: "ghost" | "primary" | "danger";
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      className={cn(
        "flex min-h-[var(--library-toolbar-height)] items-center gap-1.5 rounded-xl px-3 text-[length:var(--library-toolbar-size)] font-medium transition-all duration-150 select-none",
        "disabled:opacity-40 disabled:cursor-not-allowed",
        variant === "primary"
          ? [
              "bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] text-primary-foreground",
              "shadow-[0_2px_8px_hsl(var(--primary-deep)/0.4),inset_0_1px_0_hsl(0_0%_100%/0.15)]",
              "hover:brightness-110 active:scale-95"
            ].join(" ")
          : variant === "danger"
            ? "text-destructive hover:bg-destructive/10 hover:text-destructive active:bg-destructive/15"
            : [
              "text-[hsl(var(--media-muted-foreground))] hover:bg-white/[0.07] hover:text-[hsl(var(--media-foreground))] active:bg-white/[0.04]"
            ].join(" ")
      )}
    >
      {loading ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : icon}
      <span className="hidden sm:inline">{label}</span>
    </button>
  );
}

/* ══════════════════════════════════════════════════════
   LIBRARY TABLE — sticky head + sticky title column
   + edge-shadow on horizontal scroll
══════════════════════════════════════════════════════ */
function LibraryTable({
  items,
  selectedIds,
  onSelect,
  onToggle,
  onToggleAll,
  allSelected,
  someSelected,
  onEndReached
}: {
  items: MediaItem[];
  selectedIds: string[];
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onToggleAll: () => void;
  allSelected: boolean;
  someSelected: boolean;
  onEndReached: () => void;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const tableRef = useRef<HTMLTableElement>(null);
  const [focusIndex, setFocusIndex] = useState(0);
  const rowVirtualizer = useVirtualizer({ count: items.length, getScrollElement: () => scrollRef.current, estimateSize: () => 65, overscan: 10 });
  const virtualRows = rowVirtualizer.getVirtualItems();

  useEffect(() => {
    const lastRow = virtualRows.at(-1);
    if (lastRow && lastRow.index >= items.length - 2) onEndReached();
  }, [items.length, onEndReached, virtualRows]);

  useEffect(() => {
    const el = scrollRef.current;
    const table = tableRef.current;
    if (!el || !table) return;
    function onScroll() {
      if (!el || !table) return;
      if (el.scrollLeft > 2) {
        table.classList.add("is-scrolled");
      } else {
        table.classList.remove("is-scrolled");
      }
    }
    el.addEventListener("scroll", onScroll, { passive: true });
    return () => el.removeEventListener("scroll", onScroll);
  }, []);

  // Keep focus index inside current list bounds whenever items change.
  useEffect(() => {
    if (focusIndex >= items.length) setFocusIndex(Math.max(0, items.length - 1));
  }, [items.length, focusIndex]);

  function focusRow(next: number) {
    const clamped = Math.max(0, Math.min(items.length - 1, next));
    setFocusIndex(clamped);
    const row = tableRef.current?.querySelector<HTMLTableRowElement>(
      `tbody tr[data-row-index="${clamped}"]`
    );
    row?.focus();
    row?.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }

  function handleRowKey(event: React.KeyboardEvent<HTMLTableRowElement>, index: number, item: MediaItem) {
    switch (event.key) {
      case "ArrowDown":
      case "j":
        event.preventDefault();
        focusRow(index + 1);
        break;
      case "ArrowUp":
      case "k":
        event.preventDefault();
        focusRow(index - 1);
        break;
      case "Home":
        event.preventDefault();
        focusRow(0);
        break;
      case "End":
        event.preventDefault();
        focusRow(items.length - 1);
        break;
      case "PageDown":
        event.preventDefault();
        focusRow(index + 10);
        break;
      case "PageUp":
        event.preventDefault();
        focusRow(index - 10);
        break;
      case "Enter":
        event.preventDefault();
        onSelect(item);
        break;
      case "x":
      case " ":
        event.preventDefault();
        onToggle(item.id);
        break;
      default:
        break;
    }
  }

  return (
    <div ref={scrollRef} className="max-h-[calc(100dvh-260px)] overflow-auto">
      <table
        ref={tableRef}
        className="data-table min-w-[900px] text-[13px]"
        role="grid"
        aria-rowcount={items.length}
      >
        <thead>
          <tr>
            <th className="col-sticky w-10" style={{ minWidth: 40 }}>
              <TableCheckbox
                checked={allSelected}
                indeterminate={someSelected}
                onChange={onToggleAll}
              />
            </th>
            <th className="col-sticky" style={{ left: 40, minWidth: 280 }}>Title</th>
            <th className="hidden md:table-cell">Quality</th>
            <th>Status</th>
            <th className="hidden lg:table-cell">Genre</th>
            <th className="num hidden lg:table-cell">Size</th>
            <th className="num hidden md:table-cell">Rating</th>
            <th className="hidden xl:table-cell">Added</th>
          </tr>
        </thead>
        <tbody>
          {virtualRows.length > 0 && virtualRows[0].start > 0 ? <tr aria-hidden="true"><td colSpan={8} style={{ height: virtualRows[0].start, padding: 0 }} /></tr> : null}
          {virtualRows.map((virtualRow) => {
            const index = virtualRow.index;
            const item = items[index];
            const isSelected = selectedIds.includes(item.id);
            const isFocused = index === focusIndex;
            return (
              <tr
                key={item.id}
                data-selected={isSelected}
                data-row-index={index}
                tabIndex={isFocused ? 0 : -1}
                aria-selected={isSelected}
                aria-rowindex={index + 1}
                onFocus={() => setFocusIndex(index)}
                onKeyDown={(event) => handleRowKey(event, index, item)}
                className="outline-none focus-visible:shadow-[inset_0_0_0_2px_hsl(var(--primary)/0.7)]"
              >
                <td className="col-sticky" style={{ minWidth: 40 }}>
                  <TableCheckbox
                    checked={isSelected}
                    onChange={() => onToggle(item.id)}
                  />
                </td>
                <td
                  className="col-sticky cursor-pointer"
                  style={{ left: 40, minWidth: 280 }}
                  onClick={() => onSelect(item)}
                >
                  <div className="flex items-center gap-3">
                    <PosterArtwork
                      src={item.poster}
                      title={item.title}
                      className="h-11 w-[30px] shrink-0 rounded-md shadow-card"
                      compact
                    />
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
                        <StatusDot status={item.status} />
                        <p className="truncate font-medium text-foreground">{item.title}</p>
                      </div>
                      <p className="text-[11px] text-muted-foreground">
                        {item.type === "movie" ? "Movie" : "TV"} · {item.year}
                        {item.monitored ? " · Monitored" : " · Not monitored"}
                      </p>
                    </div>
                  </div>
                </td>
                <td className="hidden md:table-cell">
                  <Badge>{item.quality ? shortQuality(item.quality) : "Unknown"}</Badge>
                </td>
                <td>
                  <StatusBadge status={item.status} />
                </td>
                <td className="hidden text-muted-foreground lg:table-cell">
                  {item.genres.slice(0, 2).join(", ")}
                </td>
                <td className="num hidden text-muted-foreground lg:table-cell">
                  {formatBytesFromGb(item.sizeGb)}
                </td>
                <td className="num hidden md:table-cell">
                  <span className="inline-flex items-center gap-1 text-foreground">
                    <Star className="h-3 w-3 fill-warning text-warning" />
                    {item.rating !== null ? item.rating.toFixed(1) : "Unknown"}
                  </span>
                </td>
                <td className="hidden text-muted-foreground xl:table-cell">{item.added}</td>
              </tr>
            );
          })}
          {virtualRows.length > 0 && rowVirtualizer.getTotalSize() - virtualRows.at(-1)!.end > 0 ? <tr aria-hidden="true"><td colSpan={8} style={{ height: rowVirtualizer.getTotalSize() - virtualRows.at(-1)!.end, padding: 0 }} /></tr> : null}
        </tbody>
      </table>
    </div>
  );
}

/* Premium circle checkbox for table rows */
function TableCheckbox({ checked, indeterminate, onChange }: {
  checked: boolean;
  indeterminate?: boolean;
  onChange: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onChange}
      className={cn(
        "flex h-4 w-4 shrink-0 items-center justify-center rounded-full border transition-all duration-200",
        checked || indeterminate
          ? "border-primary bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] text-primary-foreground shadow-[0_0_8px_hsl(var(--primary)/0.5)]"
          : "border-border/60 bg-background hover:border-primary/50 dark:bg-white/[0.04]"
      )}
      aria-label={checked ? "Deselect" : "Select"}
    >
      {checked ? (
        <svg width="7" height="6" viewBox="0 0 7 6" fill="none">
          <path d="M1 3L2.8 4.8L6 1" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
        </svg>
      ) : indeterminate ? (
        <span className="h-0.5 w-2 rounded-full bg-primary" />
      ) : null}
    </button>
  );
}

/* ══════════════════════════════════════════════════════
   CONTROL RAIL — premium floating bar with sliding indicator
══════════════════════════════════════════════════════ */
function ControlRail({
  label,
  query,
  setQuery,
  quickFilter,
  setQuickFilter,
  sortField,
  setSortField,
  sortDirection,
  setSortDirection,
  view,
  setView,
  cardSize,
  changeSize,
  displayOptions,
  setDisplayOptions,
  facets,
  savedPresets,
  newPresetName,
  setNewPresetName,
  isSavingPreset,
  saveCurrentPreset,
  applyPreset,
  deletePreset,
  activeFilterCount
}: {
  label: string;
  query: string;
  setQuery: (v: string) => void;
  quickFilter: QuickFilter;
  setQuickFilter: (v: QuickFilter) => void;
  sortField: SortField;
  setSortField: (v: SortField) => void;
  sortDirection: SortDirection;
  setSortDirection: (v: SortDirection) => void;
  view: ViewMode;
  setView: (v: ViewMode) => void;
  cardSize: CardSize;
  changeSize: (v: CardSize) => void;
  displayOptions: DisplayOptions;
  setDisplayOptions: (v: DisplayOptions) => void;
  facets: CatalogueFacets | null;
  savedPresets: SavedFilterPreset[];
  newPresetName: string;
  setNewPresetName: (v: string) => void;
  isSavingPreset: boolean;
  saveCurrentPreset: () => void | Promise<void>;
  applyPreset: (preset: SavedFilterPreset) => void;
  deletePreset: (presetId: string) => void;
  activeFilterCount: number;
}) {
  const [openPanel, setOpenPanel] = useState<"view" | "sort" | "filter" | null>(null);
  const pillTrackRef = useRef<HTMLDivElement>(null);
  const btnRefs = useRef<Map<QuickFilter, HTMLButtonElement | null>>(new Map());
  const [pill, setPill] = useState({ left: 0, width: 0, ready: false });

  useLayoutEffect(() => {
    const track = pillTrackRef.current;
    const btn = btnRefs.current.get(quickFilter);
    if (!track || !btn) return;
    const tRect = track.getBoundingClientRect();
    const bRect = btn.getBoundingClientRect();
    setPill({ left: bRect.left - tRect.left, width: bRect.width, ready: true });
  }, [quickFilter]);

  const counts: Record<QuickFilter, number> = {
    all: facets?.all ?? 0,
    monitored: facets?.monitored ?? 0,
    unmonitored: facets?.unmonitored ?? 0,
    downloaded: facets?.downloaded ?? 0,
    missing: facets?.missing ?? 0,
    upgrades: facets?.upgrades ?? 0
  };

  return (
    <div className="sticky top-[var(--topbar-height-mobile)] z-20 lg:top-topbar">
      <div
        className={cn(
          "relative overflow-hidden rounded-2xl",
          "border border-hairline/60 dark:border-white/[0.07]",
          "bg-background/80 backdrop-blur-2xl supports-[backdrop-filter]:bg-background/72",
          "dark:bg-[hsl(226_24%_7%/0.88)]",
          "shadow-[0_2px_16px_hsl(0_0%_0%/0.06),0_1px_3px_hsl(0_0%_0%/0.04)]",
          "dark:shadow-[0_4px_24px_hsl(0_0%_0%/0.28),0_1px_4px_hsl(0_0%_0%/0.2)]"
        )}
      >
        <div
          aria-hidden
          className="pointer-events-none absolute inset-x-0 top-0 h-px opacity-60"
          style={{ background: "linear-gradient(90deg, transparent 5%, hsl(var(--primary)/0.4) 35%, hsl(var(--primary-2)/0.4) 65%, transparent 95%)" }}
        />

        <div className="px-[calc(var(--tile-pad)*0.8)] py-[calc(var(--tile-pad)*0.65)]">
          <div className="flex flex-wrap items-center gap-2">
            <div className={cn(
              "group relative flex min-w-[240px] flex-1 items-center gap-2.5 rounded-xl px-3.5 transition-all duration-200",
              "min-h-[var(--library-toolbar-height)] bg-foreground/[0.04] dark:bg-white/[0.05]",
              "ring-1 ring-inset ring-hairline/60 dark:ring-white/[0.06]",
              "focus-within:bg-foreground/[0.06] focus-within:ring-primary/35",
              "focus-within:shadow-[0_0_0_3px_hsl(var(--primary)/0.09)]"
            )}>
              <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground/50 transition-colors duration-200 group-focus-within:text-primary/70" strokeWidth={2} />
              <Input
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder={`Search ${label}…`}
                className="h-full border-0 bg-transparent px-0 text-[length:var(--library-toolbar-size)] shadow-none placeholder:text-muted-foreground/40 focus-visible:ring-0"
              />
              {query ? (
                <button
                  type="button"
                  onClick={() => setQuery("")}
                  aria-label="Clear"
                  className="flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-muted-foreground/20 text-muted-foreground transition hover:bg-foreground/15 hover:text-foreground"
                >
                  <span className="text-[8.5px] font-bold leading-none">×</span>
                </button>
              ) : (
                <kbd className="hidden shrink-0 rounded border border-hairline/70 bg-background/50 px-1.5 py-px font-mono text-[length:var(--library-badge-size)] text-muted-foreground/40 group-focus-within:hidden sm:block">
                  /
                </kbd>
              )}
            </div>

            <ToolbarMenuButton
              label="Display"
              icon={LayoutTemplate}
              active={openPanel === "view"}
              meta={view === "grid" ? `Poster grid · ${cardSize === "sm" ? "Small" : cardSize === "lg" ? "Large" : "Medium"}` : "Compact list"}
              onClick={() => setOpenPanel((current) => current === "view" ? null : "view")}
            />
            <ToolbarMenuButton
              label="Order"
              icon={ArrowUpDown}
              active={openPanel === "sort"}
              meta={`${sortFieldOptions.find((option) => option.value === sortField)?.label ?? "Title"} · ${sortDirection === "asc" ? "A–Z" : "Z–A"}`}
              onClick={() => setOpenPanel((current) => current === "sort" ? null : "sort")}
            />
            <ToolbarMenuButton
              label="Refine"
              icon={Filter}
              active={openPanel === "filter"}
              meta={activeFilterCount > 0 ? `${activeFilterCount} active` : "Quick filters"}
              onClick={() => setOpenPanel((current) => current === "filter" ? null : "filter")}
            />
          </div>

          <div className="mt-2.5">
            <div ref={pillTrackRef} className="relative flex flex-wrap items-center gap-0.5">
              {pill.ready ? (
                <div
                  aria-hidden
                  className="absolute rounded-lg bg-foreground/[0.07] dark:bg-white/[0.09]"
                  style={{
                    left: pill.left,
                    width: pill.width,
                    height: "calc(var(--library-toolbar-height) * 0.74)",
                    top: "50%",
                    transform: "translateY(-50%)",
                    transition: "left 0.22s cubic-bezier(0.4,0,0.2,1), width 0.22s cubic-bezier(0.4,0,0.2,1)"
                  }}
                />
              ) : null}

              {quickFilterConfig.map((chip) => {
                const active = quickFilter === chip.key;
                return (
                  <button
                    key={chip.key}
                    ref={(element) => { btnRefs.current.set(chip.key, element); }}
                    type="button"
                    onClick={() => setQuickFilter(chip.key)}
                    className={cn(
                      "relative flex min-h-[calc(var(--library-toolbar-height)*0.78)] items-center gap-1.5 rounded-lg px-3 text-[length:var(--library-toolbar-size)] select-none",
                      active ? "font-semibold text-foreground" : "font-medium text-muted-foreground hover:text-foreground"
                    )}
                  >
                    <span>{chip.label}</span>
                    <span
                      className={cn(
                        "tabular rounded-md px-1.5 py-px text-[length:var(--library-badge-size)] font-bold leading-tight",
                        active ? "bg-primary/15 text-primary dark:bg-primary/20" : "bg-foreground/[0.06] text-muted-foreground dark:bg-white/[0.07]"
                      )}
                    >
                      {counts[chip.key]}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>

          {openPanel === "view" ? (
            <div className="mt-3 overflow-hidden rounded-2xl border border-hairline bg-surface-1">
              <LibraryControlPanelHeader
                icon={LayoutTemplate}
                eyebrow="Display"
                title="Choose how your library feels"
                description="Start with a visual poster grid or a dense list. Your choice is remembered separately for movies and TV."
                onClose={() => setOpenPanel(null)}
              />
              <div className="grid gap-[var(--grid-gap)] p-[calc(var(--tile-pad)*0.8)] xl:grid-cols-[minmax(0,1.05fr)_minmax(320px,0.95fr)]">
                <div className="space-y-[var(--grid-gap)]">
                  <div>
                    <SectionLabel>Layout</SectionLabel>
                    <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">Pick the view that best fits the job in front of you.</p>
                  </div>
                  <div className="grid gap-2 sm:grid-cols-2">
                    <LibraryViewChoice mode="grid" label="Poster grid" description="Artwork-led browsing for your collection." selected={view === "grid"} onClick={() => setView("grid")} />
                    <LibraryViewChoice mode="list" label="Compact list" description="More titles and file details in less space." selected={view === "list"} onClick={() => setView("list")} />
                  </div>

                  {view === "grid" ? (
                    <div className="rounded-xl border border-hairline bg-background/45 p-3">
                      <SectionLabel>Poster size</SectionLabel>
                      <div className="mt-2 grid grid-cols-3 gap-2">
                        {(["sm", "md", "lg"] as CardSize[]).map((size) => (
                          <PosterSizeChoice key={size} size={size} selected={cardSize === size} onClick={() => changeSize(size)} />
                        ))}
                      </div>
                    </div>
                  ) : null}
                </div>

                <div className="rounded-xl border border-hairline bg-background/45 p-3">
                  <SectionLabel>What each poster shows</SectionLabel>
                  <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">Keep the essentials visible; turn on extra metadata only when it helps your workflow.</p>
                  <div className="mt-3 grid gap-2 sm:grid-cols-2">
                    <DisplayToggle label="Title" description="The movie or series name" checked={displayOptions.showTitle} onChange={(checked) => setDisplayOptions({ ...displayOptions, showTitle: checked })} />
                    <DisplayToggle label="Year & monitoring" description="Release year and monitored state" checked={displayOptions.showMeta} onChange={(checked) => setDisplayOptions({ ...displayOptions, showMeta: checked })} />
                    <DisplayToggle label="Availability" description="Missing, downloading, or imported" checked={displayOptions.showStatusPill} onChange={(checked) => setDisplayOptions({ ...displayOptions, showStatusPill: checked })} />
                    <DisplayToggle label="Quality" description="Current or target quality" checked={displayOptions.showQualityBadge} onChange={(checked) => setDisplayOptions({ ...displayOptions, showQualityBadge: checked })} />
                    <DisplayToggle label="Rating" description="The preferred metadata score" checked={displayOptions.showRating} onChange={(checked) => setDisplayOptions({ ...displayOptions, showRating: checked })} />
                  </div>
                </div>
              </div>
            </div>
          ) : null}

          {openPanel === "sort" ? (
            <div className="mt-3 overflow-hidden rounded-2xl border border-hairline bg-surface-1">
              <LibraryControlPanelHeader
                icon={ArrowUpDown}
                eyebrow="Order"
                title="Put the right titles first"
                description="Every available order is performed by the paged catalogue query."
                onClose={() => setOpenPanel(null)}
              />
              <div className="grid gap-[var(--grid-gap)] p-[calc(var(--tile-pad)*0.8)] xl:grid-cols-[minmax(0,1fr)_18rem]">
                <div>
                  <SectionLabel>Sort by</SectionLabel>
                  <div className="mt-2 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                    {sortFieldOptions.map((option) => (
                      <SortChoice key={option.value} label={option.label} selected={sortField === option.value} onClick={() => setSortField(option.value)} />
                    ))}
                  </div>
                </div>
                <div className="rounded-xl border border-hairline bg-background/45 p-3">
                  <SectionLabel>Direction</SectionLabel>
                  <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">Choose whether the smallest or largest value leads.</p>
                  <div className="mt-3 grid gap-2">
                    <SortDirectionChoice icon={ArrowDownAZ} label="Ascending" description="A–Z, oldest first, or lowest value." selected={sortDirection === "asc"} onClick={() => setSortDirection("asc")} />
                    <SortDirectionChoice icon={ArrowUpDown} label="Descending" description="Z–A, newest first, or highest value." selected={sortDirection === "desc"} onClick={() => setSortDirection("desc")} />
                  </div>
                </div>
              </div>
            </div>
          ) : null}

          {openPanel === "filter" ? (
            <div className="mt-3 overflow-hidden rounded-2xl border border-hairline bg-surface-1">
              <LibraryControlPanelHeader
                icon={Filter}
                eyebrow="Refine"
                title="Narrow the library without losing your place"
                description={`You are viewing ${quickFilterConfig.find((filter) => filter.key === quickFilter)?.label.toLowerCase() ?? "all"} titles. Quick filters and search run in the catalogue query.`}
                onClose={() => setOpenPanel(null)}
              />
            <div className="space-y-[calc(var(--field-group-pad)*0.8)] p-[calc(var(--tile-pad)*0.8)]">
              <div className="space-y-[calc(var(--field-group-pad)*0.8)]">
                  <div className="space-y-2">
                    <SectionLabel>Saved library views</SectionLabel>
                    <div className="flex gap-2">
                      <Input value={newPresetName} onChange={(event) => setNewPresetName(event.target.value)} placeholder="Name this filter" className="h-[var(--control-height-sm)]" />
                      <Button type="button" size="sm" onClick={saveCurrentPreset} disabled={isSavingPreset}>
                        {isSavingPreset ? "Saving…" : "Save"}
                      </Button>
                    </div>
                  </div>

                  {savedPresets.length > 0 ? (
                    <div className="space-y-2">
                      {savedPresets.map((preset) => (
                        <div key={preset.id} className="flex items-center justify-between gap-3 rounded-xl border border-hairline bg-background/40 px-3 py-3">
                          <button type="button" className="min-w-0 flex-1 text-left" onClick={() => applyPreset(preset)}>
                            <p className="truncate text-sm font-medium text-foreground">{preset.name}</p>
                            <p className="text-xs text-muted-foreground">
                              {preset.quickFilter !== "all" ? `${preset.quickFilter} · ` : ""}
                              Saved search, order, and display settings
                            </p>
                          </button>
                          <Button type="button" size="sm" variant="ghost" onClick={() => deletePreset(preset.id)}>
                            Remove
                          </Button>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <div className="rounded-xl border border-dashed border-hairline bg-background/40 px-4 py-4 text-sm text-muted-foreground">
                      Save a search, quick filter, order, and display choice once, then return to it in one click.
                    </div>
                  )}
                </div>
              </div>
            </div>
          ) : null}
        </div>
      </div>
    </div>
  );
}

function ToolbarMenuButton({
  label,
  icon: Icon,
  active,
  meta,
  onClick
}: {
  label: string;
  icon: typeof Filter;
  active: boolean;
  meta: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "group inline-flex min-h-[var(--library-toolbar-height)] items-center gap-2 rounded-xl px-2.5 pr-3 text-left text-[length:var(--library-toolbar-size)] font-medium transition-all",
        active
          ? "bg-primary/10 text-primary ring-1 ring-inset ring-primary/25"
          : "bg-foreground/[0.04] text-foreground ring-1 ring-inset ring-hairline/60 hover:bg-foreground/[0.06] dark:bg-white/[0.05] dark:ring-white/[0.06]"
      )}
    >
      <span className={cn("flex h-6 w-6 shrink-0 items-center justify-center rounded-lg", active ? "bg-primary/16" : "bg-foreground/[0.06] dark:bg-white/[0.07]")}>
        <Icon className="h-3.5 w-3.5" />
      </span>
      <span className="hidden min-w-0 leading-tight sm:block">
        <span className="block font-semibold">{label}</span>
        <span className={cn("block max-w-28 truncate text-[length:var(--type-micro)] font-medium", active ? "text-primary/75" : "text-muted-foreground")}>{meta}</span>
      </span>
      <span className="sm:hidden font-semibold">{label}</span>
      <ChevronDown className={cn("h-3.5 w-3.5 shrink-0 transition-transform", active && "rotate-180")} />
    </button>
  );
}

function LibraryControlPanelHeader({
  icon: Icon,
  eyebrow,
  title,
  description,
  onClose
}: {
  icon: typeof Filter;
  eyebrow: string;
  title: string;
  description: string;
  onClose: () => void;
}) {
  return (
    <div className="flex items-start justify-between gap-3 border-b border-hairline bg-background/35 px-[calc(var(--tile-pad)*0.8)] py-3">
      <div className="flex min-w-0 items-start gap-3">
        <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-xl border border-primary/20 bg-primary/10 text-primary">
          <Icon className="h-4 w-4" />
        </span>
        <div className="min-w-0">
          <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.16em] text-primary">{eyebrow}</p>
          <h3 className="mt-0.5 font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">{title}</h3>
          <p className="mt-1 max-w-4xl text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{description}</p>
        </div>
      </div>
      <button type="button" onClick={onClose} aria-label={`Close ${eyebrow.toLowerCase()} controls`} className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg text-muted-foreground transition hover:bg-muted hover:text-foreground">
        <X className="h-3.5 w-3.5" />
      </button>
    </div>
  );
}

function LibraryViewChoice({
  mode,
  label,
  description,
  selected,
  onClick
}: {
  mode: ViewMode;
  label: string;
  description: string;
  selected: boolean;
  onClick: () => void;
}) {
  const Icon = mode === "grid" ? LayoutGrid : List;
  return (
    <button type="button" onClick={onClick} className={cn("rounded-xl border p-3 text-left transition", selected ? "border-primary/35 bg-primary/[0.09] shadow-[inset_0_0_0_1px_hsl(var(--primary)/0.1)]" : "border-hairline bg-background/45 hover:border-primary/25 hover:bg-background/70")}>
      <div className="flex items-center justify-between gap-3">
        <span className={cn("flex h-8 w-8 items-center justify-center rounded-lg", selected ? "bg-primary/15 text-primary" : "bg-muted text-muted-foreground")}><Icon className="h-4 w-4" /></span>
        {selected ? <span className="rounded-full bg-primary/15 px-2 py-0.5 text-[length:var(--type-micro)] font-bold text-primary">Selected</span> : null}
      </div>
      <p className="mt-3 text-sm font-semibold text-foreground">{label}</p>
      <p className="mt-1 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{description}</p>
    </button>
  );
}

function PosterSizeChoice({ size, selected, onClick }: { size: CardSize; selected: boolean; onClick: () => void }) {
  const labels: Record<CardSize, { label: string; detail: string; height: string }> = {
    sm: { label: "Small", detail: "More titles", height: "h-6" },
    md: { label: "Medium", detail: "Balanced", height: "h-8" },
    lg: { label: "Large", detail: "Artwork first", height: "h-10" }
  };
  const item = labels[size];
  return (
    <button type="button" onClick={onClick} className={cn("rounded-lg border px-2 py-2 text-center transition", selected ? "border-primary/35 bg-primary/[0.09] text-primary" : "border-hairline bg-background/45 text-muted-foreground hover:border-primary/25")}>
      <span className={cn("mx-auto block w-7 rounded-md border", item.height, selected ? "border-primary/45 bg-primary/20" : "border-hairline bg-muted/50")} />
      <span className="mt-1.5 block text-[length:var(--type-caption)] font-semibold">{item.label}</span>
      <span className="block text-[length:var(--type-micro)] opacity-75">{item.detail}</span>
    </button>
  );
}

function SortChoice({ label, selected, onClick }: { label: string; selected: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} className={cn("flex min-h-[var(--control-height)] items-center justify-between rounded-xl border px-3 text-left text-[length:var(--type-caption)] font-semibold transition", selected ? "border-primary/35 bg-primary/[0.09] text-primary" : "border-hairline bg-background/45 text-foreground hover:border-primary/25 hover:bg-background/70")}>
      {label}
      {selected ? <span className="h-1.5 w-1.5 rounded-full bg-primary shadow-[0_0_8px_hsl(var(--primary)/0.75)]" /> : null}
    </button>
  );
}

function SortDirectionChoice({
  icon: Icon,
  label,
  description,
  selected,
  onClick
}: {
  icon: typeof ArrowDownAZ;
  label: string;
  description: string;
  selected: boolean;
  onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} className={cn("flex items-center gap-3 rounded-xl border p-3 text-left transition", selected ? "border-primary/35 bg-primary/[0.09]" : "border-hairline bg-background/45 hover:border-primary/25 hover:bg-background/70")}>
      <span className={cn("flex h-8 w-8 shrink-0 items-center justify-center rounded-lg", selected ? "bg-primary/15 text-primary" : "bg-muted text-muted-foreground")}><Icon className="h-4 w-4" /></span>
      <span className="min-w-0 flex-1"><span className={cn("block text-[length:var(--type-caption)] font-semibold", selected ? "text-primary" : "text-foreground")}>{label}</span><span className="mt-0.5 block text-[length:var(--type-micro)] leading-snug text-muted-foreground">{description}</span></span>
    </button>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <p className="text-[length:var(--type-caption)] font-semibold uppercase tracking-[0.18em] text-muted-foreground">
      {children}
    </p>
  );
}

function BulkField({
  label,
  description,
  children
}: {
  label: string;
  description: string;
  children: React.ReactNode;
}) {
  return (
    <div className="space-y-2">
      <p className="text-sm font-medium text-foreground">{label}</p>
      <p className="text-xs text-muted-foreground">{description}</p>
      {children}
    </div>
  );
}

function DisplayToggle({
  label,
  description,
  checked,
  onChange
}: {
  label: string;
  description: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className={cn("flex cursor-pointer items-start gap-2.5 rounded-xl border px-3 py-2.5 transition", checked ? "border-primary/25 bg-primary/[0.06]" : "border-hairline bg-background/45 hover:border-primary/20")}>
      <input className="mt-0.5 accent-[hsl(var(--primary))]" type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span className="min-w-0"><span className="block text-[length:var(--type-caption)] font-semibold text-foreground">{label}</span><span className="mt-0.5 block text-[length:var(--type-micro)] leading-snug text-muted-foreground">{description}</span></span>
    </label>
  );
}

function defaultDisplayOptions(): DisplayOptions {
  return {
    showTitle: true,
    showMeta: true,
    showStatusPill: true,
    showQualityBadge: true,
    showRating: true
  };
}

function parseDisplayOptions(raw: string | null | undefined): DisplayOptions {
  if (!raw) {
    return defaultDisplayOptions();
  }

  try {
    const parsed = JSON.parse(raw) as Partial<DisplayOptions>;
    return {
      showTitle: parsed.showTitle ?? true,
      showMeta: parsed.showMeta ?? true,
      showStatusPill: parsed.showStatusPill ?? true,
      showQualityBadge: parsed.showQualityBadge ?? true,
      showRating: parsed.showRating ?? true
    };
  } catch {
    return defaultDisplayOptions();
  }
}

function parseCustomRules(raw: string | null | undefined): CustomFilterRule[] {
  if (!raw) {
    return [];
  }

  try {
    const parsed = JSON.parse(raw) as Array<Partial<CustomFilterRule>>;
    return Array.isArray(parsed)
      ? parsed.map((rule) => ({
          id: rule.id ?? crypto.randomUUID(),
          field: rule.field ?? "title",
          comparator: rule.comparator ?? "contains",
          value: rule.value ?? ""
        }))
      : [];
  } catch {
    return [];
  }
}

function defaultComparatorForField(field: FilterField): FilterComparator {
  const kind = filterFieldOptions.find((option) => option.value === field)?.kind;
  if (kind === "number") return "gte";
  return kind === "boolean" || kind === "enum" ? "equals" : "contains";
}

function comparatorsForField(field: FilterField): FilterComparator[] {
  const kind = filterFieldOptions.find((option) => option.value === field)?.kind;
  if (kind === "number") return ["equals", "gt", "gte", "lt", "lte"];
  if (kind === "boolean" || kind === "enum") return ["equals", "notEquals"];
  return ["contains", "equals", "notEquals"];
}

function friendlyComparatorLabel(comparator: FilterComparator) {
  return {
    contains: "contains",
    equals: "is",
    notEquals: "is not",
    gt: ">",
    gte: "≥",
    lt: "<",
    lte: "≤"
  }[comparator];
}

function placeholderForField(field: FilterField) {
  return {
    title: "e.g. Dune",
    quality: "e.g. 2160p",
    genre: "e.g. Animation",
    year: "e.g. 2024",
    rating: "e.g. 8.0",
    sizeGb: "e.g. 25",
    bitrateMbps: "e.g. 16.5",
    network: "e.g. HBO",
    releaseGroup: "e.g. FraMeSToR",
    tags: "e.g. anime",
    source: "",
    codec: "",
    audioCodec: "",
    audioChannels: "",
    language: "",
    hdrFormat: "",
    releaseStatus: "",
    certification: "",
    collection: "e.g. A24",
    minimumAvailability: "",
    consideredAvailable: "",
    digitalRelease: "e.g. 2024",
    physicalRelease: "e.g. 2024",
    releaseDate: "e.g. 2024",
    inCinemas: "e.g. 2024",
    originalLanguage: "",
    originalTitle: "e.g. original localized title",
    path: "e.g. /media/movies",
    qualityProfile: "",
    runtimeMinutes: "e.g. 120",
    studio: "e.g. HBO",
    tmdbRating: "e.g. 8.2",
    tmdbVotes: "e.g. 10000",
    imdbRating: "e.g. 8.1",
    imdbVotes: "e.g. 250000",
    traktRating: "e.g. 8.4",
    traktVotes: "e.g. 5000",
    tomatoRating: "e.g. 92",
    tomatoVotes: "e.g. 250",
    popularity: "e.g. 100",
    keywords: "e.g. atmos",
    wantedReason: "e.g. quality upgrade",
    currentQuality: "e.g. WEB-DL 1080p",
    targetQuality: "e.g. Bluray-2160p",
    status: "",
    monitored: "",
    type: ""
  }[field];
}

function matchesCustomRule(item: MediaItem, rule: CustomFilterRule) {
  if (!rule.value.trim()) return true;

  const rawValue = resolveRuleValue(item, rule.field);
  if (rawValue === null || rawValue === undefined) return false;

  if (typeof rawValue === "number") {
    const target = Number(rule.value);
    if (Number.isNaN(target)) return false;
    switch (rule.comparator) {
      case "equals": return rawValue === target;
      case "gt": return rawValue > target;
      case "gte": return rawValue >= target;
      case "lt": return rawValue < target;
      case "lte": return rawValue <= target;
      default: return false;
    }
  }

  const normalizedValue = String(rawValue).toLowerCase();
  const normalizedTarget = rule.value.toLowerCase();
  switch (rule.comparator) {
    case "contains":
      return normalizedValue.includes(normalizedTarget);
    case "equals":
      return normalizedValue === normalizedTarget;
    case "notEquals":
      return normalizedValue !== normalizedTarget;
    default:
      return false;
  }
}

function resolveRuleValue(item: MediaItem, field: FilterField): string | number | boolean | null | undefined {
  switch (field) {
    case "title":
      return item.title;
    case "status":
      return item.status;
    case "monitored":
      return item.monitored;
    case "quality":
      return item.quality;
    case "genre":
      return item.genres.join(" ");
    case "year":
      return item.year;
    case "rating":
      return item.rating;
    case "sizeGb":
      return item.sizeGb;
    case "bitrateMbps":
      return item.bitrateMbps ?? null;
    case "network":
      return item.network ?? null;
    case "releaseGroup":
      return item.releaseGroup ?? null;
    case "tags":
      return item.tags?.join(" ") ?? null;
    case "source":
      return item.source ?? null;
    case "codec":
      return item.codec ?? null;
    case "audioCodec":
      return item.audioCodec ?? null;
    case "audioChannels":
      return item.audioChannels ?? null;
    case "language":
      return item.language ?? null;
    case "hdrFormat":
      return item.hdrFormat ?? null;
    case "releaseStatus":
      return item.releaseStatus ?? null;
    case "certification":
      return item.certification ?? null;
    case "collection":
      return item.collection ?? null;
    case "minimumAvailability":
      return item.minimumAvailability ?? null;
    case "consideredAvailable":
      return item.consideredAvailable ?? null;
    case "digitalRelease":
      return item.digitalRelease ?? null;
    case "physicalRelease":
      return item.physicalRelease ?? null;
    case "releaseDate":
      return item.releaseDate ?? null;
    case "inCinemas":
      return item.inCinemas ?? null;
    case "originalLanguage":
      return item.originalLanguage ?? null;
    case "originalTitle":
      return item.originalTitle ?? null;
    case "path":
      return item.path ?? null;
    case "qualityProfile":
      return item.qualityProfile ?? null;
    case "runtimeMinutes":
      return item.runtimeMinutes ?? null;
    case "studio":
      return item.studio ?? null;
    case "tmdbRating":
      return item.tmdbRating ?? null;
    case "tmdbVotes":
      return item.tmdbVotes ?? null;
    case "imdbRating":
      return item.imdbRating ?? null;
    case "imdbVotes":
      return item.imdbVotes ?? null;
    case "traktRating":
      return item.traktRating ?? null;
    case "traktVotes":
      return item.traktVotes ?? null;
    case "tomatoRating":
      return item.tomatoRating ?? null;
    case "tomatoVotes":
      return item.tomatoVotes ?? null;
    case "popularity":
      return item.popularity ?? null;
    case "keywords":
      return item.keywords?.join(" ") ?? null;
    case "wantedReason":
      return item.wantedReason ?? null;
    case "currentQuality":
      return item.currentQuality ?? null;
    case "targetQuality":
      return item.targetQuality ?? null;
    case "type":
      return item.type;
    default:
      return null;
  }
}
