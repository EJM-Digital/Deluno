import { useRef } from "react";
import { cardIdents, releaseKey, STAGES, type JourneyCard } from "./journey";

/**
 * One lasting element for a title's journey (#1508). A title is first a search card (`search:<id>`), then a "sending" card
 * (`work:...`), then the client's card, then the hand-off's: four list keys for one journey. Each change of key tore the
 * element down and drew a new one with its entry animation, and a search card that came back for a moment drew the title in an
 * earlier lane. Here a card that takes over a journey the last few seconds had (the same library title, or the same title name)
 * keeps that journey's element key, and a search card that would pull a title back from a later lane is not drawn.
 */

/** How long a journey's element key is kept for a card that takes it over after the old one has gone. */
export const IDENT_GRACE_MS = 10_000;
/** How long a title that has been further along refuses to be drawn as a search again (the lists disagree for a moment, not for long). */
export const BACKSTEP_GRACE_MS = 3_000;

export interface JourneyMemory {
  ident: string;
  key: string;
  stage: JourneyCard["stage"];
  entityId?: string;
  title: string;
  seenAt: number;
  /** The card as last drawn, held in its lane while a search card tries to pull the title back. */
  card: JourneyCard;
}

const stageIndex = (card: Pick<JourneyCard, "stage">) => STAGES.indexOf(card.stage);
const nameOf = (card: Pick<JourneyCard, "title">) => releaseKey(card.title);

function sameJourney(card: JourneyCard, memory: JourneyMemory): boolean {
  if (card.key === memory.key) return true;
  if (card.entity && memory.entityId) return card.entity.id === memory.entityId;
  return nameOf(card) !== "" && nameOf(card) === memory.title;
}

export function stabilizeJourney(cards: JourneyCard[], previous: readonly JourneyMemory[], now: number): { cards: JourneyCard[]; idents: Map<string, string>; memory: JourneyMemory[] } {
  const base = cardIdents(cards);
  const live = previous.filter((entry) => now - entry.seenAt <= IDENT_GRACE_MS);
  const claimed = new Set<JourneyMemory>();
  const used = new Set<string>();
  const idents = new Map<string, string>();
  const kept: JourneyCard[] = [];
  const memory: JourneyMemory[] = [];
  // A title with a card past the search, on this very board: its search card is the same journey, not a second one.
  const furtherAlong = (card: JourneyCard) => cards.some((other) => other !== card && other.stage !== "searching" && other.stage !== "imported" && sameJourney(other, { ident: "", key: "", stage: other.stage, entityId: card.entity?.id, title: nameOf(card), seenAt: now, card: other }));

  for (const card of cards) {
    if (card.stage === "imported") {
      kept.push(card);
      idents.set(card.key, base.get(card.key) ?? card.key);
      continue;
    }
    const before = live.find((entry) => !claimed.has(entry) && entry.key === card.key) ?? live.find((entry) => !claimed.has(entry) && entry.stage !== "imported" && sameJourney(card, entry));
    if (card.stage === "searching") {
      const backwards = before && stageIndex({ stage: before.stage }) > 0 && now - before.seenAt <= BACKSTEP_GRACE_MS;
      if (furtherAlong(card)) continue;
      if (backwards && before) {
        // Held where it was, for a moment: the title neither leaves the board nor goes to an earlier lane.
        claimed.add(before);
        idents.set(before.card.key, before.ident);
        used.add(before.ident);
        kept.push(before.card);
        memory.push(before);
        continue;
      }
    }
    if (before) claimed.add(before);
    let ident = before?.ident ?? base.get(card.key) ?? card.key;
    if (used.has(ident)) ident = card.key;
    used.add(ident);
    idents.set(card.key, ident);
    kept.push(card);
    // The record keeps the furthest stage the journey has reached, so a step back is noticed.
    const stage = before && stageIndex({ stage: before.stage }) > stageIndex(card) && now - before.seenAt <= BACKSTEP_GRACE_MS ? before.stage : card.stage;
    memory.push({ ident, key: card.key, stage, entityId: card.entity?.id, title: nameOf(card), seenAt: now, card });
  }
  // A journey whose card has just gone stays remembered a little, for the card that takes over from it.
  for (const entry of live) if (!claimed.has(entry)) memory.push(entry);
  return { cards: kept.length === cards.length && kept.every((card, index) => card === cards[index]) ? cards : kept, idents, memory };
}

/** The board's cards with each journey's lasting element key; the same result while the cards are the same list. */
export function useJourneyIdentity(cards: JourneyCard[]): { cards: JourneyCard[]; idents: Map<string, string> } {
  const memory = useRef<JourneyMemory[]>([]);
  const last = useRef<{ input: JourneyCard[]; out: { cards: JourneyCard[]; idents: Map<string, string> } } | null>(null);
  if (last.current?.input === cards) return last.current.out;
  const result = stabilizeJourney(cards, memory.current, Date.now());
  memory.current = result.memory;
  const out = { cards: result.cards, idents: result.idents };
  last.current = { input: cards, out };
  return out;
}
