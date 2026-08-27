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
 * Every sort the server can actually perform, and no more.
 *
 * It was once fourteen values here and four in `library-control-rail.tsx` — the
 * same redeclaration the QuickFilter comment in that file describes itself as
 * having fixed, left in place one line below it. The ten extra were
 * unreachable: nothing can set a sort the menu does not list.
 *
 * `runtime` and `popularity` are new here and were never new in the database —
 * both have had an index since V0011/V0012 and neither was ever offered.
 *
 * Size and quality are deliberately absent: they live on the wanted state,
 * which the catalogue page reaches through a correlated pick, so ordering by
 * them would run that pick for every title in the library and sort the lot.
 * See `CatalogueSortFields` for the full account.
 */
export type SortField = "title" | "year" | "rating" | "added" | "runtime" | "popularity";
export type SortDirection = "asc" | "desc";

/**
 * The narrowing beyond a status and a library.
 *
 * The shape mirrors `CatalogueFilters` on the server one for one, because it is
 * sent straight to it. Deliberately not a generic field/operator/value engine:
 * the last one of those lived in this very file, could express filters nothing
 * could answer, and two of its branches matched zero rows forever without
 * anybody noticing (#302).
 */
export interface CustomFilters {
  /** Quality tiers as the ladder names them — `WEB 2160p`, `Remux 1080p`. */
  qualities: string[];
  /** Every genre listed must be present. */
  genres: string[];
  minSizeGb: number | null;
  maxSizeGb: number | null;
  minYear: number | null;
  maxYear: number | null;
  minRuntime: number | null;
  maxRuntime: number | null;
  minRating: number | null;
}

export function emptyCustomFilters(): CustomFilters {
  return {
    qualities: [], genres: [],
    minSizeGb: null, maxSizeGb: null,
    minYear: null, maxYear: null,
    minRuntime: null, maxRuntime: null,
    minRating: null
  };
}

/**
 * How many questions this is asking. Drives the number on the Filters button,
 * so a narrowed shelf can never look like an unnarrowed one — which is the way
 * people lose half their library and conclude Deluno has.
 */
export function customFilterCount(filters: CustomFilters): number {
  return (
    (filters.qualities.length > 0 ? 1 : 0) +
    (filters.genres.length > 0 ? 1 : 0) +
    (filters.minSizeGb !== null || filters.maxSizeGb !== null ? 1 : 0) +
    (filters.minYear !== null || filters.maxYear !== null ? 1 : 0) +
    (filters.minRuntime !== null || filters.maxRuntime !== null ? 1 : 0) +
    (filters.minRating !== null ? 1 : 0)
  );
}

/** Writes the filters onto a catalogue request. Only what is set is sent. */
export function applyCustomFilters(params: URLSearchParams, filters: CustomFilters) {
  if (filters.qualities.length) params.set("quality", filters.qualities.join(","));
  if (filters.genres.length) params.set("genre", filters.genres.join(","));
  if (filters.minSizeGb !== null) params.set("minSizeGb", String(filters.minSizeGb));
  if (filters.maxSizeGb !== null) params.set("maxSizeGb", String(filters.maxSizeGb));
  if (filters.minYear !== null) params.set("minYear", String(filters.minYear));
  if (filters.maxYear !== null) params.set("maxYear", String(filters.maxYear));
  if (filters.minRuntime !== null) params.set("minRuntime", String(filters.minRuntime));
  if (filters.maxRuntime !== null) params.set("maxRuntime", String(filters.maxRuntime));
  if (filters.minRating !== null) params.set("minRating", String(filters.minRating));
}

/**
 * Reads the custom filters back off a saved view.
 *
 * Anything unreadable is "no filters", never a partial set: a saved view that
 * silently narrowed by half of what you saved would be worse than one that
 * narrowed by none of it, because you would not be able to tell.
 */
export function parseCustomFilters(raw: string | null | undefined): CustomFilters {
  if (!raw) return emptyCustomFilters();
  try {
    const parsed = JSON.parse(raw) as Partial<CustomFilters>;
    // An array is the legacy `rulesJson` value the browser-side rule engine
    // left behind (#302). It is not a filter set, and reading it as one would
    // spread an array's indices over these fields.
    if (!parsed || Array.isArray(parsed) || typeof parsed !== "object") return emptyCustomFilters();
    return { ...emptyCustomFilters(), ...parsed };
  } catch {
    return emptyCustomFilters();
  }
}

/**
 * What a poster may carry, beyond the artwork.
 *
 * This interface was declared twice — here and in `library-grid.tsx` — with the
 * same five fields, in a file whose own header describes that exact defect. The
 * grid re-exports this one now, so adding an option cannot leave half the app
 * behind.
 *
 * The extras all land on one line under the title rather than one row each. Six
 * new switches each claiming their own line would bury the artwork the grid
 * exists to show; joined into a sentence, a card stays calm however many are on.
 */
export interface DisplayOptions {
  showTitle: boolean;
  showMeta: boolean;
  showStatusPill: boolean;
  showQualityBadge: boolean;
  showRating: boolean;
  showSize: boolean;
  showGenres: boolean;
  showRuntime: boolean;
  showReleaseGroup: boolean;
  showCodec: boolean;
  showAdded: boolean;
}

export function defaultDisplayOptions(): DisplayOptions {
  return {
    showTitle: true, showMeta: true, showStatusPill: true, showQualityBadge: true, showRating: true,
    // Off by default. They are the answer to "show me more", and a card that
    // arrives already carrying everything has nothing left to ask for.
    showSize: false, showGenres: false, showRuntime: false, showReleaseGroup: false, showCodec: false, showAdded: false
  };
}

export function parseDisplayOptions(raw: string | null | undefined): DisplayOptions {
  if (!raw) return defaultDisplayOptions();
  try {
    return { ...defaultDisplayOptions(), ...(JSON.parse(raw) as Partial<DisplayOptions>) };
  } catch {
    return defaultDisplayOptions();
  }
}
