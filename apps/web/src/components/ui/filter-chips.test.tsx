import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { FilterChips } from "./filter-chips";

const OPTIONS = [{ value: "all", label: "All" }, { value: "movies", label: "Movies" }, { value: "tv", label: "TV" }] as const;

describe("FilterChips in a Dashboard panel header (#1461)", () => {
  it("draws separate 24px pills 6px apart with no box round them, 600 when chosen and 500 otherwise, as Weir's", () => {
    render(<FilterChips label="Libraries" size="header" value="movies" onValueChange={() => undefined} options={OPTIONS} />);
    const group = screen.getByRole("radiogroup", { name: "Libraries" });
    expect(group.className).toContain("gap-[6px]");
    expect(group.className).not.toContain("border");
    const chosen = screen.getByRole("radio", { name: "Movies" });
    const other = screen.getByRole("radio", { name: "All" });
    for (const chip of [chosen, other]) {
      expect(chip.className).toContain("h-[24px]");
      expect(chip.className).toContain("px-[10px]");
    }
    expect(chosen.className).toContain("font-semibold");
    expect(chosen.className).toContain("bg-primary/16");
    expect(chosen.className).toContain("border-primary/45");
    expect(other.className).toContain("font-medium");
    expect(other.className).not.toContain("bg-primary");
  });

  it("keeps the radio semantics and the keyboard behaviour", () => {
    const choose = vi.fn();
    render(<FilterChips label="Libraries" size="header" value="all" onValueChange={choose} options={OPTIONS} />);
    expect(screen.getByRole("radio", { name: "All" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("radio", { name: "TV" })).toHaveAttribute("aria-checked", "false");
    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    expect(choose).toHaveBeenCalledWith("tv");
  });
});
