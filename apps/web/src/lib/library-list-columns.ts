/**
 * The columns in a compact library list, after the fixed selection and Title
 * columns.  The order is a user preference, not a sort: dragging a header
 * changes where a fact is read, while the Sort control still decides which
 * title appears first.
 *
 * Title stays fixed because it is the row's anchor and the selection checkbox
 * stays fixed because it belongs to the row rather than to the list's facts.
 * Movies and shows keep separate defaults because Episodes only exists for a
 * show.
 */
export type LibraryListVariant = "movies" | "shows";

export type LibraryListColumnKey =
  | "quality"
  | "status"
  | "episodes"
  | "subtitles"
  | "genre"
  | "size"
  | "rating"
  | "added";

const MOVIE_COLUMNS: readonly LibraryListColumnKey[] = [
  "quality",
  "status",
  "subtitles",
  "genre",
  "size",
  "rating",
  "added"
];

const SHOW_COLUMNS: readonly LibraryListColumnKey[] = [
  "quality",
  "status",
  "episodes",
  "subtitles",
  "genre",
  "size",
  "rating",
  "added"
];

export const LIST_COLUMN_LABELS: Record<LibraryListColumnKey, string> = {
  quality: "Quality",
  status: "Status",
  episodes: "Episodes",
  subtitles: "Subtitles",
  genre: "Genre",
  size: "Size",
  rating: "Rating",
  added: "Added"
};

export function listColumnsFor(variant: LibraryListVariant): readonly LibraryListColumnKey[] {
  return variant === "shows" ? SHOW_COLUMNS : MOVIE_COLUMNS;
}

export function listColumnLabel(column: LibraryListColumnKey): string {
  return LIST_COLUMN_LABELS[column];
}

/**
 * Repairs a stored or dragged order against the columns this shelf actually
 * supports.  Duplicates, unknown keys, and a movie-only Episodes entry are
 * discarded; any newly-added supported column is appended at its default
 * position rather than disappearing from the list.
 */
export function normalizeListColumnOrder(
  order: readonly string[],
  variant: LibraryListVariant
): LibraryListColumnKey[] {
  const defaults = listColumnsFor(variant);
  const allowed = new Set<LibraryListColumnKey>(defaults);
  const seen = new Set<LibraryListColumnKey>();
  const valid = order.filter((value): value is LibraryListColumnKey => {
    if (!allowed.has(value as LibraryListColumnKey)) return false;
    const key = value as LibraryListColumnKey;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });

  return [...valid, ...defaults.filter((key) => !seen.has(key))];
}

/** Reads the JSON shape kept in localStorage without trusting old values. */
export function parseListColumnOrder(
  raw: string | null | undefined,
  variant: LibraryListVariant
): LibraryListColumnKey[] {
  if (!raw) return [...listColumnsFor(variant)];
  try {
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed)
      ? normalizeListColumnOrder(parsed.filter((value): value is string => typeof value === "string"), variant)
      : [...listColumnsFor(variant)];
  } catch {
    return [...listColumnsFor(variant)];
  }
}

export function listColumnStorageKey(variant: LibraryListVariant): string {
  return `deluno-list-columns-${variant}`;
}

/** Moves one supported column and leaves the fixed Title anchor untouched. */
export function moveListColumn(
  order: readonly LibraryListColumnKey[],
  source: LibraryListColumnKey,
  target: LibraryListColumnKey
): LibraryListColumnKey[] {
  const from = order.indexOf(source);
  const to = order.indexOf(target);
  if (from < 0 || to < 0 || from === to) return [...order];

  const next = [...order];
  next.splice(from, 1);
  next.splice(from < to ? to - 1 : to, 0, source);
  return next;
}

/** Moves a column one slot left or right for the keyboard equivalent of drag. */
export function shiftListColumn(
  order: readonly LibraryListColumnKey[],
  source: LibraryListColumnKey,
  direction: -1 | 1
): LibraryListColumnKey[] {
  const index = order.indexOf(source);
  const target = index + direction;
  if (index < 0 || target < 0 || target >= order.length) return [...order];

  const next = [...order];
  next.splice(index, 1);
  next.splice(target, 0, source);
  return next;
}
