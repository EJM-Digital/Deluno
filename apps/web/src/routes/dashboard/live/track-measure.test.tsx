import { cleanup, render, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { CardContext } from "./card";
import { byStage, laneCounts, type JourneyCard } from "./journey";
import { NO_TITLES } from "./titles";
import { Journey } from "./track";

const fit = vi.hoisted(() => vi.fn());
vi.mock("./detail-fit", () => ({ fitDetails: fit }));

const context: CardContext = { titles: NO_TITLES, arrivals: new Set(), indexers: 3 };
const cards: JourneyCard[] = [{ key: "k", stage: "downloading", title: "Sintel (2010)", state: { tone: "info", label: "Downloading" }, to: "/queue", progress: 43, speedMbps: 6.2, client: "qBittorrent" }];
const grouped = byStage(cards);

function journey(props: { budget?: number; fill?: boolean }) {
  return render(
    <MemoryRouter>
      <Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={context} hasProcessor usual={{}} by={undefined} lastArrival={null} {...props} />
    </MemoryRouter>
  );
}

afterEach(() => {
  cleanup();
  fit.mockReset();
  vi.restoreAllMocks();
  Object.defineProperty(document, "fonts", { configurable: true, value: undefined });
});

describe("the journey's measuring", () => {
  it("counts the rows a scrolling page's budget holds from the space above the lanes, measured in the page", () => {
    const height = () => journey({ budget: 500 }).container.querySelector<HTMLElement>(".lj-lanes")!.style.height;
    const plain = height();
    cleanup();
    // The stations and header above the lanes are 60px tall here: the lanes have that much less to share.
    vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
      return { top: this.classList.contains("lj-lanes") ? 60 : 0, bottom: 0, left: 0, right: 0, width: 0, height: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
    });
    expect(height()).not.toBe(plain);
  });

  it("fits the card details again when the fonts have loaded or a weight loads later, and not after the journey has gone", async () => {
    let loaded!: () => void;
    const listeners: Record<string, () => void> = {};
    Object.defineProperty(document, "fonts", {
      configurable: true,
      value: { ready: new Promise<void>((resolve) => { loaded = resolve; }), addEventListener: (name: string, call: () => void) => { listeners[name] = call; }, removeEventListener: vi.fn() }
    });
    const first = journey({ fill: true });
    const drawn = fit.mock.calls.length;
    expect(drawn).toBeGreaterThan(0);
    listeners.loadingdone();
    expect(fit.mock.calls.length).toBe(drawn + 1);
    loaded();
    await waitFor(() => expect(fit.mock.calls.length).toBe(drawn + 2));
    // A late answer after the journey is gone fits nothing.
    first.unmount();
    const afterUnmount = fit.mock.calls.length;
    listeners.loadingdone();
    expect(fit.mock.calls.length).toBe(afterUnmount);
  });
});
