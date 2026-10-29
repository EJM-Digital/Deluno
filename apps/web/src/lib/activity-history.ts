/**
 * The Activity history's filters, kept in the address (#1100).
 *
 * A filtered view is a link: `/queue?view=history&type=turned-down&since=today&reason=language`. The page reads
 * the filters from the address and writes every change back to it, so a view can be bookmarked, shared
 * with a person, or opened from Live, a title's page or a file's page. This file is the one place that
 * turns the address into filters, the filters back into an address, and the filters into the question
 * asked of `GET /api/activity` -- the page and the links into it never build either by hand.
 */

import { historyPath } from "./transfers-view";

export interface ActivityFilters {
  /** One kind of event: search, turned-down, grabbed, imported, subtitles, blocklisted, removed, restored, failed, health, other. */
  type: string;
  /** Part of a title (film, show, season or episode). */
  title: string;
  /** Part of a file path or name. */
  file: string;
  /** indexer, client, processor or connection. */
  source: string;
  /** good or problem. */
  outcome: string;
  /** For turned-down releases, the plain reason group (language, notBetter, ...). */
  reason: string;
  /** today, yesterday, 7d, 30d or a day written YYYY-MM-DD. */
  since: string;
  /** A day written YYYY-MM-DD; that whole day is included. */
  until: string;
  /** The id of one title, from its page. */
  item: string;
  /** What kind of title the id belongs to (movie or series). */
  itemType: string;
}

/** The order here is the order the parameters are written in an address, so a link reads the way it is said. */
export const emptyActivityFilters: ActivityFilters = {
  type: "",
  since: "",
  until: "",
  reason: "",
  title: "",
  file: "",
  source: "",
  outcome: "",
  item: "",
  itemType: ""
};

export const ACTIVITY_FILTER_KEYS = Object.keys(emptyActivityFilters) as (keyof ActivityFilters)[];

export function filtersFromParams(params: URLSearchParams): ActivityFilters {
  const filters = { ...emptyActivityFilters };
  for (const key of ACTIVITY_FILTER_KEYS) {
    filters[key] = (params.get(key) ?? "").trim();
  }
  return filters;
}

/** The address for these filters, keeping every other parameter already in it (the section, for one). */
export function paramsFromFilters(filters: ActivityFilters, base?: URLSearchParams): URLSearchParams {
  const params = new URLSearchParams(base);
  for (const key of ACTIVITY_FILTER_KEYS) {
    const value = filters[key].trim();
    if (value) params.set(key, value);
    else params.delete(key);
  }
  return params;
}

export function hasActivityFilters(filters: ActivityFilters): boolean {
  return ACTIVITY_FILTER_KEYS.some((key) => filters[key].trim() !== "");
}

export function hasActivityFilterParams(params: URLSearchParams): boolean {
  return ACTIVITY_FILTER_KEYS.some((key) => (params.get(key) ?? "").trim() !== "");
}

/** Which part of the list Activity shows: everything, or one kind of step. No `status` in the address means all. */
export type ActivityStatus = "all" | "in-progress" | "done" | "failed" | "needs-decision";

export const ACTIVITY_STATUSES: readonly Exclude<ActivityStatus, "all">[] = ["in-progress", "done", "failed", "needs-decision"];

/**
 * The status a link asks for. `status` wins; an old `section` link (from before the tabs became chips)
 * means jobs -> In progress, imports -> Needs a decision, events -> All.
 */
export function activityStatusFromParams(params: URLSearchParams): ActivityStatus {
  const status = params.get("status");
  if (status && (ACTIVITY_STATUSES as readonly string[]).includes(status)) return status as ActivityStatus;
  switch (params.get("section")) {
    case "jobs":
      return "in-progress";
    case "imports":
      return "needs-decision";
    default:
      return "all";
  }
}

/** The address with this status chosen: `all` is the absence of one, and the old `section` goes. */
export function paramsWithStatus(status: ActivityStatus, base?: URLSearchParams): URLSearchParams {
  const params = new URLSearchParams(base);
  params.delete("section");
  if (status === "all") params.delete("status");
  else params.set("status", status);
  return params;
}

/** The status chips across the top of Activity, as they are written in the address (`status=...`). */
export type ActivityStatusChip = Exclude<ActivityStatus, "all">;

/**
 * The link into Activity on one status chip, optionally narrowed by the history filters:
 * `/queue?view=history&status=failed&since=today`. No status is All. Every link from Live, the bell, alerts and Transfers is
 * built here, so each lands on the matching chip.
 */
export function activityLinkForStatus(status?: ActivityStatusChip | null, filters?: Partial<ActivityFilters>): string {
  const query = paramsFromFilters({ ...emptyActivityFilters, ...filters }, status ? new URLSearchParams({ status }) : undefined).toString();
  return historyPath(query);
}

/** The link into the Activity history for a title (its own events, and any that name it). */
export function activityLinkForTitle(title: string, entityType: "movie" | "series", entityId: string): string {
  return activityLink({ title, item: entityId, itemType: entityType });
}

/** The link into the Activity history for a file. */
export function activityLinkForFile(path: string): string {
  return activityLink({ file: path });
}

/** The link into the Activity history with these filters. */
export function activityLink(filters: Partial<ActivityFilters>): string {
  return historyPath(paramsFromFilters({ ...emptyActivityFilters, ...filters }));
}

function localDayText(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/**
 * The link to one event in the history (the bell's rows): that event's own day, with the event opened.
 * The day keeps it inside the first page or two however long the history is.
 */
export function activityLinkForEvent(event: { id: string; createdUtc: string }): string {
  const day = localDayText(new Date(event.createdUtc));
  const params = new URLSearchParams({ event: event.id, since: day, until: day });
  return historyPath(params);
}

function startOfLocalDay(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate());
}

function addLocalDays(date: Date, days: number): Date {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate() + days);
}

function parseDay(token: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(token);
  if (!match) return null;
  const day = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return Number.isNaN(day.getTime()) ? null : day;
}

/** The moment a "since" word starts, in the person's own day. Null when it is not one. */
export function resolveSince(token: string, now: Date = new Date()): Date | null {
  switch (token) {
    case "":
      return null;
    case "today":
      return startOfLocalDay(now);
    case "yesterday":
      return addLocalDays(startOfLocalDay(now), -1);
    case "7d":
      return addLocalDays(startOfLocalDay(now), -6);
    case "30d":
      return addLocalDays(startOfLocalDay(now), -29);
    default:
      return parseDay(token);
  }
}

/** The moment just after an "until" day ends: that whole day is included. Null when it is not a day. */
export function resolveUntil(token: string, now: Date = new Date()): Date | null {
  if (token === "today") return addLocalDays(startOfLocalDay(now), 1);
  if (token === "yesterday") return startOfLocalDay(now);
  const day = parseDay(token);
  return day ? addLocalDays(day, 1) : null;
}

/** Which column the Activity list is sorted by, and which way. The server sorts before it pages (#1119). */
export interface ActivitySort {
  id: "title" | "what" | "when";
  direction: "asc" | "desc";
}

/** What the list is sorted by until a heading is chosen: when it happened, newest first. */
export const ACTIVITY_DEFAULT_SORT: ActivitySort = { id: "when", direction: "desc" };

/** The sort a remembered column sort means for Activity; anything it has no column for is the default. */
export function activitySortFrom(sort: { id: string; direction: "asc" | "desc" } | null | undefined): ActivitySort {
  return sort && (sort.id === "title" || sort.id === "what" || sort.id === "when") ? { id: sort.id, direction: sort.direction } : ACTIVITY_DEFAULT_SORT;
}

/**
 * The rows built in the browser (open work, live steps) in the order the list is sorted. The history's own rows
 * arrive already in order from the server and are not touched. Ties keep newest first, as the server does.
 */
export function sortActivityRows<T extends { title: string; detail?: string; whenUtc: string }>(rows: readonly T[], sort: ActivitySort): T[] {
  const sign = sort.direction === "asc" ? 1 : -1;
  const text = (row: T) => (sort.id === "title" ? row.title : row.detail ?? "").toLowerCase();
  const time = (row: T) => new Date(row.whenUtc).getTime();
  return [...rows].sort((a, b) => {
    if (sort.id === "when") return sign * (time(a) - time(b));
    const a1 = text(a);
    const b1 = text(b);
    return sign * (a1 < b1 ? -1 : a1 > b1 ? 1 : 0) || time(b) - time(a);
  });
}

/** The query string GET /api/activity is asked, for these filters. Newest first is the server's own order, so it is not asked for. */
export function activityQueryString(filters: ActivityFilters, pageSize: number, pageToken?: string, now: Date = new Date(), sort: ActivitySort = ACTIVITY_DEFAULT_SORT): string {
  const params = new URLSearchParams();
  params.set("pageSize", String(pageSize));
  if (filters.type) params.set("type", filters.type);
  if (filters.title) params.set("title", filters.title);
  if (filters.file) params.set("file", filters.file);
  if (filters.source) params.set("source", filters.source);
  if (filters.outcome) params.set("outcome", filters.outcome);
  if (filters.reason) params.set("reason", filters.reason);
  if (filters.item) {
    params.set("relatedEntityId", filters.item);
    if (filters.itemType) params.set("relatedEntityType", filters.itemType);
  }
  const since = resolveSince(filters.since, now);
  if (since) params.set("since", since.toISOString());
  const until = resolveUntil(filters.until, now);
  if (until) params.set("until", until.toISOString());
  if (sort.id !== ACTIVITY_DEFAULT_SORT.id || sort.direction !== ACTIVITY_DEFAULT_SORT.direction) {
    params.set("sortBy", sort.id);
    params.set("sortDir", sort.direction);
  }
  if (pageToken) params.set("pageToken", pageToken);
  return params.toString();
}

/** The plain words for a "since" choice. */
export const ACTIVITY_SINCE_CHOICES: readonly { value: string; label: string }[] = [
  { value: "", label: "Any time" },
  { value: "today", label: "Today" },
  { value: "yesterday", label: "Since yesterday" },
  { value: "7d", label: "Last 7 days" },
  { value: "30d", label: "Last 30 days" }
];

/** Whether a "since" value is one of the quick choices (anything else is a day picked by hand). */
export function isQuickSince(token: string): boolean {
  return ACTIVITY_SINCE_CHOICES.some((choice) => choice.value === token);
}

