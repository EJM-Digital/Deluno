/**
 * Dashboard, Live (#1087, #1098): everything Deluno is doing, as it does it, in one
 * place, and nothing that needs a person (problems are in the bell).
 *
 * Along the top, three cards of one template: Speed with its live trace, Working on
 * now, and Next (scheduled jobs and episodes about to air). Under them the journey:
 * every title on its way as a card in its station, gliding on as it moves. Under the
 * journey, side by side, Arrived today (posters) and Turned down today (a bar per
 * plain reason). Down the right, at full height: the indexers, the download clients,
 * everything else Deluno is connected to, and the activity stream.
 *
 * The page is fluid. Nothing is a fixed pixel size: the journey shows the rows its
 * measured height holds and falls back to compact tiles when there are more, the
 * shelf sizes its posters to its space, the right column is about a quarter of the
 * width within bounds, and below the `lg` breakpoint it moves under the journey and
 * everything stacks and scrolls. Everything that moves is moved by a real change: a
 * pushed event (#1097) or the clock. Nothing loops for decoration, and with reduced
 * motion the new value is simply shown. Each fact appears once: the library counts,
 * storage and system problems are on System.
 */
import type { JSX } from "react";
import { useEffect, useMemo, useRef } from "react";
import { journeyBudget, pageLayout } from "./live/layout";
import { Link } from "react-router-dom";
import { cn } from "../../lib/utils";
import { OnboardingBanner } from "../../components/shell/onboarding-banner";
import { SetupProgressLadder } from "../../components/shell/setup-progress-ladder";
import { Button } from "../../components/ui/button";
import type { DashboardSummary, DownloadDispatchItem } from "../../lib/api";
import { queryClient, useRouteSources } from "../../lib/route-cache";
import { authedFetch } from "../../lib/use-auth";
import { attentionSources } from "./attention";
import { useLiveActivity, useLiveProgress, useWorkingNow } from "./live-events";
import { Band, buildOperations } from "./live/band";
import { type CardContext } from "./live/card";
import { arrivalItems, recentArrivalItems, requestCards, withSeerr } from "./live/extras";
import { useArrivals, useElementSize, useFreshKeys, useNow, useRemPx, useTitles } from "./live/hooks";
import { SourcesCard } from "./live/sources-map";
import { byStage, buildJourney, laneCounts } from "./live/journey";
import { useFlyToShelf } from "./live/motion";
import { arrivedHeading, LATEST_ARRIVALS_HEADING, Shelf } from "./live/shelf";
import { ActivityStream } from "./live/stream";
import { allInBy, usualTimes } from "./live/times";
import { entityFromRelated, type Entity } from "./live/titles";
import { nameOf } from "./live/arrivals";
import { Journey } from "./live/track";
import { TurnedDownCard } from "./live/turned-down-card";
import { useConnections, useTurnedDown } from "./live/use-pushed";
import { useLiveBoardEvents } from "./live/use-live-board";
import { liveSources, useSetupState } from "./sources";

const NO_ROWS: never[] = [];

export function LiveTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  const setup = useSetupState();
  const { data } = useRouteSources({ ...attentionSources, ...liveSources });
  const liveProgress = useLiveProgress();
  const latest = useLiveActivity(data.latestActivity?.length ? data.latestActivity : summary?.activity?.recent ?? []);
  // Every pushed change is applied to the list it changes, so the board below moves as it happens (#1097).
  const pushed = useLiveBoardEvents();
  const work = useWorkingNow(summary, pushed);
  const now = useNow(60_000);
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
  const seeding = useMemo(() => new Map((data.telemetry?.clients ?? []).map((client) => [client.clientId, client.summary?.seedingCount ?? 0])), [data.telemetry]);
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
    () => buildJourney({ queue, handoffs: data.handoffs ?? NO_ROWS, dispatches, live: liveProgress, summary, now: Date.now(), queueCapturedUtc: data.telemetry?.capturedUtc, work }),
    // `now` moves the clock-dependent parts (today, recent) along once a minute.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [queue, data.handoffs, dispatches, liveProgress, summary, data.telemetry?.capturedUtc, work, now]
  );
  // Seerr requests are the first station of the journey: one that already has a card further along is that card.
  const cards = useMemo(() => withSeerr([...built, ...requestCards(extras?.requests, built)], extras?.requests), [built, extras?.requests]);
  const grouped = useMemo(() => byStage(cards), [cards]);
  const items = useMemo(() => arrivalItems(extras, cards), [extras, cards]);
  // With nothing in today, the shelf shows the latest of the last seven days instead of an empty box.
  const recent = useMemo(() => recentArrivalItems(extras), [extras]);
  const showingLatest = items.length === 0 && recent.length > 0;
  const shelfItems = showingLatest ? recent : items;
  const counts = useMemo(() => laneCounts(grouped, summary), [grouped, summary]);
  // Titles are asked for in one request: the cards' and the activity's.
  const extraEntities = useMemo<Array<Entity | undefined>>(
    () => [
      ...latest.slice(0, 40).map((row) => entityFromRelated(row.relatedEntityType, row.relatedEntityId)),
      ...shelfItems.slice(0, 40).map((item) => item.entity),
      ...(extras?.requests ?? NO_ROWS).map((request) => entityFromRelated(request.mediaType.toLowerCase().startsWith("movie") ? "movie" : "series", request.entityId))
    ],
    [latest, shelfItems, extras?.requests]
  );
  const titles = useTitles(cards, extraEntities);
  // An import, a refused release or a request that has just happened changes what the shelf and counters say: read them again now rather than at the next timer.
  const newest = latest[0];
  useEffect(() => {
    if (newest?.fresh && /import|block|refus|seerr|request|upgrade/i.test(newest.category)) void queryClient.invalidateQueries({ queryKey: liveSources.liveExtras.key });
  }, [newest?.id, newest?.fresh, newest?.category]);

  const arrivals = useArrivals(cards);
  const newestArrivals = useFreshKeys(useMemo(() => items.map((item) => item.key), [items]), 6000);
  const turnedDown = useTurnedDown(extras);
  const connections = useConnections(extras);
  const operations = useMemo(() => buildOperations(work, cards, titles), [work, cards, titles]);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const usual = useMemo(() => usualTimes(dispatches, data.handoffs ?? NO_ROWS, Date.now()), [dispatches, data.handoffs, now]);
  const by = useMemo(() => allInBy(cards), [cards]);
  const hasProcessor = grouped.processor.length > 0 || (summary?.processors?.total ?? 0) > 0 || (connections.connections ?? NO_ROWS).some((connection) => connection.kind === "processor" && connection.enabled);
  const indexerCount = (data.indexers ?? NO_ROWS).filter((indexer) => indexer.isEnabled).length;
  const context: CardContext = { titles, arrivals, indexers: indexerCount };
  const first = items[0];
  const lastArrival = first ? { title: nameOf(first, titles).title, at: first.at } : null;
  const upgraded = Math.max(items.filter((item) => item.upgraded).length, extras?.upgrades?.length ?? 0);

  const root = useRef<HTMLDivElement>(null);
  const [pageRef, page] = useElementSize<HTMLDivElement>();
  const [restRef, rest] = useElementSize<HTMLDivElement>();
  const rem = useRemPx();
  const layout = pageLayout(page.width, rem);
  const side = layout.sideBySide;
  const budget = journeyBudget(side, rest.height, typeof window === "undefined" ? 0 : window.innerHeight);
  // The right column (beside the page or under it): the Sources map and Activity share its height; Activity's rows go to one line when it is short.
  const [sideRef, sideBox] = useElementSize<HTMLDivElement>();
  const compactActivity = side && sideBox.height > 0 && sideBox.height < 50 * rem;
  useFlyToShelf(root, cards, items);

  function dismissOnboarding() {
    void authedFetch("/api/setup/progress", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ lastCompletedStep: setup.progress.lastCompletedStep, isSkipped: true, isCompleted: setup.progress.isCompleted })
    }).catch(() => undefined);
  }

  return (
    <div ref={(node) => { root.current = node; pageRef.current = node; }} data-page-layout={side ? "beside" : "below"} className={cn("flex flex-col gap-[var(--grid-gap)]", side && "h-full min-h-0")}>
      {showSetup ? (
        <div className="flex shrink-0 flex-col gap-[var(--grid-gap)]">
          <OnboardingBanner isSetupSuppressed={setup.progress.isSkipped || setup.progress.isCompleted} onDismiss={dismissOnboarding} />
          <SetupProgressLadder status={setup.status} />
        </div>
      ) : null}

      {emptyLibrary ? (
        <div className="flex shrink-0 flex-wrap items-center gap-3 rounded-2xl border border-hairline bg-card px-[var(--card-pad-x)] py-2 shadow-card dark:border-white/[0.07]">
          <p className="text-[length:var(--type-body-sm)] text-muted-foreground">Nothing in your library yet.</p>
          <Button asChild size="sm" variant="outline">
            <Link to="/movies?add=true">Add a movie</Link>
          </Button>
        </div>
      ) : null}

      <div className={cn("grid gap-[var(--grid-gap)]", side && "min-h-0 flex-1 grid-cols-[minmax(0,1fr)_clamp(22rem,28%,38rem)]")}>
        <div className="flex min-h-0 min-w-0 flex-col gap-[var(--grid-gap)]">
          <Band summary={summary} samples={data.throughput?.samples ?? NO_ROWS} operations={operations} upcoming={upcoming} across={layout.band === "across"} />
          <div ref={restRef} className={cn("flex min-h-0 flex-col gap-[var(--grid-gap)]", side && "flex-1")}>
            <Journey budget={budget} grouped={grouped} counts={counts} context={context} hasProcessor={hasProcessor} usual={usual} by={by} lastArrival={lastArrival} />
            <div className={cn("grid min-h-0 gap-[var(--grid-gap)] md:grid-cols-[minmax(0,1fr)_clamp(17rem,30%,28rem)]", side && "flex-1")}>
              <Shelf items={shelfItems} titles={titles} newest={newestArrivals} heading={showingLatest ? LATEST_ARRIVALS_HEADING : arrivedHeading(Math.max(items.length, extras?.importsToday ?? 0), upgraded, extras?.importedBytesToday ?? 0)} />
              <TurnedDownCard view={turnedDown.view} lit={turnedDown.lit} />
            </div>
          </div>
        </div>

        <div ref={sideRef} className={cn("min-w-0 gap-[var(--grid-gap)]", side ? "flex min-h-0 flex-col" : "flex flex-col")}>
          <div className={cn("flex min-h-0 flex-col", side ? "flex-[5_1_0%]" : "h-[26rem]")}>
            <SourcesCard
              indexers={data.indexers ?? NO_ROWS}
              scoreboard={data.scoreboard}
              summary={summary}
              now={now}
              clientRows={summary?.downloads?.clients ?? NO_ROWS}
              clients={data.clients ?? NO_ROWS}
              speeds={clientSpeeds}
              seeding={seeding}
              connections={connections.connections}
            />
          </div>
          {side ? <div className="flex min-h-0 flex-[3_1_0%] flex-col"><ActivityStream rows={latest} titles={titles} dense={compactActivity} /></div> : <div className="flex h-[26rem] flex-col"><ActivityStream rows={latest} titles={titles} /></div>}
        </div>
      </div>
    </div>
  );
}
