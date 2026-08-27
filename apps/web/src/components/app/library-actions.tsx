import { Plus, RefreshCw, Zap } from "lucide-react";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";

/**
 * The two things you can do about what the filter row shows, plus one
 * maintenance action.
 *
 * This was a band of its own — `LibrarySummaryHeader` — carrying these buttons
 * above a line of counts. The counts were Missing, Monitored, Unmonitored and
 * Upgradable, every one of which is a chip a few pixels below with the same
 * number on it, so the screen stated the same four facts twice: once as
 * something you could click and once as something you could not. The chips kept
 * the numbers and gained the colours; the buttons moved into that row; the band
 * went.
 *
 * Two labelled actions at most, per the page grammar: the primary, and Hunt when
 * there is something to hunt. Refreshing metadata is rare maintenance, so it
 * stays an icon.
 */
export function LibraryActions({
  label,
  singular,
  missingCount,
  onToggleCreate,
  isUpdatingMetadata,
  onUpdateMetadata,
  isHuntingMissing = false,
  onHuntMissing
}: {
  label: string;
  singular: string;
  missingCount: number;
  onToggleCreate: () => void;
  isUpdatingMetadata: boolean;
  onUpdateMetadata: () => void;
  isHuntingMissing?: boolean;
  onHuntMissing?: () => void;
}) {
  return (
    <>
      <Button size="sm" className="gap-2" onClick={onToggleCreate}>
        <Plus className="h-4 w-4" strokeWidth={2.5} />
        Add {singular}
      </Button>
      {missingCount > 0 ? (
        <Button
          type="button"
          size="sm"
          variant="secondary"
          className="gap-2"
          onClick={onHuntMissing}
          disabled={isHuntingMissing || !onHuntMissing}
          title={`Search now for the ${missingCount} missing ${missingCount === 1 ? singular.toLowerCase() : label}`}
        >
          <Zap className={cn("h-4 w-4", isHuntingMissing && "animate-pulse")} />
          {isHuntingMissing ? "Hunting…" : `Hunt ${missingCount} missing`}
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
