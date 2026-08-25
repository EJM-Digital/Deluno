import { Plus, RefreshCw, Zap } from "lucide-react";
import { librarySummaryTone } from "../../lib/media-status-presentation";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";

type LibrarySummaryHeaderProps = {
  label: string;
  singular: string;
  /** True only while the very first page is loading — zeros are not yet facts, so the stat line shows placeholders. */
  isLoading?: boolean;
  totalCount: number;
  downloadedCount: number;
  monitoredCount: number;
  missingCount: number;
  downloadingCount: number;
  onToggleCreate: () => void;
  isUpdatingMetadata: boolean;
  onUpdateMetadata: () => void;
  isHuntingMissing?: boolean;
  onHuntMissing?: () => void;
};

/**
 * One band, not three.
 *
 * This used to be a hero card that restated the page name ("Browse and manage
 * your movies") under a topbar already saying "Movies", then repeated the same
 * counts the filter pills carry directly below it — roughly 230px and four
 * containers before the first poster (#261). The topbar names the page, the
 * filter pills carry the per-status counts, and this row carries the totals and
 * the actions.
 *
 * Two labelled actions at most, per the page grammar: the primary, and Hunt
 * when there is something to hunt. Refreshing metadata is a rare maintenance
 * action, so it keeps its place as an icon.
 */
export function LibrarySummaryHeader({
  label,
  singular,
  isLoading = false,
  totalCount,
  downloadedCount,
  monitoredCount,
  missingCount,
  downloadingCount,
  onToggleCreate,
  isUpdatingMetadata,
  onUpdateMetadata,
  isHuntingMissing = false,
  onHuntMissing,
}: LibrarySummaryHeaderProps) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-[var(--grid-gap)]">
      <p className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-[length:var(--type-body-sm)] text-muted-foreground">
        {isLoading ? (
          <span>Loading your {label}…</span>
        ) : (
          <>
            <span>
              <span className="tabular font-semibold text-foreground">{totalCount.toLocaleString()}</span>{" "}
              {totalCount === 1 ? singular.toLowerCase() : label}
            </span>
            <span className="text-muted-foreground/45">·</span>
            <span>
              <span className={cn("tabular font-semibold", librarySummaryTone("availability", downloadedCount))}>{downloadedCount}</span> downloaded
            </span>
            <span className="text-muted-foreground/45">·</span>
            <span><span className="tabular font-semibold text-muted-foreground">{monitoredCount}</span> monitored</span>
            {missingCount > 0 ? <><span className="text-muted-foreground/45">·</span><span><span className="tabular font-semibold text-warning">{missingCount}</span> missing</span></> : null}
            {downloadingCount > 0 ? <><span className="text-muted-foreground/45">·</span><span><span className="tabular font-semibold text-info">{downloadingCount}</span> downloading</span></> : null}
          </>
        )}
      </p>

      <div className="flex shrink-0 flex-wrap items-center gap-2">
        <Button className="gap-2" onClick={onToggleCreate}>
          <Plus className="h-4 w-4" strokeWidth={2.5} />
          Add {singular}
        </Button>
        {missingCount > 0 ? (
          <Button
            type="button"
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
      </div>
    </div>
  );
}
