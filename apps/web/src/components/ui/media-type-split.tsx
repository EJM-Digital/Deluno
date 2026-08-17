/**
 * The Movies / TV split, as one mechanism every list shares.
 *
 * Deluno keeps separate movie and TV engines, so which side of the app a row
 * belongs to is load-bearing information, not decoration. Rather than duplicate
 * a whole card per type, a list stays one card and gets:
 *
 *   MediaTypeFilter — All · Movies · TV in the page toolbar, to focus one side
 *   ListGroupHeader — a sticky "Movies" / "TV shows" divider inside the list
 *                     while All is selected, so the split is visible unfiltered
 *
 * Use `useMediaTypeSplit` to get both from one piece of state.
 */
import { useMemo, useState } from "react";
import { cn } from "../../lib/utils";
import { SegmentedControl } from "./segmented-control";

export type MediaTypeScope = "all" | "movies" | "tv";

export function normalizeMediaType(value: string | null | undefined): "movies" | "tv" {
  return value === "tv" ? "tv" : "movies";
}

export function mediaTypeLabel(value: string | null | undefined) {
  return normalizeMediaType(value) === "tv" ? "TV shows" : "Movies";
}

/** All · Movies · TV, for the page toolbar. Hidden when only one type exists. */
export function MediaTypeFilter({
  value,
  onValueChange,
  counts
}: {
  value: MediaTypeScope;
  onValueChange: (value: MediaTypeScope) => void;
  counts?: { movies: number; tv: number };
}) {
  if (counts && (counts.movies === 0 || counts.tv === 0)) return null;
  return (
    <SegmentedControl<MediaTypeScope>
      aria-label="Filter by media type"
      value={value}
      onValueChange={onValueChange}
      className="w-auto"
      options={[
        { value: "all", label: counts ? `All ${counts.movies + counts.tv}` : "All" },
        { value: "movies", label: counts ? `Movies ${counts.movies}` : "Movies" },
        { value: "tv", label: counts ? `TV ${counts.tv}` : "TV" }
      ]}
    />
  );
}

/** Divider row inside a ListTable. Sticks under the column header while scrolling. */
export function ListGroupHeader({ label, count }: { label: string; count?: number }) {
  return (
    <div
      role="row"
      className={cn(
        "sticky top-0 z-10 flex items-center gap-2 border-b border-hairline bg-surface-2/90 px-[var(--card-pad-x)] py-1.5 backdrop-blur",
        "text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground"
      )}
    >
      <span role="columnheader">{label}</span>
      {count !== undefined ? <span className="font-normal normal-case tracking-normal opacity-70">{count}</span> : null}
    </div>
  );
}

/**
 * Splits a list by media type once, and returns everything a page needs:
 * the filter element, the counts, and either one flat list (filtered) or the
 * two groups to render with headers (unfiltered).
 */
export function useMediaTypeSplit<T>(items: T[], getMediaType: (item: T) => string | null | undefined) {
  const [scope, setScope] = useState<MediaTypeScope>("all");

  return useMemo(() => {
    const movies = items.filter((item) => normalizeMediaType(getMediaType(item)) === "movies");
    const tv = items.filter((item) => normalizeMediaType(getMediaType(item)) === "tv");
    const counts = { movies: movies.length, tv: tv.length };
    const groups =
      scope === "movies"
        ? [{ key: "movies" as const, label: "Movies", items: movies }]
        : scope === "tv"
          ? [{ key: "tv" as const, label: "TV shows", items: tv }]
          : [
              { key: "movies" as const, label: "Movies", items: movies },
              { key: "tv" as const, label: "TV shows", items: tv }
            ];

    return {
      scope,
      setScope,
      counts,
      /** Only worth grouping when both sides actually have rows. */
      showGroups: counts.movies > 0 && counts.tv > 0,
      groups: groups.filter((group) => group.items.length > 0),
      visibleCount: groups.reduce((total, group) => total + group.items.length, 0)
    };
  }, [items, getMediaType, scope]);
}
