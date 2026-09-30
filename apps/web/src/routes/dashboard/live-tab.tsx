/**
 * Dashboard, Live (#1087, #1096): everything Deluno is doing, as it does it, in
 * one place.
 *
 * Across the top, one strip: the library counts, the speed (always a reading)
 * and the connection's heartbeat. Under it, the centre is the journey of every
 * title in flight, as lanes (searching, grabbed, downloading, with the
 * processor, importing, in the library) with a card for each title, and beneath
 * it the speed over six hours, today's activity by the hour, the countdowns to
 * the next searches and tasks, and the indexers. Down the right: Needs you (the
 * real problems, each with its button), what Deluno just did as a live stream,
 * and what airs next.
 *
 * Everything that moves is moved by a real change or by the clock: a bar and a
 * speed follow the readings the download clients push, a card glides to the
 * next lane when its stage changes, a new activity row slides in, a number
 * counts to its new value, and countdowns tick by the second. Nothing loops for
 * decoration, and with reduced motion the new value is simply shown.
 *
 * Each fact appears once. The connection, storage and system problems are on
 * System. Where the numbers come from: the Dashboard summary (a prop), stored
 * lists the other screens already cache, and the realtime events. Nothing here
 * reaches a client, indexer or disk while the screen waits.
 */
import type { JSX } from "react";
import { useMemo } from "react";
import { Link } from "react-router-dom";
import { OnboardingBanner } from "../../components/shell/onboarding-banner";
import { SetupProgressLadder } from "../../components/shell/setup-progress-ladder";
import { Button } from "../../components/ui/button";
import { CountUp } from "../../components/ui/count-up";
import { SummaryStrip, type SummaryCell } from "../../components/ui/summary-strip";
import type { DashboardSummary, SeriesUpcomingEpisodeItem } from "../../lib/api";
import { useRouteSources } from "../../lib/route-cache";
import { authedFetch } from "../../lib/use-auth";
import { formatSpeed, useAction } from "./downloads-kit";
import { until } from "./format";
import { NeedsYou } from "./needs-you";
import { FitList, Line, Panel, Plain } from "./parts";
import { attentionSources } from "./attention";
import { useLiveActivity, useLiveProgress, useWorkingNow } from "./live-events";
import { Countdowns } from "./live/countdowns";
import { Flow } from "./live/flow";
import { HeartbeatValue, UpdatedAgo } from "./live/heartbeat";
import { useArrivals, useNow, usePosters, useSummaryUpdatedAt } from "./live/hooks";
import { Indexers } from "./live/indexers";
import { byStage, buildJourney, laneCounts } from "./live/journey";
import { Rhythm } from "./live/rhythm";
import { SpeedChart } from "./live/speed-chart";
import { ActivityStream } from "./live/stream";
import { liveSources, useSetupState } from "./sources";
import { useDashboardAttention } from "./use-dashboard-attention";

function episodeCode(item: SeriesUpcomingEpisodeItem): string {
  return `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`;
}

/** One cell of the strip. A number that can change carries `count`, so it travels to its new value. */
export interface LiveCell extends Omit<SummaryCell, "value"> {
  value: string;
  count?: { value: number; format?: (value: number) => string };
}

/** The strip's numbers: the library counts and the speed (always a reading). The connection cell is added beside them. */
export function buildLiveCells(summary: DashboardSummary | undefined): LiveCell[] {
  const library = summary?.library;
  const downloads = summary?.downloads;
  const movies = library?.movies;
  const shows = library?.shows;
  const missing = (movies?.missing ?? 0) + (shows?.missing ?? 0);
  const upgradable = (movies?.upgradable ?? 0) + (shows?.upgradable ?? 0);
  const speed = Number.isFinite(downloads?.downloadMbps) ? Math.max(0, downloads?.downloadMbps ?? 0) : 0;

  let speedHelp: string;
  if (downloads?.hasReading === false) speedHelp = "waiting for the first reading";
  else if ((downloads?.active ?? 0) > 0) speedHelp = `${downloads?.active} downloading${(downloads?.queued ?? 0) > 0 ? `, ${downloads?.queued} queued` : ""}`;
  else speedHelp = (downloads?.queued ?? 0) > 0 ? `nothing moving, ${downloads?.queued} queued` : "nothing downloading";

  const whole = (value: number) => Math.round(value).toLocaleString("en-AU");
  return [
    { label: "Movies", value: (movies?.total ?? 0).toLocaleString("en-AU"), count: { value: movies?.total ?? 0, format: whole }, help: movies && movies.total > 0 ? "in your library" : "none yet", href: "/movies" },
    { label: "TV shows", value: (shows?.total ?? 0).toLocaleString("en-AU"), count: { value: shows?.total ?? 0, format: whole }, help: shows && shows.total > 0 ? "in your library" : "none yet", href: "/tv" },
    { label: "Missing", value: missing.toLocaleString("en-AU"), count: { value: missing, format: whole }, help: "no acceptable release yet", tone: missing > 0 ? "danger" : undefined, href: "/search-cycles/missing" },
    { label: "Upgradable", value: upgradable.toLocaleString("en-AU"), count: { value: upgradable, format: whole }, help: "a better release would be taken", tone: upgradable > 0 ? "warning" : undefined, href: "/search-cycles/upgrades" },
    { label: "Speed now", value: formatSpeed(speed), count: { value: speed, format: formatSpeed }, help: speedHelp, href: "/queue" }
  ];
}

function toSummaryCells(cells: LiveCell[]): SummaryCell[] {
  return cells.map(({ count, value, ...cell }) => ({ ...cell, value: count ? <CountUp value={count.value} format={count.format} /> : value }));
}

const NO_ROWS: never[] = [];

export function LiveTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  const setup = useSetupState();
  const action = useAction();
  const attention = useDashboardAttention(summary, setup.needs);
  const { data } = useRouteSources({ ...attentionSources, ...liveSources });
  const liveProgress = useLiveProgress();
  const latest = useLiveActivity(summary?.activity?.recent ?? []);
  const work = useWorkingNow(summary);
  const now = useNow(60_000);
  const updatedAt = useSummaryUpdatedAt(summary);
  const upcoming = data.upcoming ?? [];
  const emptyLibrary = summary?.library ? summary.library.movies.total + summary.library.shows.total === 0 : false;
  const showSetup = setup.ready && !setup.status.isComplete;

  const queue = useMemo(() => (data.telemetry?.clients ?? []).flatMap((client) => client.queue), [data.telemetry]);
  const cards = useMemo(
    () => buildJourney({ queue, handoffs: data.handoffs ?? NO_ROWS, dispatches: data.dispatches ?? NO_ROWS, live: liveProgress, summary, now: Date.now(), queueCapturedUtc: data.telemetry?.capturedUtc, work }),
    // `now` moves the clock-dependent parts (today, recent) along once a minute.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [queue, data.handoffs, data.dispatches, liveProgress, summary, data.telemetry?.capturedUtc, work, now]
  );
  const grouped = useMemo(() => byStage(cards), [cards]);
  const counts = useMemo(() => laneCounts(grouped, summary), [grouped, summary]);
  const posters = usePosters(cards);
  const arrivals = useArrivals(cards);
  const stalledWithButton = useMemo(() => new Set(attention.downloads.stalled.map((item) => `${item.clientId}:${item.id}`)), [attention.downloads.stalled]);
  const rhythmRows = useMemo(() => [...latest, ...(Array.isArray(data.activityToday) ? data.activityToday : NO_ROWS)], [latest, data.activityToday]);

  function dismissOnboarding() {
    void authedFetch("/api/setup/progress", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ lastCompletedStep: setup.progress.lastCompletedStep, isSkipped: true, isCompleted: setup.progress.isCompleted })
    }).catch(() => undefined);
  }

  const connection: SummaryCell = { label: "Connection", value: <HeartbeatValue />, help: <UpdatedAgo updatedAt={updatedAt} /> };
  const cells = [...toSummaryCells(buildLiveCells(summary)), connection];

  return (
    <div className="flex flex-col gap-[var(--grid-gap)] lg:h-full lg:min-h-0">
      {showSetup ? (
        <div className="flex shrink-0 flex-col gap-[var(--grid-gap)]">
          <OnboardingBanner isSetupSuppressed={setup.progress.isSkipped || setup.progress.isCompleted} onDismiss={dismissOnboarding} />
          <SetupProgressLadder status={setup.status} />
        </div>
      ) : null}

      <SummaryStrip className="shrink-0 lg:grid-cols-6" cells={cells} />

      {emptyLibrary ? (
        <div className="flex shrink-0 flex-wrap items-center gap-3 rounded-2xl border border-hairline bg-card px-[var(--card-pad-x)] py-2 shadow-card dark:border-white/[0.07]">
          <p className="text-[length:var(--type-body-sm)] text-muted-foreground">Nothing in your library yet.</p>
          <Button asChild size="sm" variant="outline">
            <Link to="/movies?add=true">Add a movie</Link>
          </Button>
        </div>
      ) : null}

      <div className="grid gap-[var(--grid-gap)] lg:min-h-0 lg:flex-1 lg:grid-cols-[minmax(0,2.4fr)_minmax(0,1fr)]">
        <div className="flex min-h-0 flex-col gap-[var(--grid-gap)]">
          <div className="flex min-h-0 flex-[5] flex-col [&>section]:min-h-0 [&>section]:flex-1">
            <Flow grouped={grouped} counts={counts} summary={summary} posters={posters} arrivals={arrivals} action={action} stalledWithButton={stalledWithButton} />
          </div>
          <div className="grid min-h-0 flex-[3] gap-[var(--grid-gap)] sm:grid-cols-2 lg:grid-cols-[1.25fr_1.25fr_1fr_1fr] [&>section]:min-h-0">
            <SpeedChart samples={data.throughput?.samples ?? []} summary={summary} />
            <Rhythm rows={rhythmRows} />
            <Countdowns summary={summary} />
            <Indexers indexers={data.indexers ?? NO_ROWS} summary={summary} now={now} />
          </div>
        </div>

        <div className="flex min-h-0 flex-col gap-[var(--grid-gap)]">
          <div className="flex min-h-0 flex-[4] flex-col [&>section]:min-h-0 [&>section]:flex-1">
            <NeedsYou attention={attention} action={action} />
          </div>
          <div className="flex min-h-0 flex-[4] flex-col [&>section]:min-h-0 [&>section]:flex-1">
            <ActivityStream rows={latest} />
          </div>
          <div className="flex min-h-0 flex-[3] flex-col [&>section]:min-h-0 [&>section]:flex-1">
            <Panel title="Coming up" count="next 72 hours" to="/calendar" toLabel="Schedule">
              {upcoming.length === 0 ? (
                <Plain>Nothing airs in the next 72 hours.</Plain>
              ) : (
                <FitList
                  wrap={(shown) => <div>{shown}</div>}
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
          </div>
        </div>
      </div>
    </div>
  );
}
