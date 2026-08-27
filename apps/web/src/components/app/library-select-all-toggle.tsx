import { cn } from "../../lib/utils";

type LibrarySelectAllToggleProps = {
  totalCount: number;
  loadedCount: number;
  /** The shelf is still filling behind the reader, so the number below it is not final yet. */
  isLoadingMore: boolean;
  filteredCount: number;
  selectedCount: number;
  allVisibleSelected: boolean;
  onToggle: () => void;
  view: "grid" | "list";
};

export function LibrarySelectAllToggle({
  totalCount,
  loadedCount,
  isLoadingMore,
  filteredCount,
  selectedCount,
  allVisibleSelected,
  onToggle,
  view,
}: LibrarySelectAllToggleProps) {
  if (loadedCount === 0 || (view === "list" && totalCount <= loadedCount)) return null;

  return (
    <div className="flex items-center justify-between gap-3">
      {/*
        The shelf is one continuous list now, so this counts what is on it and
        says plainly when the rest is still on its way. It used to be the
        caption for a hundred-title page — "Showing 100 loaded of 6,000" — which
        was true and told a reader nothing they could act on.
      */}
      <p className="text-[length:var(--library-toolbar-size)] font-medium text-muted-foreground">
        {isLoadingMore && totalCount > loadedCount ? (
          <>Showing <span className="font-bold tabular text-foreground">{filteredCount.toLocaleString()}</span> of {totalCount.toLocaleString()} <span className="animate-pulse">— still loading</span></>
        ) : totalCount > loadedCount ? (
          <>Showing <span className="font-bold tabular text-foreground">{filteredCount.toLocaleString()}</span> of {totalCount.toLocaleString()}</>
        ) : (
          <><span className="font-bold tabular text-foreground">{filteredCount.toLocaleString()}</span> {filteredCount === 1 ? "title" : "titles"} shown</>
        )}
      </p>
      <button
        type="button"
        onClick={onToggle}
        className={cn(
          "group flex min-h-[var(--library-toolbar-height)] items-center gap-2 rounded-xl px-3 py-1.5 text-[length:var(--library-toolbar-size)] font-medium transition-all duration-200 select-none",
          selectedCount > 0
            ? "bg-primary/10 text-primary ring-1 ring-inset ring-primary/20 hover:bg-primary/15"
            : "text-muted-foreground hover:bg-muted/60 hover:text-foreground dark:hover:bg-white/[0.05]"
        )}
      >
        <span className={cn(
          "flex h-4 w-4 shrink-0 items-center justify-center rounded-[4px] border transition-all duration-200",
          allVisibleSelected
            ? "border-primary bg-primary text-primary-foreground shadow-[0_0_8px_hsl(var(--primary)/0.5)]"
            : selectedCount > 0
              ? "border-primary/60 bg-primary/15"
              : "border-hairline bg-background group-hover:border-primary/40 dark:bg-white/[0.04]"
        )}>
          {allVisibleSelected ? (
            <svg width="9" height="7" viewBox="0 0 9 7" fill="none">
              <path d="M1 3.5L3.5 6L8 1" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round"/>
            </svg>
          ) : selectedCount > 0 ? <span className="h-0.5 w-2 rounded-full bg-primary" /> : null}
        </span>
        {selectedCount > 0 ? `${selectedCount} selected` : "Select all shown"}
      </button>
    </div>
  );
}
