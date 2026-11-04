import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { LibrarySelectionCommandBar } from "./library-selection-command-bar";

afterEach(cleanup);

const show = (over: Partial<Parameters<typeof LibrarySelectionCommandBar>[0]> = {}) => {
  const props = {
    count: 3, isUpdating: false, canUndo: true, canRedo: true,
    onUndo: vi.fn(), onRedo: vi.fn(), onOpenBulkTools: vi.fn(), onRemove: vi.fn(), onClear: vi.fn(), ...over
  };
  render(<LibrarySelectionCommandBar {...props} />);
  return props;
};
const button = (name: string) => screen.getByRole("button", { name });

describe("the bar over a selection", () => {
  it("draws nothing while nothing is selected", () => {
    const { container } = render(<LibrarySelectionCommandBar count={0} isUpdating={false} canUndo canRedo onUndo={vi.fn()} onRedo={vi.fn()} onOpenBulkTools={vi.fn()} onRemove={vi.fn()} onClear={vi.fn()} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("says how many are selected, in the singular and the plural", () => {
    show({ count: 1 });
    expect(screen.getByText("item selected")).toBeInTheDocument();
    cleanup();
    show({ count: 12 });
    expect(screen.getByText("items selected")).toBeInTheDocument();
    expect(screen.getByText("12", { selector: "span" })).toBeInTheDocument();
  });

  it("offers its seven actions in order, then Clear", () => {
    show();
    expect(screen.getAllByRole("button").map((each) => each.getAttribute("aria-label"))).toEqual(["Undo", "Redo", "Monitor", "Search now", "Unmonitor", "Remove", "Bulk edit", "Clear selection"]);
  });

  it("undoes, redoes, removes and clears", () => {
    const props = show();
    fireEvent.click(button("Undo"));
    fireEvent.click(button("Redo"));
    fireEvent.click(button("Remove"));
    fireEvent.click(button("Clear selection"));
    expect(props.onUndo).toHaveBeenCalledTimes(1);
    expect(props.onRedo).toHaveBeenCalledTimes(1);
    expect(props.onRemove).toHaveBeenCalledTimes(1);
    expect(props.onClear).toHaveBeenCalledTimes(1);
  });

  it("opens the bulk tools for each action with what it means", () => {
    const props = show();
    fireEvent.click(button("Monitor"));
    expect(props.onOpenBulkTools).toHaveBeenLastCalledWith("monitoring", true);
    fireEvent.click(button("Unmonitor"));
    expect(props.onOpenBulkTools).toHaveBeenLastCalledWith("monitoring", false);
    fireEvent.click(button("Search now"));
    expect(props.onOpenBulkTools).toHaveBeenLastCalledWith("search");
    fireEvent.click(button("Bulk edit"));
    expect(props.onOpenBulkTools).toHaveBeenLastCalledWith("quality");
    expect(props.onOpenBulkTools).toHaveBeenCalledTimes(4);
  });

  it("cannot undo or redo when there is nothing to", () => {
    show({ canUndo: false, canRedo: false });
    expect(button("Undo")).toBeDisabled();
    expect(button("Redo")).toBeDisabled();
    expect(button("Remove")).not.toBeDisabled();
    cleanup();
    show({ canUndo: true, canRedo: false });
    expect(button("Undo")).not.toBeDisabled();
    expect(button("Redo")).toBeDisabled();
    cleanup();
    show({ canUndo: false, canRedo: true });
    expect(button("Undo")).toBeDisabled();
    expect(button("Redo")).not.toBeDisabled();
  });

  it("holds every action while an update is running, and shows Monitor as working", () => {
    show({ isUpdating: true });
    for (const name of ["Undo", "Redo", "Monitor", "Search now", "Unmonitor", "Remove", "Bulk edit"]) expect(button(name)).toBeDisabled();
    expect(button("Clear selection")).not.toBeDisabled();
    expect(button("Monitor").querySelector("svg")).toHaveClass("animate-spin");
    expect(button("Search now").querySelector("svg")).not.toHaveClass("animate-spin");
    cleanup();
    show();
    expect(button("Monitor").querySelector("svg")).not.toHaveClass("animate-spin");
  });

  it("shows Monitor as the main action and Remove as the dangerous one", () => {
    show();
    expect(button("Monitor").className).toContain("text-primary-foreground");
    expect(button("Remove").className).toContain("text-destructive");
    expect(button("Undo").className).not.toContain("text-destructive");
    expect(button("Undo").className).not.toContain("text-primary-foreground");
  });

  it("tells that Escape clears the selection", () => {
    show();
    expect(button("Clear selection")).toHaveTextContent("ClearEsc");
  });
});
