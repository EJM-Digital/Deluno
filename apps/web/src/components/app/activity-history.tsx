/**
 * The Activity history's data and filters (#1100, #1114): everything that happens to a title, a release
 * or a file, newest first unless a heading sorts it, filterable by title, file, kind of event, where it came from, how it went and when.
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
  ACTIVITY_DEFAULT_SORT,
  ACTIVITY_SINCE_CHOICES,
  activityQueryString,
  emptyActivityFilters,
  filtersFromParams,
  hasActivityFilters,
  isQuickSince,
  paramsFromFilters,
  type ActivityFilters,
  type ActivitySort
} from "../../lib/activity-history";
import { isLiveStep, LIVE_STEPS } from "../../lib/activity-live-steps";
import { foldCrossMediaJobRuns, foldRoutineJobEvents } from "../../lib/activity-format";
import { Search, X } from "lucide-react";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { FILTER_MENU_TRIGGER } from "../ui/filter-chips";
import { MenuSelect } from "../ui/menu-select";
import { HeaderTabsPortal, useHeaderSlot } from "../ui/header-tabs";
import { cn } from "../../lib/utils";

const PAGE_SIZE = 50;


/** The height of the chips box in the header (Weir: `.mm-header__slot .mm-segmented`), shared by the search and the dropdowns. */
const CONTROL_HEIGHT = "h-[2.375rem]";
/** How wide each stage of the group is, in rem, for deciding which stage the title line can hold (#1115). */
const SEARCH_OPEN_REM = 11;
const MAGNIFIER_REM = 2.375;
const SCOPE_FULL_REM = 18.5;
const SCOPE_COMPACT_REM = 15.5;
const CHIPS_SCROLL_REM = 9;

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
export function useActivityHistory({ enabled, onOpenEvent, sort = ACTIVITY_DEFAULT_SORT }: { enabled: boolean; onOpenEvent: (event: ActivityEventItem) => void; sort?: ActivitySort }) {
  const [searchParams, setSearchParams] = useSearchParams();
  const filters = useMemo(() => filtersFromParams(searchParams), [searchParams]);

  const setFilters = (patch: Partial<ActivityFilters>) =>
    setSearchParams((current) => paramsFromFilters({ ...filtersFromParams(current), ...patch }, current), { replace: true });
  const clearFilters = () => setSearchParams((current) => paramsFromFilters(emptyActivityFilters, current), { replace: true });

  const history = useInfiniteQuery({
    // The sort is part of the question, so choosing another starts again from the first page, sorted on the server (#1119).
    queryKey: ["activity-history", filters, sort.id, sort.direction],
    queryFn: ({ pageParam }) => fetchPage<ActivityEventItem>(`/api/activity?${activityQueryString(filters, PAGE_SIZE, pageParam, undefined, sort)}`),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => (page.hasMore ? page.nextPageToken ?? undefined : undefined),
    refetchInterval: 10_000,
    refetchIntervalInBackground: false,
    // The three steps built from live sources never ask the history.
    enabled: enabled && !isLiveStep(filters.type)
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
 * two dropdowns, on the header's title line after the title. As the line runs short they give way in turn (#1115): the
 * search becomes a 38px magnifier that opens to 11rem while focused or holding text, then the dropdowns go to the compact
 * 28px size, then the chips scroll sideways; only when even that does not fit, measured, they take a row of their own
 * directly under the header, in the same order. Below `lg` they are always in that row. The chips are passed in because the page owns the status and its counts.
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
  // 0 = everything full size; 1 = search is a magnifier; 2 = dropdowns compact too; 3 = chips scroll too; null = does not fit.
  const [stage, setStage] = useState<0 | 1 | 2 | 3 | null>(0);
  const [searchFocused, setSearchFocused] = useState(false);

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
    const chipsBox = controls.querySelector<HTMLElement>("[data-activity-chips] > *");
    if (!chipsBox) return;
    const rem = parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
    const chipsWidth = chipsBox.scrollWidth;
    const actions = document.getElementById("topbar-page-actions");
    const buttons = actions && actions.childElementCount ? actions.getBoundingClientRect().width : 0;
    const mainGap = parseFloat(getComputedStyle(main).columnGap) || 0;
    const titleGap = parseFloat(getComputedStyle(title.parentElement ?? title).columnGap) || 0;
    const room = main.getBoundingClientRect().width - (buttons > 0 ? buttons + mainGap : 0) - title.getBoundingClientRect().width - titleGap;
    // Each stage's least width, with the 16px of gaps between the three parts.
    const need = (search: number, chipsMin: number, scope: number) => (search + scope) * rem + chipsMin + 16;
    const stages = [
      need(SEARCH_OPEN_REM, chipsWidth, SCOPE_FULL_REM),
      need(MAGNIFIER_REM, chipsWidth, SCOPE_FULL_REM),
      need(MAGNIFIER_REM, chipsWidth, SCOPE_COMPACT_REM),
      need(MAGNIFIER_REM, CHIPS_SCROLL_REM * rem, SCOPE_COMPACT_REM)
    ];
    const found = stages.findIndex((width) => width <= room);
    setStage(found < 0 ? null : (found as 0 | 1 | 2 | 3));
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

  const inHeader = slot !== null && stage !== null;
  // In the row under the header there is room for everything, at full size.
  const level = inHeader ? stage ?? 0 : 0;
  const magnifier = level >= 1;
  const compact = level >= 2;
  const searchOpen = searchFocused || titleDraft !== "";
  const customSince = filters.since !== "" && !isQuickSince(filters.since);
  const typeOptions = [{ value: "", label: "Any step" }, ...(choices.data?.types ?? []), ...LIVE_STEPS.map((step) => ({ value: step.value, label: step.label }))];
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
      <div
        className={cn(
          "relative min-w-0 shrink-0 transition-[width] duration-150 motion-reduce:transition-none",
          magnifier ? (searchOpen ? "w-[11rem]" : "w-[2.375rem]") : "w-auto flex-[0_100_260px] max-lg:max-w-[22rem] max-lg:flex-[1_1_12rem]"
        )}
      >
        {magnifier ? <Search aria-hidden className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" /> : null}
        <Input
          data-activity-search
          type="search"
          aria-label="Find a title"
          placeholder="Find a title"
          value={titleDraft}
          onChange={(event) => setTitleDraft(event.target.value)}
          onFocus={() => setSearchFocused(true)}
          onBlur={() => setSearchFocused(false)}
          onKeyDown={(event) => {
            if (event.key === "Escape") event.currentTarget.blur();
          }}
          className={cn(CONTROL_HEIGHT, "rounded-[12px]", magnifier ? "cursor-pointer pl-9 pr-2 focus:cursor-text" : "min-w-[6.5rem]", magnifier && !searchOpen && "placeholder:text-transparent")}
        />
      </div>
      <div data-activity-chips className="flex min-w-0 shrink items-center max-lg:order-3 max-lg:basis-full">
        {chips}
      </div>
      <div data-activity-scope className="ml-auto flex shrink-0 items-center gap-2">
        <MenuSelect
          label="Filter by step"
          value={filters.type}
          onChange={(value) => setFilters({ type: value })}
          options={typeOptions}
          className={compact ? "w-[7.5rem]" : "w-[9rem]"}
          triggerClassName={compact ? FILTER_MENU_TRIGGER : MENU_TRIGGER}
        />
        <MenuSelect
          label="Filter by time"
          value={filters.since}
          onChange={(value) => setFilters({ since: value, until: "" })}
          options={sinceOptions}
          className={compact ? "w-[7.5rem]" : "w-[9rem]"}
          triggerClassName={compact ? FILTER_MENU_TRIGGER : MENU_TRIGGER}
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
