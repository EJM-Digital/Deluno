/**
 * The one movement on Live that is not a number changing (#1098), driven by a real change and nothing else: a title that
 * finishes importing shrinks and flies from its card into the Arrived today shelf, where its poster appears
 * (`useFlyToShelf`). (A card changing station glides by a CSS transition: see journey.css.)
 *
 * With reduced motion, or a page nobody is looking at, nothing moves: the card is
 * simply in its new place and the poster simply appears.
 */
import { useLayoutEffect, useRef, type RefObject } from "react";
import { motionAllowed } from "../../../lib/page-motion";
import type { ArrivalItem } from "./extras";
import { releaseKey, type JourneyCard } from "./journey";

const FLIGHT_MS = 950;
/** A card that left Importing waits this long for its poster to turn up on the shelf (the stored counts are read a moment after the import). */
const PENDING_MS = 12_000;

/** Whether an arrival on the shelf is the title a card was carrying: the same library title, or the same release. */
export function sameTitle(card: Pick<JourneyCard, "entity" | "release">, item: Pick<ArrivalItem, "entity" | "release">): boolean {
  if (card.entity && item.entity && card.entity.id === item.entity.id) return true;
  const a = releaseKey(card.release);
  const b = releaseKey(item.release);
  return a !== "" && a === b;
}

/** Cards that left the Importing station and are not on the board any more: the titles that just arrived in the library. */
export function departedFromImporting(before: ReadonlyMap<string, JourneyCard["stage"]>, cards: readonly JourneyCard[]): string[] {
  const now = new Set(cards.map((card) => card.key));
  return [...before].filter(([key, stage]) => stage === "importing" && !now.has(key)).map(([key]) => key);
}

/** Flies a copy of a poster from one place to another. The target stays hidden until it lands. */
export function flyPoster(from: DOMRect, target: HTMLElement, src: string | null): void {
  if (typeof target.animate !== "function") return;
  const to = target.getBoundingClientRect();
  if (to.width === 0 || to.height === 0) return;
  const ghost = document.createElement("div");
  ghost.setAttribute("aria-hidden", "true");
  Object.assign(ghost.style, {
    position: "fixed", left: `${from.left}px`, top: `${from.top}px`, width: `${from.width}px`, height: `${from.height}px`,
    borderRadius: "0.5rem", overflow: "hidden", zIndex: "60", pointerEvents: "none", transformOrigin: "top left",
    background: "hsl(var(--surface-3))", boxShadow: "0 8px 24px hsl(0 0% 0% / 0.35)"
  });
  if (src) {
    const image = document.createElement("img");
    image.src = src;
    image.alt = "";
    Object.assign(image.style, { width: "100%", height: "100%", objectFit: "cover", display: "block" });
    ghost.appendChild(image);
  }
  document.body.appendChild(ghost);
  const previous = target.style.visibility;
  target.style.visibility = "hidden";
  const done = () => {
    ghost.remove();
    target.style.visibility = previous;
  };
  const flight = ghost.animate(
    [
      { transform: "translate(0, 0) scale(1, 1)", opacity: 1 },
      { transform: `translate(${to.left - from.left}px, ${to.top - from.top}px) scale(${to.width / from.width}, ${to.height / from.height})`, opacity: 0.95 }
    ],
    { duration: FLIGHT_MS, easing: "cubic-bezier(0.2, 0.8, 0.2, 1)", fill: "forwards" }
  );
  flight.onfinish = done;
  flight.oncancel = done;
}

interface Pending {
  card: { entity: JourneyCard["entity"]; release?: string };
  rect: DOMRect;
  src: string | null;
  until: number;
}

/**
 * When a card leaves Importing because the title was imported, its poster flies into the shelf where the arrival appears.
 * The card is remembered as it leaves and matched to the arrival that turns up for the same title, in either order.
 */
export function useFlyToShelf(root: RefObject<HTMLElement | null>, cards: readonly JourneyCard[], arrivals: readonly ArrivalItem[]): void {
  const previous = useRef<{ stages: Map<string, JourneyCard["stage"]>; cards: Map<string, JourneyCard>; arrivals: Set<string> } | null>(null);
  const rects = useRef(new Map<string, { rect: DOMRect; src: string | null }>());
  const pending = useRef<Pending[]>([]);
  const waiting = useRef(new Map<string, { item: ArrivalItem; until: number }>());

  // Where each card's poster is, as drawn now (read before the next change moves it).
  useLayoutEffect(() => {
    const host = root.current;
    if (!host) return;
    const next = new Map<string, { rect: DOMRect; src: string | null }>();
    for (const element of Array.from(host.querySelectorAll<HTMLElement>("[data-card-poster]"))) {
      const image = element.querySelector("img");
      next.set(element.dataset.cardPoster ?? "", { rect: element.getBoundingClientRect(), src: image?.getAttribute("src") ?? null });
    }
    // Rects of cards that are gone are kept from the render before, so the one that just left can still be found.
    const before = previous.current;
    const departed = before ? departedFromImporting(before.stages, cards) : [];
    const now = Date.now();
    for (const key of departed) {
      const card = before?.cards.get(key);
      const seen = rects.current.get(key);
      if (card && seen) pending.current.push({ card: { entity: card.entity, release: card.release }, rect: seen.rect, src: seen.src, until: now + PENDING_MS });
    }
    // An arrival that is new since the last render waits for its card to leave, and the other way round.
    const knownArrivals = before?.arrivals ?? null;
    if (knownArrivals) for (const item of arrivals) if (!knownArrivals.has(item.key)) waiting.current.set(item.key, { item, until: now + PENDING_MS });
    pending.current = pending.current.filter((entry) => entry.until > now);
    for (const [key, entry] of waiting.current) if (entry.until <= now) waiting.current.delete(key);

    if (motionAllowed()) {
      for (const entry of [...pending.current]) {
        const match = [...waiting.current.values()].find(({ item }) => sameTitle(entry.card, item));
        if (!match) continue;
        const target = Array.from(host.ownerDocument.querySelectorAll<HTMLElement>("[data-arrival-poster]")).find((element) => element.dataset.arrivalPoster === match.item.key || (element.dataset.arrivalMembers ?? "").split("|").includes(match.item.key));
        if (!target) continue;
        flyPoster(entry.rect, target, entry.src);
        pending.current = pending.current.filter((candidate) => candidate !== entry);
        waiting.current.delete(match.item.key);
      }
    } else {
      pending.current = [];
      waiting.current.clear();
    }

    rects.current = next;
    previous.current = { stages: new Map(cards.map((card) => [card.key, card.stage])), cards: new Map(cards.map((card) => [card.key, card])), arrivals: new Set(arrivals.map((item) => item.key)) };
  });
}
