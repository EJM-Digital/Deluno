/**
 * The two axes a shelf is narrowed on that are *not* served field declarations:
 * the mark a title carries, and whether Deluno acts on it.
 *
 * Everything else moved. The filter fields, the orders and the poster options are
 * declared per media kind by the server and fetched by `library-controls.ts`
 * (#324) — this file used to hold the browser's own copy of the sorts and its own
 * `DisplayOptions`, which is the shape every defect in this codebase has had.
 *
 * It also used to hold a client-side filter engine — `filterAndSortLibraryItems`,
 * `matchesCustomRule`, `resolveRuleValue` and a 45-value `FilterField` union.
 * Nothing imported any of it: the catalogue is paged and filtered by the server,
 * so the engine was a second, unreachable definition of the same states — and it
 * disagreed with the live one. Its `downloading` and `needsAttention` branches
 * tested `MediaItem.status` values nothing ever set, so both could only ever
 * match nothing. See #302.
 */
import type { PosterOptionSpec } from "./library-controls";
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
  /** Found and handed to a download client. On its way. */
  | "downloading"
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
  upcoming: "upcoming",
  downloading: "downloading"
};

/**
 * The id of an order, checked against the list the server serves rather than
 * against a union declared here.
 *
 * It was a nine-value union in this file and a nine-entry array in
 * `library-control-rail.tsx`, beside a nine-constant class on the server — three
 * copies of one list, which had already been the subject of two comments in those
 * very files. There is one list now and it arrives with the controls.
 */
export type SortField = string;
export type SortDirection = "asc" | "desc";

/**
 * What a poster carries, as a set of switches keyed by the option ids the
 * control set declares.
 *
 * A record rather than an interface with eleven named booleans, because the list
 * of options is now per media kind and served — TV gets ones a film has no answer
 * for. `defaultDisplayOptions` fills it from the declaration, so a switch cannot
 * exist without a label and a description beside it.
 */
export type DisplayOptions = Record<string, boolean>;

export function defaultDisplayOptions(options: PosterOptionSpec[]): DisplayOptions {
  return Object.fromEntries(options.map((option) => [option.id, option.defaultOn]));
}

/**
 * A stored choice, plus anything the declaration has gained since it was saved.
 *
 * The defaults come first so somebody who saved their layout last month does not
 * get `undefined` for a switch added since — the same reason the old
 * `parseDisplayOptions` spread its defaults, and the reason it still matters now
 * that the option list can grow per media kind.
 */
export function parseDisplayOptions(raw: string | null | undefined, options: PosterOptionSpec[]): DisplayOptions {
  const defaults = defaultDisplayOptions(options);
  if (!raw) return defaults;
  try {
    const stored = JSON.parse(raw) as Record<string, unknown>;
    if (!stored || typeof stored !== "object" || Array.isArray(stored)) return defaults;
    for (const key of Object.keys(defaults)) {
      if (typeof stored[key] === "boolean") defaults[key] = stored[key];
    }
    return defaults;
  } catch {
    return defaults;
  }
}
