import type { MediaItem } from "../../lib/media-types";
import { Plus } from "lucide-react";
import type { Density } from "../../lib/use-density";
import type { CardSize, DisplayOptions } from "./library-grid";
import { ProgressiveGrid } from "./library-grid";
import { LibraryTable } from "./library-table";
import { EmptyState } from "../shell/empty-state";
import { GlassTile } from "../shell/page-hero";
import { LibraryGridSkeleton } from "../shell/skeleton";
import { Button } from "../ui/button";

type LibraryResultsProps = {
  isLoading: boolean;
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
  isLoadingMore: boolean;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
  onOpenCreate: () => void;
  onClearFilters: () => void;
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onToggleAll: () => void;
  onPreviousPage: () => void;
  onNextPage: () => void;
};

export function LibraryResults({
  isLoading, hasLoadedOnce, items, label, singular, libraryCount, hasActiveFilter, view, cardSize, density, displayOptions, selectedIds,
  keyBust, isLoadingMore, hasPreviousPage, hasNextPage, onOpenCreate,
  onClearFilters, onSelect, onToggle, onToggleAll, onPreviousPage, onNextPage,
}: LibraryResultsProps) {
  return <>
    {(isLoading || !hasLoadedOnce) && items.length === 0 ? (
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
      <ProgressiveGrid items={items} cardSize={cardSize} density={density} displayOptions={displayOptions} selectedIds={selectedIds} keyBust={keyBust} onSelect={onSelect} onToggle={onToggle} onEndReached={() => undefined} />
    ) : (
      <GlassTile className="p-0"><LibraryTable items={items} selectedIds={selectedIds} onSelect={onSelect} onToggle={onToggle} onToggleAll={onToggleAll} allSelected={items.length > 0 && items.every((item) => selectedIds.includes(item.id))} someSelected={selectedIds.length > 0 && !items.every((item) => selectedIds.includes(item.id))} onEndReached={() => undefined} /></GlassTile>
    )}
    {items.length > 0 && (hasPreviousPage || hasNextPage) ? (
      <div className="flex items-center justify-between gap-3 border-t border-hairline pt-3">
        <Button type="button" variant="outline" disabled={!hasPreviousPage || isLoadingMore} onClick={onPreviousPage}>Previous 100</Button>
        <p className="text-sm text-muted-foreground">Only this page is kept in memory.</p>
        <Button type="button" variant="outline" disabled={!hasNextPage || isLoadingMore} onClick={onNextPage}>Next 100</Button>
      </div>
    ) : null}
  </>;
}
