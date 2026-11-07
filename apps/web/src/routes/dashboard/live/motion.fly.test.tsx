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

function Harness({ cards, arrivals, settled = true }: { cards: JourneyCard[]; arrivals: ArrivalItem[]; settled?: boolean }) {
  const root = useRef<HTMLDivElement>(null);
  useFlyToShelf(root, cards, arrivals, settled);
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

/** jsdom loads no pictures: say whether the card's poster has finished loading. */
function posterLoaded(loaded: boolean) {
  vi.spyOn(HTMLImageElement.prototype, "complete", "get").mockReturnValue(loaded);
  vi.spyOn(HTMLImageElement.prototype, "naturalWidth", "get").mockReturnValue(loaded ? 300 : 0);
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "requestAnimationFrame", "cancelAnimationFrame", "performance", "Date"] });
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockReturnValue({ x: 10, y: 10, left: 10, top: 10, right: 110, bottom: 160, width: 100, height: 150, toJSON: () => ({}) } as DOMRect);
  posterLoaded(true);
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
  // A later reading still shows it importing: it has been watched live.
  view.rerender(<Harness cards={[{ ...card() }]} arrivals={[]} />);
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
    view.rerender(<Harness cards={[card()]} arrivals={[]} />);

    view.rerender(<Harness cards={[]} arrivals={[{ ...arrival(), key: "arrival-2", entity: { kind: "movie", id: "other" }, release: "Heat.1995.1080p" } as ArrivalItem]} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });

    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
  });

  it("flies nothing on a first load, where the title was imported hours ago and its card is only showing as done", async () => {
    reducedMotion(false);
    const done = { ...card(), landed: true } as JourneyCard;
    const view = render(<Harness cards={[done]} arrivals={[]} />);

    view.rerender(<Harness cards={[]} arrivals={[arrival()]} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });

    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
    expect(view.getByTestId("shelf-poster")).not.toHaveAttribute(HELD_ATTRIBUTE);
  });

  it("flies once, carrying the poster, for a card seen importing that then completes live", async () => {
    reducedMotion(false);
    const view = render(<Harness cards={[card()]} arrivals={[]} />);
    view.rerender(<Harness cards={[{ ...card() }]} arrivals={[]} />);
    view.rerender(<Harness cards={[{ ...card(), landed: true } as JourneyCard]} arrivals={[]} />);
    view.rerender(<Harness cards={[]} arrivals={[arrival()]} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });

    const flights = document.body.querySelectorAll(".lj-fly");
    expect(flights).toHaveLength(1);
    expect(flights[0]!.querySelector("img")?.getAttribute("src")).toBe("/poster.jpg");
  });

  it("does not fly when the poster has not loaded", async () => {
    reducedMotion(false);
    posterLoaded(false);
    const view = await importIt();

    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
    expect(view.getByTestId("shelf-poster")).not.toHaveAttribute(HELD_ATTRIBUTE);
  });

  it("does not fly when the shelf tile is below the window", async () => {
    reducedMotion(false);
    const view = render(<Harness cards={[card()]} arrivals={[]} />);
    view.rerender(<Harness cards={[card()]} arrivals={[]} />);
    const visible = { call: (_el?: unknown) => ({ x: 10, y: 10, left: 10, top: 10, right: 110, bottom: 160, width: 100, height: 150, toJSON: () => ({}) }) as DOMRect };
    const below = { x: 10, y: 5000, left: 10, top: 5000, right: 110, bottom: 5150, width: 100, height: 150, toJSON: () => ({}) } as DOMRect;
    vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) { return this.hasAttribute("data-arrival-poster") ? below : visible.call(this); });
    view.rerender(<Harness cards={[]} arrivals={[arrival()]} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
    expect(view.getByTestId("shelf-poster")).not.toHaveAttribute(HELD_ATTRIBUTE);
  });

  it("does not fly when the card was scrolled off the window", async () => {
    reducedMotion(false);
    const view = render(<Harness cards={[card()]} arrivals={[]} />);
    const visible = { call: (_el?: unknown) => ({ x: 10, y: 10, left: 10, top: 10, right: 110, bottom: 160, width: 100, height: 150, toJSON: () => ({}) }) as DOMRect };
    const above = { x: 10, y: -900, left: 10, top: -900, right: 110, bottom: -750, width: 100, height: 150, toJSON: () => ({}) } as DOMRect;
    vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) { return this.closest("[data-card-poster]") ? above : visible.call(this); });
    view.rerender(<Harness cards={[card()]} arrivals={[]} />);
    view.rerender(<Harness cards={[]} arrivals={[arrival()]} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
  });

  it("flies a fast import that went from downloading straight to done between two readings", async () => {
    reducedMotion(false);
    const view = render(<Harness cards={[{ ...card(), stage: "downloading" } as JourneyCard]} arrivals={[]} />);
    view.rerender(<Harness cards={[{ ...card(), stage: "downloading" } as JourneyCard]} arrivals={[]} />);
    view.rerender(<Harness cards={[]} arrivals={[arrival()]} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(1);
  });

  it("flies nothing for a card drawn from the saved board that completes on the first fresh reading", async () => {
    reducedMotion(false);
    const view = render(<Harness cards={[card()]} arrivals={[]} settled={false} />);
    view.rerender(<Harness cards={[]} arrivals={[arrival()]} settled />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
  });

  it("flies nothing when Live is returned to and the card completes on the fresh read", async () => {
    reducedMotion(false);
    // Returning to Live: the cached board is drawn while the sources are read again, and the first fresh answer lands it.
    const back = render(<Harness cards={[card()]} arrivals={[]} settled={false} />);
    back.rerender(<Harness cards={[card()]} arrivals={[]} settled />);
    back.rerender(<Harness cards={[]} arrivals={[arrival()]} settled />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(document.body.querySelectorAll(".lj-fly")).toHaveLength(0);
  });
});
