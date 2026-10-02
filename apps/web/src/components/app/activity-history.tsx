/**
 * The Activity history's data and filters (#1100, #1114): everything that happens to a title, a release
 * or a file, newest first, filterable by title, file, kind of event, where it came from, how it went and when.
 *
 * Every filter lives in the address (`/activity?type=turned-down&since=today&reason=language`), so a
 * filtered view is a link: Live's Turned down card, a title's page and a file's page all open it. The
 * list is asked for page by page, so a long history costs what is on screen. The page lists these events
 * with the open work in one table; this file holds the question asked of the server and the filter bar.
 *
 * Contracts: GET /api/activity (paged, filtered), GET /api/activity/filters (the words each filter offers).
 */
import { useEffect, useMemo, useRef, useState } from "react";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { useSearchParams } from "react-router-dom";
import { fetchJson, fetchPage, type ActivityEventItem, type ActivityFilterChoices } from "../../lib/api";
import {
  ACTIVITY_SINCE_CHOICES,
  activityQueryString,
  emptyActivityFilters,
  filtersFromParams,
  hasActivityFilters,
  isQuickSince,
  paramsFromFilters,
  type ActivityFilters
} from "../../lib/activity-history";
import { foldCrossMediaJobRuns, foldRoutineJobEvents } from "../../lib/activity-format";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { MenuSelect } from "../ui/menu-select";
import { FILTER_MENU_TRIGGER, FILTER_ROW_CLASS } from "../ui/filter-chips";

const PAGE_SIZE = 50;


const FIELD_CLASS = "h-[var(--control-height-sm)] w-44 rounded-[8px] px-2.5 text-[length:var(--type-caption)]";

function useDebouncedWrite(value: string, urlValue: string, write: (next: string) => void) {
  useEffect(() => {
    if (value === urlValue) return;
    const handle = window.setTimeout(() => write(value), 350);
    return () => window.clearTimeout(handle);
    // write changes with the address; only a new draft should restart the wait.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);
}

/** The words each filter offers, from the server, so the screen carries no second list (#1100). */
export function useActivityFilterChoices() {
  return useQuery({
    queryKey: ["activity-filter-choices"],
    queryFn: () => fetchJson<ActivityFilterChoices>("/api/activity/filters"),
    staleTime: Infinity
  });
}

/** The filters in the address, how to change them, and the history they ask for (read only while `enabled`). */
export function useActivityHistory({ enabled, onOpenEvent }: { enabled: boolean; onOpenEvent: (event: ActivityEventItem) => void }) {
  const [searchParams, setSearchParams] = useSearchParams();
  const filters = useMemo(() => filtersFromParams(searchParams), [searchParams]);

  const setFilters = (patch: Partial<ActivityFilters>) =>
    setSearchParams((current) => paramsFromFilters({ ...filtersFromParams(current), ...patch }, current), { replace: true });
  const clearFilters = () => setSearchParams((current) => paramsFromFilters(emptyActivityFilters, current), { replace: true });

  const history = useInfiniteQuery({
    queryKey: ["activity-history", filters],
    queryFn: ({ pageParam }) => fetchPage<ActivityEventItem>(`/api/activity?${activityQueryString(filters, PAGE_SIZE, pageParam)}`),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => (page.hasMore ? page.nextPageToken ?? undefined : undefined),
    refetchInterval: 10_000,
    refetchIntervalInBackground: false,
    enabled
  });

  const events = useMemo(() => history.data?.pages.flatMap((page) => page.items) ?? [], [history.data]);

  // A link to one event (the bell's rows) opens it as soon as it has been read, reading older pages until it is found.
  const eventParam = searchParams.get("event");
  const openedFromAddress = useRef<string | null>(null);
  useEffect(() => {
    if (!enabled || !eventParam || openedFromAddress.current === eventParam) return;
    const hit = events.find((event) => event.id === eventParam);
    if (hit) {
      openedFromAddress.current = eventParam;
      onOpenEvent(hit);
    } else if (history.hasNextPage && !history.isFetchingNextPage) {
      void history.fetchNextPage();
    }
  }, [enabled, eventParam, events, history, onOpenEvent]);

  // A routine background job's queued/started/completed fold into the one row that answers "what happened" (#916).
  const folded = useMemo(() => foldCrossMediaJobRuns(foldRoutineJobEvents(events)), [events]);

  return { filters, setFilters, clearFilters, history, events, folded, filtered: hasActivityFilters(filters) };
}

/** The history filters, as a second line of the Activity card. */
export function ActivityFilterBar({
  filters,
  setFilters,
  clearFilters
}: {
  filters: ActivityFilters;
  setFilters: (patch: Partial<ActivityFilters>) => void;
  clearFilters: () => void;
}) {
  const choices = useActivityFilterChoices();

  // Typing waits a moment before it reaches the address, so every key is not a new question.
  const [titleDraft, setTitleDraft] = useState(filters.title);
  const [fileDraft, setFileDraft] = useState(filters.file);
  useEffect(() => setTitleDraft(filters.title), [filters.title]);
  useEffect(() => setFileDraft(filters.file), [filters.file]);
  useDebouncedWrite(titleDraft, filters.title, (next) => setFilters({ title: next }));
  useDebouncedWrite(fileDraft, filters.file, (next) => setFilters({ file: next }));

  const filtered = hasActivityFilters(filters);
  const customDays = filters.since !== "" && !isQuickSince(filters.since);
  const showCustomDays = customDays || filters.until !== "";
  const showReason = filters.type === "turned-down" || filters.reason !== "";

  const menu = (key: keyof ActivityFilters, label: string, any: string, options: { value: string; label: string }[] | undefined) => (
    <MenuSelect
      label={label}
      value={filters[key]}
      onChange={(value) => setFilters({ [key]: value })}
      options={[{ value: "", label: any }, ...(options ?? [])]}
      triggerClassName={FILTER_MENU_TRIGGER}
    />
  );

  return (
    <div className={FILTER_ROW_CLASS} role="group" aria-label="Filter the activity">
      <Input
        type="search"
        aria-label="Filter by title"
        placeholder="Title"
        value={titleDraft}
        onChange={(event) => setTitleDraft(event.target.value)}
        className={FIELD_CLASS}
      />
      <Input
        type="search"
        aria-label="Filter by file"
        placeholder="File or folder"
        value={fileDraft}
        onChange={(event) => setFileDraft(event.target.value)}
        className={FIELD_CLASS}
      />
      {menu("type", "Filter by event type", "All kinds of event", choices.data?.types)}
      {menu("source", "Filter by source", "Any source", choices.data?.sources)}
      {menu("outcome", "Filter by outcome", "Any outcome", choices.data?.outcomes)}
      {showReason ? menu("reason", "Filter by reason", "Any reason", choices.data?.reasons) : null}
      <MenuSelect
        label="Filter by date"
        value={customDays ? "custom" : filters.since}
        onChange={(value) => (value === "custom" ? setFilters({ since: filters.until || filters.since || "today" }) : setFilters({ since: value, until: "" }))}
        options={[...ACTIVITY_SINCE_CHOICES, { value: "custom", label: "Pick days" }]}
        triggerClassName={FILTER_MENU_TRIGGER}
      />
      {showCustomDays ? (
        <>
          <Input
            type="date"
            aria-label="From day"
            value={customDays ? filters.since : ""}
            onChange={(event) => setFilters({ since: event.target.value })}
            className={FIELD_CLASS}
          />
          <Input
            type="date"
            aria-label="Until day"
            value={filters.until}
            onChange={(event) => setFilters({ until: event.target.value })}
            className={FIELD_CLASS}
          />
        </>
      ) : null}
      {filters.item ? (
        <Button type="button" variant="outline" size="sm" onClick={() => setFilters({ item: "", itemType: "" })}>
          Only this title: stop
        </Button>
      ) : null}
      {filtered ? (
        <Button type="button" variant="outline" size="sm" onClick={clearFilters}>
          Clear filters
        </Button>
      ) : null}
    </div>
  );
}
