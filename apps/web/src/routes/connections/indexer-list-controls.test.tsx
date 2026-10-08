import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { IndexerItem } from "../../lib/api";
import {
  emptyIndexerFilters,
  filterIndexers,
  IndexerBulkBar,
  IndexerFilterBar,
  IndexerFilterChips,
  IndexerSelectAllCheckbox,
  indexerFiltersActive,
  indexerSortValue,
  type IndexerSortKey
} from "./indexer-list-controls";
import { sortListItems } from "../../lib/use-list-columns";

function indexer(overrides: Partial<IndexerItem> = {}): IndexerItem {
  return {
    id: overrides.id ?? "1",
    name: "NZBGeek",
    protocol: "newznab",
    privacy: "private",
    baseUrl: "https://api.nzbgeek.info",
    priority: 10,
    categories: "",
    tags: "",
    isEnabled: true,
    mediaScope: "both",
    healthStatus: "healthy",
    lastHealthMessage: null,
    lastHealthLatencyMs: null,
    lastHealthTestUtc: null,
    consecutiveFailures: 0,
    rateLimitedUntilUtc: null,
    disabledReason: null,
    createdUtc: "2026-01-01T00:00:00Z",
    updatedUtc: "2026-01-01T00:00:00Z",
    ...overrides
  } as IndexerItem;
}

describe("indexer column sort values (#737, #1119)", () => {
  const a = indexer({ id: "a", name: "Zeta", protocol: "torznab", lastHealthTestUtc: "2026-01-01T00:00:00Z" });
  const b = indexer({ id: "b", name: "Alpha", protocol: "newznab", lastHealthTestUtc: "2026-01-03T00:00:00Z" });
  const c = indexer({ id: "c", name: "Mid", protocol: "rss", lastHealthTestUtc: null });
  const sortBy = (key: IndexerSortKey, direction: "asc" | "desc") =>
    sortListItems([a, b, c], { label: key, sort: (item: IndexerItem) => indexerSortValue(item, key) }, direction).map((item) => item.id);

  it("sorts by name ascending and descending", () => {
    expect(sortBy("name", "asc")).toEqual(["b", "c", "a"]);
    expect(sortBy("name", "desc")).toEqual(["a", "c", "b"]);
  });

  it("sorts a never-tested indexer to the front ascending, back descending", () => {
    expect(sortBy("lastTest", "asc")).toEqual(["c", "a", "b"]);
    expect(sortBy("lastTest", "desc")).toEqual(["b", "a", "c"]);
  });

  it("sorts disabled indexers apart from enabled ones", () => {
    const off = indexer({ id: "off", name: "Aaa", isEnabled: false, healthStatus: "healthy" });
    const on = indexer({ id: "on", name: "Zzz", isEnabled: true, healthStatus: "healthy" });
    const ordered = sortListItems([off, on], { label: "Status", sort: (item: IndexerItem) => indexerSortValue(item, "status") }, "asc");
    expect(ordered.map((item) => item.id)).toEqual(["on", "off"]);
  });
});

describe("filterIndexers (#737)", () => {
  const items = [
    indexer({ id: "1", protocol: "torznab", privacy: "private", isEnabled: true, mediaScope: "movies" }),
    indexer({ id: "2", protocol: "newznab", privacy: "public", isEnabled: false, mediaScope: "tv" }),
    indexer({ id: "3", protocol: "torznab", privacy: "public", isEnabled: true, mediaScope: "both" })
  ];

  it("narrows by protocol", () => {
    expect(filterIndexers(items, { ...emptyIndexerFilters(), protocol: "newznab" }).map((item) => item.id)).toEqual(["2"]);
  });
  it("narrows by privacy", () => {
    expect(filterIndexers(items, { ...emptyIndexerFilters(), privacy: "public" }).map((item) => item.id)).toEqual(["2", "3"]);
  });
  it("narrows by enabled/disabled status", () => {
    expect(filterIndexers(items, { ...emptyIndexerFilters(), status: "disabled" }).map((item) => item.id)).toEqual(["2"]);
  });
  it("narrows by what it searches for, counting an indexer used for both under Movies and under TV", () => {
    expect(filterIndexers(items, { ...emptyIndexerFilters(), scope: "movies" }).map((item) => item.id)).toEqual(["1", "3"]);
    expect(filterIndexers(items, { ...emptyIndexerFilters(), scope: "tv" }).map((item) => item.id)).toEqual(["2", "3"]);
  });
  it("combines filters", () => {
    expect(filterIndexers(items, { protocol: "torznab", privacy: "public", status: "all", scope: "" }).map((item) => item.id)).toEqual(["3"]);
  });
  it("reports whether any filter narrows the list", () => {
    expect(indexerFiltersActive(emptyIndexerFilters())).toBe(false);
    expect(indexerFiltersActive({ ...emptyIndexerFilters(), status: "enabled" })).toBe(true);
  });
});

describe("IndexerFilterChips", () => {
  it("offers All, On and Off with their counts, and Movies or TV only when they differ", () => {
    const items = [indexer({ id: "1", isEnabled: true, mediaScope: "movies" }), indexer({ id: "2", isEnabled: false, mediaScope: "both" })];
    const onChange = vi.fn();
    render(<IndexerFilterChips indexers={items} filters={emptyIndexerFilters()} onChange={onChange} />);
    expect(screen.getByRole("radio", { name: "All 2" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("radio", { name: "On 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Off 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Movies 2" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "TV 1" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("radio", { name: "Off 1" }));
    expect(onChange).toHaveBeenLastCalledWith({ ...emptyIndexerFilters(), status: "disabled" });
  });
});

describe("IndexerFilterBar (#737)", () => {
  it("only offers a protocol or privacy filter when there is more than one to choose between", () => {
    const items = [indexer({ id: "1", protocol: "torznab", privacy: "private" }), indexer({ id: "2", protocol: "newznab", privacy: "private" })];
    render(<IndexerFilterBar indexers={items} filters={emptyIndexerFilters()} onChange={() => undefined} />);
    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    expect(screen.getByRole("radiogroup", { name: "Filter by protocol" })).toBeInTheDocument();
    // Every indexer here is private, so a privacy filter has nothing to narrow.
    expect(screen.queryByRole("radiogroup", { name: "Filter by privacy" })).not.toBeInTheDocument();
  });

  it("shows Clear filters only once a filter narrows the list", () => {
    const { rerender } = render(<IndexerFilterBar indexers={[indexer()]} filters={emptyIndexerFilters()} onChange={() => undefined} />);
    expect(screen.queryByRole("button", { name: "Clear filters" })).not.toBeInTheDocument();
    rerender(<IndexerFilterBar indexers={[indexer()]} filters={{ ...emptyIndexerFilters(), status: "enabled" }} onChange={() => undefined} />);
    expect(screen.getByRole("button", { name: "Clear filters" })).toBeVisible();
  });
});

describe("IndexerSelectAllCheckbox (#737)", () => {
  it("selects every visible indexer when checked, none when unchecked", () => {
    const items = [indexer({ id: "1" }), indexer({ id: "2" })];
    const onChange = vi.fn();
    render(<IndexerSelectAllCheckbox items={items} selected={new Set()} onChange={onChange} />);
    fireEvent.click(screen.getByRole("checkbox"));
    expect(onChange).toHaveBeenCalledWith(["1", "2"]);
  });

  it("shows indeterminate when only some indexers are selected", () => {
    const items = [indexer({ id: "1" }), indexer({ id: "2" })];
    render(<IndexerSelectAllCheckbox items={items} selected={new Set(["1"])} onChange={() => undefined} />);
    expect((screen.getByRole("checkbox") as HTMLInputElement).indeterminate).toBe(true);
  });
});

describe("IndexerBulkBar (#737)", () => {
  it("renders nothing when no indexer is selected", () => {
    const { container } = render(
      <IndexerBulkBar count={0} busy={false} onEnable={() => undefined} onPause={() => undefined} onRemove={() => undefined} onChangeScope={() => undefined} onClear={() => undefined} />
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("wires each bulk action to its handler", () => {
    const onEnable = vi.fn();
    const onPause = vi.fn();
    const onRemove = vi.fn();
    render(
      <IndexerBulkBar count={2} busy={false} onEnable={onEnable} onPause={onPause} onRemove={onRemove} onChangeScope={() => undefined} onClear={() => undefined} />
    );
    expect(screen.getByText("2 selected")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Enable" }));
    fireEvent.click(screen.getByRole("button", { name: "Pause" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    expect(onEnable).toHaveBeenCalledOnce();
    expect(onPause).toHaveBeenCalledOnce();
    expect(onRemove).toHaveBeenCalledOnce();
  });

  it("applies a chosen used-for scope to the selection", () => {
    const onChangeScope = vi.fn();
    render(
      <IndexerBulkBar count={1} busy={false} onEnable={() => undefined} onPause={() => undefined} onRemove={() => undefined} onChangeScope={onChangeScope} onClear={() => undefined} />
    );
    fireEvent.change(screen.getByLabelText("Change used-for scope for selected indexers"), { target: { value: "tv" } });
    expect(onChangeScope).toHaveBeenCalledWith("tv");
  });
});
