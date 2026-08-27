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
 * The filters the toolbar offers, which are the marks plus monitoring.
 *
 * `upgrades` and `covered` are the two rungs a title with a file can be on;
 * `downloaded` is deliberately not here, because it spans both and so selects a
 * set nobody asks for. Downloading joins the list when live transfer state does
 * (DESIGN-001 step 5) — a chip that can never match is worse than no chip.
 */
export type QuickFilter =
  | "all"
  | "monitored"
  | "unmonitored"
  | "missing"
  /** Has a file and can still get better. The stored `upgrade`. */
  | "upgrades"
  /** Has what the profile asked for — the rung above `upgrades`. */
  | "covered"
  /** Not out yet, so its absence is not a shortfall. */
  | "upcoming";

/** The mark a quick filter selects, or null for one that is not about a mark. */
export const QUICK_FILTER_MARK: Record<QuickFilter, TitleMark | null> = {
  all: null,
  monitored: null,
  unmonitored: null,
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
