import { cleanup, render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { stabilizeJourney, type JourneyMemory } from "./identity";
import { byStage, laneCounts, STAGES, type JourneyCard } from "./journey";
import { NO_TITLES } from "./titles";
import { Journey } from "./track";

afterEach(cleanup);

const entity = { kind: "movie" as const, id: "m-fd3" };
const base: Omit<JourneyCard, "key" | "stage" | "state"> = { title: "Final Destination 3 (2006)", to: "/movies/m-fd3" };
const search: JourneyCard = { ...base, key: "search:m-fd3", stage: "searching", entity, state: { tone: "info", label: "Checking 5 indexers" } };
const sending: JourneyCard = { ...base, key: "work:grab:Final.Destination.3.2006", stage: "grabbed", state: { tone: "info", label: "Sending to qBittorrent" } };
const queued: JourneyCard = { ...base, key: "qb:abc", stage: "grabbed", entity, dispatchId: "d1", state: { tone: "info", label: "Sent to qBittorrent" } };
const downloading: JourneyCard = { ...queued, stage: "downloading", state: { tone: "info", label: "Downloading" }, progress: 3 };

/** The journey as the board draws it, one render per step, through the same stabilizer the dashboard uses. */
function walk(steps: JourneyCard[][], gapMs = 500) {
  let memory: JourneyMemory[] = [];
  let now = 1_000_000;
  return steps.map((cards) => {
    const out = stabilizeJourney(cards, memory, now);
    memory = out.memory;
    now += gapMs;
    return out;
  });
}

describe("one lasting element for a title's journey (#1508)", () => {
  it("keeps one element key from search through sending, client-queued and downloading", () => {
    const out = walk([[search], [sending], [queued], [downloading]]);
    const idents = out.map((step) => [...step.idents.values()]);
    expect(new Set(idents.flat()).size).toBe(1);
    expect(out.map((step) => step.cards.length)).toEqual([1, 1, 1, 1]);
  });

  it("does not draw the search card again once the title is further along, even for a moment (the flapping on the rig)", () => {
    const out = walk([[search], [sending], [search], [sending], [search, queued]]);
    const stages = out.map((step) => step.cards.map((card) => STAGES.indexOf(card.stage)));
    expect(stages).toEqual([[0], [1], [1], [1], [1]]);
    // the lane never goes back while the same attempt continues
    const lanes = out.flatMap((step) => step.cards.map((card) => STAGES.indexOf(card.stage)));
    expect(lanes).toEqual([...lanes].sort((a, b) => a - b));
    expect(new Set(out.flatMap((step) => [...step.idents.values()])).size).toBe(1);
  });

  it("lets a title that starts over after the grace be a search card again", () => {
    const out = walk([[downloading], [], [search]], 6_000);
    expect(out[2].cards).toHaveLength(1);
  });

  it("keeps two titles apart", () => {
    const other: JourneyCard = { ...search, key: "search:m-other", entity: { kind: "movie", id: "m-other" }, title: "Sintel (2010)" };
    const out = walk([[search, other], [sending, other]]);
    expect(new Set(out[1].idents.values()).size).toBe(2);
  });

  it("draws the same DOM node through every step", () => {
    const steps = [[search], [sending], [queued], [downloading]];
    let memory: JourneyMemory[] = [];
    let now = 5_000_000;
    const run = (cards: JourneyCard[]) => {
      const out = stabilizeJourney(cards, memory, now);
      memory = out.memory;
      now += 400;
      const grouped = byStage(out.cards);
      return <MemoryRouter><Journey grouped={grouped} counts={laneCounts(grouped, undefined)} idents={out.idents} context={{ titles: NO_TITLES, arrivals: new Set(), indexers: 5 }} hasProcessor usual={{}} by={undefined} lastArrival={null} /></MemoryRouter>;
    };
    const view = render(run(steps[0]));
    const node = view.container.querySelector(".lj-token.lj-fresh");
    expect(node).not.toBeNull();
    const lanes: number[] = [];
    for (const step of steps.slice(1)) {
      view.rerender(run(step));
      const tokens = view.container.querySelectorAll(".lj-token");
      expect(tokens).toHaveLength(1);
      expect(tokens[0]).toBe(node);
      lanes.push(tokens.length);
    }
    expect(lanes).toEqual([1, 1, 1]);
  });
});
