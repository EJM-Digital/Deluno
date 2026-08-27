import { Plus, RefreshCw, Search } from "lucide-react";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";

/**
 * What you can do about the titles in front of you.
 *
 * This was a band of its own — `LibrarySummaryHeader` — carrying these buttons
 * above a line of counts that repeated the chips a few pixels below. The chips
 * kept the numbers and gained the colours; the buttons moved into that row.
 *
 * **Search acts on what is on screen, and nothing else.** It used to be
 * "Hunt N missing", which asked a different question from the one the shelf was
 * showing: it built its own query — library and sort only — so with a search
 * typed or a genre picked it said "Hunt 5 missing" and hunted ten. James, on
 * seeing that: *"the way radarr does it is whatever is shown is what can be
 * searched and I think we need to do the same, if we create a filter for
 * something specific we can only search that specific on screen."*
 *
 * That is the model now, and it is also why the mismatch cannot come back:
 * there is no second query to disagree with the first. The button searches the
 * rows the grid is rendering, so narrowing the shelf *is* choosing what to
 * search — Missing, one genre, under 5 GB, whatever the row above selects.
 */
export function LibraryActions({
  label,
  singular,
  shownCount,
  onToggleCreate,
  isUpdatingMetadata,
  onUpdateMetadata,
  isSearchingShown = false,
  onSearchShown
}: {
  label: string;
  singular: string;
  /** How many titles the grid is actually rendering — the same number the count line reads. */
  shownCount: number;
  onToggleCreate: () => void;
  isUpdatingMetadata: boolean;
  onUpdateMetadata: () => void;
  isSearchingShown?: boolean;
  onSearchShown?: () => void;
}) {
  return (
    <>
      <Button size="sm" className="gap-2" onClick={onToggleCreate}>
        <Plus className="h-4 w-4" strokeWidth={2.5} />
        Add {singular}
      </Button>
      {shownCount > 0 ? (
        <Button
          type="button"
          size="sm"
          variant="secondary"
          className="gap-2"
          onClick={onSearchShown}
          disabled={isSearchingShown || !onSearchShown}
          title={
            shownCount === 1
              ? `Search now for the ${singular.toLowerCase()} on screen`
              : `Search now for all ${shownCount} ${label} on screen. Narrow the shelf first to search fewer.`
          }
        >
          <Search className={cn("h-4 w-4", isSearchingShown && "animate-pulse")} />
          {isSearchingShown ? "Searching…" : `Search these ${shownCount}`}
        </Button>
      ) : null}
      <Button
        type="button"
        variant="outline"
        size="icon"
        onClick={onUpdateMetadata}
        disabled={isUpdatingMetadata}
        // The accessible name stays the action itself; the sentence explaining
        // it is the tooltip.
        aria-label="Update all metadata"
        title={`Queue a metadata refresh for every ${singular.toLowerCase()} in Deluno`}
      >
        <RefreshCw className={cn("h-4 w-4", isUpdatingMetadata && "animate-spin")} />
      </Button>
    </>
  );
}
