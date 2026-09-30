/**
 * Dashboard, Now (#1087): everything that is moving.
 *
 * One strip across the top with the library counts, the speed and the next
 * search, one short fact each. Beneath it, Needs you (the real problems about
 * downloads, imports and titles, each with its button), Downloads now, Coming
 * up and Latest (what Deluno just did, in plain words).
 *
 * Each fact appears once: the strip holds the numbers; the panels say what is
 * behind them. The connection, storage and system problems are on Setup health.
 *
 * Where the numbers come from: the Dashboard summary (a prop) and stored lists
 * the other screens already cache (the download queue, the next episodes).
 * Nothing here reaches a client, indexer or disk while the screen waits.
 */
import type { JSX } from "react";
import { Link } from "react-router-dom";
import { OnboardingBanner } from "../../components/shell/onboarding-banner";
import { SetupProgressLadder } from "../../components/shell/setup-progress-ladder";
import { SummaryStrip, type SummaryCell } from "../../components/ui/summary-strip";
import type { DashboardActivityItem, DashboardSummary, SeriesUpcomingEpisodeItem } from "../../lib/api";
import { useRouteSources } from "../../lib/route-cache";
import { authedFetch } from "../../lib/use-auth";
import { DownloadsNow } from "./downloads-panels";
import { formatSpeed, useAction } from "./downloads-kit";
import { ago, until } from "./format";
import { NeedsYou } from "./needs-you";
import { FitList, Line, Panel, Plain } from "./parts";
import { attentionSources } from "./attention";
import { nowSources, useSetupState } from "./sources";
import { useDashboardAttention } from "./use-dashboard-attention";

function episodeCode(item: SeriesUpcomingEpisodeItem): string {
  return `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`;
}

function entityLink(item: DashboardActivityItem): string {
  const kind = item.relatedEntityType?.toLowerCase() ?? "";
  if (!item.relatedEntityId) return "/activity";
  if (kind.includes("movie")) return `/movies/${item.relatedEntityId}`;
  if (kind.includes("series") || kind.includes("show")) return `/tv/${item.relatedEntityId}`;
  return "/activity";
}

/** The strip: the library counts, the speed (always a reading) and the next search. */
export function buildNowCells(summary: DashboardSummary | undefined): SummaryCell[] {
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

  let speedHelp: string;
  if (downloads?.hasReading === false) speedHelp = "waiting for the first reading";
  else if ((downloads?.active ?? 0) > 0) speedHelp = `${downloads?.active} downloading${(downloads?.queued ?? 0) > 0 ? `, ${downloads?.queued} queued` : ""}`;
  else speedHelp = (downloads?.queued ?? 0) > 0 ? `nothing moving, ${downloads?.queued} queued` : "nothing downloading";

  return [
    { label: "Movies", value: (movies?.total ?? 0).toLocaleString("en-AU"), help: movies && movies.total > 0 ? "in your library" : "none yet", href: "/movies" },
    { label: "TV shows", value: (shows?.total ?? 0).toLocaleString("en-AU"), help: shows && shows.total > 0 ? "in your library" : "none yet", href: "/tv" },
    { label: "Missing", value: missing.toLocaleString("en-AU"), help: "no acceptable release yet", tone: missing > 0 ? "danger" : undefined, href: "/search-cycles/missing" },
    { label: "Upgradable", value: upgradable.toLocaleString("en-AU"), help: "a better release would be taken", tone: upgradable > 0 ? "warning" : undefined, href: "/search-cycles/upgrades" },
    { label: "Speed now", value: formatSpeed(downloads?.downloadMbps), help: speedHelp, href: "/queue" },
    {
      label: "Next search",
      value: next ? until(next) : "Not scheduled",
      help: last ? `last one ${ago(last.completedUtc ?? last.startedUtc)}` : "no search has run yet",
      href: "/search-cycles"
    }
  ];
}

export function NowTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  const setup = useSetupState();
  const action = useAction();
  const attention = useDashboardAttention(summary, setup.needs);
  const { data } = useRouteSources({ ...attentionSources, ...nowSources });
  const latest = summary?.activity?.recent ?? [];
  const upcoming = data.upcoming ?? [];
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

      <SummaryStrip className="shrink-0 lg:grid-cols-6" cells={buildNowCells(summary)} />

      <div className="grid gap-[var(--grid-gap)] lg:min-h-0 lg:flex-1 lg:grid-cols-[minmax(0,1.6fr)_minmax(0,1fr)] lg:grid-rows-[minmax(0,1fr)_minmax(0,1.25fr)]">
        <NeedsYou attention={attention} action={action} />

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

        <DownloadsNow summary={summary} telemetry={data.telemetry} attention={attention} action={action} />

        <Panel title="Latest" count="what Deluno just did" to="/activity" toLabel="All activity">
          {latest.length === 0 ? (
            <Plain>Nothing has happened yet. What Deluno does shows up here as it works.</Plain>
          ) : (
            <FitList
              moreTo="/activity"
              rows={latest.map((item) => (
                <li key={item.id} className="border-b border-hairline last:border-b-0">
                  <Link
                    to={entityLink(item)}
                    className="block px-[var(--card-pad-x)] py-2 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
                  >
                    <span className="block text-[length:var(--type-body-sm)] leading-snug text-foreground">{item.message}</span>
                    <span className="block text-[length:var(--type-caption)] text-muted-foreground">
                      {item.label} · {ago(item.createdUtc)}
                    </span>
                  </Link>
                </li>
              ))}
            />
          )}
        </Panel>
      </div>
    </div>
  );
}
