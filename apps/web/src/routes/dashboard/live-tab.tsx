/**
 * Dashboard, Live (#1087): everything that is happening, as it happens.
 *
 * One strip across the top with the library counts, the speed and the next
 * search. Beneath it, Needs you (the real problems about downloads, imports and
 * titles, each with its button), Downloads now, Working now (what Deluno is
 * doing this moment), Coming up and Latest (what Deluno just did, in plain
 * words).
 *
 * Everything that moves is moved by a real change: a bar and a speed follow the
 * readings the download clients push, a new activity row slides in when it
 * arrives, a number counts to its new value when the summary changes, and work
 * shows while it runs. Nothing loops for decoration, and with reduced motion the
 * new value is simply shown. See `live-events.ts`.
 *
 * Each fact appears once: the strip holds the numbers; the panels say what is
 * behind them. The connection, storage and system problems are on System.
 *
 * Where the numbers come from: the Dashboard summary (a prop), stored lists the
 * other screens already cache (the download queue, the next episodes) and the
 * realtime events. Nothing here reaches a client, indexer or disk while the
 * screen waits.
 */
import type { JSX, ReactNode } from "react";
import { Link } from "react-router-dom";
import { OnboardingBanner } from "../../components/shell/onboarding-banner";
import { SetupProgressLadder } from "../../components/shell/setup-progress-ladder";
import { Button } from "../../components/ui/button";
import { CountUp } from "../../components/ui/count-up";
import { StatusLed } from "../../components/ui/status-led";
import { SummaryStrip, type SummaryCell } from "../../components/ui/summary-strip";
import type { DashboardSummary, SeriesUpcomingEpisodeItem } from "../../lib/api";
import { eventLabel } from "../../lib/activity-format";
import { useRouteSources } from "../../lib/route-cache";
import { authedFetch } from "../../lib/use-auth";
import { cn } from "../../lib/utils";
import { DownloadsNow } from "./downloads-panels";
import { formatSpeed, useAction } from "./downloads-kit";
import { ago, until } from "./format";
import { NeedsYou } from "./needs-you";
import { FitList, Line, Panel, Plain } from "./parts";
import { attentionSources } from "./attention";
import { useLiveActivity, useLiveProgress, useWorkingNow, type LiveActivityRow, type WorkItem } from "./live-events";
import { liveSources, useSetupState } from "./sources";
import { useDashboardAttention } from "./use-dashboard-attention";

function episodeCode(item: SeriesUpcomingEpisodeItem): string {
  return `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`;
}

function entityLink(item: Pick<LiveActivityRow, "relatedEntityType" | "relatedEntityId">): string {
  const kind = item.relatedEntityType?.toLowerCase() ?? "";
  if (!item.relatedEntityId) return "/activity";
  if (kind.includes("movie")) return `/movies/${item.relatedEntityId}`;
  if (kind.includes("series") || kind.includes("show")) return `/tv/${item.relatedEntityId}`;
  return "/activity";
}

/** One cell of the strip. A number that can change carries `count`, so it travels to its new value. */
export interface LiveCell extends Omit<SummaryCell, "value"> {
  value: string;
  count?: { value: number; format?: (value: number) => string };
}

/** The strip: the library counts, the speed (always a reading) and the next search. */
export function buildLiveCells(summary: DashboardSummary | undefined): LiveCell[] {
  const library = summary?.library;
  const downloads = summary?.downloads;
  const movies = library?.movies;
  const shows = library?.shows;
  const missing = (movies?.missing ?? 0) + (shows?.missing ?? 0);
  const upgradable = (movies?.upgradable ?? 0) + (shows?.upgradable ?? 0);
  const next = (summary?.automation?.libraries ?? [])
    .map((entry) => entry.nextSearchUtc)
    .filter((value): value is string => Boolean(value))
    .sort()[0];
  const last = summary?.automation?.lastSearchCycle;
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
    { label: "Speed now", value: formatSpeed(speed), count: { value: speed, format: formatSpeed }, help: speedHelp, href: "/queue" },
    {
      label: "Next search",
      value: next ? until(next) : "Not scheduled",
      help: last ? `last one ${ago(last.completedUtc ?? last.startedUtc)}` : "no search has run yet",
      href: "/search-cycles"
    }
  ];
}

function toSummaryCells(cells: LiveCell[]): SummaryCell[] {
  return cells.map(({ count, value, ...cell }) => ({ ...cell, value: count ? <CountUp value={count.value} format={count.format} /> : value }));
}

/** Work in progress reads as a line with a dot that is lit only while it is true. */
const WORK_TONE: Record<WorkItem["kind"], "info" | "ok" | "warn"> = { search: "info", grab: "info", import: "ok", processor: "info", jobs: "info" };

function WorkingNow({ summary, work }: { summary: DashboardSummary | undefined; work: WorkItem[] }): ReactNode {
  const cycle = summary?.automation?.lastSearchCycle;
  const imported = summary?.pipeline?.today.imported ?? 0;
  return (
    <Panel title="Working now" count={work.length > 0 ? <CountUp value={work.length} /> : undefined} to="/activity" toLabel="Activity">
      {work.length === 0 ? (
        <Plain>
          Nothing running right now.{" "}
          {cycle ? `The last search was ${ago(cycle.completedUtc ?? cycle.startedUtc)} in ${cycle.libraryName}` : "No search has run yet"}
          {cycle ? `, and ${imported > 0 ? `${imported.toLocaleString("en-AU")} ${imported === 1 ? "title was" : "titles were"} imported today` : "nothing has been imported today"}.` : "."}
        </Plain>
      ) : (
        <FitList
          moreTo="/activity"
          rows={work.map((item) => (
            <li key={item.key} className="live-row-in border-b border-hairline last:border-b-0">
              <Link
                to={item.to}
                className="flex items-center gap-2.5 px-[var(--card-pad-x)] py-2 text-[length:var(--type-body-sm)] text-foreground transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
              >
                <StatusLed tone={WORK_TONE[item.kind]} />
                <span className="min-w-0 flex-1 truncate">{item.text}</span>
              </Link>
            </li>
          ))}
        />
      )}
    </Panel>
  );
}

export function LiveTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  const setup = useSetupState();
  const action = useAction();
  const attention = useDashboardAttention(summary, setup.needs);
  const { data } = useRouteSources({ ...attentionSources, ...liveSources });
  const liveProgress = useLiveProgress();
  const work = useWorkingNow(summary);
  const latest = useLiveActivity(summary?.activity?.recent ?? []);
  const upcoming = data.upcoming ?? [];
  const emptyLibrary = summary?.library ? summary.library.movies.total + summary.library.shows.total === 0 : false;
  const showSetup = setup.ready && !setup.status.isComplete;

  function dismissOnboarding() {
    void authedFetch("/api/setup/progress", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ lastCompletedStep: setup.progress.lastCompletedStep, isSkipped: true, isCompleted: setup.progress.isCompleted })
    }).catch(() => undefined);
  }

  return (
    <div className="flex flex-col gap-[var(--grid-gap)] lg:h-full lg:min-h-0">
      {showSetup ? (
        <div className="flex shrink-0 flex-col gap-[var(--grid-gap)]">
          <OnboardingBanner isSetupSuppressed={setup.progress.isSkipped || setup.progress.isCompleted} onDismiss={dismissOnboarding} />
          <SetupProgressLadder status={setup.status} />
        </div>
      ) : null}

      <SummaryStrip className="shrink-0 lg:grid-cols-6" cells={toSummaryCells(buildLiveCells(summary))} />

      {emptyLibrary ? (
        <div className="flex shrink-0 flex-wrap items-center gap-3 rounded-2xl border border-hairline bg-card px-[var(--card-pad-x)] py-2 shadow-card dark:border-white/[0.07]">
          <p className="text-[length:var(--type-body-sm)] text-muted-foreground">Nothing in your library yet.</p>
          <Button asChild size="sm" variant="outline">
            <Link to="/movies?add=true">Add a movie</Link>
          </Button>
        </div>
      ) : null}

      <div className="grid gap-[var(--grid-gap)] lg:min-h-0 lg:flex-1 lg:grid-cols-[minmax(0,1.6fr)_minmax(0,1fr)] lg:grid-rows-[minmax(0,1fr)_minmax(0,1.25fr)]">
        <NeedsYou attention={attention} action={action} />

        <div className="flex min-h-0 flex-col gap-[var(--grid-gap)] lg:row-span-2">
          <div className="flex min-h-0 flex-[2] flex-col [&>section]:min-h-0 [&>section]:flex-1">
            <WorkingNow summary={summary} work={work} />
          </div>
          <div className="flex min-h-0 flex-[2] flex-col [&>section]:min-h-0 [&>section]:flex-1">
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
          <div className="flex min-h-0 flex-[3] flex-col [&>section]:min-h-0 [&>section]:flex-1">
            <Panel title="Latest" count="what Deluno just did" to="/activity" toLabel="All activity">
              {latest.length === 0 ? (
                <Plain>Nothing has happened yet. What Deluno does shows up here as it works.</Plain>
              ) : (
                <FitList
                  moreTo="/activity"
                  rows={latest.map((item) => (
                    <li key={item.id} className={cn("border-b border-hairline last:border-b-0", item.fresh && "live-row-in")}>
                      <Link
                        to={entityLink(item)}
                        className="block px-[var(--card-pad-x)] py-2 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
                      >
                        <span className="block text-[length:var(--type-body-sm)] leading-snug text-foreground">{item.message}</span>
                        <span className="block text-[length:var(--type-caption)] text-muted-foreground">
                          {eventLabel(item)} · {ago(item.createdUtc)}
                        </span>
                      </Link>
                    </li>
                  ))}
                />
              )}
            </Panel>
          </div>
        </div>

        <DownloadsNow summary={summary} telemetry={data.telemetry} attention={attention} action={action} live={liveProgress} />
      </div>
    </div>
  );
}
