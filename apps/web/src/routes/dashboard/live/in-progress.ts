/**
 * How many titles are in progress (#1499): the number the Dashboard pipeline says ("8 in progress") and the number beside
 * In progress in the sidebar. One rule, written once: the cards the pipeline draws, counted at every station before the
 * library. A download that needs the person is a card on its station like any other and is counted by the sidebar apart.
 */
import type { DashboardLiveExtras, DownloadDispatchItem, DownloadQueueItem, ProcessorHandoffItem } from "../../../lib/api";
import { searchingCards, withSeerr } from "./extras";
import { buildJourney, byStage, laneCounts, type JourneyCard, type Stage } from "./journey";

/** The stations a title passes before it is in the library. */
const STATIONS_BEFORE_LIBRARY: Stage[] = ["searching", "grabbed", "downloading", "processor", "importing"];

/** The in-progress total from the count at each station. A station with no cards counts nothing, so a missing processor adds nothing. */
export function inProgressTotal(counts: Record<Stage, number>): number {
  return STATIONS_BEFORE_LIBRARY.reduce((sum, stage) => sum + counts[stage], 0);
}

/** Every card on the pipeline: the journey of what the clients and the hand-offs hold, plus the titles being searched for. */
export function pipelineCards(built: JourneyCard[], extras: Pick<DashboardLiveExtras, "searching" | "requests"> | null | undefined): JourneyCard[] {
  return withSeerr([...built, ...searchingCards(extras?.searching, extras?.requests, built)], extras?.requests);
}

/** The in-progress total worked out from the stored lists alone, for a screen that is not the Dashboard (the sidebar). */
export function inProgressFromLists(input: {
  queue: DownloadQueueItem[];
  handoffs: ProcessorHandoffItem[];
  dispatches: DownloadDispatchItem[];
  extras: DashboardLiveExtras | null | undefined;
  now: number;
  queueCapturedUtc?: string;
}): number {
  const built = buildJourney({ queue: input.queue, handoffs: input.handoffs, dispatches: input.dispatches, live: new Map(), summary: undefined, now: input.now, queueCapturedUtc: input.queueCapturedUtc });
  const grouped = byStage(pipelineCards(built, input.extras));
  return inProgressTotal(laneCounts(grouped, undefined));
}
