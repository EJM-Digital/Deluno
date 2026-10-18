/**
 * Indexer History (#754) — every RSS sync, search, grab, and auth failure
 * across every indexer, newest first, in one combined feed.
 *
 *   PageToolbar (Find & Download tabs)
 *   Filters     (action type · indexer)
 *   ListTable   (row -> drawer with request/response detail)
 *   Clear       (confirm dialog, hides the combined view only)
 *
 * Contracts: GET /api/indexer-history, POST /api/indexer-history/clear.
 * The existing per-movie (/api/movies/search-history) and per-series
 * (/api/series/search-history) history endpoints are unchanged; this screen
 * reads the same underlying events through a separate, wider aggregation.
 */
import { useEffect, useRef, useState } from "react";
import { Eraser, RefreshCw } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerFacts } from "../components/ui/drawer";
import { MoreFilters } from "../components/ui/filter-chips";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK, type ListColumn } from "../components/ui/list-card";
import { useListColumns } from "../lib/use-list-columns";
import { PageToolbar } from "../components/ui/page-toolbar";
import { configurationNavAreas } from "../components/app/settings-shell";
import { formatDateTime, useDisplayPreferences } from "../lib/display-preferences";
import { indexerHistoryOutcomeWords } from "../lib/indexer-history-outcomes";
import { fetchPage } from "../lib/api/client";
import type { ApiPage } from "../lib/api/types/catalogue";
import type { Tone } from "../lib/status-tones";
import { authedFetch } from "../lib/use-auth";
import { toast } from "../components/shell/toaster";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";

const TABS = configurationNavAreas.find((area) => area.to === "/indexers/indexers")?.items ?? [];

export interface IndexerHistoryItem {
  id: string;
  indexerId: string | null;
  indexerName: string;
  eventType: "rss-sync" | "search" | "grab" | "auth-failure";
  outcome: string;
  message: string;
  detailsJson: string | null;
  relatedEntityType: string | null;
  relatedEntityId: string | null;
  createdUtc: string;
}

const EVENT_TYPE_OPTIONS = [
  { value: "", label: "Every action" },
  { value: "rss-sync", label: "RSS sync" },
  { value: "search", label: "Search" },
  { value: "grab", label: "Grab" },
  { value: "auth-failure", label: "Auth failure" }
];

const EVENT_TYPE_LABELS: Record<string, string> = {
  "rss-sync": "RSS sync",
  search: "Search",
  grab: "Grab",
  "auth-failure": "Auth failure"
};

// The feed loads 50 at a time, so the server does the sorting (#1119): a column header asks for the
// whole feed in that order, and "Show more" carries on in it.
const HISTORY_COLUMNS: Array<ListColumn<IndexerHistoryItem>> = [
  { id: "event", label: "Event", sort: "event" },
  { id: "indexer", label: "Indexer", sort: "indexer" },
  { id: "outcome", label: "Outcome", width: LIST_TRACK.status, mobile: true, sort: "outcome" },
  { id: "when", label: "When", width: "180px", sort: "when" }
];

function eventTone(item: IndexerHistoryItem): Tone {
  if (item.eventType === "auth-failure") return "bad";
  if (item.outcome === "failed") return "warn";
  if (item.eventType === "grab") return "ok";
  return "idle";
}

interface LoaderData {
  page: ApiPage<IndexerHistoryItem>;
}

export async function indexerHistoryLoader(): Promise<LoaderData> {
  const page = await fetchPage<IndexerHistoryItem>("/api/indexer-history?pageSize=50");
  return { page };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["IndexerChanged"] };

export function IndexerHistoryPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { page } = useLoaderData() as LoaderData;
  const { preferences } = useDisplayPreferences();
  const revalidator = useRevalidator();

  const [items, setItems] = useState<IndexerHistoryItem[]>(page.items);
  const [nextPageToken, setNextPageToken] = useState<string | null>(page.nextPageToken);
  const [eventType, setEventType] = useState("");
  const [indexerName, setIndexerName] = useState("");
  const [loadingMore, setLoadingMore] = useState(false);
  const [selected, setSelected] = useState<IndexerHistoryItem | null>(null);
  const [confirmClear, setConfirmClear] = useState(false);
  const [clearing, setClearing] = useState(false);

  // The router reads the loader again when the server says something changed (IndexerChanged), and the new first page
  // replaces what the list shows; without this the copy made on the first render never saw the new event.
  const seenPage = useRef(page);
  useEffect(() => {
    if (seenPage.current === page) return;
    seenPage.current = page;
    setItems(page.items);
    setNextPageToken(page.nextPageToken);
  }, [page]);

  const list = useListColumns("indexer-history", HISTORY_COLUMNS, items, { serverSort: true });
  const sortKey = list.sort && list.serverSortKey ? list.serverSortKey : "";
  const sortDirection = list.sort?.direction ?? "asc";
  const sortSignature = sortKey ? `${sortKey}:${sortDirection}` : "";
  const requestedSort = useRef("");

  function addSort(params: URLSearchParams) {
    if (sortKey) {
      params.set("sortBy", sortKey);
      params.set("sortDirection", sortDirection);
    }
  }

  async function applyFilters(nextEventType: string, nextIndexerName: string) {
    try {
      const params = new URLSearchParams({ pageSize: "50" });
      if (nextEventType) params.set("eventType", nextEventType);
      if (nextIndexerName.trim()) params.set("indexerName", nextIndexerName.trim());
      addSort(params);
      const page = await fetchPage<IndexerHistoryItem>(`/api/indexer-history?${params.toString()}`);
      setItems(page.items);
      setNextPageToken(page.nextPageToken);
    } catch {
      toast.error("Indexer history could not be loaded.");
    }
  }

  // A new sort (or one remembered from last time) starts the feed again from the top, in that order.
  useEffect(() => {
    if (requestedSort.current === sortSignature) return;
    requestedSort.current = sortSignature;
    void applyFilters(eventType, indexerName);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sortSignature]);

  // The indexer search is typed into the card's header, so it asks again a moment after the typing stops.
  const indexerTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  function changeIndexerName(next: string) {
    setIndexerName(next);
    if (indexerTimer.current) clearTimeout(indexerTimer.current);
    indexerTimer.current = setTimeout(() => void applyFilters(eventType, next), 350);
  }

  async function loadMore() {
    if (!nextPageToken) return;
    setLoadingMore(true);
    try {
      const params = new URLSearchParams({ pageSize: "50", pageToken: nextPageToken });
      if (eventType) params.set("eventType", eventType);
      if (indexerName.trim()) params.set("indexerName", indexerName.trim());
      addSort(params);
      const page = await fetchPage<IndexerHistoryItem>(`/api/indexer-history?${params.toString()}`);
      setItems((current) => [...current, ...page.items]);
      setNextPageToken(page.nextPageToken);
    } catch {
      toast.error("More indexer history could not be loaded.");
    } finally {
      setLoadingMore(false);
    }
  }

  async function clearHistory() {
    setClearing(true);
    try {
      const response = await authedFetch("/api/indexer-history/clear", { method: "POST" });
      if (!response.ok) throw new Error("Indexer history could not be cleared.");
      setConfirmClear(false);
      toast.success("Indexer history cleared");
      setItems([]);
      setNextPageToken(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Indexer history could not be cleared.");
    } finally {
      setClearing(false);
    }
  }

  return (
    <div className="flex flex-col gap-(--page-gap)">
      <PageToolbar
        tabs={TABS}
        actions={
          <Button type="button" variant="outline" size="sm" onClick={() => setConfirmClear(true)} disabled={items.length === 0}>
            <Eraser aria-hidden className="h-3.5 w-3.5" />
            Clear
          </Button>
        }
      />

      <ListCard
        title={`${items.length}${nextPageToken ? "+" : ""} event${items.length === 1 && !nextPageToken ? "" : "s"}`}
        titleCase={false}
        filter={{ value: indexerName, onChange: changeIndexerName, placeholder: "Find an indexer" }}
        actions={
          <MoreFilters
            groups={[{
              label: "Action",
              ariaLabel: "Filter by action type",
              value: eventType,
              onValueChange: (next) => { setEventType(next); void applyFilters(next, indexerName); },
              options: EVENT_TYPE_OPTIONS.map((option) => (option.value === "" ? { ...option, label: "Any action" } : option))
            }]}
          />
        }
      >
        {items.length === 0 ? (
          <ListEmpty
            title="No indexer activity yet"
            description="RSS syncs, searches, grabs and auth failures across every indexer show up here as they happen."
          />
        ) : (
          <ListTable list={list}>
            {() => items.map((item) => (
              <ListRow key={item.id} onClick={() => setSelected(item)}>
                <ListNameCell name={EVENT_TYPE_LABELS[item.eventType] ?? item.eventType} sub={item.message} />
                <ListCell primary={item.indexerName} />
                <ListCell mobile>
                  <Chip tone={eventTone(item)}>{indexerHistoryOutcomeWords(item.outcome)}</Chip>
                </ListCell>
                <ListCell numeric primary={formatDateTime(item.createdUtc, preferences)} />
              </ListRow>
            ))}
          </ListTable>
        )}

        {nextPageToken ? (
          <div className="flex justify-center p-(--card-pad-x)">
            <Button type="button" variant="outline" size="sm" onClick={() => void loadMore()} disabled={loadingMore}>
              <RefreshCw aria-hidden className={`h-3.5 w-3.5 ${loadingMore ? "animate-spin" : ""}`} />
              {loadingMore ? "Loading…" : "Load more"}
            </Button>
          </div>
        ) : null}
      </ListCard>

      <Drawer
        open={selected !== null}
        onOpenChange={(open) => { if (!open) setSelected(null); }}
        title={selected ? EVENT_TYPE_LABELS[selected.eventType] ?? selected.eventType : ""}
        description={selected?.message}
      >
        {selected ? (
          <DrawerFacts
            items={[
              { label: "Indexer", value: selected.indexerName },
              { label: "Outcome", value: indexerHistoryOutcomeWords(selected.outcome) },
              { label: "When", value: formatDateTime(selected.createdUtc, preferences) },
              ...(selected.relatedEntityType
                ? [{ label: "Related to", value: `${selected.relatedEntityType} ${selected.relatedEntityId ?? ""}` }]
                : []),
              ...(selected.detailsJson
                ? [{ label: "Request/response detail", value: <pre className="whitespace-pre-wrap wrap-break-word text-(length:--type-caption)">{formatDetails(selected.detailsJson)}</pre>, mono: true }]
                : [])
            ]}
          />
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmClear}
        onOpenChange={setConfirmClear}
        title="Clear Indexer History?"
        description="This clears the combined feed of RSS syncs, searches, grabs and auth failures shown here. It never removes the per-movie or per-series search history those screens show — this cannot be undone from this screen alone."
        confirmLabel="Clear history"
        busy={clearing}
        onConfirm={() => void clearHistory()}
      />
    </div>
  );
}

function formatDetails(detailsJson: string): string {
  try {
    return JSON.stringify(JSON.parse(detailsJson), null, 2);
  } catch {
    return detailsJson;
  }
}
