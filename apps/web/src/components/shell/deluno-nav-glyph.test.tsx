import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { DelunoNavGlyph, type DelunoNavGlyphKind } from "./deluno-nav-glyph";

afterEach(cleanup);

const KINDS: DelunoNavGlyphKind[] = [
  "dashboard", "movies", "shows", "schedule", "transfers", "automation", "activity", "setup", "library", "connections",
  "plans", "quality", "size", "scoring", "destinations", "discover", "search", "recovery", "system"
];

describe("the navigation glyphs", () => {
  it.each(KINDS)("draws the %s glyph as its own shapes, hidden from assistive technology", (kind) => {
    const { container } = render(<DelunoNavGlyph kind={kind} />);
    const svg = container.querySelector("svg");
    expect(svg).not.toBeNull();
    expect(svg?.getAttribute("aria-hidden")).toBe("true");
    expect(svg?.children.length).toBeGreaterThan(0);
  });

  it("adds the caller's class to the default size", () => {
    const { container } = render(<DelunoNavGlyph kind="activity" className="text-red-500" />);
    const svg = container.querySelector("svg");
    expect(svg?.getAttribute("class")).toContain("h-5 w-5");
    expect(svg?.getAttribute("class")).toContain("text-red-500");
  });
});
