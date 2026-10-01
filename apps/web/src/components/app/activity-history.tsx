/**
 * The Activity history (#1100): everything that happens to a title, a release or a file, newest first,
 * filterable by title, file, kind of event, where it came from, how it went and when.
 *
 * Every filter lives in the address (`/activity?type=turned-down&since=today&reason=language`), so a
 * filtered view is a link: Live's Turned down card, a title's page and a file's page all open it. The
 * list is asked for page by page, so a long history costs what is on screen.
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
import {
  eventLabel,
  foldCrossMediaJobRuns,
  foldRoutineJobEvents
} from "../../lib/activity-format";
import { formatDateTime, useDisplayPreferences } from "../../lib/display-preferences";
import { useColumnVisibility, type ColumnDefinition } from "../../lib/use-column-visibility";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { ColumnPicker } from "../ui/column-picker";
import { Input } from "../ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../ui/list-card";
import { MenuSelect } from "../ui/menu-select";

/** Optional columns of the history table, remembered per person (#798). */
const HISTORY_COLUMNS: readonly ColumnDefinition[] = [
  { key: "about", label: "About", defaultVisible: true },
  { key: "from", label: "From", defaultVisible: true },
  { key: "kind", label: "Kind", defaultVisible: true },
  { key: "when", label: "When", defaultVisible: true }
];

const PAGE_SIZE = 50;

const MENU_TRIGGER =
  "h-[var(--control-height-sm)] rounded-[8px] border border-hairline bg-surface-1 px-2.5 text-[length:var(--type-caption)] font-semibold text-muted-foreground hover:text-foreground";

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

export function ActivityHistory({
  openEventId,
  onOpenEvent,
  onLoaded
}: {
  openEventId: string | null;
  onOpenEvent: (event: ActivityEventItem) => void;
  /** Hands the page every event read so far, for what the drawer needs beside the one it shows. */
  onLoaded?: (events: ActivityEventItem[]) => void;
}) {
  const [searchParams, setSearchParams] = useSearchParams();
  const filters = useMemo(() => filtersFromParams(searchParams), [searchParams]);
  const { preferences } = useDisplayPreferences();
  const columns = useColumnVisibility("activity-page.history-columns", HISTORY_COLUMNS);

  const setFilters = (patch: Partial<ActivityFilters>) =>
    setSearchParams((current) => paramsFromFilters({ ...filtersFromParams(current), ...patch }, current), { replace: true });

  // Typing waits a moment before it reaches the address, so every key is not a new question.
  const [titleDraft, setTitleDraft] = useState(filters.title);
  const [fileDraft, setFileDraft] = useState(filters.file);
  useEffect(() => setTitleDraft(filters.title), [filters.title]);
  useEffect(() => setFileDraft(filters.file), [filters.file]);
  useDebouncedWrite(titleDraft, filters.title, (next) => setFilters({ title: next }));
  useDebouncedWrite(fileDraft, filters.file, (next) => setFilters({ file: next }));

  const choices = useActivityFilterChoices();

  const history = useInfiniteQuery({
    queryKey: ["activity-history", filters],
    queryFn: ({ pageParam }) => fetchPage<ActivityEventItem>(`/api/activity?${activityQueryString(filters, PAGE_SIZE, pageParam)}`),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => (page.hasMore ? page.nextPageToken ?? undefined : undefined),
    refetchInterval: 10_000,
    refetchIntervalInBackground: false
  });

  const events = useMemo(() => history.data?.pages.flatMap((page) => page.items) ?? [], [history.data]);
  useEffect(() => onLoaded?.(events), [events, onLoaded]);

  // A link to one event (the bell's rows) opens it as soon as it has been read, reading older pages until it is found.
  const eventParam = searchParams.get("event");
  const openedFromAddress = useRef<string | null>(null);
  useEffect(() => {
    if (!eventParam || openedFromAddress.current === eventParam) return;
    const hit = events.find((event) => event.id === eventParam);
    if (hit) {
      openedFromAddress.current = eventParam;
      onOpenEvent(hit);
    } else if (history.hasNextPage && !history.isFetchingNextPage) {
      void history.fetchNextPage();
    }
  }, [eventParam, events, history, onOpenEvent]);

  // A routine background job's queued/started/completed fold into the one row that answers "what happened" (#916).
  const folded = useMemo(() => foldCrossMediaJobRuns(foldRoutineJobEvents(events)), [events]);

  const sourceWords = useMemo(
    () => new Map((choices.data?.sources ?? []).map((choice) => [choice.value, choice.label])),
    [choices.data]
  );
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
      triggerClassName={MENU_TRIGGER}
    />
  );

  const countText = history.isPending
    ? undefined
    : events.length === 0
      ? undefined
      : history.hasNextPage
        ? `Latest ${events.length}, more below`
        : `${events.length} ${events.length === 1 ? "event" : "events"}`;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Filter the history">
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
          triggerClassName={MENU_TRIGGER}
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
          <Button type="button" variant="outline" size="sm" onClick={() => setSearchParams((current) => paramsFromFilters(emptyActivityFilters, current), { replace: true })}>
            Clear filters
          </Button>
        ) : null}
      </div>

      <ListCard
        title="History"
        count={countText}
        actions={<ColumnPicker columns={HISTORY_COLUMNS} isVisible={columns.isVisible} onToggle={columns.toggle} />}
      >
        {history.isPending ? (
          <ListEmpty title="Reading the history" description="This takes a moment." />
        ) : history.isError ? (
          <ListEmpty
            title="The history could not be read"
            description="Deluno could not load it just now."
            actions={
              <Button type="button" variant="outline" size="sm" onClick={() => void history.refetch()}>
                Try again
              </Button>
            }
          />
        ) : folded.length === 0 ? (
          filtered ? (
            <ListEmpty
              title="Nothing matches these filters"
              description="Widen the dates, or clear a filter, to see more."
              actions={
                <Button type="button" variant="outline" size="sm" onClick={() => setSearchParams((current) => paramsFromFilters(emptyActivityFilters, current), { replace: true })}>
                  Clear filters
                </Button>
              }
            />
          ) : (
            <ListEmpty title="Nothing has happened yet" description="Every consequential thing Deluno does is recorded here, with the reason behind it." />
          )
        ) : (
          <>
            <ListTable
              columns={[
                { label: "Event" },
                ...(columns.isVisible("about") ? [{ label: "About", mobile: true as const }] : []),
                ...(columns.isVisible("from") ? [{ label: "From" }] : []),
                ...(columns.isVisible("kind") ? [{ label: "Kind" }] : []),
                ...(columns.isVisible("when") ? [{ label: "When" }] : [])
              ]}
            >
              {folded.map(({ event, foldedFrom }) => (
                <ListRow key={event.id} onClick={() => onOpenEvent(event)} selected={openEventId === event.id}>
                  <ListNameCell
                    name={event.message}
                    sub={
                      foldedFrom.length
                        ? [event.detail, `+${foldedFrom.length} more step${foldedFrom.length === 1 ? "" : "s"} for this run — open to see them`]
                            .filter(Boolean)
                            .join(" · ")
                        : event.detail
                    }
                  />
                  {columns.isVisible("about") ? <ListCell primary={event.title ?? "—"} mobile /> : null}
                  {columns.isVisible("from") ? (
                    <ListCell primary={event.sourceName ?? (event.source ? sourceWords.get(event.source) : undefined) ?? "—"} />
                  ) : null}
                  {columns.isVisible("kind") ? (
                    <ListCell>
                      <span className="flex min-w-0 items-center gap-2">
                        <span className="truncate text-foreground">{eventLabel(event)}</span>
                        {event.outcome === "problem" ? <Chip tone="bad">Went wrong</Chip> : null}
                      </span>
                    </ListCell>
                  ) : null}
                  {columns.isVisible("when") ? <ListCell primary={formatDateTime(event.createdUtc, preferences)} /> : null}
                </ListRow>
              ))}
            </ListTable>
            {history.hasNextPage ? (
              <div className="flex justify-center border-t border-hairline p-3">
                <Button type="button" variant="outline" size="sm" onClick={() => void history.fetchNextPage()} disabled={history.isFetchingNextPage}>
                  {history.isFetchingNextPage ? "Loading" : "Show older events"}
                </Button>
              </div>
            ) : null}
          </>
        )}
      </ListCard>
    </div>
  );
}
