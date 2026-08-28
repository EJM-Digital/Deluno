import type { SortField } from "./library-filters";
import type { MediaItem } from "./media-types";

/**
 * The rail beside the shelf: where each run of like titles begins, so a reader
 * can reach the middle of a twenty-thousand-title library in one click.
 *
 * The buckets are derived from **the rows on the shelf**, not from a second
 * query that counts the same thing again. That is deliberate. A grouped SQL
 * count would be a second implementation of the same filter, and the first time
 * the two disagreed the rail would say "S — 214" over a shelf holding 210. The
 * shelf is loaded in full, in order, so walking it is exact by construction and
 * the counts above it, the rail beside it, and the posters on it are one answer.
 *
 * It also means the rail is only as complete as the load. Until the whole
 * library has arrived, a bucket with nothing behind it is *unknown*, not empty —
 * see `isComplete` in `library-jump-rail.tsx`, which is what stops the rail
 * greying out "W" one second before W arrives.
 */
export interface JumpBucket {
  /** What the rail prints. */
  label: string;
  /**
   * Index of the first loaded title in this bucket, or `null` when nothing
   * loaded so far falls under it.
   */
  index: number | null;
  count: number;
}

/**
 * The alphabet exists whether or not a library uses it. Under a title sort the
 * rail always shows all 27 stops, because a rail that grows a letter at a time
 * is not something you can aim at.
 */
export const TITLE_BUCKET_UNIVERSE: readonly string[] = [
  "#",
  ..."ABCDEFGHIJKLMNOPQRSTUVWXYZ"
];

/**
 * The letter a title is *filed* under, which is not always the letter it starts
 * with: leading articles are dropped, so The Matrix sits under M.
 *
 * The rail has to agree with the shelf or it is worse than useless — clicking M
 * to find The Matrix and landing somewhere else is the one thing a jump rail
 * must never do. The shelf orders by a stored  computed by a SQLite
 * trigger; this is the same rule, in the browser, for the rows it already has.
 *
 * Anything not a plain A–Z start — digits, symbols, accented letters — is #.
 */
const LEADING_ARTICLES = ["the ", "an ", "a "];

export function sortTitle(title: string): string {
  const trimmed = title.trim();
  const lower = trimmed.toLowerCase();

  for (const article of LEADING_ARTICLES) {
    // A title that is only an article keeps it: "The" is a real film, and
    // filing it under nothing puts it in a bucket the rail cannot name.
    if (lower.length > article.length && lower.startsWith(article)) {
      return trimmed.slice(article.length).trim().toLowerCase();
    }
  }

  return lower;
}

function titleBucket(title: string): string {
  const first = sortTitle(title).charAt(0).toUpperCase();
  return first >= "A" && first <= "Z" ? first : "#";
}

function bandOf(value: number, edges: readonly number[], labels: readonly string[]): string {
  for (let index = 0; index < edges.length; index += 1) {
    if (value < edges[index]) return labels[index];
  }
  return labels[edges.length];
}

const UNKNOWN = "—";

const SIZE_EDGES = [1, 2, 5, 10, 20, 50] as const;
const SIZE_LABELS = ["< 1 GB", "1–2 GB", "2–5 GB", "5–10 GB", "10–20 GB", "20–50 GB", "50 GB +"] as const;

const BITRATE_EDGES = [2, 5, 10, 20, 40] as const;
const BITRATE_LABELS = ["< 2", "2–5", "5–10", "10–20", "20–40", "40 +"] as const;

const RUNTIME_EDGES = [30, 60, 90, 120, 150] as const;
const RUNTIME_LABELS = ["< 30m", "30–60m", "1–1½h", "1½–2h", "2–2½h", "2½h +"] as const;

const RATING_EDGES = [5, 6, 7, 8, 9] as const;
const RATING_LABELS = ["< 5", "5–6", "6–7", "7–8", "8–9", "9 +"] as const;

const POPULARITY_EDGES = [1, 10, 100, 1000] as const;
const POPULARITY_LABELS = ["< 1", "1–10", "10–100", "100–1k", "1k +"] as const;

/**
 * Which stop on the rail a title belongs to, under the order currently applied.
 *
 * One label per sort field and nothing else: the rail's stops are the sort's own
 * grain. Radarr has an A–Z rail and only an A–Z rail, so sorting by year there
 * leaves the rail meaningless beside a list it no longer describes.
 */
export function bucketLabel(item: MediaItem, sortField: SortField): string {
  switch (sortField) {
    case "title":
      return titleBucket(item.title);

    case "year":
      return item.year === null ? UNKNOWN : `${Math.floor(item.year / 10) * 10}s`;

    case "added": {
      if (!item.addedUtc) return UNKNOWN;
      const added = new Date(item.addedUtc);
      return Number.isNaN(added.getTime())
        ? UNKNOWN
        : added.toLocaleDateString([], { month: "short", year: "numeric" });
    }

    // A title with no file has no size and no bitrate, the same rule the poster
    // line and the size filter follow. Bucketing it as "< 1 GB" would put every
    // missing title in the library under one stop and call them small.
    case "size":
      return item.hasFile === false || typeof item.sizeGb !== "number" || item.sizeGb <= 0
        ? UNKNOWN
        : bandOf(item.sizeGb, SIZE_EDGES, SIZE_LABELS);

    case "bitrate":
      return item.hasFile === false || typeof item.bitrateMbps !== "number" || item.bitrateMbps <= 0
        ? UNKNOWN
        : bandOf(item.bitrateMbps, BITRATE_EDGES, BITRATE_LABELS);

    // The ladder's own names, not a band: "Bluray-1080p" is the answer to
    // "where does the 1080p Blu-ray part of my library start".
    case "quality":
      return item.currentQuality?.trim() || UNKNOWN;

    case "runtime":
      return typeof item.runtimeMinutes === "number" && item.runtimeMinutes > 0
        ? bandOf(item.runtimeMinutes, RUNTIME_EDGES, RUNTIME_LABELS)
        : UNKNOWN;

    case "rating":
      return typeof item.rating === "number" && item.rating > 0
        ? bandOf(item.rating, RATING_EDGES, RATING_LABELS)
        : UNKNOWN;

    case "popularity":
      return typeof item.popularity === "number" && item.popularity > 0
        ? bandOf(item.popularity, POPULARITY_EDGES, POPULARITY_LABELS)
        : UNKNOWN;

    default:
      return UNKNOWN;
  }
}

/**
 * Walk the shelf once and record where each bucket starts.
 *
 * Bucket *order* is the shelf's order — no second rule about which decade comes
 * before which, and nothing to keep in step with the sort direction, because the
 * rows are already in it. The one exception is the alphabet, which is a fixed
 * universe rather than whatever the library happens to contain.
 */
export function buildJumpBuckets(
  items: MediaItem[],
  sortField: SortField,
  sortDirection: "asc" | "desc"
): JumpBucket[] {
  const seen = new Map<string, JumpBucket>();
  const order: string[] = [];

  for (let index = 0; index < items.length; index += 1) {
    const label = bucketLabel(items[index], sortField);
    const existing = seen.get(label);
    if (existing) {
      existing.count += 1;
      continue;
    }

    seen.set(label, { label, index, count: 1 });
    order.push(label);
  }

  if (sortField !== "title") {
    return order.map((label) => seen.get(label)!);
  }

  const letters = sortDirection === "desc"
    ? [...TITLE_BUCKET_UNIVERSE].reverse()
    : [...TITLE_BUCKET_UNIVERSE];

  return letters.map((label) => seen.get(label) ?? { label, index: null, count: 0 });
}
