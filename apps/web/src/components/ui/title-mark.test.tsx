import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MarkStrip, TitleMarkBarLegend, TitleMarkTopBar } from "./title-mark";
import { TITLE_BAR_SEGMENTS, TITLE_MARK_LADDER, TITLE_MARK_PRESENTATION } from "../../lib/status-tones";
import { quickFiltersFor } from "../app/library-control-rail";
import railSource from "../app/library-control-rail.tsx?raw";

/**
 * The legend row, held to the shelf it explains.
 *
 * A poster has not carried a dot since the state became a bar across its top,
 * and the chips above the shelf went on drawing 13px dots for months after —
 * a legend teaching a shape that was not down there any more. Nothing failed,
 * because nothing was reading the swatch back. These are the assertions that do.
 */
function swatches(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>("span[aria-hidden='true']"))
    .filter((element) => element.className.includes("rounded-full"));
}

describe("the swatch a legend wears", () => {
  it("is a strip, at the height of the one bar on a poster that never moves", () => {
    // James: the state bar is thin until the Quality switch makes it a labelled
    // bar, so there is no one height for the legend to match — "just use the
    // same as subs". The subtitle bar is `h-1` on the poster's bottom edge.
    const { container } = render(<MarkStrip mark="missing" />);
    const strip = swatches(container)[0];

    expect(strip.className).toContain("h-1");
    expect(strip.className).toContain("w-4");
    // A square would be a dot again under a different name.
    expect(strip.className).not.toMatch(/\bh-(?:2|3|4)\b/);
  });

  it("is the same height as the bar it explains, read off the bar itself", () => {
    // Measured through the component that draws the poster's bottom edge, not
    // off a constant either side could drift from.
    const { container } = render(
      <TitleMarkTopBar item={{ monitored: true, hasFile: true }} label={null} />
    );
    const bar = container.querySelector<HTMLElement>("div[role='img']");

    // The state bar is thin without a label and grows with one. That is exactly
    // why the legend does not follow it.
    expect(bar?.className).toContain("h-[5px]");

    const { container: withLabel } = render(
      <TitleMarkTopBar item={{ monitored: true, hasFile: true }} label="1080p" />
    );
    expect(withLabel.querySelector<HTMLElement>("div[role='img']")?.className).not.toContain("h-[5px]");
  });

  it("wears the mark's own colour and nothing hand-written", () => {
    for (const mark of ["missing", "upgrade", "covered"] as const) {
      const { container } = render(<MarkStrip mark={mark} />);
      expect(swatches(container)[0].className).toContain(TITLE_MARK_PRESENTATION[mark].dot);
    }
  });

  it("glints only where the thing it explains glints", () => {
    // The state bar draws gold leaf; the subtitle bar is a flat gradient. A
    // swatch that glinted for both would be showing a treatment the subtitle
    // bar never wears.
    const sheen = TITLE_MARK_PRESENTATION.covered.sheen ?? "";
    // Gold leaf, and the assertions below say nothing if it is ever dropped.
    expect(sheen.length).toBeGreaterThan(0);

    const { container: withSheen } = render(<MarkStrip mark="covered" sheen />);
    expect(swatches(withSheen)[0].className).toContain(sheen);

    const { container: flat } = render(<MarkStrip mark="covered" />);
    expect(swatches(flat)[0].className).not.toContain(sheen);
  });
});

describe("the subtitle bar's legend", () => {
  it("draws one strip per segment and no dot", () => {
    const { container } = render(<TitleMarkBarLegend />);
    const strips = swatches(container);

    expect(strips).toHaveLength(TITLE_BAR_SEGMENTS.length);
    for (const strip of strips) {
      expect(strip.className).toContain("h-1");
      expect(strip.className).toContain("w-4");
    }
  });

  it("names nothing a poster cannot draw", () => {
    // An Episodes entry sat behind a prop nothing ever passed, for a strip
    // posters stopped carrying when episode counts moved to a show's own page.
    const { container } = render(<TitleMarkBarLegend />);

    expect(container.textContent).not.toContain("Episodes");
    expect(container.textContent).toContain("Subtitles");
  });
});

describe("the chip row and the bar legend", () => {
  it("wear one swatch between them, so the row reads as one legend", () => {
    // Both halves of the row explain a bar now. Two shapes on one row would say
    // they explain two different kinds of thing — which is exactly what the row
    // did while the chips drew dots and the segments drew strips.
    //
    // Read off the rail's own source, the way `WorkPlanner`'s interval tests
    // are: rendering the rail proves the swatch it happens to draw today, while
    // this fails the moment somebody hand-rolls a second one beside it.
    expect(railSource).toContain("<MarkStrip mark={chip.mark} sheen />");
    // The dot it used to draw, and the constant that sized it.
    expect(railSource).not.toContain("MARK_DOT_SIZE");
    expect(railSource).not.toMatch(/rounded-full[^"]*TITLE_MARK_PRESENTATION/);
  });

  it("offers a chip for every mark the shelf can draw", () => {
    // A colour on a poster that no swatch on the row names is the mirror of a
    // chip that can never match, and both were live defects here.
    const chips = quickFiltersFor("shows").map((entry) => entry.mark).filter(Boolean);

    for (const mark of TITLE_MARK_LADDER) {
      expect(chips).toContain(mark);
    }
  });
});
