import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FitList, rowsThatFit } from "./parts";

describe("rowsThatFit", () => {
  const bottoms = [40, 80, 120, 160, 200];

  it("shows everything when everything fits, and when nothing has been laid out", () => {
    expect(rowsThatFit(bottoms, 200)).toBe(5);
    expect(rowsThatFit(bottoms, 0)).toBe(5);
    expect(rowsThatFit([], 100)).toBe(0);
  });

  it("keeps only whole rows and leaves room for the and-N-more line", () => {
    // 150 high, 36 kept for the link: rows ending at 40 and 80 fit; the one ending at 120 does not.
    expect(rowsThatFit(bottoms, 150)).toBe(2);
    expect(rowsThatFit(bottoms, 76)).toBe(1);
    expect(rowsThatFit(bottoms, 30)).toBe(0);
  });

  it("never leaves a heading as the last thing shown", () => {
    // Row 1 is a caption; showing rows 0 and 1 would end on it, so it is dropped too.
    expect(rowsThatFit(bottoms, 150, 36, (index) => index === 1)).toBe(1);
  });
});

describe("FitList", () => {
  const ROW = 40;
  let rect: typeof HTMLElement.prototype.getBoundingClientRect;

  beforeEach(() => {
    rect = HTMLElement.prototype.getBoundingClientRect;
  });
  afterEach(() => {
    HTMLElement.prototype.getBoundingClientRect = rect;
    vi.restoreAllMocks();
  });

  /** A panel of the given height whose rows are 40 px each, laid out one under another. */
  function mockLayout(boxHeight: number) {
    HTMLElement.prototype.getBoundingClientRect = function (this: HTMLElement) {
      const make = (top: number, height: number) => ({ top, bottom: top + height, height, left: 0, right: 0, width: 0, x: 0, y: top, toJSON: () => ({}) }) as DOMRect;
      if (this.hasAttribute("data-fit-row")) {
        const siblings = Array.from(document.querySelectorAll("[data-fit-row]"));
        return make(siblings.indexOf(this) * ROW, ROW);
      }
      if (this.className.includes("overflow-hidden")) return make(0, boxHeight);
      return make(0, 0);
    };
  }

  const rows = (count: number) => Array.from({ length: count }, (_, index) => <li key={index}>Row {index + 1}</li>);

  it("shows only the rows that fit whole, then how many more and where they are", async () => {
    mockLayout(200);
    render(
      <MemoryRouter>
        <FitList rows={rows(30)} moreTo="/queue" moreLabel={(n) => `and ${n} more on Transfers`} />
      </MemoryRouter>
    );

    // 200 px, 36 kept for the link: 4 whole rows of 40.
    await waitFor(() => expect(screen.getAllByText(/^Row /)).toHaveLength(4));
    expect(screen.getByRole("link", { name: /and 26 more on Transfers/ })).toHaveAttribute("href", "/queue");
  });

  it("counts rows the list was never given as more too", async () => {
    mockLayout(200);
    render(
      <MemoryRouter>
        <FitList rows={rows(3)} moreTo="/queue" extra={17} />
      </MemoryRouter>
    );
    await waitFor(() => expect(screen.getByRole("link", { name: /and 17 more/ })).toBeInTheDocument());
    expect(screen.getAllByText(/^Row /)).toHaveLength(3);
  });

  it("shows every row and no link when they all fit", async () => {
    mockLayout(400);
    render(
      <MemoryRouter>
        <FitList rows={rows(5)} moreTo="/queue" />
      </MemoryRouter>
    );
    await waitFor(() => expect(screen.getAllByText(/^Row /)).toHaveLength(5));
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("shows every row where nothing is laid out at all", () => {
    render(
      <MemoryRouter>
        <FitList rows={rows(12)} moreTo="/queue" />
      </MemoryRouter>
    );
    expect(screen.getAllByText(/^Row /)).toHaveLength(12);
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("sends the link to where the first hidden row lives", async () => {
    mockLayout(120);
    render(
      <MemoryRouter>
        <FitList rows={rows(10)} moreTo={(hidden) => `/first-hidden/${hidden[0].key}`} />
      </MemoryRouter>
    );
    // 120 - 36 = 84: two whole rows, so the first hidden is row 2.
    await waitFor(() => expect(screen.getByRole("link")).toHaveAttribute("href", "/first-hidden/2"));
  });
});
