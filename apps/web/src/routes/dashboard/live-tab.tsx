/**
 * Dashboard, Live (#1087, #1096): everything Deluno is doing, as it does it, in
 * one place.
 *
 * Along the top, the Right now band: the speed with its live trace, what is
 * running this second, the next thing it will do with a countdown ring, and the
 * indexers as lights that flash when they are asked. Under it, the hero: the
 * journey of every title as lanes, with Needs you as the first (every problem a
 * person must settle, as cards with their buttons: a download that stopped, an
 * import that did not finish, a hand-off the processor refused), then searching,
 * grabbed, downloading, with the processor, importing and in the library, and the
 * stages with nothing in them as one quiet column. Down the right, one activity
 * stream (today by the hour as a sparkline, then what Deluno did, in plain words
 * with titles) and what airs next.
 *
 * Everything that moves is moved by a real change or by the clock: a bar and a
 * speed follow the readings the download clients push, a card glides to the
 * next lane when its stage changes, a new activity row slides in, a number
 * counts to its new value, and countdowns tick by the second. Nothing loops for
 * decoration, and with reduced motion the new value is simply shown.
 *
 * Each fact appears once. The library counts, the connection, storage and system
 * problems are on System. Where the numbers come from: the Dashboard summary (a
 * prop), stored lists the other screens already cache, and the realtime events.
 * Nothing here reaches a client, indexer or disk while the screen waits.
 */
import type { JSX } from "react";
import { useEffect, useMemo } from "react";
import { Link } from "react-router-dom";
import { OnboardingBanner } from "../../components/shell/onboarding-banner";
import { SetupProgressLadder } from "../../components/shell/setup-progress-ladder";
import { Button } from "../../components/ui/button";
import { cn } from "../../lib/utils";
import type { DashboardSummary, DownloadDispatchItem } from "../../lib/api";
import { queryClient, useRouteSources } from "../../lib/route-cache";
import { authedFetch } from "../../lib/use-auth";
import { useAction } from "./downloads-kit";
import { attentionSources } from "./attention";
import { useLiveActivity, useLiveProgress, useWorkingNow } from "./live-events";
import { AiringSoon } from "./live/airing";
import { RightNow } from "./live/band";
import { Flow } from "./live/flow";
import { arrivalItems, requestCards } from "./live/extras";
import { useLiveBoardEvents } from "./live/use-live-board";
import { useArrivals, useElementHeight, useFreshKeys, useNow, useSummaryUpdatedAt, useTitles } from "./live/hooks";
import { byStage, buildJourney, laneCounts, oneCardPerTitle } from "./live/journey";
import { entityFromRelated, type Entity } from "./live/titles";
import { ActivityStream } from "./live/stream";
import { liveSources, useSetupState } from "./sources";
import { useDashboardAttention } from "./use-dashboard-attention";

const NO_ROWS: never[] = [];

export function LiveTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  const setup = useSetupState();
  const action = useAction();
  const attention = useDashboardAttention(summary, setup.needs);
  const { data } = useRouteSources({ ...attentionSources, ...liveSources });
  const liveProgress = useLiveProgress();
  const latest = useLiveActivity(data.latestActivity?.length ? data.latestActivity : summary?.activity?.recent ?? []);
  // Every pushed change is applied to the list it changes, so the board below moves as it happens (#1097).
  const pushed = useLiveBoardEvents();
  const work = useWorkingNow(summary, pushed);
  const now = useNow(60_000);
  const updatedAt = useSummaryUpdatedAt(summary);
  const upcoming = data.upcoming ?? [];
  const emptyLibrary = summary?.library ? summary.library.movies.total + summary.library.shows.total === 0 : false;
  const showSetup = setup.ready && !setup.status.isComplete;

  const queue = useMemo(() => (data.telemetry?.clients ?? []).flatMap((client) => client.queue), [data.telemetry]);
  // What each client is downloading at: the pushed reading when it is newer than the stored one, else the stored one.
  const clientSpeeds = useMemo(() => {
    const speeds = new Map<string, number>();
    const captured = Date.parse(data.telemetry?.capturedUtc ?? "");
    for (const item of queue) {
      if (item.status !== "downloading") continue;
      const reading = liveProgress.get(item.id);
      const speed = reading && reading.status === "downloading" && reading.at > (Number.isFinite(captured) ? captured : 0) ? reading.speedMbps : item.speedMbps;
      speeds.set(item.clientId, (speeds.get(item.clientId) ?? 0) + (Number.isFinite(speed) ? speed : 0));
    }
    return speeds;
  }, [queue, liveProgress, data.telemetry?.capturedUtc]);
  // The newest sent downloads and the longer list with the archived ones, as one list.
  const dispatches = useMemo(() => {
    const seen = new Set<string>();
    const all: DownloadDispatchItem[] = [];
    for (const dispatch of [...(data.dispatches ?? NO_ROWS), ...(data.boardDispatches ?? NO_ROWS)]) {
      if (seen.has(dispatch.id)) continue;
      seen.add(dispatch.id);
      all.push(dispatch);
    }
    return all;
  }, [data.dispatches, data.boardDispatches]);
  const extras = data.liveExtras ?? undefined;
  const built = useMemo(
    () => buildJourney({ queue, handoffs: data.handoffs ?? NO_ROWS, dispatches, live: liveProgress, summary, now: Date.now(), queueCapturedUtc: data.telemetry?.capturedUtc, work, cases: attention.downloads.recoveryCases, importJobs: attention.downloads.failedImportJobs, searches: pushed.searches }),
    // `now` moves the clock-dependent parts (today, recent) along once a minute.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [queue, data.handoffs, dispatches, liveProgress, summary, data.telemetry?.capturedUtc, work, attention.downloads.recoveryCases, attention.downloads.failedImportJobs, pushed.searches, now]
  );
  // Seerr requests are the first stage of the journey: one that already has a card further along is that card.
  const cards = useMemo(() => oneCardPerTitle([...built, ...requestCards(extras?.requests, built)]), [built, extras?.requests]);
  const grouped = useMemo(() => byStage(cards), [cards]);
  const items = useMemo(() => arrivalItems(extras, cards), [extras, cards]);
  const counts = useMemo(() => {
    const base = laneCounts(grouped, summary, attention.counts.live);
    return { ...base, imported: Math.max(base.imported, extras?.importsToday ?? 0) };
  }, [grouped, summary, attention.counts.live, extras?.importsToday]);
  // Titles are asked for in one request: the cards' and the activity's.
  const extraEntities = useMemo<Array<Entity | undefined>>(
    () => [
      ...latest.slice(0, 40).map((row) => entityFromRelated(row.relatedEntityType, row.relatedEntityId)),
      ...items.slice(0, 40).map((item) => item.entity),
      ...(extras?.requests ?? NO_ROWS).map((request) => entityFromRelated(request.mediaType.toLowerCase().startsWith("movie") ? "movie" : "series", request.entityId))
    ],
    [latest, items, extras?.requests]
  );
  const titles = useTitles(cards, extraEntities);
  // An import, a refused release or a request that has just happened changes what the wall and counters say: read them again now rather than at the next timer.
  const newest = latest[0];
  useEffect(() => {
    if (newest?.fresh && /import|block|refus|seerr|request|upgrade/i.test(newest.category)) void queryClient.invalidateQueries({ queryKey: liveSources.liveExtras.key });
  }, [newest?.id, newest?.fresh, newest?.category]);
  const [sideRef, sideHeight] = useElementHeight<HTMLDivElement>();
  const arrivals = useArrivals(cards);
  const freshArrivals = useFreshKeys(useMemo(() => items.map((item) => item.key), [items]));
  const today = data.activityToday;
  const rhythmRows = useMemo(() => [...latest, ...(today?.rows ?? NO_ROWS)], [latest, today]);
  const activityToday = useMemo(() => ({ rows: rhythmRows, counts: today?.counts, countedAt: today?.fetchedAt, capped: today?.capped }), [rhythmRows, today]);

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

      <RightNow summary={summary} samples={data.throughput?.samples ?? []} indexers={data.indexers ?? NO_ROWS} scoreboard={data.scoreboard} work={work} updatedAt={updatedAt} now={now} clients={data.clients ?? NO_ROWS} clientSpeeds={clientSpeeds} extras={extras ?? null} />

      {emptyLibrary ? (
        <div className="flex shrink-0 flex-wrap items-center gap-3 rounded-2xl border border-hairline bg-card px-[var(--card-pad-x)] py-2 shadow-card dark:border-white/[0.07]">
          <p className="text-[length:var(--type-body-sm)] text-muted-foreground">Nothing in your library yet.</p>
          <Button asChild size="sm" variant="outline">
            <Link to="/movies?add=true">Add a movie</Link>
          </Button>
        </div>
      ) : null}

      <div className="grid gap-[var(--grid-gap)] lg:min-h-0 lg:flex-1 lg:grid-cols-[minmax(0,3.4fr)_minmax(0,1fr)]">
        <div className="flex min-h-0 flex-col [&>section]:min-h-0 [&>section]:flex-1">
          <Flow grouped={grouped} counts={counts} summary={summary} titles={titles} arrivals={arrivals} action={action} hasRequests={(summary?.requests?.seerrServers ?? 0) > 0} arrivalItems={items} freshArrivals={freshArrivals} />
        </div>

        <div ref={sideRef} className="flex min-h-0 flex-col gap-[var(--grid-gap)]">
          <div className="flex min-h-0 flex-[2.3] flex-col [&>section]:min-h-0 [&>section]:flex-1">
            <ActivityStream rows={latest} titles={titles} today={activityToday} />
          </div>
          {sideHeight > 0 && sideHeight < 480 ? null : <div className={cn("flex min-h-0 flex-col [&>section]:min-h-0", sideHeight > 0 && sideHeight < 620 ? "shrink-0" : "flex-[1.5] [&>section]:flex-1")}>
            <AiringSoon upcoming={upcoming} summary={summary} compact={sideHeight > 0 && sideHeight < 620} />
          </div>}
        </div>
      </div>
    </div>
  );
}
