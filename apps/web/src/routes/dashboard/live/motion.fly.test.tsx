import { act, cleanup, render } from "@testing-library/react";
import { useRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ArrivalItem } from "./extras";
import type { JourneyCard } from "./journey";
import { FLIGHT_MS, LANDING_FADE_MS } from "./flight-path";
import { FLYING_ATTRIBUTE, HELD_ATTRIBUTE } from "./shelf-slots";
import { useFlyToShelf } from "./motion";

/**
 * The flight of an imported title's poster (#1329): when a card leaves the board and the same title turns up on the shelf, its poster
 * flies from the card to the shelf and lands in its place; with reduced motion nothing flies and the poster simply appears.
 */
const card = (): JourneyCard => ({ key: "card-1", stage: "importing", title: "Arrival", release: "Arrival.2016.1080p", entity: { kind: "movie", id: "m1" }, state: { tone: "ok", label: "Importing" } }) as unknown as JourneyCard;
const arrival = (): ArrivalItem => ({ key: "arrival-1", entity: { kind: "movie", id: "m1" }, fallbackTitle: "Arrival", release: "Arrival.2016.1080p", quality: null, previousQuality: null, upgraded: false, sizeBytes: null, at: "2026-10-30T00:00:00Z" }) as unknown as ArrivalItem;

function Harness({ cards, arrivals }: { cards: JourneyCard[]; arrivals: ArrivalItem[] }) {
  const root = useRef<HTMLDivElement>(null);
  useFlyToShelf(root, cards, arrivals);
  return (
    <div ref={root}>
      {cards.map((item) => <div key={item.key} data-card-poster={item.key}><img alt="" src="/poster.jpg" /></div>)}
      {arrivals.map((item) => <div key={item.key} data-arrival-poster={item.key} data-testid="shelf-poster" />)}
    </div>
  );
}

function reducedMotion(reduce: boolean) {
  window.matchMedia = ((query: string) => ({ matches: reduce && query.includes("prefers-reduced-motion"), media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "requestAnimationFrame", "cancelAnimationFrame", "performance", "Date"] });
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockReturnValue({ x: 10, y: 10, left: 10, top: 10, right: 110, bottom: 160, width: 100, height: 150, toJSON: () => ({}) } as DOMRect);
  Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
});
afterEach(() => {
  cleanup();
  document.body.querySelectorAll(".lj-fly").forEach((ghost) => ghost.remove());
  vi.useRealTimers();
  vi.restoreAllMocks();
});

async function importIt() {
  const view = render(<Harness cards={[card()]} arrivals={[]} />);
  // The card leaves the board and its title turns up on the shelf.
  view.rerender(<Harness cards={[]} arrivals={[arrival()]} />);
  await act(async () => { await vi.advanceTimersByTimeAsync(0); });
  return view;
}

describe("the poster of a title that has just been imported", () => {
  it("flies from its card to the shelf, holds the shelf poster out of sight while it flies, and leaves it showing once it has landed", async () => {
    reducedMotion(false);

    const view = await importIt();

    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(1);
    const shelf = view.getByTestId("shelf-poster");
    expect(shelf).toHaveAttribute(FLYING_ATTRIBUTE);
    expect(shelf).toHaveAttribute(HELD_ATTRIBUTE);
    expect(shelf.style.visibility).toBe("hidden");

    await act(async () => { await vi.advanceTimersByTimeAsync(FLIGHT_MS + LANDING_FADE_MS + 100); });

    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
    expect(shelf).not.toHaveAttribute(FLYING_ATTRIBUTE);
    expect(shelf).not.toHaveAttribute(HELD_ATTRIBUTE);
    expect(shelf.style.visibility).toBe("");
  });

  it("with reduced motion nothing flies and the poster is simply on the shelf", async () => {
    reducedMotion(true);

    const view = await importIt();

    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
    const shelf = view.getByTestId("shelf-poster");
    expect(shelf).not.toHaveAttribute(FLYING_ATTRIBUTE);
    expect(shelf).not.toHaveAttribute(HELD_ATTRIBUTE);
    expect(shelf.style.visibility).toBe("");
  });

  it("does not fly for a card that left when a different title arrived", async () => {
    reducedMotion(false);
    const view = render(<Harness cards={[card()]} arrivals={[]} />);

    view.rerender(<Harness cards={[]} arrivals={[{ ...arrival(), key: "arrival-2", entity: { kind: "movie", id: "other" }, release: "Heat.1995.1080p" } as ArrivalItem]} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });

    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
  });
});
