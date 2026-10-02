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
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
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
import { X } from "lucide-react";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { MenuSelect } from "../ui/menu-select";
import { HeaderTabsPortal, useHeaderSlot } from "../ui/header-tabs";
import { cn } from "../../lib/utils";

const PAGE_SIZE = 50;


/** The height of the chips box in the header (Weir: `.mm-header__slot .mm-segmented`), shared by the search and the dropdowns. */
const CONTROL_HEIGHT = "h-[2.375rem]";
const MENU_TRIGGER = `${CONTROL_HEIGHT} rounded-[12px] border border-hairline bg-card px-3 text-[length:var(--type-control)] font-medium text-muted-foreground hover:text-foreground`;

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

/**
 * What narrows the list, as Weir's History draws it (James, 2 Oct 2026): a search, the status chips and, at the right,
 * two dropdowns, on the header's title line after the title. Where the title, these and the page's buttons do not
 * fit that line, measured, they take a row of their own directly under the header, in the same order; below `lg`
 * they are always in that row. The chips are passed in because the page owns the status and its counts.
 */
export function ActivityFilters({
  filters,
  setFilters,
  chips
}: {
  filters: ActivityFilters;
  setFilters: (patch: Partial<ActivityFilters>) => void;
  chips: React.ReactNode;
}) {
  const choices = useActivityFilterChoices();
  const slot = useHeaderSlot();
  const controlsRef = useRef<HTMLDivElement>(null);
  const [fits, setFits] = useState(true);

  // Typing waits a moment before it reaches the address, so every key is not a new question.
  const [titleDraft, setTitleDraft] = useState(filters.title);
  useEffect(() => setTitleDraft(filters.title), [filters.title]);
  useDebouncedWrite(titleDraft, filters.title, (next) => setFilters({ title: next }));

  // The title line holds them only while the title, their least width and the page's buttons fit `#topbar-main`.
  const measure = () => {
    if (!slot) return;
    const controls = controlsRef.current;
    const main = document.getElementById("topbar-main");
    const title = document.getElementById("topbar-title");
    if (!controls || !main || !title) return;
    const search = controls.querySelector<HTMLElement>("[data-activity-search]");
    const chipsBox = controls.querySelector<HTMLElement>("[data-activity-chips] > *");
    const scope = controls.querySelector<HTMLElement>("[data-activity-scope]");
    if (!search || !chipsBox || !scope) return;
    const minimum = (parseFloat(getComputedStyle(search).minWidth) || 0) + chipsBox.scrollWidth + scope.offsetWidth + 16;
    const actions = document.getElementById("topbar-page-actions");
    const buttons = actions && actions.childElementCount ? actions.getBoundingClientRect().width : 0;
    const mainGap = parseFloat(getComputedStyle(main).columnGap) || 0;
    const titleGap = parseFloat(getComputedStyle(title.parentElement ?? title).columnGap) || 0;
    const room = main.getBoundingClientRect().width - (buttons > 0 ? buttons + mainGap : 0);
    setFits(title.getBoundingClientRect().width + titleGap + minimum <= room);
  };
  useLayoutEffect(measure);
  useLayoutEffect(() => {
    if (!slot || typeof ResizeObserver === "undefined") return undefined;
    const observer = new ResizeObserver(measure);
    for (const id of ["topbar-main", "topbar-title", "topbar-page-actions"]) {
      const element = document.getElementById(id);
      if (element) observer.observe(element);
    }
    const fonts = typeof document !== "undefined" ? document.fonts : undefined;
    void fonts?.ready.then(measure);
    return () => observer.disconnect();
    // measure reads only the document and the controls' own box.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [slot]);

  const inHeader = slot !== null && fits;
  const customSince = filters.since !== "" && !isQuickSince(filters.since);
  const typeOptions = [{ value: "", label: "Any step" }, ...(choices.data?.types ?? [])];
  const sinceOptions = customSince
    ? [...ACTIVITY_SINCE_CHOICES, { value: filters.since, label: `Since ${filters.since}` }]
    : ACTIVITY_SINCE_CHOICES.map((choice) => ({ ...choice }));

  const controls = (
    <div
      ref={controlsRef}
      role="group"
      aria-label="Filter the activity"
      data-testid="activity-filters"
      data-placement={inHeader ? "header" : "row"}
      data-header-controls={inHeader ? "" : undefined}
      className={cn("flex min-w-0 items-center gap-2", inHeader ? "flex-1" : "w-full pt-3 max-lg:flex-wrap")}
    >
      <Input
        data-activity-search
        type="search"
        aria-label="Find a title"
        placeholder="Find a title"
        value={titleDraft}
        onChange={(event) => setTitleDraft(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === "Escape") event.currentTarget.blur();
        }}
        className={cn(CONTROL_HEIGHT, "w-auto min-w-[6.5rem] flex-[0_100_260px] rounded-[12px] max-lg:max-w-[22rem] max-lg:flex-[1_1_12rem]")}
      />
      <div data-activity-chips className="flex min-w-0 shrink items-center max-lg:order-3 max-lg:basis-full">
        {chips}
      </div>
      <div data-activity-scope className="ml-auto flex shrink-0 items-center gap-2">
        <MenuSelect
          label="Filter by step"
          value={filters.type}
          onChange={(value) => setFilters({ type: value })}
          options={typeOptions}
          className="w-[9rem]"
          triggerClassName={MENU_TRIGGER}
        />
        <MenuSelect
          label="Filter by time"
          value={filters.since}
          onChange={(value) => setFilters({ since: value, until: "" })}
          options={sinceOptions}
          className="w-[9rem]"
          triggerClassName={MENU_TRIGGER}
        />
      </div>
    </div>
  );

  return inHeader ? <HeaderTabsPortal slot={slot}>{controls}</HeaderTabsPortal> : controls;
}

/**
 * The filters that no longer have a control but that a link can still set (a file, a source, an outcome, a reason,
 * one title, an end day), as removable tokens at the start of the list card, with one link to clear everything.
 */
export function ActivityFilterTokens({
  filters,
  setFilters,
  clearFilters
}: {
  filters: ActivityFilters;
  setFilters: (patch: Partial<ActivityFilters>) => void;
  clearFilters: () => void;
}) {
  const choices = useActivityFilterChoices();
  const word = (list: { value: string; label: string }[] | undefined, value: string) => list?.find((choice) => choice.value === value)?.label ?? value;
  const tokens: { key: string; text: string; clear: Partial<ActivityFilters> }[] = [];
  if (filters.file) tokens.push({ key: "file", text: `File: ${filters.file}`, clear: { file: "" } });
  if (filters.source) tokens.push({ key: "source", text: `From: ${word(choices.data?.sources, filters.source)}`, clear: { source: "" } });
  if (filters.outcome) tokens.push({ key: "outcome", text: `Outcome: ${word(choices.data?.outcomes, filters.outcome)}`, clear: { outcome: "" } });
  if (filters.reason) tokens.push({ key: "reason", text: `Reason: ${word(choices.data?.reasons, filters.reason)}`, clear: { reason: "" } });
  if (filters.item) tokens.push({ key: "item", text: "Only this title", clear: { item: "", itemType: "" } });
  else if (filters.itemType) tokens.push({ key: "itemType", text: `Kind of title: ${filters.itemType}`, clear: { itemType: "" } });
  if (filters.until) tokens.push({ key: "until", text: `Until: ${filters.until}`, clear: { until: "" } });
  if (tokens.length === 0) return null;
  return (
    <div className="flex flex-wrap items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2" role="group" aria-label="Filters set by a link">
      {tokens.map((token) => (
        <span key={token.key} className="inline-flex h-[var(--control-height-sm)] max-w-full items-center gap-1 rounded-full border border-hairline bg-surface-1 pl-3 pr-1 text-[length:var(--type-caption)] font-medium text-foreground">
          <span className="truncate">{token.text}</span>
          <button
            type="button"
            aria-label={`Remove filter ${token.text}`}
            onClick={() => setFilters(token.clear)}
            className="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-full text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <X aria-hidden className="h-3 w-3" />
          </button>
        </span>
      ))}
      <Button type="button" variant="ghost" size="sm" onClick={clearFilters}>
        Clear filters
      </Button>
    </div>
  );
}
