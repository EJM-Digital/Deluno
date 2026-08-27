/**
 * Episode search — every episode Deluno still wants, in one list.
 *
 * One query rather than a fetch per series: after a catalogue sync a single show
 * can hold hundreds of episodes, and this page used to pull every inventory in
 * turn to find the handful that are missing.
 *
 * Contracts: GET /api/series/episodes/wanted;
 * POST /api/series/{id}/episodes/search.
 */
import { useMemo, useState } from "react";
import { Link, useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { ArrowLeft, LoaderCircle, Search } from "lucide-react";
import { fetchJson } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { Button } from "../components/ui/button";
import { ListGroupHeader } from "../components/ui/media-type-split";
import {
  LIST_TRACK,
  ListCard,
  ListCell,
  ListEmpty,
  ListNameCell,
  ListRow,
  ListTable
} from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { toast } from "../components/shell/toaster";
import { TitleMarkLabel } from "../components/ui/title-mark";
import { describeSearchReason } from "../lib/search-reasons";

interface WantedEpisode {
  episodeId: string;
  seriesId: string;
  seriesTitle: string;
  seasonNumber: number;
  episodeNumber: number;
  title: string | null;
  airDateUtc: string | null;
  monitored: boolean;
  wantedStatus: string;
  wantedReason: string;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
}

interface EpisodeSearchLoaderData {
  episodes: WantedEpisode[];
}

export async function episodeSearchLoader(): Promise<EpisodeSearchLoaderData> {
  const episodes = await fetchJson<WantedEpisode[]>("/api/series/episodes/wanted?take=300").catch(() => []);
  return { episodes };
}

type Filter = "all" | "aired" | "monitored" | "never-searched";

export function EpisodeSearchPage() {
  const data = useLoaderData() as EpisodeSearchLoaderData;
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [busy, setBusy] = useState<string | null>(null);

  const { episodes } = data;

  const now = Date.now();
  const visible = useMemo(
    () =>
      episodes.filter((episode) => {
        const haystack = `${episode.seriesTitle} ${code(episode)} ${episode.title ?? ""}`.toLowerCase();
        if (query.trim() && !haystack.includes(query.trim().toLowerCase())) return false;
        if (filter === "aired") return episode.airDateUtc !== null && new Date(episode.airDateUtc).getTime() <= now;
        if (filter === "monitored") return episode.monitored;
        if (filter === "never-searched") return episode.lastSearchUtc === null;
        return true;
      }),
    [episodes, filter, now, query]
  );

  const bySeries = useMemo(() => {
    const groups = new Map<string, WantedEpisode[]>();
    for (const episode of visible) {
      groups.set(episode.seriesId, [...(groups.get(episode.seriesId) ?? []), episode]);
    }
    return [...groups.values()].sort((left, right) => left[0].seriesTitle.localeCompare(right[0].seriesTitle));
  }, [visible]);

  const airedCount = episodes.filter((e) => e.airDateUtc && new Date(e.airDateUtc).getTime() <= now).length;
  // An episode with no air date is not a future episode. Treating "not aired"
  // as "everything else" reported two upcoming episodes for a show that ended
  // in 2013, because the provider had no dates for them (#259).
  const unairedCount = episodes.filter((e) => e.airDateUtc && new Date(e.airDateUtc).getTime() > now).length;
  const undatedCount = episodes.filter((e) => !e.airDateUtc).length;
  const neverSearched = episodes.filter((e) => e.lastSearchUtc === null).length;
  const monitored = episodes.filter((e) => e.monitored).length;

  async function searchEpisodes(seriesId: string, episodeIds: string[], key: string) {
    if (!episodeIds.length) return;
    setBusy(key);
    try {
      const response = await authedFetch(`/api/series/${seriesId}/episodes/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds })
      });
      if (!response.ok) throw new Error("search-failed");
      const payload = (await response.json()) as { searchedEpisodes?: number; matchedCount?: number; reason?: string };
      const searched = payload.searchedEpisodes ?? episodeIds.length;
      const matched = payload.matchedCount ?? 0;
      if (payload.reason && payload.reason !== "ok") {
        const explained = describeSearchReason(payload.reason, `Searched ${searched} episode${searched === 1 ? "" : "s"}. Nothing matched yet.`);
        const action = explained.action;
        toast.info(explained.title, {
          description: explained.description,
          action: action ? { label: action.label, onClick: () => navigate(action.href) } : undefined
        });
      } else {
        toast.success(
          matched > 0
            ? `Searched ${searched} episode${searched === 1 ? "" : "s"}, matched ${matched}.`
            : `Searched ${searched} episode${searched === 1 ? "" : "s"}. Nothing matched yet.`
        );
      }
      revalidator.revalidate();
    } catch {
      toast.error("That episode search failed.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        actions={
          <>
            <Button asChild type="button" variant="outline">
              <Link to="/tv">
                <ArrowLeft className="h-4 w-4" />
                All TV
              </Link>
            </Button>
            <Button
            type="button"
            onClick={() => {
              // Everything visible, grouped per series so each show gets one call.
              for (const group of bySeries) {
                void searchEpisodes(group[0].seriesId, group.map((item) => item.episodeId), "all");
              }
            }}
            disabled={busy !== null || visible.length === 0}
          >
            {busy === "all" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Search these {visible.length || ""}
            </Button>
          </>
        }
      />

      <SummaryStrip
        cells={[
          { label: "Wanted", value: episodes.length, help: "missing or upgradeable" },
          { label: "Already aired", value: airedCount, tone: airedCount > 0 ? "warning" : undefined, help: "should be findable" },
          {
            label: "Not out yet",
            value: unairedCount,
            help: undatedCount > 0 ? `${undatedCount} with no air date` : "nothing to find yet"
          },
          { label: "Never searched", value: neverSearched, help: "no attempt recorded" },
          { label: "Monitored", value: monitored, help: `of ${episodes.length} watched` }
        ]}
      />

      <ListCard
        title="Wanted episodes"
        count={`${visible.length} of ${episodes.length} shown`}
        filter={{ value: query, onChange: setQuery, placeholder: "Filter by show, code or title" }}
        actions={
          <Select
            aria-label="Filter episodes"
            className="h-[var(--control-height-sm)] w-44 py-0 text-[length:var(--type-caption)]"
            value={filter}
            onChange={(event) => setFilter(event.target.value as Filter)}
            options={[
              { value: "all", label: "All wanted" },
              { value: "aired", label: "Already aired" },
              { value: "monitored", label: "Monitored" },
              { value: "never-searched", label: "Never searched" }
            ]}
          />
        }
      >
        {visible.length === 0 ? (
          <ListEmpty
            title={episodes.length ? "No episodes match" : "Nothing is wanted"}
            description={
              episodes.length
                ? "Try a different filter, or clear the search box."
                : "Every episode Deluno knows about is either on disk or not yet announced. Link a show to its provider record to learn about the ones you do not have."
            }
          />
        ) : (
          <ListTable
            columns={[
              { label: "Episode" },
              { label: "Aired" },
              { label: "Last search" },
              { label: "Next try" },
              { label: "Status", width: LIST_TRACK.status },
              { label: "Search", width: "auto", align: "end", mobile: true }
            ]}
            chevron={false}
          >
            {bySeries.map((group) => (
              <div key={group[0].seriesId}>
                <ListGroupHeader
                  label={group[0].seriesTitle}
                  detail={`${group.length} wanted`}
                  actions={
                    <Button
                      type="button"
                      size="sm"
                      variant="ghost"
                      disabled={busy !== null}
                      onClick={() => void searchEpisodes(group[0].seriesId, group.map((item) => item.episodeId), group[0].seriesId)}
                    >
                      {busy === group[0].seriesId ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
                      Search all
                    </Button>
                  }
                />
                {group.map((episode) => {
                  return (
                    <ListRow key={episode.episodeId} onClick={() => navigate(`/tv/${episode.seriesId}`)}>
                      <ListNameCell name={code(episode)} sub={episode.title ?? "Episode title pending"} />
                      <ListCell primary={episode.airDateUtc ? formatDate(episode.airDateUtc) : "Not announced"} />
                      <ListCell primary={episode.lastSearchUtc ? formatDateTime(episode.lastSearchUtc) : "Never"} />
                      <ListCell primary={episode.nextEligibleSearchUtc ? formatDateTime(episode.nextEligibleSearchUtc) : "Any time"} />
                      <ListCell>
                        {/* An episode is a title. Same five marks (DESIGN-001). */}
                        <TitleMarkLabel item={{ monitored: episode.monitored, wantedStatus: episode.wantedStatus }} />
                      </ListCell>
                      <div role="cell" className="flex justify-end">
                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          disabled={busy !== null}
                          onClick={(event) => {
                            event.stopPropagation();
                            void searchEpisodes(episode.seriesId, [episode.episodeId], episode.episodeId);
                          }}
                        >
                          {busy === episode.episodeId ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
                          Search
                        </Button>
                      </div>
                    </ListRow>
                  );
                })}
              </div>
            ))}
          </ListTable>
        )}
      </ListCard>
    </div>
  );
}

/* -------------------------------------------------------------- helpers */

function code(episode: WantedEpisode) {
  return `S${String(episode.seasonNumber).padStart(2, "0")}E${String(episode.episodeNumber).padStart(2, "0")}`;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", year: "numeric" }).format(new Date(value));
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}
