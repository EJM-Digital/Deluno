import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { SummaryStrip, type SummaryCell } from "./summary-strip";

afterEach(cleanup);

const cell = (n: number, over: Partial<SummaryCell> = {}): SummaryCell => ({ label: `Label ${n}`, value: `Value ${n}`, ...over });
const show = (cells: SummaryCell[], className?: string) => render(<MemoryRouter><SummaryStrip cells={cells} className={className} /></MemoryRouter>);
const strip = (container: HTMLElement) => container.firstElementChild as HTMLElement;

describe("the summary strip", () => {
  it("shows each cell's label and value", () => {
    show([cell(1), cell(2)]);
    expect(screen.getByText("Label 1")).toBeInTheDocument();
    expect(screen.getByText("Value 1")).toBeInTheDocument();
    expect(screen.getByText("Label 2")).toBeInTheDocument();
    expect(screen.getByText("Value 2")).toBeInTheDocument();
  });

  it("is two columns on a narrow screen, and one per cell on a wide one up to six", () => {
    const columns = (count: number) => {
      const { container } = show(Array.from({ length: count }, (_, index) => cell(index)));
      const classes = strip(container).className;
      cleanup();
      return classes.match(/md:grid-cols-\d/)![0];
    };
    expect(columns(1)).toBe("md:grid-cols-2");
    expect(columns(2)).toBe("md:grid-cols-2");
    expect(columns(3)).toBe("md:grid-cols-3");
    expect(columns(4)).toBe("md:grid-cols-4");
    expect(columns(5)).toBe("md:grid-cols-5");
    expect(columns(6)).toBe("md:grid-cols-6");
    expect(columns(7)).toBe("md:grid-cols-6");
    const { container } = show([cell(1)]);
    expect(strip(container).className).toContain("grid-cols-2");
  });

  it("takes an extra class", () => {
    const { container } = show([cell(1)], "extra");
    expect(strip(container).className).toContain("extra");
  });

  it("says what the number means under it, with the same words in its tooltip", () => {
    show([cell(1, { help: "Free of 200 GB" }), cell(2)]);
    const help = screen.getByText("Free of 200 GB");
    expect(help).toHaveAttribute("title", "Free of 200 GB");
    expect(help.className).toContain("truncate");
    expect(document.querySelectorAll("[title]")).toHaveLength(1);
  });

  it("draws a help of zero, which is still something to say", () => {
    show([cell(1, { help: 0 })]);
    expect(screen.getByText("0")).toBeInTheDocument();
  });

  it("colours only the value by its tone, and plain when it has none", () => {
    show([cell(1), cell(2, { tone: "warning" }), cell(3, { tone: "danger" }), cell(4, { tone: "success" })]);
    expect(screen.getByText("Value 1").className).toContain("text-foreground");
    expect(screen.getByText("Value 2").className).toContain("text-warning");
    expect(screen.getByText("Value 3").className).toContain("text-destructive");
    expect(screen.getByText("Value 4").className).toContain("text-success");
    expect(screen.getByText("Value 2").className).not.toContain("text-foreground");
    expect(screen.getByText("Label 2").className).toContain("text-muted-foreground");
  });

  it("draws a bar for a proportion, in the tone's colour, as wide as the proportion", () => {
    const { container } = show([cell(1, { fill: 0.25 }), cell(2, { fill: 0.5, tone: "warning" }), cell(3, { fill: 0.5, tone: "danger" }), cell(4, { fill: 0.5, tone: "success" }), cell(5)]);
    const bars = Array.from(container.querySelectorAll<HTMLElement>("[aria-hidden] > span"));
    expect(bars.map((bar) => bar.style.width)).toEqual(["25%", "50%", "50%", "50%"]);
    expect(bars[0].className).toContain("bg-primary");
    expect(bars[1].className).toContain("bg-warning");
    expect(bars[2].className).toContain("bg-destructive");
    expect(bars[3].className).toContain("bg-success");
    expect(container.querySelectorAll("[aria-hidden]")).toHaveLength(4);
  });

  it("keeps the bar between empty and full, and draws one for a proportion of nothing", () => {
    const { container } = show([cell(1, { fill: 0 }), cell(2, { fill: 7 }), cell(3, { fill: -2 })]);
    const bars = Array.from(container.querySelectorAll<HTMLElement>("[aria-hidden] > span"));
    expect(bars.map((bar) => bar.style.width)).toEqual(["0%", "100%", "0%"]);
  });

  it("leads a cell to the page that deals with it, and leaves the others as plain cells", () => {
    show([cell(1, { href: "/system/health" }), cell(2)]);
    const link = screen.getByRole("link");
    expect(link).toHaveAttribute("href", "/system/health");
    expect(link).toHaveTextContent("Label 1");
    expect(link.className).toContain("hover:bg-primary/6");
    expect(screen.getByText("Label 2").closest("a")).toBeNull();
    expect(screen.getAllByRole("link")).toHaveLength(1);
  });

  it("divides its cells with a line on the right, none after the last, and one under each on a narrow screen", () => {
    const { container } = show([cell(1), cell(2), cell(3)]);
    const cells = Array.from(strip(container).children);
    for (const each of cells) {
      expect(each.className).toContain("border-b border-r last:border-r-0 md:border-b-0");
      expect(each.className).toContain("border-hairline");
    }
  });

  it("closes the second-last cell of an even strip with a line on a wide screen too, and no other", () => {
    const even = show([cell(1), cell(2), cell(3), cell(4)]);
    expect(Array.from(strip(even.container).children).map((each) => each.className.includes("md:border-r"))).toEqual([false, false, true, false]);
    cleanup();
    const odd = show([cell(1), cell(2), cell(3)]);
    expect(Array.from(strip(odd.container).children).map((each) => each.className.includes("md:border-r"))).toEqual([false, false, false]);
  });
});
