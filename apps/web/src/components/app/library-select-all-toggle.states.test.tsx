import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { LibrarySelectAllToggle } from "./library-select-all-toggle";

const base = { totalCount: 6000, loadedCount: 100, isLoadingMore: false, isQueryLoading: false, filteredCount: 100, selectedCount: 0, allVisibleSelected: false, onToggle: vi.fn(), view: "grid" as const };

describe("the shelf's select-all button", () => {
  it("is not shown while nothing is loaded", () => {
    const { container } = render(<LibrarySelectAllToggle {...base} loadedCount={0} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("is not shown in the list when every title is already loaded", () => {
    const { container } = render(<LibrarySelectAllToggle {...base} view="list" totalCount={100} />);
    expect(container).toBeEmptyDOMElement();
    const exact = render(<LibrarySelectAllToggle {...base} view="list" totalCount={99} />);
    expect(exact.container).toBeEmptyDOMElement();
  });

  it("is shown in the list while there is more to load, and in the grid even when everything is loaded", () => {
    const list = render(<LibrarySelectAllToggle {...base} view="list" totalCount={101} />);
    expect(list.container.querySelector("button")).not.toBeNull();
    list.unmount();
    const grid = render(<LibrarySelectAllToggle {...base} view="grid" totalCount={100} />);
    expect(grid.container.querySelector("button")).not.toBeNull();
    grid.unmount();
    const overview = render(<LibrarySelectAllToggle {...base} view="overview" totalCount={100} />);
    expect(overview.container.querySelector("button")).not.toBeNull();
  });

  it("says Select all shown with an empty box when nothing is selected, and selects on click", async () => {
    const onToggle = vi.fn();
    render(<LibrarySelectAllToggle {...base} onToggle={onToggle} />);
    const button = screen.getByRole("button", { name: "Select all shown" });
    expect(button.querySelector("svg")).toBeNull();
    expect(button.querySelector("span span")).toBeNull();
    await userEvent.click(button);
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it("counts what is selected, with a dash in the box while only some are", () => {
    render(<LibrarySelectAllToggle {...base} selectedCount={3} />);
    const button = screen.getByRole("button", { name: "3 selected" });
    expect(button.querySelector("svg")).toBeNull();
    expect(button.querySelector("span span.bg-primary")).not.toBeNull();
    expect(button).toHaveClass("text-primary");
  });

  it("ticks the box when everything shown is selected", () => {
    render(<LibrarySelectAllToggle {...base} selectedCount={100} allVisibleSelected />);
    const button = screen.getByRole("button", { name: "100 selected" });
    expect(button.querySelector("svg")).not.toBeNull();
    expect(button.querySelector("span")).toHaveClass("bg-primary", "text-primary-foreground");
  });

  it("uses the quiet look when nothing is selected", () => {
    render(<LibrarySelectAllToggle {...base} />);
    expect(screen.getByRole("button")).toHaveClass("text-muted-foreground");
    expect(screen.getByRole("button")).not.toHaveClass("text-primary");
    expect(screen.getByRole("button").querySelector("span")).toHaveClass("border-hairline");
  });

  it("shades the box when some are selected", () => {
    render(<LibrarySelectAllToggle {...base} selectedCount={2} />);
    expect(screen.getByRole("button").querySelector("span")).toHaveClass("border-primary/60");
  });

  it("says the count when the total is larger than what is loaded and a query is not running", () => {
    const { container } = render(<LibrarySelectAllToggle {...base} filteredCount={1234} isLoadingMore={false} />);
    expect(container.querySelector("p")?.textContent).toBe("Showing 1,234 of 6,000");
  });

  it("does not say it is still loading once everything has arrived", () => {
    const { container } = render(<LibrarySelectAllToggle {...base} totalCount={100} isLoadingMore />);
    expect(container.querySelector("p")?.textContent).toBe("100 titles shown");
  });

  it("prefers updating over still loading when a query is running", () => {
    const { container } = render(<LibrarySelectAllToggle {...base} isLoadingMore isQueryLoading />);
    expect(container.querySelector("p")?.textContent).toBe("100 titles shown — updating");
  });
});
