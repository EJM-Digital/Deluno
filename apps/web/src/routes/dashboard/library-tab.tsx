/**
 * Library (#1082): what is in the library and where it stands, what Deluno is
 * still looking for, and what is coming, leaving or waiting on a subtitle.
 * The strip holds the counts; the panels beneath say what is behind them and
 * never restate the strip's numbers.
 */
import { useState, type JSX, type ReactElement } from "react";
import { Link } from "react-router-dom";
import { MetricChart } from "../../components/ui/metric-chart";
import { Button } from "../../components/ui/button";
import { ListCell, ListNameCell, ListRow, ListTable } from "../../components/ui/list-card";
import { StatusLed } from "../../components/ui/status-led";
import { SummaryStrip } from "../../components/ui/summary-strip";
import { toast } from "../../components/shell/toaster";
import type { DashboardMixEntry, DashboardSummary, MovieWantedItem, SeriesUpcomingEpisodeItem, SeriesWantedItem } from "../../lib/api";
import { refetchSources, useRouteSources } from "../../lib/route-cache";
import { wantedStatusPresentation } from "../../lib/status-tones";
import { authedFetch } from "../../lib/use-auth";
import { ago, bytesText, plural, total, until } from "./format";
import { Bar, FitList, Group, Line, Panel, Plain } from "./parts";
import { libraryTabSources } from "./sources";

interface WantedRow {
  key: string;
  kind: "movie" | "series";
  id: string;
  title: string;
  reason: string;
  status: string;
  have: string | null;
  want: string | null;
  lastSearchUtc: string | null;
  nextSearchUtc: string | null;
}

function wantedRows(movies: MovieWantedItem[], shows: SeriesWantedItem[]): WantedRow[] {
  const open = (status: string) => status === "missing" || status === "upgrade";
  return [
    ...movies.filter((item) => open(item.wantedStatus)).map((item): WantedRow => ({
      key: `movie:${item.movieId}`,
      kind: "movie",
      id: item.movieId,
      title: item.releaseYear ? `${item.title} (${item.releaseYear})` : item.title,
      reason: item.wantedReason,
      status: item.wantedStatus,
      have: item.currentQuality,
      want: item.targetQuality,
      lastSearchUtc: item.lastSearchUtc,
      nextSearchUtc: item.nextEligibleSearchUtc
    })),
    ...shows.filter((item) => open(item.wantedStatus)).map((item): WantedRow => ({
      key: `series:${item.seriesId}`,
      kind: "series",
      id: item.seriesId,
      title: item.title,
      reason: item.wantedReason,
      status: item.wantedStatus,
      have: item.currentQuality,
      want: item.targetQuality,
      lastSearchUtc: item.lastSearchUtc,
      nextSearchUtc: item.nextEligibleSearchUtc
    }))
  ].sort((left, right) => Number(right.status === "missing") - Number(left.status === "missing"));
}

/** One caption and its bars, as flat rows a FitList can count; the caption is flagged so it is never left alone. */
function mixRows(title: string, entries: DashboardMixEntry[]): { rows: ReactElement[]; headings: boolean[] } {
  if (entries.length === 0) return { rows: [], headings: [] };
  const top = Math.max(...entries.map((entry) => entry.count), 1);
  const shown = entries.slice(0, 5);
  return {
    rows: [
      <Group key={`${title}:title`}>{title}</Group>,
      ...shown.map((entry) => (
        <div key={`${title}:${entry.label}`} className="grid grid-cols-[minmax(0,5.5rem)_minmax(0,1fr)_auto] items-center gap-3 px-[var(--card-pad-x)] py-1">
          <span className="truncate text-[length:var(--type-body-sm)] text-foreground">{entry.label}</span>
          <Bar fraction={entry.count / top} />
          <span className="text-right text-[length:var(--type-body-sm)] font-medium tabular-nums text-foreground">{entry.count}</span>
        </div>
      ))
    ],
    headings: [true, ...shown.map(() => false)]
  };
}

const divWrap = (shown: ReactElement[]) => <div>{shown}</div>;

function episodeCode(item: SeriesUpcomingEpisodeItem): string {
  return `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`;
}

export function LibraryTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  const { data } = useRouteSources(libraryTabSources);
  const [searching, setSearching] = useState<ReadonlySet<string>>(new Set());
  const library = summary?.library ?? null;
  const rows = wantedRows(data.movieWanted.recentItems, data.seriesWanted.recentItems);
  const wantedTotal = data.movieWanted.totalWanted + data.seriesWanted.totalWanted;

  const movies = library?.movies;
  const shows = library?.shows;
  const sum = (pick: (counts: NonNullable<typeof movies>) => number) => (movies ? pick(movies) : 0) + (shows ? pick(shows) : 0);
  const empty = !library || library.movies.total + library.shows.total === 0;

  const series = data.metrics.librarySize;
  const hasGrowth = series.length >= 2 && series.some((point) => point.value > 0);
  const grown = hasGrowth ? series[series.length - 1].value - series[0].value : 0;

  async function searchNow(row: WantedRow) {
    if (searching.has(row.key)) return;
    setSearching((current) => new Set(current).add(row.key));
    try {
      const response = await authedFetch(row.kind === "movie" ? "/api/movies/bulk/search" : "/api/series/bulk/search", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(row.kind === "movie" ? { movieIds: [row.id] } : { seriesIds: [row.id] })
      });
      if (!response.ok) throw new Error("search-failed");
      const result = (await response.json()) as { searchesTriggered?: number };
      if ((result.searchesTriggered ?? 0) > 0) toast.success(`Searching for ${row.title}. Watch it in Transfers.`);
      else toast.info(`${row.title} was not searched: no library is ready to search. Check that a search source is connected.`);
      void refetchSources(libraryTabSources);
    } catch {
      toast.error(`Could not start a search for ${row.title}. Try again from its page.`);
    } finally {
      setSearching((current) => {
        const next = new Set(current);
        next.delete(row.key);
        return next;
      });
    }
  }

  const retention = summary?.retention;
  const subtitles = summary?.subtitles;
  const requests = summary?.requests;
  const collections = library?.collections;
  const mix = library?.mix;
  const mixEmpty = !mix || [mix.movieQuality, mix.movieCodec, mix.episodeQuality, mix.showCodec].every((entries) => entries.length === 0);
  const upcoming = data.upcoming;
  const mixParts = [
    mixRows("Movie quality", mix?.movieQuality ?? []),
    mixRows("Movie video", mix?.movieCodec ?? []),
    mixRows("Episode quality", mix?.episodeQuality ?? []),
    mixRows("Show video", mix?.showCodec ?? [])
  ].reduce((all, part) => ({ rows: [...all.rows, ...part.rows], headings: [...all.headings, ...part.headings] }), { rows: [] as ReactElement[], headings: [] as boolean[] });

  return (
    <div className="grid gap-[var(--grid-gap)] lg:h-full lg:min-h-0 lg:grid-cols-4 lg:grid-rows-[auto_minmax(0,1.5fr)_minmax(0,1fr)_minmax(0,0.75fr)]">
      <SummaryStrip
        className="lg:col-span-4"
        cells={[
          { label: "Movies", value: (movies?.total ?? 0).toLocaleString("en-AU"), help: movies && movies.total > 0 ? `${bytesText(movies.fileBytes)} on disk` : "none yet", href: "/movies" },
          { label: "TV shows", value: (shows?.total ?? 0).toLocaleString("en-AU"), help: shows && shows.total > 0 ? `${bytesText(shows.fileBytes)} on disk` : "none yet", href: "/tv" },
          { label: "Quality met", value: sum((counts) => counts.qualityMet).toLocaleString("en-AU"), help: "Deluno has stopped looking", tone: "success", href: "/movies?status=covered" },
          { label: "Upgradable", value: sum((counts) => counts.upgradable).toLocaleString("en-AU"), help: "a better release would be taken", tone: "warning", href: "/search-cycles/upgrades" },
          { label: "Missing", value: sum((counts) => counts.missing).toLocaleString("en-AU"), help: "no acceptable release yet", tone: "danger", href: "/search-cycles/missing" },
          { label: "Not monitored", value: sum((counts) => counts.unmonitored).toLocaleString("en-AU"), help: "Deluno isn't looking for it", href: "/movies" }
        ]}
      />

      <Panel
        title="Wanted"
        count="titles Deluno is still looking for"
        to="/search-cycles/missing"
        toLabel="Movies and TV"
        className={hasGrowth ? "lg:col-span-2" : "lg:col-span-4"}
      >
        {rows.length === 0 ? (
          <Plain>
            {empty
              ? "Nothing to look for yet. Add a movie or a show and Deluno will hunt for it."
              : "Nothing is being looked for. Every monitored title has what you asked for."}
          </Plain>
        ) : (
          <FitList
            moreTo="/search-cycles/missing"
            moreLabel={(hidden) => `and ${hidden.toLocaleString("en-AU")} more in Automation & Recovery`}
            extra={Math.max(0, wantedTotal - rows.length)}
            wrap={(shown) => (
              <ListTable
                chevron={false}
                columns={[
                  { label: "Title" },
                  { label: "State", width: "7.5rem" },
                  { label: "Has and wants" },
                  { label: "Searched", width: "minmax(0,0.9fr)" },
                  { label: "Search now", width: "7rem", srOnly: true }
                ]}
              >
                {shown}
              </ListTable>
            )}
            rows={rows.map((row) => {
                const mark = wantedStatusPresentation(row.status);
                return (
                  <ListRow key={row.key}>
                    <div role="cell" className="min-w-0 leading-tight">
                      <Link to={row.kind === "movie" ? `/movies/${row.id}` : `/tv/${row.id}`} className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground hover:underline">
                        {row.title}
                      </Link>
                      <span className="mt-0.5 block truncate text-[length:var(--type-caption)] text-muted-foreground">{row.reason}</span>
                    </div>
                    <ListCell>
                      <span className={`inline-flex items-center gap-1.5 text-[length:var(--type-body-sm)] ${mark.text}`}>
                        <span aria-hidden className={`h-2 w-2 rounded-full ${mark.dot}`} />
                        {mark.label}
                      </span>
                    </ListCell>
                    <ListCell primary={`Has ${row.have ?? "nothing yet"}`} secondary={`Wants ${row.want ?? "what your profile says"}`} />
                    <ListCell primary={row.lastSearchUtc ? ago(row.lastSearchUtc) : "not yet"} secondary={row.nextSearchUtc ? `next ${until(row.nextSearchUtc)}` : "next not scheduled"} />
                    <ListCell align="end">
                      <Button type="button" size="sm" variant="outline" disabled={searching.has(row.key)} onClick={() => void searchNow(row)} aria-label={`Search now: ${row.title}`}>
                        {searching.has(row.key) ? "Searching" : "Search now"}
                      </Button>
                    </ListCell>
                  </ListRow>
                );
              })}
          />
        )}
      </Panel>

      {hasGrowth ? (
        <div className="min-h-0 overflow-hidden lg:col-span-2 [&>*]:h-full">
          <MetricChart
            label="Growth"
            value={grown > 0 ? `${grown.toLocaleString("en-AU")} added` : "No change"}
            help="titles added over the last 30 days"
            series={series}
            tone="primary"
            size="lg"
            zeroBased={false}
          />
        </div>
      ) : null}

      <Panel title="Quality mix" to="/movies" toLabel="Open">
        {mixEmpty ? (
          <Plain>Nothing to count until files are in the library.</Plain>
        ) : (
          <FitList
            wrap={divWrap}
            moreTo={(hidden) => (/^(Episode|Show)/.test(String(hidden[0]?.key)) ? "/tv" : "/movies")}
            moreLabel={(hidden) => `and ${hidden.toLocaleString("en-AU")} more`}
            isHeading={(index) => mixParts.headings[index] ?? false}
            rows={mixParts.rows}
          />
        )}
      </Panel>

      <Panel title="Coming up" count="next 72 hours" to="/calendar" toLabel="Schedule">
        {upcoming.length === 0 ? (
          <Plain>Nothing airs in the next 72 hours.</Plain>
        ) : (
          <FitList
            wrap={divWrap}
            moreTo="/calendar"
            rows={upcoming.map((item) => (
              <Line
                key={item.episodeId}
                to={`/tv/${item.seriesId}`}
                label={item.title}
                hint={`${episodeCode(item)}${item.episodeTitle ? ` · ${item.episodeTitle}` : ""}`}
                value={until(item.airDateUtc)}
              />
            ))}
          />
        )}
      </Panel>

      <Panel title="Retention" to="/search-cycles/retention" toLabel="Open">
        {!retention || (retention.leavingSoon === 0 && retention.readyToRemove === 0 && retention.removedLast7Days === 0) ? (
          <Plain>Nothing is due to leave. Titles only leave under a rule you have turned on.</Plain>
        ) : (
          <FitList
            wrap={divWrap}
            moreTo="/search-cycles/retention"
            isHeading={(index) => retention.nextLeaving.length > 0 && index === 0}
            rows={[
              ...(retention.nextLeaving.length > 0 ? [<Group key="leaving-soon">Leaving soon</Group>] : []),
              ...retention.nextLeaving.map((item) => (
                <Line key={item.entityId} to={item.mediaType.toLowerCase().includes("movie") ? `/movies/${item.entityId}` : `/tv/${item.entityId}`} label={item.title} value={until(item.removeAfterUtc)} />
              )),
              ...(retention.readyToRemove > 0 ? [<Line key="ready" label={`${plural(retention.readyToRemove, "title")} ready to remove`} />] : []),
              ...(retention.removedLast7Days > 0
                ? [<Line key="removed" label="Removed this week" value={`${plural(retention.removedLast7Days, "title")}, ${bytesText(retention.removedBytesLast7Days)}`} />]
                : [])
            ]}
          />
        )}
      </Panel>

      <Panel title="Subtitles" to="/settings/subtitles-wanted" toLabel="Open">
        {!subtitles || subtitles.wanted + subtitles.held === 0 ? (
          <Plain>No titles are waiting for subtitles.</Plain>
        ) : (
          <>
            <Line label="Titles waiting" value={subtitles.wanted.toLocaleString("en-AU")} />
            <Line label="Files held back" value={subtitles.held.toLocaleString("en-AU")} />
          </>
        )}
      </Panel>

      <Panel title="Collections" className="lg:col-span-2" to="/collections" toLabel="Open">
        {!collections || collections.followed === 0 ? (
          <Plain>No collections followed. Follow a franchise and Deluno tracks which of its films you have.</Plain>
        ) : (
          <>
            <Line label={`${plural(collections.followed, "collection")} followed`} value={`${collections.inLibrary} of ${plural(collections.members, "title")} in your library`} />
            <Line label="Monitored" value={collections.monitored.toLocaleString("en-AU")} />
            {collections.syncFailing > 0 ? (
              <Line to="/collections" tone="warn" label={`${plural(collections.syncFailing, "collection")} could not update`} value="Look" />
            ) : (
              <Line label="Last updated" value={ago(collections.lastSyncedUtc)} />
            )}
          </>
        )}
      </Panel>

      <Panel title="Requests and lists" className="lg:col-span-2" to="/settings/lists" toLabel="Open">
        {!requests || (requests.seerrServers === 0 && requests.importLists === 0) ? (
          <Plain>No request server or lists connected yet.</Plain>
        ) : (
          <>
            {requests.seerrServers > 0 ? (
              <Line
                to="/indexers/media-servers"
                tone={requests.seerrFailing > 0 ? "warn" : "ok"}
                label={requests.seerrFailing > 0 ? `${plural(requests.seerrFailing, "request server")} not answering` : `${plural(requests.seerrServers, "request server")} answering`}
                value={`${plural(total(requests.acquisitionsLast7Days), "request")} filled this week`}
              />
            ) : null}
            {requests.importLists > 0 ? (
              <Line label={plural(requests.importLists, "list")} value={`${plural(requests.listTitlesAddedLast7Days, "title")} added this week`} />
            ) : null}
            <div className="flex items-center gap-2 px-[var(--card-pad-x)] py-2 text-[length:var(--type-caption)] text-muted-foreground">
              <StatusLed tone="idle" size={6} />
              Requests waiting on Seerr itself are not counted here: only Seerr knows them.
            </div>
          </>
        )}
      </Panel>
    </div>
  );
}
