import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { GlassTile, PageHero, StatChip } from "./page-hero";

describe("the page hero", () => {
  it("shows just the title when nothing else is given", () => {
    const { container } = render(<PageHero title="Movies" />);
    expect(screen.getByText("Movies")).toBeTruthy();
    expect(container.querySelector("h1")).toBeNull();
    expect(container.querySelector(".hero-layout-with-stats")).toBeNull();
    expect(container.querySelector("[style*='repeat']")).toBeNull();
  });

  it("shows the eyebrow with the default sparkle or the caller's icon, the subtitle and the actions", () => {
    const { container, rerender } = render(<PageHero eyebrow="Library" title="Movies" subtitle="All your films" actions={<button>Add</button>} />);
    expect(screen.getByText("Library")).toBeTruthy();
    expect(container.querySelector("svg")).not.toBeNull();
    expect(screen.getByText("All your films")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add" })).toBeTruthy();

    rerender(<PageHero eyebrow="Library" eyebrowIcon={<i data-testid="mine" />} title="Movies" size="sm" subtitle="Small" />);
    expect(screen.getByTestId("mine")).toBeTruthy();
    expect(container.querySelector("svg")).toBeNull();
  });

  it("lays out at most eight posters as a mosaic", () => {
    const { container } = render(<PageHero title="Movies" posters={Array.from({ length: 10 }, (_, i) => `/p${i}.jpg`)} />);
    const grid = container.querySelector("[style*='repeat']") as HTMLElement;
    expect(grid.style.gridTemplateColumns).toBe("repeat(8, 1fr)");
    expect(grid.children).toHaveLength(8);
  });

  it("draws no mosaic for an empty poster list, and a short list gets as many columns as posters", () => {
    const { container, rerender } = render(<PageHero title="Movies" posters={[]} />);
    expect(container.querySelector("[style*='repeat']")).toBeNull();
    rerender(<PageHero title="Movies" posters={["/a.jpg", "/b.jpg"]} />);
    expect((container.querySelector("[style*='repeat']") as HTMLElement).style.gridTemplateColumns).toBe("repeat(2, 1fr)");
  });

  it("shows each stat with its label and value in the colour of its tone, neutral by default", () => {
    const { container } = render(
      <PageHero
        title="Movies"
        stats={[
          { label: "Total", value: "120" },
          { label: "Good", value: "5", tone: "success" },
          { label: "Soon", value: "6", tone: "warn" },
          { label: "Bad", value: "7", tone: "danger" },
          { label: "Main", value: "8", tone: "primary" }
        ]}
      />
    );
    expect(container.querySelector(".hero-layout-with-stats")).not.toBeNull();
    expect(screen.getByText("120").className).toContain("text-foreground");
    expect(screen.getByText("5").className).toContain("text-success");
    expect(screen.getByText("6").className).toContain("text-warning");
    expect(screen.getByText("7").className).toContain("text-destructive");
    expect(screen.getByText("8").className).toContain("text-primary");
    expect(screen.getByText("Total")).toBeTruthy();
  });

  it("shows no stats panel for an empty list", () => {
    const { container } = render(<PageHero title="Movies" stats={[]} />);
    expect(container.querySelector(".hero-stat-grid")).toBeNull();
  });
});

describe("glass tiles and stat chips", () => {
  it("passes its attributes and class to the tile", () => {
    render(<GlassTile data-testid="tile" className="extra">Inside</GlassTile>);
    expect(screen.getByTestId("tile").className).toContain("extra");
    expect(screen.getByText("Inside")).toBeTruthy();
  });

  it.each([["primary", "text-primary"], ["success", "text-success"], ["warn", "text-warning"], ["danger", "text-destructive"], ["neutral", "text-foreground"]] as const)("a %s chip uses its colour", (tone, cls) => {
    const { container } = render(<StatChip label="Files" value={3} tone={tone} icon={<i data-testid="icon" />} />);
    expect(container.firstElementChild!.className).toContain(cls);
    expect(screen.getByText("Files")).toBeTruthy();
    expect(screen.getByText("3")).toBeTruthy();
    expect(screen.getByTestId("icon")).toBeTruthy();
  });

  it("is neutral by default and works without an icon", () => {
    const { container } = render(<StatChip label="Files" value="x" />);
    expect(container.firstElementChild!.className).toContain("text-foreground");
  });
});
