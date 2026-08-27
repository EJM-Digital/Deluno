import type { MediaItem } from "../../lib/media-types";
import { Plus } from "lucide-react";
import type { Density } from "../../lib/use-density";
import type { CardSize, DisplayOptions } from "./library-grid";
import type { SortField } from "../../lib/library-filters";
import { ProgressiveGrid } from "./library-grid";
import { LibraryTable } from "./library-table";
import { EmptyState } from "../shell/empty-state";
import { GlassTile } from "../shell/page-hero";
import { LibraryGridSkeleton } from "../shell/skeleton";
import { Button } from "../ui/button";

type LibraryResultsProps = {
  /**
   * Whether a load for the current query has ever completed. "Empty" is a
   * conclusion, not a default: without this the view announced an empty library
   * the moment `isLoading` blinked false between two fetches, which is both a
   * flash of the wrong answer and a lie while a request is still in flight.
   */
  hasLoadedOnce: boolean;
  items: MediaItem[];
  label: string;
  singular: string;
  libraryCount: number;
  hasActiveFilter: boolean;
  view: "grid" | "list";
  cardSize: CardSize;
  density: Density;
  displayOptions: DisplayOptions;
  selectedIds: string[];
  keyBust: string;
  sortField: SortField;
  sortDirection: "asc" | "desc";
  /** Whether the whole matching library has arrived, which is what makes an empty rail stop inert. */
  isComplete: boolean;
  onOpenCreate: () => void;
  onClearFilters: () => void;
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onToggleAll: () => void;
  /** The shelf has reached its end; fetch the next slice of the library behind it. */
  onEndReached: () => void;
};

export function LibraryResults({
  hasLoadedOnce, items, label, singular, libraryCount, hasActiveFilter, view, cardSize, density, displayOptions, selectedIds,
  keyBust, sortField, sortDirection, isComplete, onOpenCreate,
  onClearFilters, onSelect, onToggle, onToggleAll, onEndReached,
}: LibraryResultsProps) {
  return <>
    {/*
      The skeleton means "we do not know yet", not "we are refreshing". Gating
      it on `isLoading` meant every navigation to an empty library flashed
      twenty placeholder posters, because `isLoading` includes route transitions
      and an empty library is permanently `items.length === 0` — so Deluno
      promised content it already knew was not there, every single visit.

      Once a load has completed for this query, empty is a known fact: show it
      and keep showing it. A refetch that finds items replaces the empty state
      without a placeholder flash in between.
    */}
    {!hasLoadedOnce && items.length === 0 ? (
      <GlassTile className="p-[var(--tile-pad)]"><LibraryGridSkeleton count={20} /></GlassTile>
    ) : items.length === 0 && hasActiveFilter ? (
      <EmptyState
        variant="search"
        title="Nothing matches"
        description={`Try clearing filters or broadening your search. Your library has ${libraryCount} total title${libraryCount === 1 ? "" : "s"}.`}
        action={<Button variant="secondary" onClick={onClearFilters}>Clear filters</Button>}
      />
    ) : items.length === 0 ? (
      <EmptyState
        variant="library"
        title={`Your ${label} library is empty`}
        description={`Add your first ${singular} to start monitoring releases, running search, and building out your collection.`}
        action={<Button onClick={onOpenCreate} className="gap-1.5"><Plus className="h-4 w-4" strokeWidth={2.5} />Add {singular}</Button>}
        learnMore={`Deluno will track up to 100,000 ${label} without breaking a sweat.`}
      />
    ) : view === "grid" ? (
      <ProgressiveGrid items={items} cardSize={cardSize} density={density} displayOptions={displayOptions} selectedIds={selectedIds} keyBust={keyBust} sortField={sortField} sortDirection={sortDirection} isComplete={isComplete} onSelect={onSelect} onToggle={onToggle} onEndReached={onEndReached} />
    ) : (
      <GlassTile className="p-0"><LibraryTable items={items} selectedIds={selectedIds} onSelect={onSelect} onToggle={onToggle} onToggleAll={onToggleAll} allSelected={items.length > 0 && items.every((item) => selectedIds.includes(item.id))} someSelected={selectedIds.length > 0 && !items.every((item) => selectedIds.includes(item.id))} sortField={sortField} sortDirection={sortDirection} isComplete={isComplete} onEndReached={onEndReached} /></GlassTile>
    )}
    {/*
      `Previous 100` / `Next 100` and the line "Only this page is kept in
      memory" stood here. That line was an implementation detail presented as a
      feature, and the buttons were the feature it was defending: thirty clicks
      to reach title 3,000 of 6,000, and Ctrl+F finding one page of a library.
      The shelf is now the whole library, so there is nothing left to page.
    */}
  </>;
}
