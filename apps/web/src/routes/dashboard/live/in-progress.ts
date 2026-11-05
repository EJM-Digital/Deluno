/**
 * How many titles are in progress (#1499): the number the Dashboard pipeline says ("8 in progress") and the number beside
 * In progress in the sidebar. One rule, written once: the cards the pipeline draws, counted at every station before the
 * library. A card that needs the person is drawn but not counted here; the sidebar counts it apart as 'need you'.
 */
import type { DashboardLiveExtras, DownloadDispatchItem, DownloadQueueItem, ProcessorHandoffItem } from "../../../lib/api";
import { searchingCards, withSeerr } from "./extras";
import { buildJourney, byStage, type JourneyCard, type Stage } from "./journey";

/** The stations a title passes before it is in the library. */
const STATIONS_BEFORE_LIBRARY: Stage[] = ["searching", "grabbed", "downloading", "processor", "importing"];

/** A card that is waiting on the person, not on the pipeline: a failure (the bad tone) or a download that has stopped moving. */
export function cardNeedsAction(card: JourneyCard): boolean {
  return card.state.tone === "bad" || card.stuck === true;
}

/**
 * The in-progress total: the cards at the stations before the library that are moving or waiting their turn. A card that needs
 * the person (import failed, processor failed, stuck) stays on the board and in the "need you" count, never in this one (#1499).
 */
export function inProgressTotal(grouped: Record<Stage, JourneyCard[]>): number {
  return STATIONS_BEFORE_LIBRARY.reduce((sum, stage) => sum + grouped[stage].filter((card) => !cardNeedsAction(card)).length, 0);
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
  return inProgressTotal(grouped);
}
