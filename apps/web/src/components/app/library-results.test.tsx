import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const seen: Record<string, Record<string, unknown>> = {};
vi.mock("./library-grid", () => ({
  ProgressiveGrid: (props: Record<string, unknown>) => {
    seen.grid = props;
    return <div data-testid="grid" />;
  }
}));
vi.mock("./library-table", () => ({
  LibraryTable: (props: Record<string, unknown>) => {
    seen.table = props;
    return <div data-testid="table" />;
  }
}));
vi.mock("./library-overview", () => ({
  LibraryOverview: (props: Record<string, unknown>) => {
    seen.overview = props;
    return <div data-testid="overview" />;
  }
}));
vi.mock("../shell/skeleton", () => ({ LibraryGridSkeleton: ({ count }: { count: number }) => <div data-testid="skeleton">{count}</div> }));

import { LibraryResults } from "./library-results";
import type { MediaItem } from "../../lib/media-types";

/**
 * What the library shelf shows (#934): a placeholder while it does not know yet, a plain empty answer once it does, and
 * otherwise the grid, the list or the overview with the page's own selection and sort handed through.
 */
const item = (id: string) => ({ id, title: id }) as unknown as MediaItem;
type Props = ComponentProps<typeof LibraryResults>;

function show(over: Partial<Props> = {}) {
  const props: Props = {
    variant: "movies", hasLoadedOnce: true, isQueryLoading: false, items: [item("a"), item("b")], label: "Movies", singular: "movie",
    libraryCount: 1, hasActiveFilter: false, view: "grid", cardSize: "medium" as Props["cardSize"], displayOptions: { show: true } as unknown as Props["displayOptions"],
    listColumnOrder: ["title"] as unknown as Props["listColumnOrder"], onListColumnOrderChange: vi.fn(), selectedIds: [], keyBust: "k1",
    sortField: "title" as Props["sortField"], sortDirection: "asc", onSortChange: vi.fn(), isComplete: true, onOpenCreate: vi.fn(),
    onClearFilters: vi.fn(), onSelect: vi.fn(), onToggle: vi.fn(), onToggleAll: vi.fn(), onEndReached: vi.fn(), ...over
  };
  return { props, ...render(<LibraryResults {...props} />) };
}

beforeEach(() => {
  for (const key of Object.keys(seen)) delete seen[key];
});

describe("while the shelf does not know yet", () => {
  it("shows twenty placeholders before anything has loaded", () => {
    show({ hasLoadedOnce: false, items: [] });
    expect(screen.getByTestId("skeleton")).toHaveTextContent("20");
    expect(screen.queryByText(/library is empty/)).not.toBeInTheDocument();
  });

  it("shows them while a fetch for the current query is in flight with nothing yet", () => {
    show({ isQueryLoading: true, items: [] });
    expect(screen.getByTestId("skeleton")).toBeInTheDocument();
    expect(screen.queryByText("Nothing matches")).not.toBeInTheDocument();
  });

  it("keeps showing the rows it has while a refetch runs", () => {
    show({ hasLoadedOnce: false, isQueryLoading: true });
    expect(screen.queryByTestId("skeleton")).not.toBeInTheDocument();
    expect(screen.getByTestId("grid")).toBeInTheDocument();
  });
});

describe("when nothing is shown", () => {
  it("says nothing matches, with the library's size, and clears the filters from the button", async () => {
    const { props } = show({ items: [], hasActiveFilter: true, libraryCount: 3 });
    expect(screen.getByText("Nothing matches")).toBeInTheDocument();
    expect(screen.getByText("Try clearing filters or broadening your search. Your library has 3 total titles.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    expect(props.onClearFilters).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("skeleton")).not.toBeInTheDocument();
  });

  it("says the library is empty when no filter is on, and offers to add the first title", async () => {
    const { props } = show({ items: [], label: "TV", singular: "show" });
    expect(screen.getByText("Your TV library is empty")).toBeInTheDocument();
    expect(screen.getByText("Add your first show to start monitoring releases, running search, and building out your collection.")).toBeInTheDocument();
    expect(screen.getByText("Deluno will track up to 100,000 TV without breaking a sweat.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Add show" }));
    expect(props.onOpenCreate).toHaveBeenCalledTimes(1);
  });
});

describe("when there are titles", () => {
  it("draws the grid with the page's size, options, selection and sort", () => {
    const { props } = show({ view: "grid", selectedIds: ["a"] });
    expect(screen.getByTestId("grid")).toBeInTheDocument();
    expect(screen.queryByTestId("table")).not.toBeInTheDocument();
    expect(seen.grid).toMatchObject({
      items: props.items, cardSize: "medium", displayOptions: props.displayOptions, selectedIds: ["a"], keyBust: "k1", sortField: "title",
      sortDirection: "asc", isComplete: true, onSelect: props.onSelect, onToggle: props.onToggle, onEndReached: props.onEndReached
    });
  });

  it("keeps showing them while a filter is on", () => {
    show({ hasActiveFilter: true });
    expect(screen.getByTestId("grid")).toBeInTheDocument();
    expect(screen.queryByText("Nothing matches")).not.toBeInTheDocument();
  });

  it("draws the overview with the selection", () => {
    const { props } = show({ view: "overview", selectedIds: ["b"], isComplete: false });
    expect(screen.getByTestId("overview")).toBeInTheDocument();
    expect(seen.overview).toMatchObject({ items: props.items, selectedIds: ["b"], isComplete: false, onSelect: props.onSelect, onToggle: props.onToggle, onEndReached: props.onEndReached });
  });

  it("draws the list with its columns, sort and select-all", () => {
    const { props } = show({ view: "list", variant: "shows", selectedIds: [] });
    expect(screen.getByTestId("table")).toBeInTheDocument();
    expect(seen.table).toMatchObject({
      variant: "shows", items: props.items, selectedIds: [], sortField: "title", sortDirection: "asc", onSortChange: props.onSortChange, isComplete: true,
      columnOrder: props.listColumnOrder, onColumnOrderChange: props.onListColumnOrderChange, onSelect: props.onSelect, onToggle: props.onToggle,
      onToggleAll: props.onToggleAll, onEndReached: props.onEndReached
    });
  });

  it("tells the list when none, some or all of the rows are selected", () => {
    show({ view: "list", selectedIds: [] });
    expect(seen.table).toMatchObject({ allSelected: false, someSelected: false });
    show({ view: "list", selectedIds: ["a"] });
    expect(seen.table).toMatchObject({ allSelected: false, someSelected: true });
    show({ view: "list", selectedIds: ["a", "b", "zzz"] });
    expect(seen.table).toMatchObject({ allSelected: true, someSelected: false });
  });
});
