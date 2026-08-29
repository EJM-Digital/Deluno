import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { EpisodeProgressBar, MarkStrip, TitleMarkBar, TitleMarkBarLegend, TitleMarkTopBar } from "./title-mark";
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

describe("the subtitle bar", () => {
  it("paints its gold from the leaf, not from the text colour", () => {
    // The bar is a surface on artwork. `--mark-quality-met` is the Quality met
    // *count's* colour, dark by design in the light theme, and a dark yellow
    // painted onto a poster is brown — the same defect the state bar had.
    const { container } = render(
      <TitleMarkBar item={{
        monitored: true,
        hasFile: true,
        subtitleLanguagesWanted: 2,
        subtitleLanguagesHeld: 2,
        subtitleLanguagesSettled: 1
      }} />
    );

    const bar = container.querySelector<HTMLElement>("span[role='img']");
    const gradient = bar?.style.background ?? "";

    expect(gradient).toContain("--mark-leaf");
    expect(gradient).not.toContain("--mark-quality-met");
    // The other two rungs are unchanged: they have no surface value because
    // their one colour does both jobs.
    expect(gradient).toContain(TITLE_MARK_PRESENTATION.upgrade.cssVar);
    expect(gradient).toContain(TITLE_MARK_PRESENTATION.missing.cssVar);
  });

  it("has no bar to paint when nothing was asked for", () => {
    const { container } = render(<TitleMarkBar item={{ monitored: true, hasFile: true }} />);
    expect(container.querySelector("span[role='img']")).toBeNull();
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

  it("shines its gold, the same as everywhere else gold is drawn", () => {
    // One gold in one treatment wherever "Deluno has finished" is said. This
    // swatch was deliberately flat while the bar beside it was painted from the
    // semantic colour; both are the leaf now, so both shine.
    const { container } = render(<TitleMarkBarLegend />);
    const gold = swatches(container)
      .find((strip) => strip.className.includes(TITLE_MARK_PRESENTATION.covered.dot));

    expect(gold, "no gold swatch in the legend").toBeTruthy();
    expect(gold!.className).toContain(TITLE_MARK_PRESENTATION.covered.sheen);
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

describe("the episode count, the way Sonarr's list draws it", () => {
  const show = { monitored: true, hasFile: true, airedEpisodeCount: 16, airedWithFileCount: 12 };

  it("prints the count on a bar filled to what you hold", () => {
    const { container } = render(<EpisodeProgressBar item={show} />);
    const bar = container.querySelector<HTMLElement>("span[role='img']");

    expect(bar?.textContent).toBe("12 / 16");
    // 12 of 16 is 75%, and the fill has to be the fraction rather than the
    // count: a bar that reads 12/16 and is drawn full is worse than no bar.
    const fill = container.querySelector<HTMLElement>("span[aria-hidden]");
    expect(fill?.style.width).toBe("75%");
  });

  it("wears the title's own mark, so a row and its poster cannot disagree", () => {
    const { container } = render(<EpisodeProgressBar item={show} />);
    // Twelve of sixteen aired episodes held is Missing, and it is red here for
    // the same reason the poster is red.
    expect(container.innerHTML).toContain(TITLE_MARK_PRESENTATION.missing.dot);
  });

  it("draws nothing for a title with no episodes to count", () => {
    // A film, and a show whose counts have not arrived. Zero of zero is not a
    // fraction, and drawing one claims knowledge Deluno has not got.
    expect(render(<EpisodeProgressBar item={{ monitored: true, hasFile: true }} />)
      .container.innerHTML).toBe("");
    expect(render(<EpisodeProgressBar item={{ monitored: true, airedEpisodeCount: 0 }} />)
      .container.innerHTML).toBe("");
  });

  it("never claims more episodes than have aired", () => {
    const { container } = render(
      <EpisodeProgressBar item={{ monitored: true, airedEpisodeCount: 4, airedWithFileCount: 99 }} />
    );

    expect(container.querySelector("span[role='img']")?.textContent).toBe("4 / 4");
  });
});

describe("the state bar on a poster", () => {
  function fill(container: HTMLElement): string | undefined {
    return container.querySelector<HTMLElement>("span[aria-hidden] > span")?.style.width;
  }

  it("is filled to how far through the show you are", () => {
    // Three of twenty aired episodes drew a full red bar, identical to a show
    // holding none. `titleProgress` had computed this fraction since DESIGN-001
    // and nothing drew it.
    const { container } = render(
      <TitleMarkTopBar
        item={{ monitored: true, airedEpisodeCount: 20, airedWithFileCount: 3 }}
        label={null}
      />
    );

    expect(fill(container)).toBe("15%");
  });

  it("is solid for a film, which is not partway through itself", () => {
    // Filling by hasFile would leave every missing film with an empty strip and
    // no state on the poster at all.
    expect(fill(render(<TitleMarkTopBar item={{ monitored: true, hasFile: false }} label={null} />).container))
      .toBe("100%");
    expect(fill(render(<TitleMarkTopBar item={{ monitored: true, hasFile: true }} label={null} />).container))
      .toBe("100%");
  });

  it("is solid for a show whose episode counts have not arrived", () => {
    expect(fill(render(<TitleMarkTopBar item={{ monitored: true, airedEpisodeCount: 0 }} label={null} />).container))
      .toBe("100%");
  });

  it("says the fraction out loud, since the bar now carries it", () => {
    const { container } = render(
      <TitleMarkTopBar
        item={{ monitored: true, airedEpisodeCount: 20, airedWithFileCount: 3 }}
        label="WEB 1080p"
      />
    );

    expect(container.querySelector("div[role='img']")?.getAttribute("aria-label"))
      .toBe("WEB 1080p · Missing · 3 of 20 aired episodes on disk");
  });

  it("does not say the state twice when there is no quality to name", () => {
    // The label falls back to the state's own word, so a missing title read
    // "Missing · Missing" to a screen reader.
    const { container } = render(
      <TitleMarkTopBar item={{ monitored: true, hasFile: false }} label="Missing" />
    );

    expect(container.querySelector("div[role='img']")?.getAttribute("aria-label")).toBe("Missing");
  });

  it("keeps the state visible on a show holding nothing", () => {
    // The first version tracked in grey, so a show at 0% had no colour on it at
    // all — one commit after the state mark was made mandatory.
    const { container } = render(
      <TitleMarkTopBar
        item={{ monitored: true, airedEpisodeCount: 29, airedWithFileCount: 0 }}
        label={null}
      />
    );

    const track = container.querySelector<HTMLElement>("span[aria-hidden]");
    expect(track?.style.background).toContain(TITLE_MARK_PRESENTATION.missing.cssVar);
    expect(track?.className).not.toContain("mark-idle");
  });
});
