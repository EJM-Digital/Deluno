import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { IndexerItem } from "../../lib/api";
import {
  emptyIndexerFilters,
  filterIndexers,
  IndexerBulkBar,
  IndexerFilterBar,
  IndexerSelectAllCheckbox,
  indexerFiltersActive,
  nextIndexerSort,
  sortIndexers
} from "./indexer-list-controls";

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

describe("sortIndexers (#737)", () => {
  const a = indexer({ id: "a", name: "Zeta", protocol: "torznab", lastHealthTestUtc: "2026-01-01T00:00:00Z" });
  const b = indexer({ id: "b", name: "Alpha", protocol: "newznab", lastHealthTestUtc: "2026-01-03T00:00:00Z" });
  const c = indexer({ id: "c", name: "Mid", protocol: "rss", lastHealthTestUtc: null });

  it("sorts by name ascending and descending", () => {
    expect(sortIndexers([a, b, c], { key: "name", direction: "asc" }).map((item) => item.id)).toEqual(["b", "c", "a"]);
    expect(sortIndexers([a, b, c], { key: "name", direction: "desc" }).map((item) => item.id)).toEqual(["a", "c", "b"]);
  });

  it("sorts a never-tested indexer to the front ascending, back descending", () => {
    expect(sortIndexers([a, b, c], { key: "lastTest", direction: "asc" }).map((item) => item.id)).toEqual(["c", "a", "b"]);
    expect(sortIndexers([a, b, c], { key: "lastTest", direction: "desc" }).map((item) => item.id)).toEqual(["b", "a", "c"]);
  });

  it("returns items unchanged when no sort is set", () => {
    expect(sortIndexers([a, b, c], null)).toEqual([a, b, c]);
  });

  it("sorts disabled indexers apart from enabled ones", () => {
    const off = indexer({ id: "off", name: "Aaa", isEnabled: false, healthStatus: "healthy" });
    const on = indexer({ id: "on", name: "Zzz", isEnabled: true, healthStatus: "healthy" });
    expect(sortIndexers([off, on], { key: "status", direction: "asc" }).map((item) => item.id)).toEqual(["on", "off"]);
  });
});

describe("nextIndexerSort (#737)", () => {
  it("starts a new column ascending", () => {
    expect(nextIndexerSort(null, "name")).toEqual({ key: "name", direction: "asc" });
  });
  it("flips direction on the same column", () => {
    expect(nextIndexerSort({ key: "name", direction: "asc" }, "name")).toEqual({ key: "name", direction: "desc" });
  });
  it("resets to ascending when a different column is clicked", () => {
    expect(nextIndexerSort({ key: "name", direction: "desc" }, "protocol")).toEqual({ key: "protocol", direction: "asc" });
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
  it("narrows by media scope", () => {
    expect(filterIndexers(items, { ...emptyIndexerFilters(), scope: "movies" }).map((item) => item.id)).toEqual(["1"]);
  });
  it("combines filters", () => {
    expect(filterIndexers(items, { protocol: "torznab", privacy: "public", status: "all", scope: "" }).map((item) => item.id)).toEqual(["3"]);
  });
  it("reports whether any filter narrows the list", () => {
    expect(indexerFiltersActive(emptyIndexerFilters())).toBe(false);
    expect(indexerFiltersActive({ ...emptyIndexerFilters(), status: "enabled" })).toBe(true);
  });
});

describe("IndexerFilterBar (#737)", () => {
  it("only offers protocols and privacy values actually present", () => {
    const items = [indexer({ id: "1", protocol: "torznab", privacy: "private" }), indexer({ id: "2", protocol: "newznab", privacy: "private" })];
    render(<IndexerFilterBar indexers={items} filters={emptyIndexerFilters()} onChange={() => undefined} />);
    const protocolSelect = screen.getByLabelText("Filter by protocol") as HTMLSelectElement;
    const values = [...protocolSelect.options].map((option) => option.value).filter(Boolean);
    expect(values).toEqual(["newznab", "torznab"]);
    // Every indexer here is private, so a privacy filter has nothing to narrow.
    expect(screen.queryByLabelText("Filter by privacy")).not.toBeInTheDocument();
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
