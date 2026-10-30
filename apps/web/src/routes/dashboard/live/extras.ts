/**
 * What the Live board reads from the day's stored rows (#1096): the titles being
 * searched for as the first stage of the journey, and today's arrivals (with the upgrades
 * among them) as its last. Pure functions of the lists, so the board is only
 * ever drawing what is true.
 */
import type { DashboardLiveExtras, LiveArrivalItem, LiveRequestItem } from "../../../lib/api/types/operations";
import type { StartedBy } from "./provenance";
import { cardLink, releaseKey, titleFromRelease, type JourneyCard } from "./journey";
import { entityFromRelated, type Entity } from "./titles";

function entityOfRequest(request: LiveRequestItem): Entity {
  return { kind: request.mediaType.toLowerCase().startsWith("movie") ? "movie" : "series", id: request.entityId };
}

type SearchingItem = NonNullable<DashboardLiveExtras["searching"]>[number];

function entityOfSearch(search: SearchingItem): Entity {
  return { kind: search.entityType.toLowerCase() === "movie" ? "movie" : "series", id: search.entityId };
}

/** True when a card further along already carries this title, so the search is not said twice. */
function isFurtherAlong(entity: Entity, title: string, cards: JourneyCard[]): boolean {
  const name = releaseKey(title);
  return cards.some((card) => {
    if (card.stage === "imported" || card.stage === "searching") return false;
    if (card.entity?.id === entity.id && (card.entity.kind === entity.kind || entity.kind === "series")) return true;
    return Boolean(name) && releaseKey(card.release ?? card.title).startsWith(name);
  });
}

/** Why a search began, as the board names it. A re-grab after trouble (inherit) has no tag of its own. */
function startedByOfSearch(word: string): StartedBy | undefined {
  if (word === "manual") return "you";
  if (word === "missing" || word === "upgrade") return word;
  return undefined;
}

/**
 * Every title Deluno is searching for at this moment is the first station of its journey (#1442): a search the person
 * started, a title they just added, the title a search cycle is on, a Seerr request's search. The server lists them and
 * pushes each change, so a card is here exactly while its search runs. Once a card for the same title is further along
 * (a release was sent) it is that card, not a second one. A wanted title waiting for its turn is not in the list, so it
 * is not on the board. A Seerr request says who asked.
 */
export function searchingCards(searching: DashboardLiveExtras["searching"], requests: LiveRequestItem[] | undefined, cards: JourneyCard[]): JourneyCard[] {
  const out: JourneyCard[] = [];
  for (const search of searching ?? []) {
    const entity = entityOfSearch(search);
    if (isFurtherAlong(entity, search.title, cards)) continue;
    const asked = (requests ?? []).find((request) => request.entityId === search.entityId);
    out.push({
      key: `search:${search.entityId}`,
      stage: "searching",
      title: search.title,
      entity,
      state: { tone: "info", label: search.label },
      startedBy: asked ? "seerr" : startedByOfSearch(search.startedBy),
      routeNote: asked ? (asked.requestedBy ? `Asked by ${asked.requestedBy}` : "Asked for in Seerr") : undefined,
      at: asked?.createdUtc ?? search.startedUtc,
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
  /** The library it landed in, for the poster's tag and the shelf's chips; null when not known (#1103). */
  libraryId?: string | null;
  libraryName?: string | null;
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
    libraryId: arrival.libraryId ?? null,
    libraryName: arrival.libraryName ?? null,
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
    .map((card) => ({ key: card.key, entity: card.entity, fallbackTitle: card.title, release: card.release ?? null, quality: card.quality ?? null, previousQuality: null, upgraded: false, sizeBytes: card.sizeBytes ?? null, at: card.at ?? "", libraryId: null, libraryName: null, requestedBy: null, to: card.to }));
}
