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
import { FLIGHT_MS, LANDING_FADE_MS, easeInOut, poseAt, type Spot } from "./flight-path";
import { releaseKey, type JourneyCard } from "./journey";
import { FLYING_ATTRIBUTE, HELD_ATTRIBUTE, announceSlotsChanged, closeSlot, openSlot, settleSlot } from "./shelf-slots";

/** A flight that has not finished this long after it should have (a page whose frames stopped) is ended anyway. */
const STALL_GRACE_MS = 500;
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

/** The CSS animations on the page that end: the ones that run forever (a sheen, a pulse) are not stopped by the class. */
function endingAnimations(): Animation[] {
  if (typeof document.getAnimations !== "function") return [];
  return document.getAnimations().filter((animation) => "animationName" in animation && animation.effect?.getComputedTiming().iterations !== Infinity);
}

/**
 * Takes the class off. What ran at no time under it would run its remainder once its duration is back, and a card that
 * appeared in the middle of the resize would fade in half-way, so those animations are finished first: what appeared
 * during the resize is simply there.
 */
function endResize(): void {
  const stilled = endingAnimations();
  document.body.classList.remove("live-resizing");
  // The styles must be recalculated without the class before the animations are finished.
  void document.body.offsetHeight;
  for (const animation of stilled) animation.finish();
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
      timer = setTimeout(endResize, RESIZE_SETTLE_MS);
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

/** A poster's art on the shelf is held out of sight with its slot closed, until the poster that is coming to it takes off. */
function hold(art: HTMLElement): void {
  art.setAttribute(HELD_ATTRIBUTE, "");
  art.style.visibility = "hidden";
  closeSlot(art);
  announceSlotsChanged();
}

/** Shows the art under the poster that is landing on it, in a slot that has opened as far as it goes. */
function reveal(art: HTMLElement): void {
  art.removeAttribute(HELD_ATTRIBUTE);
  art.style.visibility = "";
  settleSlot(art);
}

function release(art: HTMLElement): void {
  reveal(art);
  art.removeAttribute(FLYING_ATTRIBUTE);
  announceSlotsChanged();
}

const spotOf = (element: HTMLElement): Spot => {
  const { left, top, width } = element.getBoundingClientRect();
  return { left, top, width };
};

const radiusOf = (element: HTMLElement): number => Number.parseFloat(getComputedStyle(element).borderTopLeftRadius) || 0;

/** A delivered title's poster as it was drawn on its card, and what it needs to be drawn again in flight. */
export interface Delivery {
  /** A copy of the poster as drawn: the picture, or the initials, so the same thing flies to the shelf. */
  look: HTMLElement;
  /** Where the poster was on screen. */
  from: DOMRect;
}

/**
 * Flies a copy of the card's poster to the title's poster on the shelf (Weir's flight): it rises a little as it leaves, bows
 * to one side and is placed against the shelf poster's place as it is now, so it lands where the poster is even if the shelf
 * moved; its corners pass from the card's to the shelf's, the shelf's slot opens by the flight's own curve, and the poster
 * then gives way to the real one by fading. Without room to fly to, or where movement is not allowed, the poster simply shows.
 */
export function flyToShelf(delivery: Delivery, target: HTMLElement): void {
  const { from } = delivery;
  const to = target.getBoundingClientRect();
  if (!motionAllowed() || document.body.classList.contains("live-resizing") || to.width === 0 || to.height === 0 || from.width === 0 || from.height === 0) {
    release(target);
    return;
  }
  const ghost = document.createElement("div");
  ghost.className = "lj-fly";
  ghost.setAttribute("aria-hidden", "true");
  Object.assign(ghost.style, { position: "fixed", left: "0", top: "0", width: `${from.width}px`, height: `${from.height}px`, overflow: "hidden", zIndex: "60", pointerEvents: "none", transformOrigin: "top left", willChange: "transform" });
  Object.assign(delivery.look.style, { width: "100%", height: "100%" });
  ghost.appendChild(delivery.look);
  document.body.appendChild(ghost);
  hold(target);
  target.setAttribute(FLYING_ATTRIBUTE, "");
  announceSlotsChanged();
  const fromRadius = radiusOf(delivery.look);
  const toRadius = radiusOf(target);
  const startedAt = performance.now();
  let frame = 0;
  let stall = 0;
  let landed = false;
  const end = () => {
    cancelAnimationFrame(frame);
    window.clearTimeout(stall);
    ghost.remove();
    release(target);
  };
  stall = window.setTimeout(end, FLIGHT_MS + LANDING_FADE_MS + STALL_GRACE_MS);
  const step = (now: number) => {
    if (!target.isConnected) {
      end();
      return;
    }
    const elapsed = Math.max(0, now - startedAt);
    const progress = easeInOut(Math.min(1, elapsed / FLIGHT_MS));
    // The slot opens by the flight's own curve, so the poster lands as it finishes opening.
    if (!landed) openSlot(target, progress);
    const pose = poseAt(progress, from, spotOf(target));
    ghost.style.transform = `translate(${pose.x}px, ${pose.y}px) scale(${pose.scale})`;
    ghost.style.borderRadius = `${(fromRadius + (toRadius - fromRadius) * progress) / pose.scale}px`;
    if (elapsed >= FLIGHT_MS && !landed) {
      landed = true;
      reveal(target);
    }
    const fade = Math.max(0, (elapsed - FLIGHT_MS) / LANDING_FADE_MS);
    if (fade >= 1) {
      end();
      return;
    }
    if (fade > 0) ghost.style.opacity = String(1 - fade);
    frame = requestAnimationFrame(step);
  };
  frame = requestAnimationFrame(step);
}

interface Pending {
  card: { entity: JourneyCard["entity"]; release?: string };
  delivery: Delivery;
  until: number;
}

/**
 * When a card leaves Importing because the title was imported, its poster flies into the shelf where the arrival appears.
 * The card is remembered as it leaves and matched to the arrival that turns up for the same title, in either order.
 */
export function useFlyToShelf(root: RefObject<HTMLElement | null>, cards: readonly JourneyCard[], arrivals: readonly ArrivalItem[]): void {
  const previous = useRef<{ stages: Map<string, JourneyCard["stage"]>; cards: Map<string, JourneyCard>; arrivals: Set<string> } | null>(null);
  const rects = useRef(new Map<string, Delivery>());
  const pending = useRef<Pending[]>([]);
  const waiting = useRef(new Map<string, { item: ArrivalItem; until: number }>());

  // Where each card's poster is, as drawn now (read before the next change moves it).
  useLayoutEffect(() => {
    const host = root.current;
    if (!host) return;
    const next = new Map<string, Delivery>();
    for (const element of Array.from(host.querySelectorAll<HTMLElement>("[data-card-poster]"))) {
      const poster = element.firstElementChild;
      const look = poster?.cloneNode(true);
      if (poster && look instanceof HTMLElement) next.set(element.dataset.cardPoster ?? "", { look, from: poster.getBoundingClientRect() });
    }
    // Rects of cards that are gone are kept from the render before, so the one that just left can still be found.
    const before = previous.current;
    const departed = before ? departedCards(before.stages, cards) : [];
    const now = Date.now();
    for (const key of departed) {
      const card = before?.cards.get(key);
      const seen = rects.current.get(key);
      if (card && seen) pending.current.push({ card: { entity: card.entity, release: card.release }, delivery: seen, until: now + PENDING_MS });
    }
    // An arrival that is new since the last render waits for its card to leave, and the other way round.
    const knownArrivals = before?.arrivals ?? null;
    if (knownArrivals) for (const item of arrivals) if (!knownArrivals.has(item.key)) waiting.current.set(item.key, { item, until: now + PENDING_MS });
    pending.current = pending.current.filter((entry) => entry.until > now);
    for (const [key, entry] of waiting.current) if (entry.until <= now) waiting.current.delete(key);

    // A poster whose card is still showing "Imported" is kept off the shelf, its slot closed, until it flies in (the card has not left yet).
    const landedCards = cards.filter((card) => card.landed);
    const held = new Set(arrivals.filter((item) => landedCards.some((card) => sameTitle(card, item))).map((item) => item.key));
    for (const element of Array.from(host.ownerDocument.querySelectorAll<HTMLElement>("[data-arrival-poster]"))) {
      const keys = [element.dataset.arrivalPoster ?? "", ...(element.dataset.arrivalMembers ?? "").split("|")];
      if (element.hasAttribute(FLYING_ATTRIBUTE)) continue;
      const wanted = motionAllowed() && keys.some((key) => held.has(key));
      if (wanted && !element.hasAttribute(HELD_ATTRIBUTE)) hold(element);
      if (!wanted && element.hasAttribute(HELD_ATTRIBUTE)) release(element);
    }

    if (motionAllowed()) {
      for (const entry of [...pending.current]) {
        const match = [...waiting.current.values()].find(({ item }) => sameTitle(entry.card, item));
        if (!match) continue;
        const target = Array.from(host.ownerDocument.querySelectorAll<HTMLElement>("[data-arrival-poster]")).find((element) => element.dataset.arrivalPoster === match.item.key || (element.dataset.arrivalMembers ?? "").split("|").includes(match.item.key));
        if (!target) continue;
        flyToShelf(entry.delivery, target);
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
