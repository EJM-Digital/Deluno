/**
 * What a library's toolbar can ask for: which titles, in what order, drawn how.
 *
 * It used to hold a client-side filter engine as well — `filterAndSortLibraryItems`,
 * `matchesCustomRule`, `resolveRuleValue` and a 45-value `FilterField` union.
 * Nothing imported any of it: the catalogue is paged and filtered by the server
 * (`library-view.tsx` sends `status`, `sort` and `direction`), so the engine was
 * a second, unreachable definition of the same states — and it disagreed with
 * the live one. Its `downloading` and `needsAttention` branches tested
 * `MediaItem.status` values nothing ever set, so both could only ever match
 * nothing; its `missing` branch meant "no file", which is not what Missing
 * means; and `isUpgradeCandidate` re-derived Upgradable from a quality-string
 * comparison rather than the stored wanted status. See #302.
 */
import type { TitleMark } from "./status-tones";

/**
 * The filters the legend row offers: **the marks, and nothing else**.
 *
 * Monitored and Unmonitored used to be two more values here, which was a
 * category error with two consequences. It made monitoring *mutually exclusive*
 * with every real state, so "missing, and I have told Deluno to leave it alone"
 * could not be asked for at all. And it put two chips that cannot have a colour
 * in a row whose whole job is to be the colour legend for the shelf below it.
 *
 * Monitoring is a separate axis — see {@link MonitoringFilter} — because it
 * multiplies across the states rather than sitting beside them: any of these
 * four can be monitored or not.
 *
 * `upgrades` and `covered` are the two rungs a title with a file can be on;
 * `downloaded` is deliberately not here, because it spans both and so selects a
 * set nobody asks for. Downloading joins the list when live transfer state does
 * (DESIGN-001 step 5) — a chip that can never match is worse than no chip.
 */
export type QuickFilter =
  | "all"
  | "missing"
  /** Has a file and can still get better. The stored `upgrade`. */
  | "upgrades"
  /** Has what the profile asked for — the rung above `upgrades`. */
  | "covered"
  /** Not out yet, so its absence is not a shortfall. */
  | "upcoming";

/**
 * Whether Deluno acts on the title. The other axis.
 *
 * The state says what is true; this says what Deluno does about it. A missing
 * title being hunted for and a missing title you have excluded are the same
 * state and opposite intentions, so one value could never carry both.
 */
export type MonitoringFilter = "any" | "monitored" | "unmonitored";

export function isMonitoringFilter(value: string | null): value is MonitoringFilter {
  return value === "any" || value === "monitored" || value === "unmonitored";
}

/** What the catalogue query wants: `undefined` is "either". */
export function monitoringParam(value: MonitoringFilter): boolean | undefined {
  return value === "any" ? undefined : value === "monitored";
}

/** The mark a quick filter selects, or null for one that is not about a mark. */
export const QUICK_FILTER_MARK: Record<QuickFilter, TitleMark | null> = {
  all: null,
  missing: "missing",
  upgrades: "upgrade",
  covered: "covered",
  upcoming: "upcoming"
};

/**
 * One union, and the four values the sort menu actually offers.
 *
 * This was fourteen values here and four in `library-control-rail.tsx` — the
 * same redeclaration the QuickFilter comment in that file describes itself as
 * having fixed, left in place one line below it. The ten extra were unreachable:
 * nothing can set a sort the menu does not list.
 */
export type SortField = "title" | "year" | "rating" | "added";
export type SortDirection = "asc" | "desc";

export interface DisplayOptions {
  showTitle: boolean;
  showMeta: boolean;
  showStatusPill: boolean;
  showQualityBadge: boolean;
  showRating: boolean;
}

export function defaultDisplayOptions(): DisplayOptions {
  return { showTitle: true, showMeta: true, showStatusPill: true, showQualityBadge: true, showRating: true };
}

export function parseDisplayOptions(raw: string | null | undefined): DisplayOptions {
  if (!raw) return defaultDisplayOptions();
  try {
    const parsed = JSON.parse(raw) as Partial<DisplayOptions>;
    return {
      showTitle: parsed.showTitle ?? true,
      showMeta: parsed.showMeta ?? true,
      showStatusPill: parsed.showStatusPill ?? true,
      showQualityBadge: parsed.showQualityBadge ?? true,
      showRating: parsed.showRating ?? true
    };
  } catch {
    return defaultDisplayOptions();
  }
}
