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

/** Where a card counts: moving or waiting its turn, waiting on the person, or neither (finished, in the library). */
export type CardBucket = "inProgress" | "needsYou" | "neither";

/**
 * The one classification behind both numbers (#1499). Every card is in exactly one bucket, so "N in progress" and the amber
 * "need you" are a partition of the board and cannot drift: a card that needs the person (the bad tone, or a download that
 * has stopped moving) is never in progress, and a card in progress is never one that needs the person.
 */
export function cardBucket(card: JourneyCard): CardBucket {
  if (!STATIONS_BEFORE_LIBRARY.includes(card.stage)) return "neither";
  return card.state.tone === "bad" || card.state.tone === "warn" || card.stuck === true ? "needsYou" : "inProgress";
}

/** The cards at each station that are in one bucket. */
function countBucket(grouped: Record<Stage, JourneyCard[]>, bucket: CardBucket): number {
  return STATIONS_BEFORE_LIBRARY.reduce((sum, stage) => sum + grouped[stage].filter((card) => cardBucket(card) === bucket).length, 0);
}

/** The in-progress total: the cards moving or waiting their turn at the stations before the library. */
export function inProgressTotal(grouped: Record<Stage, JourneyCard[]>): number {
  return countBucket(grouped, "inProgress");
}

/** The cards on the board that need the person. The sidebar's need-you figure counts each of them (and more that are not cards). */
export function needYouOnBoard(grouped: Record<Stage, JourneyCard[]>): number {
  return countBucket(grouped, "needsYou");
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
