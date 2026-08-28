import { useEffect, useState } from "react";
import {
  conditionCount,
  fetchLibraryControls,
  type FilterCondition,
  type LibraryControlSet,
  type MediaVariant
} from "../lib/library-controls";
import { defaultDisplayOptions, parseDisplayOptions } from "../lib/library-filters";
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

const sizeStorageKey = (variant: MediaVariant) => `deluno-card-size-${variant}`;
const displayStorageKey = (variant: MediaVariant) => `deluno-display-options-${variant}`;

function initialCardSize(variant: MediaVariant): CardSize {
  try {
    const stored = localStorage.getItem(sizeStorageKey(variant));
    return stored === "sm" || stored === "lg" ? stored : "md";
  } catch {
    return "md";
  }
}

/**
 * What this shelf may be asked, ordered by and draw — declared per media kind by
 * the server (#324) and fetched here rather than declared beside it.
 *
 * Empty until it arrives, and deliberately not a hard-coded fallback: a fallback
 * is the second copy this whole change exists to delete, and it is the copy
 * nobody updates.
 */
const emptyControlSet = (variant: MediaVariant): LibraryControlSet => ({
  kind: variant,
  filterFields: [],
  sortFields: [],
  posterOptions: []
});

export function useLibraryFilters(variant: MediaVariant, urlFilter: string | null) {
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
  const [controlSet, setControlSet] = useState<LibraryControlSet>(() => emptyControlSet(variant));
  const [displayOptions, setDisplayOptions] = useState<DisplayOptions>({});
  const [conditions, setConditions] = useState<FilterCondition[]>([]);
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
    // Movies and TV do not share a quality tier list or a genre list, and a
    // 4K-Remux filter carried across to the TV shelf would silently empty it.
    // They do not share a *field* list either now, so a movie-only condition
    // would be refused by the server rather than ignored — either way it goes.
    setConditions([]);

    let cancelled = false;
    void fetchLibraryControls(variant).then((next) => {
      if (cancelled) return;
      setControlSet(next);
      // The stored layout is read against the declaration it belongs to, so a
      // switch added since somebody saved theirs arrives at its default rather
      // than as `undefined`.
      let raw: string | null = null;
      try { raw = localStorage.getItem(displayStorageKey(variant)); } catch { /* ignore */ }
      setDisplayOptions(parseDisplayOptions(raw, next.posterOptions));
      // A stored sort the server no longer performs falls back rather than
      // being sent and quietly normalised into something else.
      setSortField((current) =>
        next.sortFields.some((sort) => sort.id === current) ? current : next.sortFields[0]?.id ?? "title");
    });

    return () => { cancelled = true; };
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
    controlSet,
    conditions, setConditions,
    clearConditions: () => setConditions([]),
    sortDirection, setSortDirection, cardSize,
    displayOptions: Object.keys(displayOptions).length > 0
      ? displayOptions
      : defaultDisplayOptions(controlSet.posterOptions),
    setDisplayOptions: updateDisplayOptions,
    savedPresets, setSavedPresets, newPresetName, setNewPresetName, isSavingPreset,
    setIsSavingPreset, changeSize, updateDisplayOptions,
    // Every question being asked of the shelf, counted onto one badge. The
    // library and the quick filter are visible on screen; the conditions live
    // behind a button, and a narrowed shelf that looks unnarrowed is how people
    // lose half their library and conclude Deluno has.
    activeFilterCount:
      Number(libraryId !== null) +
      Number(quickFilter !== "all") +
      Number(monitoring !== "any") +
      // The finished conditions only. A row still waiting for a value is not
      // sent and narrows nothing; counting it would make the badge claim a
      // narrowing that is not happening, which is the same lie as leaving one
      // out — just in the other direction.
      conditionCount(conditions)
  };
}
