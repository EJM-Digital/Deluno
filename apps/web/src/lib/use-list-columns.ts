import { useCallback, useEffect, useMemo, useRef, useState } from "react";

/**
 * Column order and sort for one list, remembered per list in this browser
 * (#1119). The list's own order is the default; the person can drag columns
 * into a new order and click a header to sort (ascending, then descending, and
 * they alternate; the list's own order comes back only through Reset columns),
 * the same as Weir's lists.
 *
 * Pinned columns (action buttons, switches, anything with no label) keep
 * their slot; the movable columns are permuted among the remaining slots.
 */

export type SortDirection = "asc" | "desc";
export type SortValue = string | number | boolean | Date | null | undefined;

/** A value getter, or a string naming a sort the server does. */
export type ColumnSort<T> = ((item: T) => SortValue) | string;

export interface ListColumnSpec<T = never> {
  id?: string;
  label: unknown;
  sort?: ColumnSort<T>;
  /** A comparator, for an order a single value cannot express. Used instead of `sort`. */
  compare?: (a: T, b: T) => number;
  /** Set false where sorting makes no sense (action, chevron and switch columns). */
  sortable?: boolean;
  /** Set false to pin the column in its slot. */
  movable?: boolean;
  srOnly?: boolean;
}

export interface ColumnSortState {
  id: string;
  direction: SortDirection;
}

interface StoredState {
  order: string[];
  sort: ColumnSortState | null;
}

export function listColumnId(column: { id?: string }, index: number): string {
  return column.id ?? `c${index}`;
}

export function isColumnMovable(column: ListColumnSpec<never>): boolean {
  return column.movable !== false && !column.srOnly && column.label !== "" && column.label !== null && column.label !== undefined;
}

export function isColumnSortable(column: ListColumnSpec<never>): boolean {
  return column.sortable !== false && (column.sort !== undefined || column.compare !== undefined) && isColumnMovable(column);
}

const storageKey = (listId: string) => `deluno:list-columns:${listId}`;

function readStored(listId: string): StoredState {
  try {
    const raw = window.localStorage.getItem(storageKey(listId));
    if (!raw) return { order: [], sort: null };
    const parsed = JSON.parse(raw) as Partial<StoredState> | null;
    const order = Array.isArray(parsed?.order) ? parsed.order.filter((value): value is string => typeof value === "string") : [];
    const sort = parsed?.sort && typeof parsed.sort.id === "string" && (parsed.sort.direction === "asc" || parsed.sort.direction === "desc") ? parsed.sort : null;
    return { order, sort };
  } catch {
    return { order: [], sort: null };
  }
}

function writeStored(listId: string, state: StoredState) {
  try {
    if (state.order.length === 0 && !state.sort) window.localStorage.removeItem(storageKey(listId));
    else window.localStorage.setItem(storageKey(listId), JSON.stringify(state));
  } catch {
    // Private windows and blocked storage: the order simply is not remembered.
  }
}

/** Ids of the movable columns in the chosen order. Unknown ids are dropped, new columns keep their place. */
export function reconcileMovableOrder(defaultMovable: string[], stored: string[]): string[] {
  const known = new Set(defaultMovable);
  const kept = stored.filter((id, index) => known.has(id) && stored.indexOf(id) === index);
  const missing = defaultMovable.filter((id) => !kept.includes(id));
  return [...kept, ...missing];
}

/** Every column id in display order: pinned columns keep their slot, movable ones fill the rest in `movableOrder`. */
export function fullColumnOrder(ids: string[], movable: boolean[], movableOrder: string[]): string[] {
  let next = 0;
  return ids.map((id, index) => (movable[index] ? movableOrder[next++] : id));
}

export function compareSortValues(a: SortValue, b: SortValue): number {
  const normalise = (value: SortValue): string | number | null => {
    if (value === null || value === undefined) return null;
    if (value instanceof Date) return value.getTime();
    if (typeof value === "boolean") return value ? 1 : 0;
    return value;
  };
  const left = normalise(a);
  const right = normalise(b);
  if (left === right) return 0;
  if (left === null) return 1;
  if (right === null) return -1;
  if (typeof left === "number" && typeof right === "number") return left - right;
  return String(left).localeCompare(String(right), undefined, { numeric: true, sensitivity: "base" });
}

export function sortListItems<T>(items: readonly T[], column: ListColumnSpec<T> | undefined, direction: SortDirection): T[] {
  const sort = column?.sort;
  const compare = column?.compare;
  if (!compare && (!sort || typeof sort === "string")) return items as T[];
  const sign = direction === "asc" ? 1 : -1;
  const decorated = items.map((item, index) => ({ item, index }));
  if (compare) {
    decorated.sort((a, b) => sign * compare(a.item, b.item) || a.index - b.index);
  } else {
    const getter = sort as (item: T) => SortValue;
    const keyed = decorated.map((entry) => ({ ...entry, value: getter(entry.item) }));
    keyed.sort((a, b) => {
      const aEmpty = a.value === null || a.value === undefined;
      const bEmpty = b.value === null || b.value === undefined;
      // Rows with nothing to compare always sit last, whichever way it sorts.
      if (aEmpty || bEmpty) return aEmpty === bEmpty ? a.index - b.index : aEmpty ? 1 : -1;
      return sign * compareSortValues(a.value, b.value) || a.index - b.index;
    });
    return keyed.map((entry) => entry.item);
  }
  return decorated.map((entry) => entry.item);
}

export interface ListColumnsState<T = never> {
  listId: string | undefined;
  /** Columns in display order, each carrying its resolved id and the index it had in the page's own list. */
  columns: Array<{ column: ListColumnSpec<T>; id: string; originalIndex: number }>;
  sort: ColumnSortState | null;
  /** The sorted column's `sort` string when the server does the sorting. */
  serverSortKey: string | null;
  items: T[];
  changed: boolean;
  toggleSort: (id: string) => void;
  setMovableOrder: (movableOrder: string[]) => void;
  movableOrder: string[];
  reset: () => void;
}

/**
 * Pass the page's rows as `items` to have them sorted; the sorted rows come
 * back as `items`. With `serverSort` the rows are returned untouched and
 * `serverSortKey` tells the page what to ask the API for.
 */
export function useListColumns<T>(
  listId: string | undefined,
  columns: ReadonlyArray<ListColumnSpec<T>>,
  items?: readonly T[],
  options?: { serverSort?: boolean; startOverWhen?: unknown }
): ListColumnsState<T> {
  const [stored, setStored] = useState<StoredState>(() => (listId ? readStored(listId) : { order: [], sort: null }));

  const ids = columns.map(listColumnId);
  const movable = columns.map((column) => Boolean(listId) && isColumnMovable(column));
  const defaultMovable = ids.filter((_, index) => movable[index]);
  const movableOrder = reconcileMovableOrder(defaultMovable, stored.order);
  const fullOrder = fullColumnOrder(ids, movable, movableOrder);

  const ordered = fullOrder.map((id) => {
    const originalIndex = ids.indexOf(id);
    return { column: columns[originalIndex], id, originalIndex };
  });

  const activeSort = useMemo(() => {
    if (!listId || !stored.sort) return null;
    const column = columns[ids.indexOf(stored.sort.id)];
    return column && isColumnSortable(column) ? stored.sort : null;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [listId, stored.sort, ids.join("|"), columns.map((column) => (column.sort === undefined && column.compare === undefined ? "-" : "s")).join("")]);

  const sortedItems = useMemo(() => {
    if (!items) return [] as T[];
    if (!activeSort || options?.serverSort) return items as T[];
    return sortListItems(items, columns[ids.indexOf(activeSort.id)], activeSort.direction);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items, activeSort, options?.serverSort, ids.join("|")]);

  const persist = useCallback(
    (next: StoredState) => {
      setStored(next);
      if (listId) writeStored(listId, next);
    },
    [listId]
  );

  const toggleSort = useCallback(
    (id: string) => {
      const current = stored.sort;
      const next: ColumnSortState = { id, direction: current?.id === id && current.direction === "asc" ? "desc" : "asc" };
      persist({ order: stored.order, sort: next });
    },
    [persist, stored]
  );

  const setMovableOrder = useCallback(
    (nextOrder: string[]) => {
      const isDefault = nextOrder.length === defaultMovable.length && nextOrder.every((id, index) => id === defaultMovable[index]);
      persist({ order: isDefault ? [] : nextOrder, sort: stored.sort });
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [persist, stored.sort, defaultMovable.join("|")]
  );

  const reset = useCallback(() => persist({ order: [], sort: null }), [persist]);

  // A fresh set of rows (a new search) starts from the list's own order: the sort chosen for the
  // last results is dropped, the column order the person set is kept (#1418).
  const startOverWhen = options?.startOverWhen;
  const lastStartOver = useRef(startOverWhen);
  useEffect(() => {
    if (Object.is(lastStartOver.current, startOverWhen)) return;
    lastStartOver.current = startOverWhen;
    if (stored.sort) persist({ order: stored.order, sort: null });
    // Only a change of `startOverWhen` starts over; the stored state is read as it is then.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [startOverWhen]);

  const sortColumn = activeSort ? columns[ids.indexOf(activeSort.id)] : undefined;
  const changed = movableOrder.some((id, index) => id !== defaultMovable[index]) || activeSort !== null;

  return {
    listId,
    columns: ordered,
    sort: activeSort,
    serverSortKey: activeSort && typeof sortColumn?.sort === "string" ? sortColumn.sort : null,
    items: sortedItems,
    changed,
    toggleSort,
    setMovableOrder,
    movableOrder,
    reset
  };
}
