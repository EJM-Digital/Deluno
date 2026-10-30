/**
 * #840's cross-library subtitle Wanted screen: every title or episode, in
 * every library, still short of a wanted subtitle language — reachable from
 * the same navigation area as the languages and providers settings screens,
 * but this one is not a setting, it is a worklist.
 *
 * Contracts: GET /api/subtitles/wanted, POST /api/subtitles/wanted/search-all.
 */
import { useEffect, useRef, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { RefreshCw, Search } from "lucide-react";
import { toast } from "sonner";
import { fetchPage } from "../lib/api/client";
import type { ApiPage } from "../lib/api/types/catalogue";
import type { MediaSubtitleAttemptHistoryEntry } from "../lib/api/types/resources";
import { authedFetch } from "../lib/use-auth";
import { describeRequestFailure } from "../lib/search-reasons";
import { configurationNavAreas } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";

const TABS = configurationNavAreas.find((area) => area.to === "/settings/libraries")?.items ?? [];

interface SubtitleWantedRow {
  kind: "Movie" | "Series" | string;
  mediaId: string;
  libraryId: string;
  libraryName: string;
  title: string;
  year: number | null;
  seasonNumber: number | null;
  episodeNumber: number | null;
  episodeTitle: string | null;
  languagesMissing: string[];
  /**
   * This title's single most recent subtitle search attempt (#982), from
   * the same append-only history the title's own subtitle panel shows.
   * Null when nothing has been attempted yet.
   */
  lastAttempt: MediaSubtitleAttemptHistoryEntry | null;
}

function lastTryLabel(row: SubtitleWantedRow): string {
  if (!row.lastAttempt) {
    return "Not tried yet";
  }
  const when = new Date(row.lastAttempt.attemptedUtc).toLocaleString();
  const provider = row.lastAttempt.provider ? ` via ${row.lastAttempt.provider}` : "";
  return `${row.lastAttempt.outcome}${provider} — ${when}`;
}

/** The wanted list loads 100 titles at a time; the server also says how many are waiting in all. */
type WantedPage = ApiPage<SubtitleWantedRow> & { totalCount?: number };

const PAGE_SIZE = 100;

export async function subtitleWantedLoader() {
  const page = await fetchPage<SubtitleWantedRow>(`/api/subtitles/wanted?pageSize=${PAGE_SIZE}`) as WantedPage;
  return { wanted: page.items, nextPageToken: page.nextPageToken, totalCount: page.totalCount ?? page.items.length };
}

function rowLabel(row: SubtitleWantedRow): string {
  if (row.kind === "Series" && row.seasonNumber !== null && row.episodeNumber !== null) {
    return `S${String(row.seasonNumber).padStart(2, "0")}E${String(row.episodeNumber).padStart(2, "0")}${row.episodeTitle ? ` — ${row.episodeTitle}` : ""}`;
  }
  return row.year ? `${row.year}` : "";
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["MovieChanged", "EpisodeChanged"], areas: [ConfigAreas.subtitles] };

export function SubtitleWantedPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const loaded = useLoaderData() as { wanted: SubtitleWantedRow[]; nextPageToken?: string | null; totalCount?: number };
  const revalidator = useRevalidator();
  const [wanted, setWanted] = useState(loaded.wanted);
  const [nextPageToken, setNextPageToken] = useState(loaded.nextPageToken ?? null);
  const [totalCount, setTotalCount] = useState(loaded.totalCount ?? loaded.wanted.length);
  const [loadingMore, setLoadingMore] = useState(false);

  // The router reads the loader again when the server says something changed, and the new first page replaces
  // the pages loaded so far.
  const seenLoaded = useRef(loaded);
  useEffect(() => {
    if (seenLoaded.current === loaded) return;
    seenLoaded.current = loaded;
    setWanted(loaded.wanted);
    setNextPageToken(loaded.nextPageToken ?? null);
    setTotalCount(loaded.totalCount ?? loaded.wanted.length);
  }, [loaded]);

  async function loadMore() {
    if (!nextPageToken) return;
    setLoadingMore(true);
    try {
      const page = await fetchPage<SubtitleWantedRow>(
        `/api/subtitles/wanted?pageSize=${PAGE_SIZE}&pageToken=${encodeURIComponent(nextPageToken)}`
      ) as WantedPage;
      setWanted((current) => [...current, ...page.items]);
      setNextPageToken(page.nextPageToken);
      setTotalCount(page.totalCount ?? totalCount);
    } catch {
      toast.error("More titles could not be loaded.");
    } finally {
      setLoadingMore(false);
    }
  }
  const [searching, setSearching] = useState<"all" | string | null>(null);

  async function searchItems(items: SubtitleWantedRow[], busyKey: "all" | string) {
    setSearching(busyKey);
    let response: Response | null = null;
    try {
      response = await authedFetch("/api/subtitles/wanted/search-all", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          items: items.map((item) => ({ kind: item.kind, mediaId: item.mediaId, libraryId: item.libraryId }))
        })
      });
      const payload = (await response.json().catch(() => null)) as { queued?: number } | null;
      if (!response.ok) throw new Error("subtitle-wanted-search-failed");
      const queued = payload?.queued ?? items.length;
      toast.success(queued === 1 ? "Queued 1 search." : `Queued ${queued} searches.`);
    } catch (error) {
      const explained = await describeRequestFailure(response, error, { action: "search for these subtitles" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setSearching(null);
      revalidator.revalidate();
    }
  }

  return (
    <div className="grid gap-(--section-gap)">
      <PageToolbar tabs={TABS} />

      <ListCard
        title={totalCount ? `${totalCount} short of a language` : "Nothing waiting"}
        titleCase={false}
        actions={
          wanted.length > 0 ? (
            <Button
              type="button"
              size="sm"
              disabled={searching !== null}
              onClick={() => void searchItems(wanted, "all")}
            >
              <Search className="h-3.5 w-3.5" />
              Search all
            </Button>
          ) : undefined
        }
      >
        {wanted.length === 0 ? (
          <ListEmpty
            title="Nothing is missing a subtitle"
            description="Every title across every library that asks for subtitles already has what it wants, or is waiting on its next scheduled try."
          />
        ) : (
          <ListTable
            listId="subtitles-wanted"
            items={wanted}
            columns={[
              { id: "title", label: "Title", sort: (row) => row.title.toLowerCase() },
              { id: "library", label: "Library", mobile: true, sort: (row) => row.libraryName.toLowerCase() },
              { id: "missing", label: "Missing", sort: (row) => row.languagesMissing.length },
              { id: "lastTry", label: "Last try", mobile: true, sort: (row) => lastTryLabel(row) },
              { id: "action", label: "Action", width: LIST_TRACK.action, align: "end", mobile: true }
            ]}
          >
            {(rows) => rows.map((row) => (
              <ListRow key={`${row.kind}-${row.mediaId}`}>
                <ListNameCell name={row.title} sub={rowLabel(row)} />
                <ListCell primary={row.libraryName} mobile />
                <ListCell primary={row.languagesMissing.map((code) => code.toUpperCase()).join(", ")} />
                <ListCell primary={lastTryLabel(row)} mobile />
                <div role="cell" className="flex justify-end">
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    disabled={searching !== null}
                    aria-label={`Search subtitles for ${row.title}`}
                    onClick={() => void searchItems([row], row.mediaId)}
                  >
                    <Search className="h-3.5 w-3.5" />
                    Search
                  </Button>
                </div>
              </ListRow>
            ))}
          </ListTable>
        )}

        {nextPageToken ? (
          <div className="flex flex-wrap items-center justify-center gap-3 p-(--card-pad-x)">
            <span className="text-(length:--type-caption) text-muted-foreground">
              Showing {wanted.length} of {totalCount}
            </span>
            <Button type="button" variant="outline" size="sm" onClick={() => void loadMore()} disabled={loadingMore}>
              <RefreshCw aria-hidden className={`h-3.5 w-3.5 ${loadingMore ? "animate-spin" : ""}`} />
              {loadingMore ? "Loading…" : "Load more"}
            </Button>
          </div>
        ) : null}
      </ListCard>
    </div>
  );
}
