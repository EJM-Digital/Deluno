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
import { useState } from "react";
import { Eraser, RefreshCw } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerFacts } from "../components/ui/drawer";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { configurationNavAreas } from "../components/app/settings-shell";
import { formatDateTime, useDisplayPreferences } from "../lib/display-preferences";
import { fetchPage } from "../lib/api/client";
import type { ApiPage } from "../lib/api/types/catalogue";
import type { Tone } from "../lib/status-tones";
import { authedFetch } from "../lib/use-auth";
import { toast } from "../components/shell/toaster";
import { useLoaderData, useRevalidator } from "react-router-dom";

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

export function IndexerHistoryPage() {
  const { page } = useLoaderData() as LoaderData;
  const { preferences } = useDisplayPreferences();
  const revalidator = useRevalidator();

  const [items, setItems] = useState<IndexerHistoryItem[]>(page.items);
  const [nextPageToken, setNextPageToken] = useState<string | null>(page.nextPageToken);
  const [eventType, setEventType] = useState("");
  const [indexerName, setIndexerName] = useState("");
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [selected, setSelected] = useState<IndexerHistoryItem | null>(null);
  const [confirmClear, setConfirmClear] = useState(false);
  const [clearing, setClearing] = useState(false);

  async function applyFilters(nextEventType: string, nextIndexerName: string) {
    setLoading(true);
    try {
      const params = new URLSearchParams({ pageSize: "50" });
      if (nextEventType) params.set("eventType", nextEventType);
      if (nextIndexerName.trim()) params.set("indexerName", nextIndexerName.trim());
      const page = await fetchPage<IndexerHistoryItem>(`/api/indexer-history?${params.toString()}`);
      setItems(page.items);
      setNextPageToken(page.nextPageToken);
    } catch {
      toast.error("Indexer history could not be loaded.");
    } finally {
      setLoading(false);
    }
  }

  async function loadMore() {
    if (!nextPageToken) return;
    setLoadingMore(true);
    try {
      const params = new URLSearchParams({ pageSize: "50", pageToken: nextPageToken });
      if (eventType) params.set("eventType", eventType);
      if (indexerName.trim()) params.set("indexerName", indexerName.trim());
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
    <div className="flex flex-col gap-[var(--page-gap)]">
      <PageToolbar
        tabs={TABS}
        actions={
          <Button type="button" variant="outline" size="sm" onClick={() => setConfirmClear(true)} disabled={items.length === 0}>
            <Eraser aria-hidden className="h-3.5 w-3.5" />
            Clear
          </Button>
        }
      />

      <ListCard title="Indexer History" count={`${items.length}${nextPageToken ? "+" : ""} event${items.length === 1 && !nextPageToken ? "" : "s"} across every indexer`}>
        <div className="flex flex-wrap items-end gap-3 border-b border-hairline p-[var(--card-pad-x)]">
          <div className="grid gap-1">
            <label htmlFor="indexer-history-type" className="text-[length:var(--type-caption)] text-muted-foreground">Action</label>
            <Select
              value={eventType}
              onChange={(event) => { setEventType(event.target.value); void applyFilters(event.target.value, indexerName); }}
              options={EVENT_TYPE_OPTIONS}
              disabled={loading}
              id="indexer-history-type"
              aria-label="Filter by action type"
            />
          </div>
          <div className="grid gap-1">
            <label htmlFor="indexer-history-indexer" className="text-[length:var(--type-caption)] text-muted-foreground">Indexer</label>
            <Input
              id="indexer-history-indexer"
              value={indexerName}
              onChange={(event) => setIndexerName(event.target.value)}
              onBlur={() => void applyFilters(eventType, indexerName)}
              placeholder="Any indexer"
              className="max-w-[16rem]"
              aria-label="Filter by indexer name"
            />
          </div>
        </div>

        {items.length === 0 ? (
          <ListEmpty
            title="No indexer activity yet"
            description="RSS syncs, searches, grabs and auth failures across every indexer show up here as they happen."
          />
        ) : (
          <ListTable columns={[{ label: "Event" }, { label: "Indexer" }, { label: "Outcome", width: LIST_TRACK.status, mobile: true }, { label: "When", width: "180px" }]}>
            {items.map((item) => (
              <ListRow key={item.id} onClick={() => setSelected(item)}>
                <ListNameCell name={EVENT_TYPE_LABELS[item.eventType] ?? item.eventType} sub={item.message} />
                <ListCell primary={item.indexerName} />
                <ListCell mobile>
                  <Chip tone={eventTone(item)}>{item.outcome}</Chip>
                </ListCell>
                <ListCell numeric primary={formatDateTime(item.createdUtc, preferences)} />
              </ListRow>
            ))}
          </ListTable>
        )}

        {nextPageToken ? (
          <div className="flex justify-center p-[var(--card-pad-x)]">
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
              { label: "Outcome", value: selected.outcome },
              { label: "When", value: formatDateTime(selected.createdUtc, preferences) },
              ...(selected.relatedEntityType
                ? [{ label: "Related to", value: `${selected.relatedEntityType} ${selected.relatedEntityId ?? ""}` }]
                : []),
              ...(selected.detailsJson
                ? [{ label: "Request/response detail", value: <pre className="whitespace-pre-wrap break-words text-[length:var(--type-caption)]">{formatDetails(selected.detailsJson)}</pre>, mono: true }]
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
