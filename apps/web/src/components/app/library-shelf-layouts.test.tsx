import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { listColumnsFor } from "../../lib/library-list-columns";
import type { MediaItem } from "../../lib/media-types";
import { stubVirtualLayout } from "../../test/virtual-layout";
import { JumpRail } from "./library-jump-rail";
import { LibraryOverview } from "./library-overview";
import { LibraryTable } from "./library-table";

/**
 * The shelf's other layouts and its jump rail (coverage register, area library): the compact list's checkboxes and keys, the
 * Overview rows with their click and Ctrl/Cmd-click, and the A-Z rail beside the shelf. Rendered as the shelf renders them, on a
 * stubbed screen size because jsdom lays nothing out.
 */
const item = (id: string, title: string, over: Partial<MediaItem> = {}): MediaItem => ({
  id, title, year: 2020, type: "movie", poster: null, backdrop: null, quality: "WEB 1080p", monitored: true, sizeGb: 4, rating: 7.1,
  genres: ["Drama"], added: "12 Aug", overview: `About ${title}.`, ...over
});
const ITEMS = [item("a", "Alien"), item("b", "Aliens", { monitored: false, overview: "" }), item("c", "Heat")];

let restoreLayout: () => void;
beforeEach(() => {
  restoreLayout = stubVirtualLayout();
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => restoreLayout());

describe("the compact list", () => {
  function renderTable(props: Partial<React.ComponentProps<typeof LibraryTable>> = {}) {
    const handlers = { onSelect: vi.fn(), onToggle: vi.fn(), onToggleAll: vi.fn(), onSortChange: vi.fn(), onEndReached: vi.fn(), onColumnOrderChange: vi.fn() };
    render(
      <MemoryRouter>
        <LibraryTable
          items={ITEMS} selectedIds={[]} allSelected={false} someSelected={false} sortField="title" sortDirection="asc" isComplete
          variant="movies" columnOrder={[...listColumnsFor("movies")]} {...handlers} {...props}
        />
      </MemoryRouter>
    );
    return handlers;
  }

  it("draws a checkbox on every row that toggles that row, and a header checkbox that selects all", async () => {
    const handlers = renderTable();

    const rows = await screen.findAllByRole("checkbox", { name: "Select row" });
    expect(rows).toHaveLength(3);
    fireEvent.click(rows[1]);
    expect(handlers.onToggle).toHaveBeenCalledWith("b");
    fireEvent.click(screen.getByRole("checkbox", { name: "Select all rows" }));
    expect(handlers.onToggleAll).toHaveBeenCalledTimes(1);
  });

  it("the header checkbox is mixed when some rows are selected and checked when all are, and a selected row says so", async () => {
    const { unmount } = render(<span />);
    unmount();
    renderTable({ selectedIds: ["a"], someSelected: true });
    const header = await screen.findByRole("checkbox", { name: "Select all rows" });
    expect(header).toHaveAttribute("aria-checked", "mixed");
    const selectedRow = within(document.querySelector('tr[data-row-index="0"]') as HTMLElement).getByRole("checkbox", { name: "Select row" });
    expect(selectedRow).toBeChecked();
    expect(document.querySelector('tr[data-row-index="0"]')).toHaveAttribute("aria-selected", "true");
    expect(document.querySelector('tr[data-row-index="1"]')).toHaveAttribute("aria-selected", "false");
  });

  it("the header checkbox reads as checked once every row is selected", async () => {
    renderTable({ selectedIds: ["a", "b", "c"], allSelected: true });

    expect(await screen.findByRole("checkbox", { name: "Select all rows" })).toBeChecked();
  });

  it("the keyboard moves between rows with j, k, Home and End, opens with Enter and selects with x or Space", async () => {
    const handlers = renderTable();
    await screen.findAllByRole("checkbox", { name: "Select row" });
    const row = (index: number) => document.querySelector(`tr[data-row-index="${index}"]`) as HTMLElement;
    row(0).focus();

    fireEvent.keyDown(row(0), { key: "j" });
    expect(document.activeElement).toBe(row(1));
    fireEvent.keyDown(row(1), { key: "End" });
    expect(document.activeElement).toBe(row(2));
    fireEvent.keyDown(row(2), { key: "k" });
    expect(document.activeElement).toBe(row(1));
    fireEvent.keyDown(row(1), { key: "Home" });
    expect(document.activeElement).toBe(row(0));
    fireEvent.keyDown(row(0), { key: "Enter" });
    expect(handlers.onSelect).toHaveBeenLastCalledWith(ITEMS[0]);
    fireEvent.keyDown(row(0), { key: "x" });
    fireEvent.keyDown(row(0), { key: " " });
    expect(handlers.onToggle).toHaveBeenCalledTimes(2);
    expect(handlers.onToggle).toHaveBeenLastCalledWith("a");
  });

  it("clicking a row's title opens that title", async () => {
    const handlers = renderTable();

    fireEvent.click(await screen.findByText("Heat"));

    expect(handlers.onSelect).toHaveBeenLastCalledWith(ITEMS[2]);
  });

  it("Alt+Right on a focused header moves that column one place later; Selection and Title are not draggable", async () => {
    const order = [...listColumnsFor("movies")];
    const handlers = renderTable({ columnOrder: order });
    await screen.findAllByRole("checkbox", { name: "Select row" });
    const header = document.querySelector(`th[data-column-key="${order[0]}"]`) as HTMLElement;

    fireEvent.keyDown(header, { key: "ArrowRight", altKey: true });

    expect(handlers.onColumnOrderChange).toHaveBeenLastCalledWith([order[1], order[0], ...order.slice(2)]);
    expect(document.querySelectorAll("th[draggable]")).toHaveLength(order.length);
    expect(screen.getByRole("columnheader", { name: /Title/ }).hasAttribute("draggable")).toBe(false);
  });
});

describe("the Overview layout", () => {
  function renderOverview(props: Partial<React.ComponentProps<typeof LibraryOverview>> = {}) {
    const handlers = { onSelect: vi.fn(), onToggle: vi.fn(), onEndReached: vi.fn() };
    render(<MemoryRouter><LibraryOverview items={ITEMS} selectedIds={[]} isComplete {...handlers} {...props} /></MemoryRouter>);
    return handlers;
  }

  it("draws a row per title with its year, monitoring, rating, quality, synopsis and genres, and says so when there is no synopsis", () => {
    renderOverview();

    expect(screen.getAllByRole("button", { name: /^Open / })).toHaveLength(3);
    expect(screen.getByText("About Alien.")).toBeInTheDocument();
    expect(screen.getAllByText("Monitored").length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText("Unmonitored").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("WEB 1080p")).toHaveLength(3);
    expect(screen.getAllByText("7.1")).toHaveLength(3);
    expect(screen.getAllByText("Drama")).toHaveLength(3);
    expect(screen.getByText("No synopsis has been stored yet. Refresh metadata when you want Deluno to enrich this title.")).toBeInTheDocument();
  });

  it("a click opens the title and a Ctrl or Cmd click toggles its selection instead", () => {
    const handlers = renderOverview();

    fireEvent.click(screen.getByRole("button", { name: "Open Alien" }));
    expect(handlers.onSelect).toHaveBeenLastCalledWith(ITEMS[0]);
    expect(handlers.onToggle).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Open Heat" }), { ctrlKey: true });
    fireEvent.click(screen.getByText("Aliens"), { metaKey: true });

    expect(handlers.onToggle.mock.calls).toEqual([["c"], ["b"]]);
    expect(handlers.onSelect).toHaveBeenCalledTimes(1);
  });

  it("a selected row is drawn selected, and each row links to its own page", () => {
    renderOverview({ selectedIds: ["b"] });

    const selected = screen.getByRole("button", { name: "Open Aliens" }).closest("div.group");
    expect(selected?.className).toMatch(/border-primary\/70/);
    expect(screen.getByRole("button", { name: "Open Alien" }).closest("div.group")?.className).not.toMatch(/border-primary\/70/);
    expect(screen.getAllByRole("link", { name: /Open$/ }).map((link) => link.getAttribute("href"))).toEqual(["/movies/a", "/movies/b", "/movies/c"]);
  });

  describe("reaching the end", () => {
    let callback: IntersectionObserverCallback;
    beforeEach(() => {
      vi.stubGlobal("IntersectionObserver", class {
        constructor(cb: IntersectionObserverCallback) { callback = cb; }
        observe() {} unobserve() {} disconnect() {}
      });
    });
    afterEach(() => vi.unstubAllGlobals());

    it("asks for the next slice when the end comes into view while more is coming, and never asks once the library is complete", async () => {
      const handlers = renderOverview({ isComplete: false });
      callback([{ isIntersecting: true } as IntersectionObserverEntry], {} as IntersectionObserver);
      await waitFor(() => expect(handlers.onEndReached).toHaveBeenCalledTimes(1));
      callback([{ isIntersecting: false } as IntersectionObserverEntry], {} as IntersectionObserver);
      expect(handlers.onEndReached).toHaveBeenCalledTimes(1);
    });

    it("does not even watch for the end when everything has arrived", () => {
      const observed = vi.fn();
      vi.stubGlobal("IntersectionObserver", class { constructor() {} observe = observed; unobserve() {} disconnect() {} });
      renderOverview({ isComplete: true });
      expect(observed).not.toHaveBeenCalled();
    });
  });

  // DEFECT (missing control): the register row promises a per-row checkbox in the Overview layout. OverviewRow draws none, so a row
  // can only be selected with Ctrl or Cmd held, which a phone or tablet cannot do.
  it("every Overview row has its own checkbox", () => {
    renderOverview();
    expect(screen.getAllByRole("checkbox")).toHaveLength(3);
  });
});

describe("the jump rail", () => {
  const buckets = [
    { label: "A", index: 0, count: 12 },
    { label: "B", index: 12, count: 1 },
    { label: "C", index: null, count: 0 },
    { label: "D", index: null, count: 0 }
  ];

  it("shows each stop with its count, marks the one you are at, and jumps to a loaded stop", () => {
    const onJump = vi.fn();
    render(<JumpRail buckets={buckets} activeIndex={13} isComplete={false} onJump={onJump} />);

    expect(screen.getByRole("navigation", { name: "Jump to" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "A" })).toHaveAttribute("title", "A — 12 titles");
    expect(screen.getByRole("button", { name: "B" })).toHaveAttribute("title", "B — 1 title");
    expect(screen.getByRole("button", { name: "B" })).toHaveAttribute("aria-current", "true");
    expect(screen.getByRole("button", { name: "A" })).not.toHaveAttribute("aria-current");
    fireEvent.click(screen.getByRole("button", { name: "A" }));
    expect(onJump).toHaveBeenCalledWith(0);
  });

  it("a stop whose titles have not arrived says it is still loading, and the jump happens once they do", () => {
    const onJump = vi.fn();
    const { rerender } = render(<JumpRail buckets={buckets} activeIndex={0} isComplete={false} onJump={onJump} />);

    expect(screen.getByRole("button", { name: "C" })).toHaveAttribute("title", "C — still loading");
    expect(screen.getByRole("button", { name: "C" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "C" }));
    expect(onJump).not.toHaveBeenCalled();

    rerender(<JumpRail buckets={buckets.map((bucket) => (bucket.label === "C" ? { ...bucket, index: 40, count: 3 } : bucket))} activeIndex={0} isComplete={false} onJump={onJump} />);
    expect(onJump).toHaveBeenCalledWith(40);
  });

  it("once the library is complete an empty stop is inert and says no titles are under it, and a wait on it is dropped", () => {
    const onJump = vi.fn();
    const { rerender } = render(<JumpRail buckets={buckets} activeIndex={0} isComplete={false} onJump={onJump} />);
    fireEvent.click(screen.getByRole("button", { name: "D" }));

    rerender(<JumpRail buckets={buckets} activeIndex={0} isComplete onJump={onJump} />);

    const d = screen.getByRole("button", { name: "D" });
    expect(d).toBeDisabled();
    expect(d).toHaveAttribute("title", "No titles under D");
    expect(onJump).not.toHaveBeenCalled();
  });

  it("draws no rail for fewer than two stops", () => {
    render(<JumpRail buckets={[buckets[0]]} activeIndex={0} isComplete onJump={vi.fn()} />);

    expect(screen.queryByRole("navigation", { name: "Jump to" })).toBeNull();
  });
});

