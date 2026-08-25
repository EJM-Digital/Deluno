import { useEffect, useState } from "react";
import { defaultDisplayOptions } from "../lib/library-filters";
import {
  isQuickFilter,
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
  const [view, setView] = useState<ViewMode>("grid");
  const [sortField, setSortField] = useState<SortField>("title");
  const [sortDirection, setSortDirection] = useState<SortDirection>("asc");
  const [cardSize, setCardSize] = useState<CardSize>(() => initialCardSize(variant));
  const [displayOptions, setDisplayOptions] = useState<DisplayOptions>(() => initialDisplayOptions(variant));
  const [savedPresets, setSavedPresets] = useState<SavedFilterPreset[]>([]);
  const [newPresetName, setNewPresetName] = useState("");
  const [isSavingPreset, setIsSavingPreset] = useState(false);

  useEffect(() => {
    setSavedPresets([]);
    setLibraryId(null);
    setQuickFilter("all");
    setSortField("title");
    setSortDirection("asc");
    setCardSize(initialCardSize(variant));
    setDisplayOptions(initialDisplayOptions(variant));
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
    sortDirection, setSortDirection, cardSize, displayOptions, setDisplayOptions,
    savedPresets, setSavedPresets, newPresetName, setNewPresetName, isSavingPreset,
    setIsSavingPreset, changeSize, updateDisplayOptions,
    activeFilterCount: Number(libraryId !== null) + Number(quickFilter !== "all")
  };
}
