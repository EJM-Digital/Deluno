import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SourceMark } from "./source-mark";

describe("the rating source marks", () => {
  it.each([["tmdb", "TMDB"], ["imdb", "IMDb"], ["rotten_tomatoes", "RT"], ["metacritic", "MC"]])("%s is written as its own wordmark and keeps its full name as the title", (source, text) => {
    render(<SourceMark source={source} label={`Full ${source}`} className="extra" />);
    const mark = screen.getByText(text);
    expect(mark.getAttribute("title")).toBe(`Full ${source}`);
    expect(mark.className).toContain("extra");
  });

  it("falls back to the plain label for a source it has no mark for", () => {
    render(<SourceMark source="letterboxd" label="Letterboxd" />);
    const label = screen.getByText("Letterboxd");
    expect(label.hasAttribute("title")).toBe(false);
  });

  it("keeps a caller's class on the fallback label", () => {
    render(<SourceMark source="letterboxd" label="Letterboxd" className="mine" />);
    expect(screen.getByText("Letterboxd").className).toContain("mine");
  });
});
