/**
 * What the Live board reads from the day's stored rows (#1096): Seerr requests
 * as the first stage of the journey, and today's arrivals (with the upgrades
 * among them) as its last. Pure functions of the lists, so the board is only
 * ever drawing what is true.
 */
import type { DashboardLiveExtras, LiveRequestItem } from "../../../lib/api/types/operations";
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
 * A request Deluno has turned into a title and is searching for is the first stage of its journey. Once a card for the
 * same title is further along it is that card, not a second one. A request whose search failed is a problem to settle.
 */
export function requestCards(requests: LiveRequestItem[] | undefined, cards: JourneyCard[]): JourneyCard[] {
  const out: JourneyCard[] = [];
  for (const request of requests ?? []) {
    const status = request.status.toLowerCase();
    const entity = entityOfRequest(request);
    const asked = request.requestedBy ? `Asked by ${request.requestedBy}` : "Asked for in Seerr";
    if (status === "added" && !isOnTheBoard(request, cards)) {
      out.push({ key: `request:${request.entityId}`, stage: "requested", title: request.title, entity, state: { tone: "info", label: "Requested · searching" }, routeNote: asked, at: request.createdUtc, to: cardLink(entity) });
    } else if (status === "search-failed") {
      out.push({ key: `request:${request.entityId}`, stage: "needs", title: request.title, entity, state: { tone: "bad", label: "Search failed" }, reason: "Deluno could not search for what was requested.", routeNote: asked, at: request.updatedUtc, to: cardLink(entity) });
    }
  }
  return out;
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
  /** Who asked for it in Seerr, when it came from a request. */
  requestedBy: string | null;
  to: string;
}

/** Today's arrivals, newest first: the day's real imports when the server has them, otherwise the imported cards. */
export function arrivalItems(extras: DashboardLiveExtras | null | undefined, cards: JourneyCard[]): ArrivalItem[] {
  if (extras) {
    const asked = new Map((extras.requests ?? []).map((request) => [request.entityId, request.requestedBy]));
    return extras.arrivals.map((arrival, index) => {
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
        requestedBy: (arrival.entityId ? asked.get(arrival.entityId) : null) ?? null,
        to: cardLink(entity)
      };
    });
  }
  return cards
    .filter((card) => card.stage === "imported")
    .map((card) => ({ key: card.key, entity: card.entity, fallbackTitle: card.title, release: card.release ?? null, quality: card.quality ?? null, previousQuality: null, upgraded: false, sizeBytes: card.sizeBytes ?? null, at: card.at ?? "", requestedBy: null, to: card.to }));
}
