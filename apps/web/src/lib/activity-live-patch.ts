/**
 * Putting a pushed Activity event into the lists already on screen (#1179).
 *
 * `ActivityEventAdded` now carries the same facts `GET /api/activity` gives a row, so the
 * Activity history and the bell's Recent list take a new row straight from the push instead of
 * asking again on a timer. The history is filtered by the address, so each cached view first asks
 * whether the new event belongs in it: yes (add it), no (leave it) or "can't tell from the push"
 * (read that view again once).
 */
import type { ActivityEventItem } from "./api";
import { ACTIVITY_DEFAULT_SORT, resolveSince, resolveUntil, type ActivityFilters } from "./activity-history";
import type { ActivityEventAddedEvent } from "./use-signalr";

export type FilterMatch = "yes" | "no" | "unknown";

/** A pushed event as the row the list endpoint would have given. */
export function activityItemFromPush(event: ActivityEventAddedEvent): ActivityEventItem {
  return {
    id: event.id,
    category: event.category,
    label: event.label,
    message: event.message,
    severity: event.severity,
    detailsJson: event.detailsJson ?? null,
    relatedJobId: event.relatedJobId ?? null,
    relatedEntityType: event.relatedEntityType ?? null,
    relatedEntityId: event.relatedEntityId ?? null,
    createdUtc: event.createdUtc,
    title: event.title ?? null,
    filePath: event.filePath ?? null,
    sourceName: event.sourceName ?? null,
    reasonGroup: event.reasonGroup ?? null,
    type: event.type,
    source: event.source ?? null,
    outcome: event.outcome
  };
}

function includes(haystack: string | null | undefined, needle: string): boolean {
  return (haystack ?? "").toLowerCase().includes(needle.trim().toLowerCase());
}

/** Whether a new event belongs in the history view these filters ask for. */
export function activityEventMatches(filters: ActivityFilters, event: ActivityEventItem, now: Date = new Date()): FilterMatch {
  let unknown = false;
  const need = (value: string | null | undefined, test: (value: string) => boolean) => {
    if (value === null || value === undefined || value === "") {
      unknown = true;
      return true;
    }
    return test(value);
  };

  if (filters.type && event.type !== filters.type) return "no";
  if (filters.source && (event.source ?? "") !== filters.source) return "no";
  if (filters.outcome && (event.outcome ?? "") !== filters.outcome) return "no";
  if (filters.reason && (event.reasonGroup ?? "") !== filters.reason) return "no";
  if (filters.itemType && event.relatedEntityType && event.relatedEntityType !== filters.itemType) return "no";

  const created = new Date(event.createdUtc);
  const since = filters.since ? resolveSince(filters.since, now) : null;
  if (since && created < since) return "no";
  const until = filters.until ? resolveUntil(filters.until, now) : null;
  if (until && created >= until) return "no";

  if (filters.title && !need(event.title, (title) => includes(title, filters.title))) return "no";
  if (filters.file && !need(event.filePath, (path) => includes(path, filters.file))) return "no";
  if (filters.item) {
    if (event.relatedEntityId !== filters.item) unknown = true;
  }

  return unknown ? "unknown" : "yes";
}

interface HistoryPages {
  pages: { items: ActivityEventItem[] }[];
  pageParams: unknown[];
}

/** The first page with the new event at its head; the same data back if it is already there. */
export function addToHistory<T extends HistoryPages>(data: T, item: ActivityEventItem): T {
  if (data.pages.length === 0) return data;
  if (data.pages.some((page) => page.items.some((existing) => existing.id === item.id))) return data;
  const [first, ...rest] = data.pages;
  return { ...data, pages: [{ ...first, items: [item, ...first.items] }, ...rest] };
}

/** Whether a cached history view is newest first, the order a new event can simply be put at the head of. */
export function isNewestFirst(sortId: unknown, direction: unknown): boolean {
  return sortId === ACTIVITY_DEFAULT_SORT.id && direction === ACTIVITY_DEFAULT_SORT.direction;
}

/** A new row at the head of a flat list (the bell's Recent), once, capped. */
export function prependOnce(list: ActivityEventItem[], item: ActivityEventItem, limit: number): ActivityEventItem[] {
  if (list.some((existing) => existing.id === item.id)) return list;
  return [item, ...list].slice(0, limit);
}
