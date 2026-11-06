import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { FilterChips } from "./filter-chips";

const OPTIONS = [{ value: "all", label: "All" }, { value: "movies", label: "Movies" }, { value: "tv", label: "TV" }] as const;

describe("FilterChips is the boxed switch (#1530)", () => {
  it("draws a 12px-round box, 3px inside, of 30px 13px options 10px round, the chosen one tinted", () => {
    render(<FilterChips label="Libraries" value="movies" onValueChange={() => undefined} options={OPTIONS} />);
    const group = screen.getByRole("radiogroup", { name: "Libraries" });
    expect(group.className).toContain("rounded-xl");
    expect(group.className).toContain("p-[3px]");
    expect(group.className).toContain("border-hairline");
    const chosen = screen.getByRole("radio", { name: "Movies" });
    const other = screen.getByRole("radio", { name: "All" });
    for (const chip of [chosen, other]) {
      expect(chip.className).toContain("h-[30px]");
      expect(chip.className).toContain("rounded-[10px]");
      expect(chip.className).toContain("px-[14px]");
      expect(chip.className).toContain("font-medium");
    }
    expect(chosen.className).toContain("bg-primary/10");
    expect(chosen.className).toContain("border-primary/40");
    expect(other.className).not.toContain("bg-primary");
  });

  it("keeps the radio semantics and the keyboard behaviour", () => {
    const choose = vi.fn();
    render(<FilterChips label="Libraries" value="all" onValueChange={choose} options={OPTIONS} />);
    expect(screen.getByRole("radio", { name: "All" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("radio", { name: "TV" })).toHaveAttribute("aria-checked", "false");
    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    expect(choose).toHaveBeenCalledWith("tv");
  });
});
