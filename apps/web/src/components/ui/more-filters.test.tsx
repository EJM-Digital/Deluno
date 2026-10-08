import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { activeMoreFilters, MoreFilters, type MoreFilterGroup } from "./filter-chips";

function groups(overrides: Partial<MoreFilterGroup>[] = []): MoreFilterGroup[] {
  return [
    { label: "Protocol", ariaLabel: "Filter by protocol", value: "", onValueChange: () => undefined, options: [{ value: "", label: "Any protocol" }, { value: "torznab", label: "Torznab" }] },
    { label: "Privacy", ariaLabel: "Filter by privacy", value: "", onValueChange: () => undefined, options: [{ value: "", label: "Any privacy" }, { value: "private", label: "Private" }] }
  ].map((group, index) => ({ ...group, ...(overrides[index] ?? {}) }));
}

describe("MoreFilters (#1168)", () => {
  it("keeps the rarer filters behind one button and shows each as the same chips", () => {
    render(<MoreFilters groups={groups()} />);
    expect(screen.queryByRole("radiogroup")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    expect(screen.getByRole("radiogroup", { name: "Filter by protocol" })).toBeInTheDocument();
    expect(screen.getByRole("radiogroup", { name: "Filter by privacy" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Any protocol" })).toHaveAttribute("aria-checked", "true");
  });

  it("hands the choice back and says how many filters are narrowing the list", () => {
    const onValueChange = vi.fn();
    const { rerender } = render(<MoreFilters groups={groups([{ onValueChange }])} />);
    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    fireEvent.click(screen.getByRole("radio", { name: "Torznab" }));
    expect(onValueChange).toHaveBeenCalledWith("torznab");
    rerender(<MoreFilters groups={groups([{ value: "torznab" }, { value: "private" }])} />);
    expect(screen.getByRole("button", { name: "More filters, 2 on" })).toBeInTheDocument();
    expect(activeMoreFilters(groups([{ value: "torznab" }]))).toBe(1);
  });

  it("closes on Escape and gives focus back to its button", () => {
    render(<MoreFilters groups={groups()} />);
    const button = screen.getByRole("button", { name: "More filters" });
    fireEvent.click(button);
    expect(screen.getByRole("dialog", { name: "More filters" })).toBeInTheDocument();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(button).toHaveFocus();
  });
});
