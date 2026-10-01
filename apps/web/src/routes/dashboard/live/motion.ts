/**
 * The one movement on Live that is not a number changing (#1098), driven by a real change and nothing else: a title that
 * finishes importing first shows it (its import bar full and the card green, "Imported · in your library", for a moment:
 * `useLandingHold`), and then its poster flies from the card into the Your library shelf, where it lands in its place
 * (`useFlyToShelf`). (A card changing station glides by a CSS transition: see journey.css.)
 *
 * With reduced motion, or a page nobody is looking at, nothing moves: the card is
 * simply in its new place and the poster simply appears. While the window is being resized nothing animates at all
 * (`useStillWhileResizing`).
 */
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from "react";
import { motionAllowed } from "../../../lib/page-motion";
import type { ArrivalItem } from "./extras";
import { releaseKey, type JourneyCard } from "./journey";

const FLIGHT_MS = 1100;
/** How long a card whose import has finished stays on the board, full and green, before its poster flies. */
export const LANDING_HOLD_MS = 2000;
/** How long after the last change of the window's size the page counts as still being resized. */
const RESIZE_SETTLE_MS = 350;
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
/**
 * Cards that have left the board since the last render, from any station: an import can finish between two readings,
 * so a card may never be seen at Importing. Only one whose title then turns up in Arrived today flies there.
 */
export function departedCards(before: ReadonlyMap<string, JourneyCard["stage"]>, cards: readonly JourneyCard[]): string[] {
  const now = new Set(cards.map((card) => card.key));
  return [...before.keys()].filter((key) => !now.has(key));
}

/**
 * Nothing on Live animates or transitions while the window is being resized (the mockup's `body.resizing`): the layout
 * snaps, then the motion comes back 350ms after the size stops changing. The page's own rules are in journey.css.
 */
export function useStillWhileResizing(): void {
  useEffect(() => {
    let width = window.innerWidth;
    let height = window.innerHeight;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const check = () => {
      if (window.innerWidth === width && window.innerHeight === height) return;
      width = window.innerWidth;
      height = window.innerHeight;
      document.body.classList.add("live-resizing");
      if (timer !== undefined) clearTimeout(timer);
      timer = setTimeout(() => document.body.classList.remove("live-resizing"), RESIZE_SETTLE_MS);
    };
    window.addEventListener("resize", check);
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(check);
    observer?.observe(document.documentElement);
    return () => {
      window.removeEventListener("resize", check);
      observer?.disconnect();
      if (timer !== undefined) clearTimeout(timer);
      document.body.classList.remove("live-resizing");
    };
  }, []);
}

/**
 * A card whose import has just finished (`landed`) stays on the board for a moment, so the card can show it (full and
 * green) before it leaves and its poster flies to the shelf. After that it is dropped from the list like any other title
 * that has arrived. A card that is not landed is never held.
 */
export function useLandingHold(cards: JourneyCard[]): JourneyCard[] {
  const firstSeen = useRef(new Map<string, number>());
  const [version, setVersion] = useState(0);
  const held = useMemo(() => {
    const now = Date.now();
    const out: JourneyCard[] = [];
    for (const card of cards) {
      if (!card.landed) {
        out.push(card);
        continue;
      }
      const at = firstSeen.current.get(card.key) ?? now;
      firstSeen.current.set(card.key, at);
      if (now - at < LANDING_HOLD_MS) out.push(card);
    }
    for (const key of Array.from(firstSeen.current.keys())) if (!cards.some((card) => card.key === key)) firstSeen.current.delete(key);
    return out;
    // `version` re-reads the clock when a hold has run out.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cards, version]);
  useEffect(() => {
    const timers = held.filter((card) => card.landed).map((card) => setTimeout(() => setVersion((value) => value + 1), Math.max(0, LANDING_HOLD_MS - (Date.now() - (firstSeen.current.get(card.key) ?? Date.now()))) + 20));
    return () => timers.forEach(clearTimeout);
  }, [held]);
  return held;
}

/** Flies a copy of a poster from one place to another. The target stays hidden until it lands. */
export function flyPoster(from: DOMRect, target: HTMLElement, src: string | null): void {
  if (typeof target.animate !== "function") return;
  const to = target.getBoundingClientRect();
  if (to.width === 0 || to.height === 0) return;
  const ghost = document.createElement("div");
  ghost.className = "lj-fly";
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
  // A poster held back while its card showed "Imported" has no earlier state to return to: it simply appears.
  const previous = target.dataset.landingHold ? "" : target.style.visibility;
  target.style.visibility = "hidden";
  target.dataset.flying = "1";
  const done = () => {
    ghost.remove();
    target.style.visibility = previous;
    delete target.dataset.flying;
    delete target.dataset.landingHold;
  };
  const flight = ghost.animate(
    [
      { transform: "translate(0, 0) scale(1, 1)", opacity: 1 },
      { transform: `translate(${to.left - from.left}px, ${to.top - from.top}px) scale(${to.width / from.width}, ${to.height / from.height})`, opacity: 0.95 }
    ],
    { duration: FLIGHT_MS, easing: "cubic-bezier(.45,.05,.25,1)", fill: "forwards" }
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
    const departed = before ? departedCards(before.stages, cards) : [];
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

    // A poster whose card is still showing "Imported" is kept off the shelf until it flies in (the card has not left yet).
    const landedCards = cards.filter((card) => card.landed);
    const held = new Set(arrivals.filter((item) => landedCards.some((card) => sameTitle(card, item))).map((item) => item.key));
    for (const element of Array.from(host.ownerDocument.querySelectorAll<HTMLElement>("[data-arrival-poster]"))) {
      const keys = [element.dataset.arrivalPoster ?? "", ...(element.dataset.arrivalMembers ?? "").split("|")];
      const hold = motionAllowed() && keys.some((key) => held.has(key));
      if (hold && !element.dataset.landingHold && !element.dataset.flying) {
        element.dataset.landingHold = "1";
        element.style.visibility = "hidden";
      } else if (!hold && element.dataset.landingHold && !element.dataset.flying) {
        delete element.dataset.landingHold;
        element.style.visibility = "";
      }
    }

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
