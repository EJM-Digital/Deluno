import type { MediaItem } from "./media-types";

export type QuickFilter =
  | "all"
  | "monitored"
  | "unmonitored"
  | "downloaded"
  | "downloading"
  | "missing"
  | "upgrades"
  /** Has what the profile asked for — the rung above `upgrades`. */
  | "covered"
  /** Not out yet, so its absence is not a shortfall. */
  | "upcoming"
  | "needsAttention";
export type SortField = "title" | "year" | "rating" | "quality" | "added" | "size" | "status" | "bitrate" | "releaseGroup" | "codec" | "runtime" | "tmdbVotes" | "popularity" | "path";
export type SortDirection = "asc" | "desc";
export type FilterField =
  | "title" | "status" | "monitored" | "quality" | "genre" | "year" | "rating" | "sizeGb" | "bitrateMbps" | "network" | "releaseGroup" | "tags" | "source" | "codec" | "audioCodec" | "audioChannels" | "language" | "hdrFormat" | "releaseStatus" | "certification" | "collection" | "minimumAvailability" | "consideredAvailable" | "digitalRelease" | "physicalRelease" | "releaseDate" | "inCinemas" | "originalLanguage" | "originalTitle" | "path" | "qualityProfile" | "runtimeMinutes" | "studio" | "tmdbRating" | "tmdbVotes" | "imdbRating" | "imdbVotes" | "traktRating" | "traktVotes" | "tomatoRating" | "tomatoVotes" | "popularity" | "keywords" | "wantedReason" | "currentQuality" | "targetQuality" | "type";
export type FilterComparator = "contains" | "equals" | "notEquals" | "gt" | "gte" | "lt" | "lte";

export interface CustomFilterRule {
  id: string;
  field: FilterField;
  comparator: FilterComparator;
  value: string;
}

export interface DisplayOptions {
  showTitle: boolean;
  showMeta: boolean;
  showStatusPill: boolean;
  showQualityBadge: boolean;
  showRating: boolean;
}

const numericFilterFields = new Set<FilterField>(["year", "rating", "sizeGb", "bitrateMbps", "runtimeMinutes", "tmdbRating", "tmdbVotes", "imdbRating", "imdbVotes", "traktRating", "traktVotes", "tomatoRating", "tomatoVotes", "popularity"]);
const equalityFilterFields = new Set<FilterField>(["status", "monitored", "source", "codec", "audioCodec", "audioChannels", "language", "hdrFormat", "releaseStatus", "certification", "minimumAvailability", "consideredAvailable", "originalLanguage", "qualityProfile", "type"]);

export function isUpgradeCandidate(item: MediaItem) {
  return item.wantedReason?.toLowerCase().includes("upgrade") === true ||
    (item.status === "downloaded" &&
      Boolean(item.currentQuality) &&
      Boolean(item.targetQuality) &&
      item.currentQuality !== item.targetQuality);
}

export function isAttentionCandidate(item: MediaItem) {
  return item.status === "importFailed" || item.status === "processingFailed";
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

export function parseCustomRules(raw: string | null | undefined): CustomFilterRule[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as Array<Partial<CustomFilterRule>>;
    return Array.isArray(parsed)
      ? parsed.map((rule) => ({ id: rule.id ?? crypto.randomUUID(), field: rule.field ?? "title", comparator: rule.comparator ?? "contains", value: rule.value ?? "" }))
      : [];
  } catch {
    return [];
  }
}

export function defaultComparatorForField(field: FilterField): FilterComparator {
  if (numericFilterFields.has(field)) return "gte";
  return equalityFilterFields.has(field) ? "equals" : "contains";
}

export function comparatorsForField(field: FilterField): FilterComparator[] {
  if (numericFilterFields.has(field)) return ["equals", "gt", "gte", "lt", "lte"];
  return equalityFilterFields.has(field) ? ["equals", "notEquals"] : ["contains", "equals", "notEquals"];
}

export function matchesCustomRule(item: MediaItem, rule: CustomFilterRule) {
  if (!rule.value.trim()) return true;
  const rawValue = resolveRuleValue(item, rule.field);
  if (rawValue === null || rawValue === undefined) return false;

  if (typeof rawValue === "number") {
    const target = Number(rule.value);
    if (Number.isNaN(target)) return false;
    switch (rule.comparator) {
      case "equals": return rawValue === target;
      case "gt": return rawValue > target;
      case "gte": return rawValue >= target;
      case "lt": return rawValue < target;
      case "lte": return rawValue <= target;
      default: return false;
    }
  }

  const normalizedValue = String(rawValue).toLowerCase();
  const normalizedTarget = rule.value.toLowerCase();
  switch (rule.comparator) {
    case "contains": return normalizedValue.includes(normalizedTarget);
    case "equals": return normalizedValue === normalizedTarget;
    case "notEquals": return normalizedValue !== normalizedTarget;
    default: return false;
  }
}

export function resolveRuleValue(item: MediaItem, field: FilterField): string | number | boolean | null | undefined {
  switch (field) {
    case "title": return item.title;
    case "status": return item.status;
    case "monitored": return item.monitored;
    case "quality": return item.quality;
    case "genre": return item.genres.join(" ");
    case "year": return item.year;
    case "rating": return item.rating;
    case "sizeGb": return item.sizeGb;
    case "bitrateMbps": return item.bitrateMbps ?? null;
    case "network": return item.network ?? null;
    case "releaseGroup": return item.releaseGroup ?? null;
    case "tags": return item.tags?.join(" ") ?? null;
    case "source": return item.source ?? null;
    case "codec": return item.codec ?? null;
    case "audioCodec": return item.audioCodec ?? null;
    case "audioChannels": return item.audioChannels ?? null;
    case "language": return item.language ?? null;
    case "hdrFormat": return item.hdrFormat ?? null;
    case "releaseStatus": return item.releaseStatus ?? null;
    case "certification": return item.certification ?? null;
    case "collection": return item.collection ?? null;
    case "minimumAvailability": return item.minimumAvailability ?? null;
    case "consideredAvailable": return item.consideredAvailable ?? null;
    case "digitalRelease": return item.digitalRelease ?? null;
    case "physicalRelease": return item.physicalRelease ?? null;
    case "releaseDate": return item.releaseDate ?? null;
    case "inCinemas": return item.inCinemas ?? null;
    case "originalLanguage": return item.originalLanguage ?? null;
    case "originalTitle": return item.originalTitle ?? null;
    case "path": return item.path ?? null;
    case "qualityProfile": return item.qualityProfile ?? null;
    case "runtimeMinutes": return item.runtimeMinutes ?? null;
    case "studio": return item.studio ?? null;
    case "tmdbRating": return item.tmdbRating ?? null;
    case "tmdbVotes": return item.tmdbVotes ?? null;
    case "imdbRating": return item.imdbRating ?? null;
    case "imdbVotes": return item.imdbVotes ?? null;
    case "traktRating": return item.traktRating ?? null;
    case "traktVotes": return item.traktVotes ?? null;
    case "tomatoRating": return item.tomatoRating ?? null;
    case "tomatoVotes": return item.tomatoVotes ?? null;
    case "popularity": return item.popularity ?? null;
    case "keywords": return item.keywords?.join(" ") ?? null;
    case "wantedReason": return item.wantedReason ?? null;
    case "currentQuality": return item.currentQuality ?? null;
    case "targetQuality": return item.targetQuality ?? null;
    case "type": return item.type;
    default: return null;
  }
}

export function filterAndSortLibraryItems(items: MediaItem[], options: { query: string; quickFilter: QuickFilter; customRules: CustomFilterRule[]; sortField: SortField; sortDirection: SortDirection }): MediaItem[] {
  const result = items.filter((item) => {
    const matchesQuery = [item.title, item.genres.join(" "), item.network ?? "", item.quality, item.wantedReason ?? "", item.releaseGroup ?? "", item.codec ?? "", item.audioCodec ?? "", item.audioChannels ?? "", (item.tags ?? []).join(" "), item.path ?? ""].join(" ").toLowerCase().includes(options.query.toLowerCase());
    const matchesQuick = options.quickFilter === "all" ||
      (options.quickFilter === "monitored" && item.monitored) ||
      (options.quickFilter === "unmonitored" && !item.monitored) ||
      (options.quickFilter === "downloaded" && item.status === "downloaded") ||
      (options.quickFilter === "downloading" && item.status === "downloading") ||
      (options.quickFilter === "missing" && item.status === "missing") ||
      (options.quickFilter === "upgrades" && isUpgradeCandidate(item)) ||
      (options.quickFilter === "needsAttention" && isAttentionCandidate(item));
    return matchesQuery && matchesQuick && options.customRules.every((rule) => matchesCustomRule(item, rule));
  });

  // `filter` produced a fresh array, so this in-place sort cannot mutate a caller-owned list.
  return result.sort((left, right) => {
    const modifier = options.sortDirection === "asc" ? 1 : -1;
    switch (options.sortField) {
      case "year": return ((left.year ?? 0) - (right.year ?? 0)) * modifier;
      case "rating": return ((left.rating ?? 0) - (right.rating ?? 0)) * modifier;
      case "quality": return (left.quality ?? "").localeCompare(right.quality ?? "") * modifier;
      case "added": return left.added.localeCompare(right.added) * modifier;
      case "size": return ((left.sizeGb ?? 0) - (right.sizeGb ?? 0)) * modifier;
      case "status": return left.status.localeCompare(right.status) * modifier;
      case "bitrate": return ((left.bitrateMbps ?? 0) - (right.bitrateMbps ?? 0)) * modifier;
      case "releaseGroup": return (left.releaseGroup ?? "").localeCompare(right.releaseGroup ?? "") * modifier;
      case "codec": return (left.codec ?? "").localeCompare(right.codec ?? "") * modifier;
      case "runtime": return ((left.runtimeMinutes ?? 0) - (right.runtimeMinutes ?? 0)) * modifier;
      case "tmdbVotes": return ((left.tmdbVotes ?? 0) - (right.tmdbVotes ?? 0)) * modifier;
      case "popularity": return ((left.popularity ?? 0) - (right.popularity ?? 0)) * modifier;
      case "path": return (left.path ?? "").localeCompare(right.path ?? "") * modifier;
      default: return left.title.localeCompare(right.title) * modifier;
    }
  });
}
