import { useEffect, useState } from "react";
import { customFilterCount, defaultDisplayOptions, emptyCustomFilters, type CustomFilters } from "../lib/library-filters";
import {
  isQuickFilter,
  type MonitoringFilter,
  type QuickFilter,
  type SavedFilterPreset,
  type SortDirection,
  type SortField,
  type ViewMode
} from "../components/app/library-control-rail";
import type { CardSize, DisplayOptions } from "../components/app/library-grid";

type LibraryVariant = "movies" | "shows";

const sizeStorageKey = (variant: LibraryVariant) => `deluno-card-size-${variant}`;
const displayStorageKey = (variant: LibraryVariant) => `deluno-display-options-${variant}`;

function initialCardSize(variant: LibraryVariant): CardSize {
  try {
    const stored = localStorage.getItem(sizeStorageKey(variant));
    return stored === "sm" || stored === "lg" ? stored : "md";
  } catch {
    return "md";
  }
}

function initialDisplayOptions(variant: LibraryVariant): DisplayOptions {
  try {
    const raw = localStorage.getItem(displayStorageKey(variant));
    return raw ? { ...defaultDisplayOptions(), ...JSON.parse(raw) } : defaultDisplayOptions();
  } catch {
    return defaultDisplayOptions();
  }
}

export function useLibraryFilters(variant: LibraryVariant, urlFilter: string | null) {
  const [query, setQuery] = useState("");
  const [libraryId, setLibraryId] = useState<string | null>(null);
  const [quickFilter, setQuickFilter] = useState<QuickFilter>("all");
  // The other axis. See `MonitoringFilter` — a state and an intent multiply,
  // so neither can be expressed as a value of the other.
  const [monitoring, setMonitoring] = useState<MonitoringFilter>("any");
  const [view, setView] = useState<ViewMode>("grid");
  const [sortField, setSortField] = useState<SortField>("title");
  const [sortDirection, setSortDirection] = useState<SortDirection>("asc");
  const [cardSize, setCardSize] = useState<CardSize>(() => initialCardSize(variant));
  const [displayOptions, setDisplayOptions] = useState<DisplayOptions>(() => initialDisplayOptions(variant));
  const [customFilters, setCustomFilters] = useState<CustomFilters>(() => emptyCustomFilters());
  const [savedPresets, setSavedPresets] = useState<SavedFilterPreset[]>([]);
  const [newPresetName, setNewPresetName] = useState("");
  const [isSavingPreset, setIsSavingPreset] = useState(false);

  useEffect(() => {
    setSavedPresets([]);
    setLibraryId(null);
    setQuickFilter("all");
    setMonitoring("any");
    setSortField("title");
    setSortDirection("asc");
    setCardSize(initialCardSize(variant));
    setDisplayOptions(initialDisplayOptions(variant));
    // Movies and TV do not share a quality tier list or a genre list, and a
    // 4K-Remux filter carried across to the TV shelf would silently empty it.
    setCustomFilters(emptyCustomFilters());
  }, [variant]);

  useEffect(() => {
    if (isQuickFilter(urlFilter)) {
      setQuickFilter(urlFilter);
    }
  }, [urlFilter]);

  function changeSize(size: CardSize) {
    setCardSize(size);
    try { localStorage.setItem(sizeStorageKey(variant), size); } catch { /* ignore */ }
  }

  function updateDisplayOptions(options: DisplayOptions) {
    setDisplayOptions(options);
    try { localStorage.setItem(displayStorageKey(variant), JSON.stringify(options)); } catch { /* ignore */ }
  }

  return {
    query, setQuery, libraryId, setLibraryId, quickFilter, setQuickFilter, view, setView, sortField, setSortField,
    monitoring, setMonitoring,
    customFilters, setCustomFilters,
    clearCustomFilters: () => setCustomFilters(emptyCustomFilters()),
    sortDirection, setSortDirection, cardSize, displayOptions, setDisplayOptions,
    savedPresets, setSavedPresets, newPresetName, setNewPresetName, isSavingPreset,
    setIsSavingPreset, changeSize, updateDisplayOptions,
    // Every question being asked of the shelf, counted onto one badge. The
    // library and the quick filter are visible on screen; the custom ones live
    // behind a button, and a narrowed shelf that looks unnarrowed is how people
    // lose half their library and conclude Deluno has.
    activeFilterCount:
      Number(libraryId !== null) +
      Number(quickFilter !== "all") +
      Number(monitoring !== "any") +
      customFilterCount(customFilters)
  };
}
