import { describe, expect, it } from "vitest";
import { fitDetails } from "./detail-fit";

/** A card shows as many whole detail lines as its height holds (#1098): 15px a line, 8px kept under them. */
function board(cards: { bottom: number; blockTop: number; lines: number; block?: boolean }[]) {
  const host = document.createElement("div");
  for (const spec of cards) {
    const card = document.createElement("div");
    card.setAttribute("data-journey-card", "");
    card.getBoundingClientRect = () => ({ bottom: spec.bottom }) as DOMRect;
    if (spec.block !== false) {
      const block = document.createElement("div");
      block.className = "lj-x";
      block.getBoundingClientRect = () => ({ top: spec.blockTop }) as DOMRect;
      for (let i = 0; i < spec.lines; i += 1) block.appendChild(document.createElement("span"));
      card.appendChild(block);
    }
    host.appendChild(card);
  }
  return host;
}
const shown = (host: HTMLElement, card = 0) => Array.from(host.children[card].querySelectorAll(".lj-x > *")).map((line) => (line as HTMLElement).style.display);

describe("fitting a card's detail lines to its height", () => {
  it("shows the lines that fit whole and hides the rest", () => {
    const host = board([{ bottom: 100, blockTop: 40, lines: 4 }]); // (100 - 8 - 40) / 15 = 3.46
    fitDetails(host);
    expect(shown(host)).toEqual(["", "", "", "none"]);
    expect((host.querySelector(".lj-x") as HTMLElement).style.display).toBe("");
  });

  it("hides the whole block on a card too short for one line", () => {
    const host = board([{ bottom: 60, blockTop: 40, lines: 2 }]);
    fitDetails(host);
    expect(shown(host)).toEqual(["none", "none"]);
    expect((host.querySelector(".lj-x") as HTMLElement).style.display).toBe("none");
  });

  it("brings back lines hidden earlier when the card has grown", () => {
    const host = board([{ bottom: 60, blockTop: 40, lines: 2 }]);
    fitDetails(host);
    host.children[0].getBoundingClientRect = () => ({ bottom: 200 }) as DOMRect;
    fitDetails(host);
    expect(shown(host)).toEqual(["", ""]);
    expect((host.querySelector(".lj-x") as HTMLElement).style.display).toBe("");
  });

  it("fits each card on its own, and skips a card with no detail block", () => {
    const host = board([{ bottom: 70, blockTop: 40, lines: 2 }, { bottom: 0, blockTop: 0, lines: 0, block: false }, { bottom: 200, blockTop: 40, lines: 2 }]);
    fitDetails(host);
    expect(shown(host, 0)).toEqual(["", "none"]);
    expect(shown(host, 2)).toEqual(["", ""]);
  });
});
