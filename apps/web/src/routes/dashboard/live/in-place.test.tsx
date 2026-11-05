import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import type { CardContext } from "./card";
import { buildOperations } from "./band";
import { byStage, laneCounts, type JourneyCard } from "./journey";
import { ActivityStream } from "./stream";
import { NO_TITLES } from "./titles";
import { Journey } from "./track";

/** Nothing on the Live board is torn down and drawn again to show a change (#1508): the same element is updated. */
const context: CardContext = { titles: NO_TITLES, arrivals: new Set(), indexers: 3 };

function board(cards: JourneyCard[]) {
  const grouped = byStage(cards);
  return (
    <MemoryRouter>
      <Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={context} hasProcessor usual={{}} by={undefined} lastArrival={null} />
    </MemoryRouter>
  );
}

const card = (extra: Partial<JourneyCard>): JourneyCard => ({ key: "qb:1", stage: "downloading", title: "The Fall Guy (2024)", state: { tone: "info", label: "Checking files" }, to: "/queue", progress: 10, dispatchId: "d1", ...extra });
const tokens = (root: HTMLElement) => Array.from(root.querySelectorAll<HTMLElement>(".lj-token"));

describe("the Pipeline's cards", () => {
  it("keep their element when their text and progress change", () => {
    const { container, rerender } = render(board([card({})]));
    const before = tokens(container);
    expect(before).toHaveLength(1);
    rerender(board([card({ state: { tone: "info", label: "Downloading" }, progress: 55 })]));
    expect(tokens(container)[0]).toBe(before[0]);
    expect(tokens(container)[0].textContent).toContain("55%");
  });

  it("keep their element when the list that holds them changes, so the entry animation does not play again", () => {
    const { container, rerender } = render(board([card({})]));
    const before = tokens(container)[0];
    // The download left the client's list: the processor's hand-off holds the same sent download now, under another key.
    rerender(board([card({ key: "handoff:9", stage: "processor", state: { tone: "info", label: "Being cleaned" } })]));
    expect(tokens(container)[0]).toBe(before);
    // Back to the client under a key spelt another way.
    rerender(board([card({ key: "QB:1", state: { tone: "info", label: "Downloading" } })]));
    expect(tokens(container)[0]).toBe(before);
  });

  it("are two elements for two cards that share a sent download", () => {
    const { container } = render(board([card({}), card({ key: "qb:2" })]));
    expect(tokens(container)).toHaveLength(2);
  });
});

describe("Working on now", () => {
  it("names a title being cleaned the same way whichever list holds it", () => {
    const processing = (key: string) => card({ key, stage: "processor", processor: "Weir", state: { tone: "info", label: "Cleaning" }, processorStep: "Unpacking" });
    const [one] = buildOperations([], [processing("qb:1")], NO_TITLES);
    const [other] = buildOperations([], [processing("handoff:9")], NO_TITLES);
    if (one && other) expect(one.key).toBe(other.key);
  });
});

describe("the Activity stream's rows", () => {
  const row = (id: string, message: string, extra: object = {}) => ({ id, message, category: "import", label: "Search", severity: "info", relatedEntityType: null, relatedEntityId: null, createdUtc: new Date().toISOString(), fresh: false, ...extra }) as never;
  const stream = (rows: unknown[]) => <MemoryRouter><ActivityStream rows={rows as never} titles={NO_TITLES} /></MemoryRouter>;
  const items = (root: HTMLElement) => Array.from(root.querySelectorAll<HTMLElement>(".lact-ev"));

  it("keep their element when a new event arrives above them", () => {
    const { container, rerender } = render(stream([row("e2", "Imported Dune"), row("e1", "Imported Arrival")]));
    const before = items(container);
    rerender(stream([row("e3", "Imported Sintel", { fresh: true }), row("e2", "Imported Dune"), row("e1", "Imported Arrival")]));
    const after = items(container);
    expect(after).toHaveLength(3);
    expect(after[1]).toBe(before[0]);
    expect(after[2]).toBe(before[1]);
  });

  it("keep their element when a new event says the same thing and joins the line", () => {
    const { container, rerender } = render(stream([row("e1", "Imported Dune")]));
    const before = items(container)[0];
    rerender(stream([row("e2", "Imported Dune", { fresh: true }), row("e1", "Imported Dune")]));
    expect(items(container)).toHaveLength(1);
    expect(items(container)[0]).toBe(before);
    expect(items(container)[0].textContent).toContain("2 times");
  });
});
