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
  Undo2,
  Zap,
} from "lucide-react";
import React, { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate, useNavigation } from "react-router-dom";
import type { MediaItem, MediaStatus } from "../../lib/media-types";
import {
  ApiRequestError,
  fetchJson,
  readValidationProblem,
  type CreateLibraryViewRequest,
  type LibraryItem,
  type LibraryViewItem,
  type MetadataProviderStatus,
  type MetadataSearchResult,
  type QualityProfileItem
} from "../../lib/api";
import { useDensity, type Density } from "../../lib/use-density";
import { authedFetch } from "../../lib/use-auth";
import { cn, formatBytesFromGb } from "../../lib/utils";
import { GlassTile, PageHero, StatChip } from "../shell/page-hero";
import { EmptyState } from "../shell/empty-state";
import { LibraryGridSkeleton } from "../shell/skeleton";
import { toast } from "../shell/toaster";
import { Badge } from "../ui/badge";
import { Button } from "../ui/button";
import { Input } from "../ui/input";

type Variant = "movies" | "shows";
type QuickFilter =
  | "all"
  | "monitored"
  | "unmonitored"
  | "downloaded"
  | "downloading"
  | "missing"
  | "wanted";
type ViewMode = "grid" | "list";
type SortField =
  | "title"
  | "year"
  | "rating"
  | "quality"
  | "added"
  | "size"
  | "status"
  | "bitrate"
  | "releaseGroup"
  | "codec"
  | "runtime"
  | "tmdbVotes"
  | "popularity"
  | "path";
type SortDirection = "asc" | "desc";
type CardSize = "sm" | "md" | "lg";
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
  rules: CustomFilterRule[];
}

interface DisplayOptions {
  showTitle: boolean;
  showMeta: boolean;
  showStatusPill: boolean;
  showQualityBadge: boolean;
  showRating: boolean;
}

type BulkWorkflowOperation =
  | "monitoring"
  | "quality"
  | "reassignLibrary"
  | "tags"
  | "search"
  | "renamePreview";

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
  { key: "downloading", label: "Downloading" },
  { key: "missing", label: "Missing" },
  { key: "wanted", label: "Wanted" }
];

const sortFieldOptions: Array<{ value: SortField; label: string }> = [
  { value: "title", label: "Title" },
  { value: "year", label: "Year" },
  { value: "rating", label: "Rating" },
  { value: "quality", label: "Quality" },
  { value: "added", label: "Added" },
  { value: "size", label: "Size" },
  { value: "status", label: "Status" },
  { value: "bitrate", label: "Bitrate" },
  { value: "releaseGroup", label: "Release group" },
  { value: "codec", label: "Codec" },
  { value: "runtime", label: "Runtime" },
  { value: "tmdbVotes", label: "TMDb votes" },
  { value: "popularity", label: "Popularity" },
  { value: "path", label: "Path" }
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
    { value: "monitored", label: "Monitored only" }
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
    { value: "Wanted", label: "Wanted" },
    { value: "Monitored", label: "Monitored" }
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
  items,
  isRouteLoading = false,
  metadataStatus,
  onReload,
  variant
}: {
  items: MediaItem[];
  isRouteLoading?: boolean;
  metadataStatus?: MetadataProviderStatus | null;
  onReload?: () => void;
  variant: Variant;
}) {
  const navigate = useNavigate();
  const navigation = useNavigation();
  const { density } = useDensity();
  const [libraryItems, setLibraryItems] = useState(items);
  const [query, setQuery] = useState("");
  const [quickFilter, setQuickFilter] = useState<QuickFilter>("all");
  const [view, setView] = useState<ViewMode>("grid");
  const [sortField, setSortField] = useState<SortField>("title");
  const [sortDirection, setSortDirection] = useState<SortDirection>("asc");
  const [cardSize, setCardSize] = useState<CardSize>(() => resolveInitialSize(variant));
  const [displayOptions, setDisplayOptions] = useState<DisplayOptions>(() => resolveInitialDisplayOptions(variant));
  const [customRules, setCustomRules] = useState<CustomFilterRule[]>([]);
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
  const [showCreate, setShowCreate] = useState(false);
  const [isCreating, setIsCreating] = useState(false);
  const [createForm, setCreateForm] = useState(() => createInitialForm());
  const [metadataResults, setMetadataResults] = useState<MetadataSearchResult[]>([]);
  const [isSearchingMetadata, setIsSearchingMetadata] = useState(false);

  useEffect(() => {
    setLibraryItems(items);
    setSelectedIds([]);
  }, [items]);

  useEffect(() => {
    setCreateForm(createInitialForm());
  }, [variant]);

  useEffect(() => {
    setSavedPresets([]);
    setCustomRules([]);
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
            quickFilter: (item.quickFilter as QuickFilter) || "all",
            sortField: (item.sortField as SortField) || "title",
            sortDirection: item.sortDirection === "desc" ? "desc" : "asc",
            viewMode: item.viewMode === "list" ? "list" : "grid",
            cardSize: item.cardSize === "sm" || item.cardSize === "lg" ? item.cardSize : "md",
            displayOptions: parseDisplayOptions(item.displayOptionsJson),
            rules: parseCustomRules(item.rulesJson)
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

  const filtered = useMemo(() => {
    const result = libraryItems.filter((item) => {
      const matchesQuery =
        [
          item.title,
          item.genres.join(" "),
          item.network ?? "",
          item.quality,
          item.wantedReason ?? "",
          item.releaseGroup ?? "",
          item.codec ?? "",
          item.audioCodec ?? "",
          item.audioChannels ?? "",
          (item.tags ?? []).join(" "),
          item.path ?? ""
        ]
          .join(" ")
          .toLowerCase()
          .includes(query.toLowerCase());

      const matchesQuick =
        quickFilter === "all" ||
        (quickFilter === "monitored" && item.monitored) ||
        (quickFilter === "unmonitored" && !item.monitored) ||
        (quickFilter === "downloaded" && item.status === "downloaded") ||
        (quickFilter === "downloading" && item.status === "downloading") ||
        (quickFilter === "missing" && item.status === "missing") ||
        (quickFilter === "wanted" && (item.status === "missing" || item.status === "downloading" || Boolean(item.wantedReason)));

      const matchesRules = customRules.every((rule) => matchesCustomRule(item, rule));

      return matchesQuery && matchesQuick && matchesRules;
    });

    return result.sort((left, right) => {
      const modifier = sortDirection === "asc" ? 1 : -1;
      switch (sortField) {
        case "year":
          return ((left.year ?? 0) - (right.year ?? 0)) * modifier;
        case "rating":
          return ((left.rating ?? 0) - (right.rating ?? 0)) * modifier;
        case "quality":
          return (left.quality ?? "").localeCompare(right.quality ?? "") * modifier;
        case "added":
          return left.added.localeCompare(right.added) * modifier;
        case "size":
          return ((left.sizeGb ?? 0) - (right.sizeGb ?? 0)) * modifier;
        case "status":
          return left.status.localeCompare(right.status) * modifier;
        case "bitrate":
          return ((left.bitrateMbps ?? 0) - (right.bitrateMbps ?? 0)) * modifier;
        case "releaseGroup":
          return (left.releaseGroup ?? "").localeCompare(right.releaseGroup ?? "") * modifier;
        case "codec":
          return (left.codec ?? "").localeCompare(right.codec ?? "") * modifier;
        case "runtime":
          return ((left.runtimeMinutes ?? 0) - (right.runtimeMinutes ?? 0)) * modifier;
        case "tmdbVotes":
          return ((left.tmdbVotes ?? 0) - (right.tmdbVotes ?? 0)) * modifier;
        case "popularity":
          return ((left.popularity ?? 0) - (right.popularity ?? 0)) * modifier;
        case "path":
          return (left.path ?? "").localeCompare(right.path ?? "") * modifier;
        default:
          return left.title.localeCompare(right.title) * modifier;
      }
    });
  }, [customRules, libraryItems, query, quickFilter, sortDirection, sortField]);

  const selectedCount = selectedIds.length;
  const monitoredCount = libraryItems.filter((item) => item.monitored).length;
  const missingCount = libraryItems.filter((item) => item.status === "missing").length;
  const downloadingCount = libraryItems.filter((item) => item.status === "downloading").length;
  const downloadedCount = libraryItems.filter((item) => item.status === "downloaded").length;
  const totalSizeTb = (
    libraryItems.reduce((sum, item) => sum + (item.sizeGb ?? 0), 0) / 1024
  ).toFixed(1);

  const label = variant === "movies" ? "movies" : "TV shows";
  const singular = variant === "movies" ? "movie" : "TV show";

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
                monitored,
                status:
                  item.status === "missing"
                    ? "missing"
                    : monitored
                      ? item.status === "monitored"
                        ? "downloaded"
                        : item.status
                      : "monitored"
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

  function addCustomRule() {
    setCustomRules((current) => [
      ...current,
      { id: crypto.randomUUID(), field: "title", comparator: "contains", value: "" }
    ]);
  }

  function updateCustomRule(ruleId: string, patch: Partial<CustomFilterRule>) {
    setCustomRules((current) =>
      current.map((rule) => (rule.id === ruleId ? { ...rule, ...patch } : rule))
    );
  }

  function removeCustomRule(ruleId: string) {
    setCustomRules((current) => current.filter((rule) => rule.id !== ruleId));
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
        rulesJson: JSON.stringify(customRules)
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
          quickFilter: (created.quickFilter as QuickFilter) || "all",
          sortField: (created.sortField as SortField) || "title",
          sortDirection: created.sortDirection === "desc" ? "desc" : "asc",
          viewMode: created.viewMode === "list" ? "list" : "grid",
          cardSize: created.cardSize === "sm" || created.cardSize === "lg" ? created.cardSize : "md",
          displayOptions: parseDisplayOptions(created.displayOptionsJson),
          rules: parseCustomRules(created.rulesJson)
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
    setCustomRules(preset.rules);
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

  const activeFilterCount =
    (quickFilter !== "all" ? 1 : 0) + customRules.filter((rule) => rule.value.trim()).length;

  async function handleMetadataSearch() {
    const searchTitle = createForm.title.trim();
    if (!searchTitle) {
      toast.info(`Type a ${singular} name first.`);
      return;
    }

    if (metadataStatus && !metadataStatus.isConfigured) {
      toast.warning("TMDb is not configured yet.", {
        description: "Add a TMDb API key in Settings > Metadata to enable live title lookup."
      });
      return;
    }

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
      setMetadataResults(results);
      if (results.length === 0) {
        toast.info(metadataStatus?.isConfigured === false ? "TMDb is not configured yet." : "No metadata matches found.");
      }
    } catch (error) {
      const message =
        error instanceof ApiRequestError
          ? error.message
          : "Metadata search failed.";
      toast.error(message);
    } finally {
      setIsSearchingMetadata(false);
    }
  }

  function applyMetadataResult(result: MetadataSearchResult) {
    setCreateForm((current) => ({
      ...current,
      title: result.title,
      year: result.year ? String(result.year) : current.year,
      imdbId: result.imdbId ?? current.imdbId,
      metadata: result
    }));
    toast.success(`Selected ${result.title}`);
  }

  async function handleCreate(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsCreating(true);
    try {
      const response = await authedFetch(variant === "movies" ? "/api/movies" : "/api/series", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          variant === "movies"
            ? {
                title: createForm.title,
                releaseYear: createForm.year ? Number(createForm.year) : null,
                imdbId: createForm.imdbId || null,
                monitored: createForm.monitored,
                ...metadataCreatePayload(createForm.metadata)
              }
            : {
                title: createForm.title,
                startYear: createForm.year ? Number(createForm.year) : null,
                imdbId: createForm.imdbId || null,
                monitored: createForm.monitored,
                ...metadataCreatePayload(createForm.metadata)
              }
        )
      });
      if (!response.ok) {
        const problem = await readValidationProblem(response);
        throw new Error(problem?.title ?? `Could not add ${singular}.`);
      }
      toast.success(variant === "movies" ? "Movie added" : "TV show added");
      setCreateForm(createInitialForm());
      setMetadataResults([]);
      setShowCreate(false);
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
      <section className="space-y-[var(--page-gap)]">
        <div className="relative overflow-hidden rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card dark:border-white/[0.06]">
          <span
            aria-hidden
            className="pointer-events-none absolute inset-x-5 top-0 h-px rounded-full"
            style={{ background: "linear-gradient(90deg, transparent, hsl(var(--primary)/0.45), hsl(var(--primary-2)/0.28), transparent)" }}
          />
          <span aria-hidden className="pointer-events-none absolute -right-20 -top-28 h-64 w-64 rounded-full bg-primary/10 blur-3xl" />
          <div className="relative flex flex-col gap-[var(--grid-gap)] lg:flex-row lg:items-center lg:justify-between">
            <div className="min-w-0">
              <p className="flex items-center gap-2 text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground">
                <Star className="h-3.5 w-3.5 text-primary" />
                {variant === "movies" ? "Movie library" : "TV library"}
              </p>
              <h2 className="mt-1 font-display text-[length:var(--type-title-md)] font-semibold tracking-tight text-foreground">
                Browse and manage your {label}
              </h2>
              <p className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-[length:var(--type-body-sm)] text-muted-foreground">
                <span><span className="tabular font-semibold text-foreground">{libraryItems.length.toLocaleString()}</span> total</span>
                <span className="text-muted-foreground/45">·</span>
                <span><span className="tabular font-semibold text-success">{downloadedCount}</span> downloaded</span>
                <span className="text-muted-foreground/45">·</span>
                <span><span className="tabular font-semibold text-primary">{monitoredCount}</span> monitored</span>
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
                <span className="text-muted-foreground/45">·</span>
                <span><span className="tabular font-semibold text-foreground">{totalSizeTb} TB</span> on disk</span>
              </p>
            </div>
            <div className="flex shrink-0 flex-wrap items-center gap-2">
              <Button className="gap-2" onClick={() => setShowCreate((current) => !current)}>
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
              <span className="font-semibold text-foreground">{libraryItems.length.toLocaleString()} total titles</span>
              {" · "}
              <span className="font-semibold text-success">{downloadedCount} downloaded</span>
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
            { label: "Total", value: libraryItems.length.toString(), tone: "neutral" },
            { label: "Monitored", value: monitoredCount.toString(), tone: "primary" },
            { label: "Missing", value: missingCount.toString(), tone: missingCount > 0 ? "warn" : "neutral" },
            { label: "Library", value: `${totalSizeTb}TB`, tone: "neutral" }
          ]}
          actions={
            <>
              <Button className="gap-2" onClick={() => setShowCreate((c) => !c)}>
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

        {/* ═══════ CREATE FORM (inline, expandable) ═══════ */}
        {showCreate ? (
          <GlassTile className="p-5">
            <div className="mb-4 rounded-xl border border-hairline bg-background/35 p-4">
              <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
                <div>
                  <div className="flex flex-wrap items-center gap-2">
                    <p className="text-sm font-semibold text-foreground">Metadata lookup</p>
                    <Badge variant={metadataStatus?.isConfigured ? "success" : "warning"}>
                      {metadataStatus?.isConfigured ? "TMDb live" : "TMDb setup needed"}
                    </Badge>
                    {createForm.metadata ? (
                      <Badge variant="info">
                        {createForm.metadata.provider.toUpperCase()} #{createForm.metadata.providerId}
                      </Badge>
                    ) : null}
                  </div>
                  <p className="text-xs text-muted-foreground">
                    Search TMDb, select the correct match, then Deluno stores poster, backdrop, overview, rating, genres, and external IDs with the title.
                  </p>
                  {metadataStatus && !metadataStatus.isConfigured ? (
                    <p className="mt-2 text-xs text-warning">
                      Metadata lookup is disabled until a TMDb API key is saved in Settings &gt; Metadata.
                    </p>
                  ) : null}
                </div>
                <Button
                  type="button"
                  variant="secondary"
                  onClick={() => void handleMetadataSearch()}
                  disabled={isSearchingMetadata || metadataStatus?.isConfigured === false}
                  className="gap-2"
                >
                  {isSearchingMetadata ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                  Search metadata
                </Button>
              </div>
              {metadataResults.length > 0 ? (
                  <div className="mt-3 grid gap-[calc(var(--grid-gap)*0.65)] md:grid-cols-3">
                  {metadataResults.slice(0, 6).map((result) => (
                    <button
                      key={`${result.provider}:${result.providerId}`}
                      type="button"
                      onClick={() => applyMetadataResult(result)}
                      className={cn(
                        "flex min-w-0 gap-3 rounded-xl border p-2 text-left transition hover:border-primary/35 hover:bg-primary/5",
                        createForm.metadata?.provider === result.provider && createForm.metadata.providerId === result.providerId
                          ? "border-primary/45 bg-primary/10"
                          : "border-hairline bg-surface-1"
                      )}
                    >
                      {result.posterUrl ? (
                        <img src={result.posterUrl} alt="" className="h-16 w-11 rounded-md object-cover" />
                      ) : (
                        <div className="h-16 w-11 rounded-md bg-muted" />
                      )}
                      <span className="min-w-0">
                        <span className="block truncate text-sm font-semibold text-foreground">{result.title}</span>
                        <span className="mt-0.5 block text-xs text-muted-foreground">
                          {result.year ?? "Unknown year"} · {result.provider.toUpperCase()}
                        </span>
                        {result.rating ? (
                          <span className="mt-1 block font-mono text-[11px] text-primary">{result.rating.toFixed(1)} rating</span>
                        ) : null}
                      </span>
                    </button>
                  ))}
                </div>
              ) : null}
            </div>
            <form
              className="grid gap-[calc(var(--grid-gap)*0.75)] md:grid-cols-[minmax(0,1fr)_140px_180px_auto]"
              onSubmit={handleCreate}
            >
              <Input
                value={createForm.title}
                onChange={(e) =>
                  setCreateForm((c) => ({ ...c, title: e.target.value }))
                }
                placeholder={variant === "movies" ? "Movie title" : "TV show title"}
              />
              <Input
                type="number"
                value={createForm.year}
                onChange={(e) =>
                  setCreateForm((c) => ({ ...c, year: e.target.value }))
                }
                placeholder={variant === "movies" ? "Year" : "Start year"}
              />
              <Input
                value={createForm.imdbId}
                onChange={(e) =>
                  setCreateForm((c) => ({ ...c, imdbId: e.target.value }))
                }
                placeholder="IMDb ID (optional)"
              />
              <div className="flex gap-2">
                <Button type="submit" disabled={isCreating} className="gap-1.5">
                  {isCreating ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                  Add
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  onClick={() => setShowCreate(false)}
                  disabled={isCreating}
                >
                  Cancel
                </Button>
              </div>
              <label className="md:col-span-4 inline-flex select-none items-center gap-2 text-[13px] text-muted-foreground">
                <input
                  type="checkbox"
                  checked={createForm.monitored}
                  onChange={(e) =>
                    setCreateForm((c) => ({ ...c, monitored: e.target.checked }))
                  }
                  className="accent-primary"
                />
                Start monitoring immediately
              </label>
            </form>
          </GlassTile>
        ) : null}

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
          libraryItems={libraryItems}
          customRules={customRules}
          addCustomRule={addCustomRule}
          updateCustomRule={updateCustomRule}
          removeCustomRule={removeCustomRule}
          savedPresets={savedPresets}
          newPresetName={newPresetName}
          setNewPresetName={setNewPresetName}
          isSavingPreset={isSavingPreset}
          saveCurrentPreset={saveCurrentPreset}
          applyPreset={applyPreset}
          deletePreset={deletePreset}
          activeFilterCount={activeFilterCount}
        />

        {/* ═══════ RESULT + SELECT ROW ═══════ */}
        <div className="flex items-center justify-between gap-3">
          {/* Left — count */}
          <p className="text-[length:var(--library-toolbar-size)] font-medium text-muted-foreground">
            {filtered.length === libraryItems.length
              ? <><span className="font-bold tabular text-foreground">{filtered.length}</span> {label}</>
              : <><span className="font-bold tabular text-foreground">{filtered.length}</span> of {libraryItems.length} {label}</>
            }
          </p>

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
              /* Deep glass panel */
              "border border-white/[0.1] dark:border-white/[0.08]",
              "bg-[hsl(226_24%_10%/0.97)] dark:bg-[hsl(226_24%_8%/0.98)]",
              "shadow-[0_24px_60px_hsl(0_0%_0%/0.45),0_8px_20px_hsl(0_0%_0%/0.3),inset_0_1px_0_hsl(0_0%_100%/0.06)]",
              "backdrop-blur-2xl"
            )}>
              {/* Count pill */}
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

              {/* Actions */}
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
                  label="Bulk tools"
                  icon={<FolderTree className="h-3.5 w-3.5" />}
                  onClick={() => openBulkTools("quality")}
                  disabled={isBulkUpdating}
                />
              </div>

              {/* Dismiss */}
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
                <Button onClick={() => setShowCreate(true)} className="gap-1.5">
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
                    setCustomRules([]);
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
            />
          </GlassTile>
        )}
      </section>

      {isBulkToolsOpen ? (
        <div className="fixed inset-0 z-[70] flex items-center justify-center bg-black/60 px-4 py-6 backdrop-blur-sm">
          <div className="w-full max-w-2xl space-y-4 rounded-2xl border border-hairline bg-card p-5 shadow-2xl">
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

            <div className="grid gap-4 md:grid-cols-2">
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
const INITIAL_BATCH = 60;
const BATCH_INCREMENT = 48;

function ProgressiveGrid({
  items,
  cardSize,
  density,
  displayOptions,
  selectedIds,
  keyBust,
  onSelect,
  onToggle
}: {
  items: MediaItem[];
  cardSize: CardSize;
  density: Density;
  displayOptions: DisplayOptions;
  selectedIds: string[];
  keyBust: string;
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
}) {
  const [visible, setVisible] = useState(() => Math.min(items.length, INITIAL_BATCH));
  const sentinelRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    setVisible(Math.min(items.length, INITIAL_BATCH));
  }, [keyBust, items.length]);

  useEffect(() => {
    if (visible >= items.length) return;
    const el = sentinelRef.current;
    if (!el) return;
    const io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            setVisible((prev) => Math.min(items.length, prev + BATCH_INCREMENT));
          }
        }
      },
      { rootMargin: "600px 0px" }
    );
    io.observe(el);
    return () => io.disconnect();
  }, [visible, items.length]);

  const slice = items.slice(0, visible);
  const remaining = items.length - visible;

  return (
    <>
      <div
        key={keyBust}
        className="stagger grid gap-[var(--library-grid-gap)] transition-[grid-template-columns] duration-200"
        style={{
          gridTemplateColumns: `repeat(auto-fill, minmax(${GRID_MIN_BY_DENSITY[density][cardSize]}, 1fr))`
        }}
      >
        {slice.map((item) => (
          <PosterCard
            key={item.id}
            item={item}
            size={cardSize}
            density={density}
            displayOptions={displayOptions}
            selected={selectedIds.includes(item.id)}
            onSelect={() => onSelect(item)}
            onToggle={() => onToggle(item.id)}
          />
        ))}
      </div>
      {remaining > 0 ? (
        <div
          ref={sentinelRef}
          className="flex items-center justify-center py-6 text-[length:var(--type-caption)] uppercase tracking-[0.18em] text-muted-foreground"
          role="status"
          aria-live="polite"
        >
          <LoaderCircle className="mr-2 h-3.5 w-3.5 animate-spin" />
          Loading {remaining} more
        </div>
      ) : null}
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

          {/* Top-right status pill — hidden on small for space */}
          {displayOptions.showStatusPill && size !== "sm" ? (
            <div className="absolute right-1.5 top-1.5 z-10">
              <StatusPill status={item.status} />
            </div>
          ) : displayOptions.showStatusPill ? (
            /* Compact status dot on small */
            <div className={cn(
              "absolute right-1.5 top-1.5 z-10 h-2 w-2 rounded-full ring-[1.5px] ring-black/40",
              item.status === "downloaded" ? "bg-success" :
              item.status === "downloading" ? "bg-primary" :
              item.status === "missing" ? "bg-warning" : "bg-muted-foreground"
            )} />
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
                {item.monitored ? (
                  <>
                    <span className="text-[hsl(var(--media-muted-foreground)/0.45)]">·</span>
                    <ShieldCheck className="h-3 w-3 text-primary" />
                  </>
                ) : null}
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
            {item.monitored ? (
              <>
                <span className="text-foreground/20">·</span>
                <ShieldCheck className="h-3 w-3 text-primary" />
              </>
            ) : null}
          </div>
        ) : null}
      </div>
    </div>
  );
}

function StatusPill({ status }: { status: MediaStatus }) {
  const config = {
    downloaded: { dot: "bg-success", label: "Ready", tone: "border-success/30 bg-success/15 text-success" },
    downloading: { dot: "bg-info", label: "DL", tone: "border-info/30 bg-info/15 text-info" },
    processing: { dot: "bg-primary", label: "Clean", tone: "border-primary/30 bg-primary/15 text-primary" },
    processed: { dot: "bg-success", label: "Cleaned", tone: "border-success/30 bg-success/15 text-success" },
    waitingForProcessor: { dot: "bg-warning", label: "Waiting", tone: "border-warning/30 bg-warning/15 text-warning" },
    importReady: { dot: "bg-success", label: "Import", tone: "border-success/30 bg-success/15 text-success" },
    importQueued: { dot: "bg-primary", label: "Queued", tone: "border-primary/30 bg-primary/15 text-primary" },
    importFailed: { dot: "bg-destructive", label: "Import failed", tone: "border-destructive/30 bg-destructive/15 text-destructive" },
    imported: { dot: "bg-success", label: "Imported", tone: "border-success/30 bg-success/15 text-success" },
    processingFailed: { dot: "bg-destructive", label: "Review", tone: "border-destructive/30 bg-destructive/15 text-destructive" },
    monitored: { dot: "bg-primary", label: "Monitor", tone: "border-primary/30 bg-primary/15 text-primary" },
    missing: { dot: "bg-destructive", label: "Missing", tone: "border-destructive/30 bg-destructive/15 text-destructive" }
  }[status];

  return (
    <div
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-1.5 py-0.5 text-[length:var(--library-badge-size)] font-bold uppercase tracking-wider backdrop-blur-md",
        config.tone
      )}
    >
      <span className={cn("h-1.5 w-1.5 rounded-full", config.dot, status === "downloading" && "animate-pulse")} />
      {config.label}
    </div>
  );
}

function StatusDot({ status }: { status: MediaStatus }) {
  const color = {
    downloaded: "bg-success",
    downloading: "bg-info animate-pulse",
    processing: "bg-primary animate-pulse",
    processed: "bg-success",
    waitingForProcessor: "bg-warning animate-pulse",
    importReady: "bg-success",
    importQueued: "bg-primary animate-pulse",
    importFailed: "bg-destructive",
    imported: "bg-success",
    processingFailed: "bg-destructive",
    monitored: "bg-primary",
    missing: "bg-destructive"
  }[status];
  return <span className={cn("h-2 w-2 shrink-0 rounded-full", color)} />;
}

function StatusBadge({ status }: { status: MediaStatus }) {
  const variant = {
    downloaded: "success" as const,
    downloading: "info" as const,
    processing: "default" as const,
    processed: "success" as const,
    waitingForProcessor: "warning" as const,
    importReady: "success" as const,
    importQueued: "default" as const,
    importFailed: "destructive" as const,
    imported: "success" as const,
    processingFailed: "destructive" as const,
    monitored: "default" as const,
    missing: "destructive" as const
  }[status];
  const label = {
    downloaded: "Ready",
    downloading: "Downloading",
    processing: "Processing",
    processed: "Processed",
    waitingForProcessor: "Waiting for processor",
    importReady: "Import ready",
    importQueued: "Import queued",
    importFailed: "Import failed",
    imported: "Imported",
    processingFailed: "Processing failed",
    monitored: "Monitored",
    missing: "Missing"
  }[status];
  return <Badge variant={variant}>{label}</Badge>;
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

function createInitialForm() {
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

/* Premium bulk action button inside the floating command bar */
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
  variant?: "ghost" | "primary";
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={cn(
        "flex min-h-[var(--library-toolbar-height)] items-center gap-1.5 rounded-xl px-3 text-[length:var(--library-toolbar-size)] font-medium transition-all duration-150 select-none",
        "disabled:opacity-40 disabled:cursor-not-allowed",
        variant === "primary"
          ? [
              "bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] text-primary-foreground",
              "shadow-[0_2px_8px_hsl(var(--primary-deep)/0.4),inset_0_1px_0_hsl(0_0%_100%/0.15)]",
              "hover:brightness-110 active:scale-95"
            ].join(" ")
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
  someSelected
}: {
  items: MediaItem[];
  selectedIds: string[];
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onToggleAll: () => void;
  allSelected: boolean;
  someSelected: boolean;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const tableRef = useRef<HTMLTableElement>(null);
  const [focusIndex, setFocusIndex] = useState(0);

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
          {items.map((item, index) => {
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
                        {item.monitored ? " · Monitored" : ""}
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
  libraryItems,
  customRules,
  addCustomRule,
  updateCustomRule,
  removeCustomRule,
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
  libraryItems: MediaItem[];
  customRules: CustomFilterRule[];
  addCustomRule: () => void;
  updateCustomRule: (ruleId: string, patch: Partial<CustomFilterRule>) => void;
  removeCustomRule: (ruleId: string) => void;
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
    all: libraryItems.length,
    monitored: libraryItems.filter((item) => item.monitored).length,
    unmonitored: libraryItems.filter((item) => !item.monitored).length,
    downloaded: libraryItems.filter((item) => item.status === "downloaded").length,
    downloading: libraryItems.filter((item) => item.status === "downloading").length,
    missing: libraryItems.filter((item) => item.status === "missing").length,
    wanted: libraryItems.filter((item) => item.status === "missing" || item.status === "downloading" || Boolean(item.wantedReason)).length
  };

  return (
    <div className="sticky top-[var(--topbar-height-mobile)] z-20 py-3 lg:top-topbar">
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

        <div className="px-4 py-3">
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
              label="View"
              icon={LayoutTemplate}
              active={openPanel === "view"}
              meta={view === "grid" ? cardSize.toUpperCase() : "LIST"}
              onClick={() => setOpenPanel((current) => current === "view" ? null : "view")}
            />
            <ToolbarMenuButton
              label="Sort"
              icon={ArrowUpDown}
              active={openPanel === "sort"}
              meta={`${sortFieldOptions.find((option) => option.value === sortField)?.label ?? "Title"} ${sortDirection === "asc" ? "↑" : "↓"}`}
              onClick={() => setOpenPanel((current) => current === "sort" ? null : "sort")}
            />
            <ToolbarMenuButton
              label="Filter"
              icon={Filter}
              active={openPanel === "filter"}
              meta={activeFilterCount > 0 ? `${activeFilterCount} active` : "Quick + custom"}
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
            <div className="mt-3 grid gap-[var(--grid-gap)] rounded-2xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)] xl:grid-cols-[minmax(0,1fr)_minmax(320px,0.8fr)] 2xl:grid-cols-[minmax(0,1.2fr)_minmax(360px,0.7fr)]">
              <div className="space-y-[calc(var(--field-group-pad)*0.75)]">
                <SectionLabel>Display mode</SectionLabel>
                <div className="flex flex-wrap items-center gap-2">
                  {([
                    { mode: "grid" as ViewMode, icon: LayoutGrid, label: "Grid" },
                    { mode: "list" as ViewMode, icon: List, label: "List" }
                  ]).map(({ mode, icon: Icon, label: itemLabel }) => (
                    <Button key={mode} type="button" size="sm" variant={view === mode ? "default" : "outline"} onClick={() => setView(mode)}>
                      <Icon className="h-4 w-4" />
                      {itemLabel}
                    </Button>
                  ))}
                </div>

                {view === "grid" ? (
                  <div className="space-y-2">
                    <SectionLabel>Poster size</SectionLabel>
                    <div className="flex flex-wrap gap-2">
                      {(["sm", "md", "lg"] as CardSize[]).map((size) => (
                        <Button key={size} type="button" size="sm" variant={cardSize === size ? "default" : "outline"} onClick={() => changeSize(size)}>
                          {size === "sm" ? "Small" : size === "md" ? "Medium" : "Large"}
                        </Button>
                      ))}
                    </div>
                  </div>
                ) : null}
              </div>

              <div className="space-y-3">
                <SectionLabel>Poster information</SectionLabel>
                <DisplayToggle label="Show title" checked={displayOptions.showTitle} onChange={(checked) => setDisplayOptions({ ...displayOptions, showTitle: checked })} />
                <DisplayToggle label="Show year and monitored state" checked={displayOptions.showMeta} onChange={(checked) => setDisplayOptions({ ...displayOptions, showMeta: checked })} />
                <DisplayToggle label="Show status badge" checked={displayOptions.showStatusPill} onChange={(checked) => setDisplayOptions({ ...displayOptions, showStatusPill: checked })} />
                <DisplayToggle label="Show quality badge" checked={displayOptions.showQualityBadge} onChange={(checked) => setDisplayOptions({ ...displayOptions, showQualityBadge: checked })} />
                <DisplayToggle label="Show rating" checked={displayOptions.showRating} onChange={(checked) => setDisplayOptions({ ...displayOptions, showRating: checked })} />
              </div>
            </div>
          ) : null}

          {openPanel === "sort" ? (
            <div className="mt-3 grid gap-[var(--grid-gap)] rounded-2xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)] sm:grid-cols-2">
              <div className="space-y-2">
                <SectionLabel>Sort field</SectionLabel>
                <select
                  value={sortField}
                  onChange={(event) => setSortField(event.target.value as SortField)}
                  className="density-control-text h-[var(--control-height)] w-full rounded-[10px] border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                >
                  {sortFieldOptions.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
              </div>
              <div className="space-y-2">
                <SectionLabel>Direction</SectionLabel>
                <div className="flex gap-2">
                  <Button type="button" size="sm" variant={sortDirection === "asc" ? "default" : "outline"} onClick={() => setSortDirection("asc")}>
                    <ArrowDownAZ className="h-4 w-4" />
                    Ascending
                  </Button>
                  <Button type="button" size="sm" variant={sortDirection === "desc" ? "default" : "outline"} onClick={() => setSortDirection("desc")}>
                    <ArrowUpDown className="h-4 w-4" />
                    Descending
                  </Button>
                </div>
              </div>
            </div>
          ) : null}

          {openPanel === "filter" ? (
            <div className="mt-3 space-y-[calc(var(--field-group-pad)*0.8)] rounded-2xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.8)]">
              <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1fr)_minmax(360px,0.34fr)]">
                <div className="space-y-3">
                  <div className="flex items-center justify-between gap-3">
                    <SectionLabel>Custom rules</SectionLabel>
                    <Button type="button" size="sm" variant="outline" onClick={addCustomRule}>
                      <Plus className="h-4 w-4" />
                      Add rule
                    </Button>
                  </div>
                  {customRules.length === 0 ? (
                    <div className="rounded-xl border border-dashed border-hairline bg-background/40 px-4 py-4 text-sm text-muted-foreground">
                      Build filters around status, monitoring, quality, genre, rating, bitrate, release group, tags, certifications, provider ratings, path, studio, language, and more.
                    </div>
                  ) : (
                    <div className="space-y-3">
                      {customRules.map((rule) => {
                        const fieldMeta = filterFieldOptions.find((option) => option.value === rule.field) ?? filterFieldOptions[0];
                        return (
                          <div key={rule.id} className="grid gap-2 rounded-xl border border-hairline bg-background/40 p-3 md:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)_minmax(0,1.1fr)_auto]">
                            <select
                              value={rule.field}
                              onChange={(event) => updateCustomRule(rule.id, { field: event.target.value as FilterField, comparator: defaultComparatorForField(event.target.value as FilterField), value: "" })}
                              className="density-control-text h-[var(--control-height-sm)] rounded-[10px] border border-hairline bg-surface-2 px-3 text-foreground outline-none"
                            >
                              {filterFieldOptions.map((option) => (
                                <option key={option.value} value={option.value}>
                                  {option.label}
                                </option>
                              ))}
                            </select>

                            <select
                              value={rule.comparator}
                              onChange={(event) => updateCustomRule(rule.id, { comparator: event.target.value as FilterComparator })}
                              className="density-control-text h-[var(--control-height-sm)] rounded-[10px] border border-hairline bg-surface-2 px-3 text-foreground outline-none"
                            >
                              {comparatorsForField(rule.field).map((comparator) => (
                                <option key={comparator} value={comparator}>
                                  {friendlyComparatorLabel(comparator)}
                                </option>
                              ))}
                            </select>

                            {fieldMeta.kind === "enum" ? (
                              <select
                                value={rule.value}
                                onChange={(event) => updateCustomRule(rule.id, { value: event.target.value })}
                                className="density-control-text h-[var(--control-height-sm)] rounded-[10px] border border-hairline bg-surface-2 px-3 text-foreground outline-none"
                              >
                                <option value="">Choose value</option>
                                {(enumOptions[rule.field] ?? []).map((option) => (
                                  <option key={option.value} value={option.value}>
                                    {option.label}
                                  </option>
                                ))}
                              </select>
                            ) : (
                              <Input
                                type={fieldMeta.kind === "number" ? "number" : "text"}
                                value={rule.value}
                                onChange={(event) => updateCustomRule(rule.id, { value: event.target.value })}
                                placeholder={placeholderForField(rule.field)}
                                className="h-[var(--control-height-sm)]"
                              />
                            )}

                            <Button type="button" size="sm" variant="ghost" onClick={() => removeCustomRule(rule.id)}>
                              Remove
                            </Button>
                          </div>
                        );
                      })}
                    </div>
                  )}
                </div>

                <div className="space-y-[calc(var(--field-group-pad)*0.8)]">
                  <div className="space-y-2">
                    <SectionLabel>Saved filters</SectionLabel>
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
                              {preset.rules.length} custom rule{preset.rules.length === 1 ? "" : "s"}
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
                      Save complex filters here so users can jump straight into slices like anime 4K, kids missing, language-specific upgrades, or any other smart view.
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
        "inline-flex min-h-[var(--library-toolbar-height)] items-center gap-2 rounded-xl px-3 text-[length:var(--library-toolbar-size)] font-medium transition-all",
        active
          ? "bg-primary/10 text-primary ring-1 ring-inset ring-primary/25"
          : "bg-foreground/[0.04] text-foreground ring-1 ring-inset ring-hairline/60 hover:bg-foreground/[0.06] dark:bg-white/[0.05] dark:ring-white/[0.06]"
      )}
    >
      <Icon className="h-3.5 w-3.5" />
      <span>{label}</span>
      <span className="hidden text-[length:var(--type-caption)] text-muted-foreground sm:inline">{meta}</span>
      <ChevronDown className={cn("h-3.5 w-3.5 transition-transform", active && "rotate-180")} />
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
  checked,
  onChange
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="flex items-center gap-3 rounded-xl border border-hairline bg-background/40 px-3 py-3 text-sm text-foreground">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      {label}
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
