import { CalendarRange, Plus, RefreshCw, Search } from "lucide-react";
import { Link } from "react-router-dom";
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
 * typed or a genre picked it said "Hunt 5 missing" and hunted ten. The owner, on
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
  onSearchShown,
  /** Only the TV library links to Season Pass (#791) — a movie has no seasons to bulk-monitor. */
  showSeasonPassLink = false
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
  showSeasonPassLink?: boolean;
}) {
  return (
    <>
      <Button size="sm" className="gap-2" onClick={onToggleCreate}>
        <Plus className="h-4 w-4" strokeWidth={2.5} />
        Add {singular}
      </Button>
      {showSeasonPassLink ? (
        <Button asChild size="sm" variant="outline" className="w-(--library-toolbar-height) gap-2 px-0 min-[2100px]:w-auto min-[2100px]:px-3">
          {/*
            The label hides below 2100px and the icon carries it alone with
            the tooltip, a square the height of every button beside it, like
            the refresh button. TV is the only shelf with this extra action, so
            with its label it is the one thing that tips the row onto a line
            of its own where Movies' fits (#959; measured 2 Oct 2026: it wraps
            up to 2050px and fits from 2100). The aria-label keeps the
            accessible name "Season Pass" whether or not the text is painted.
          */}
          <Link to="/tv/season-pass" title="Bulk-monitor seasons across every show" aria-label="Season Pass">
            <CalendarRange className="h-4 w-4" />
            <span className="hidden min-[2100px]:inline">Season Pass</span>
          </Link>
        </Button>
      ) : null}
      <Button
        type="button"
        size="sm"
        variant="secondary"
        className="min-w-42 gap-2"
        onClick={onSearchShown}
        disabled={shownCount === 0 || isSearchingShown || !onSearchShown}
        title={
          shownCount === 0
            ? `No ${label} on screen to search for`
            : shownCount === 1
              ? `Search now for the ${singular.toLowerCase()} on screen`
              : `Search now for all ${shownCount} ${label} on screen. Narrow the shelf first to search fewer.`
        }
      >
        <Search className={cn("h-4 w-4", isSearchingShown && "animate-pulse")} />
        {isSearchingShown
          ? "Searching…"
          : <span className="tabular-nums">{`Search these ${shownCount}`}</span>}
      </Button>
      <Button
        type="button"
        variant="outline"
        size="icon"
        // The toolbar row sets the height for everything on it; this says
        // "keep me square at whatever that height is".
        data-slot="icon-action"
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
