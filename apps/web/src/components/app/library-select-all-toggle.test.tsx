import { render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { LibrarySelectAllToggle } from "./library-select-all-toggle";

/**
 * The count line above a shelf (coverage register, area library): "N titles shown", "Showing N of M", "still loading" while the
 * rest of the fill runs, and "— updating" while a query is in flight.
 */
const base = { totalCount: 6000, loadedCount: 100, isLoadingMore: false, isQueryLoading: false, filteredCount: 100, selectedCount: 0, allVisibleSelected: false, onToggle: vi.fn(), view: "grid" as const };

describe("the shelf count line", () => {
  it("says N titles shown when everything is on the shelf, Showing N of M while the total is larger, still loading while the fill runs, and updating while a query is in flight", () => {
    const { container, rerender } = render(<LibrarySelectAllToggle {...base} totalCount={100} filteredCount={1} />);
    const line = () => container.querySelector("p")?.textContent;
    expect(line()).toBe("1 title shown");

    rerender(<LibrarySelectAllToggle {...base} />);
    expect(line()).toBe("Showing 100 of 6,000");

    rerender(<LibrarySelectAllToggle {...base} isLoadingMore />);
    expect(line()).toBe("Showing 100 of 6,000 — still loading");

    rerender(<LibrarySelectAllToggle {...base} totalCount={100} filteredCount={250} isQueryLoading />);
    expect(line()).toBe("250 titles shown — updating");
  });
});
