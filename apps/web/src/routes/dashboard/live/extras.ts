/**
 * What the Live board reads from the day's stored rows (#1096): Seerr requests
 * as the first stage of the journey, and today's arrivals (with the upgrades
 * among them) as its last. Pure functions of the lists, so the board is only
 * ever drawing what is true.
 */
import type { DashboardLiveExtras, LiveArrivalItem, LiveRequestItem } from "../../../lib/api/types/operations";
import { cardLink, releaseKey, titleFromRelease, type JourneyCard } from "./journey";
import { entityFromRelated, type Entity } from "./titles";

function entityOfRequest(request: LiveRequestItem): Entity {
  return { kind: request.mediaType.toLowerCase().startsWith("movie") ? "movie" : "series", id: request.entityId };
}

/** True when a card further along already carries this request's title, so the request is not said twice. */
function isOnTheBoard(request: LiveRequestItem, cards: JourneyCard[]): boolean {
  const entity = entityOfRequest(request);
  const name = releaseKey(request.title);
  return cards.some((card) => {
    if (card.stage === "imported" || card.stage === "requested") return false;
    if (card.entity?.id === entity.id && (card.entity.kind === entity.kind || entity.kind === "series")) return true;
    return Boolean(name) && releaseKey(card.release ?? card.title).startsWith(name);
  });
}

/**
 * A request Deluno has turned into a title and is searching for is the first station of its journey. Once a card for the
 * same title is further along it is that card, not a second one. A request whose search failed is a problem for the bell,
 * not a card.
 */
export function requestCards(requests: LiveRequestItem[] | undefined, cards: JourneyCard[]): JourneyCard[] {
  const out: JourneyCard[] = [];
  for (const request of requests ?? []) {
    if (request.status.toLowerCase() !== "added" || isOnTheBoard(request, cards)) continue;
    const entity = entityOfRequest(request);
    out.push({
      key: `request:${request.entityId}`,
      stage: "requested",
      title: request.title,
      entity,
      state: { tone: "info", label: "Requested · searching" },
      startedBy: "seerr",
      routeNote: request.requestedBy ? `Asked by ${request.requestedBy}` : "Asked for in Seerr",
      at: request.createdUtc,
      to: cardLink(entity)
    });
  }
  return out;
}

/** A card for a title that was asked for in Seerr says so, wherever it is on its way. */
export function withSeerr(cards: JourneyCard[], requests: LiveRequestItem[] | undefined): JourneyCard[] {
  if (!requests || requests.length === 0) return cards;
  const asked = new Set(requests.map((request) => entityOfRequest(request).id));
  return cards.map((card) => (card.entity && asked.has(card.entity.id) && card.startedBy !== "seerr" ? { ...card, startedBy: "seerr" } : card));
}

/** One title that landed in the library today. */
export interface ArrivalItem {
  key: string;
  entity?: Entity;
  /** The release read as a title, for when the library has not named it. */
  fallbackTitle: string;
  /** The release, for an episode's number when the library names only the show. */
  release: string | null;
  quality: string | null;
  previousQuality: string | null;
  upgraded: boolean;
  sizeBytes: number | null;
  at: string;
  /** True when it landed at the quality the library asked for, false when it can still be upgraded, null when not known. */
  qualityMet?: boolean | null;
  /** Who asked for it in Seerr, when it came from a request. */
  requestedBy: string | null;
  to: string;
}

function toArrivalItem(arrival: LiveArrivalItem, index: number, asked: ReadonlyMap<string, string | null>): ArrivalItem {
  const entity = entityFromRelated(arrival.entityType, arrival.entityId);
  return {
    key: `arrival:${arrival.importedUtc}:${arrival.entityId ?? index}`,
    entity,
    fallbackTitle: arrival.release ? titleFromRelease(arrival.release) : "A title",
    release: arrival.release,
    quality: arrival.quality,
    previousQuality: arrival.previousQuality,
    upgraded: arrival.upgraded,
    sizeBytes: arrival.sizeBytes,
    at: arrival.importedUtc,
    qualityMet: arrival.qualityMet ?? null,
    requestedBy: (arrival.entityId ? asked.get(arrival.entityId) : null) ?? null,
    to: cardLink(entity)
  };
}

/** The newest arrivals of the last seven days, for the shelf when nothing has arrived today. Empty when the server does not send them. */
export function recentArrivalItems(extras: DashboardLiveExtras | null | undefined): ArrivalItem[] {
  if (!extras?.recentArrivals) return [];
  const asked = new Map((extras.requests ?? []).map((request) => [request.entityId, request.requestedBy]));
  return extras.recentArrivals.map((arrival, index) => toArrivalItem(arrival, index, asked));
}

/** Today's arrivals, newest first: the day's real imports when the server has them, otherwise the imported cards. */
export function arrivalItems(extras: DashboardLiveExtras | null | undefined, cards: JourneyCard[]): ArrivalItem[] {
  if (extras) {
    const asked = new Map((extras.requests ?? []).map((request) => [request.entityId, request.requestedBy]));
    return extras.arrivals.map((arrival, index) => toArrivalItem(arrival, index, asked));
  }
  return cards
    .filter((card) => card.stage === "imported")
    .map((card) => ({ key: card.key, entity: card.entity, fallbackTitle: card.title, release: card.release ?? null, quality: card.quality ?? null, previousQuality: null, upgraded: false, sizeBytes: card.sizeBytes ?? null, at: card.at ?? "", requestedBy: null, to: card.to }));
}
