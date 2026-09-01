import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { EpisodeProgressBar, MarkStrip, TitleMarkBar, TitleMarkBarLegend, TitleMarkCorner, TitleMarkDot, TitleMarkTopBar } from "./title-mark";
import { TITLE_BAR_SEGMENTS, TITLE_MARK_LADDER, TITLE_MARK_PAINT, TITLE_MARK_PRESENTATION, UNMONITORED_PAINT } from "../../lib/status-tones";
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

  it("is the height of the state bar, which no longer moves", () => {
    // The bar used to grow to carry the quality tier. It does not any more, so
    // the swatch and the thing it explains are finally the same size.
    const { container } = render(<TitleMarkTopBar item={{ monitored: true, hasFile: true }} />);

    expect(container.querySelector<HTMLElement>("div[role='img']")?.className).toContain("h-[5px]");
  });

  it("wears the mark's own colour and nothing hand-written", () => {
    for (const mark of ["missing", "upgrade", "covered"] as const) {
      const { container } = render(<MarkStrip mark={mark} />);
      expect(swatches(container)[0].className).toContain(TITLE_MARK_PRESENTATION[mark].dot);
    }
  });

  it("puts the rung glyph inside a compact dot", () => {
    const { container } = render(<TitleMarkDot item={{ monitored: true, wantedStatus: "missing" }} />);
    expect(container.querySelector(".title-mark-glyph")?.getAttribute("data-glyph")).toBe(TITLE_MARK_PRESENTATION.missing.glyph);
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
    expect(gradient).toContain(TITLE_MARK_PAINT.upgrade.surface);
    expect(gradient).toContain(TITLE_MARK_PAINT.missing.surface);
  });

  it("has no bar to paint when nothing was asked for", () => {
    const { container } = render(<TitleMarkBar item={{ monitored: true, hasFile: true }} />);
    expect(container.querySelector("span[role='img']")).toBeNull();
  });
});

describe("the subtitle bar's legend", () => {
  it("draws one strip per segment and no dot", () => {
    const { container } = render(<TitleMarkBarLegend type="show" />);
    const strips = swatches(container);

    expect(strips).toHaveLength(TITLE_BAR_SEGMENTS.length);
    for (const strip of strips) {
      expect(strip.className).toContain("h-1");
      expect(strip.className).toContain("w-4");
    }
    expect(container.textContent).not.toContain("Unmonitored");

    const heading = container.querySelector<HTMLElement>("[role='heading']");
    expect(heading?.textContent).toBe("Subtitles");
    expect(heading?.className).toContain("font-semibold");
    expect(heading?.className).toContain("text-foreground");
  });

  it("shines its gold, the same as everywhere else gold is drawn", () => {
    // One gold in one treatment wherever "Deluno has finished" is said. This
    // swatch was deliberately flat while the bar beside it was painted from the
    // semantic colour; both are the leaf now, so both shine.
    const { container } = render(<TitleMarkBarLegend type="show" />);
    const gold = swatches(container)
      .find((strip) => strip.style.backgroundColor.includes(TITLE_MARK_PAINT.covered.surface));

    expect(gold, "no gold swatch in the legend").toBeTruthy();
    expect(gold!.className).toContain(TITLE_MARK_PRESENTATION.covered.sheen);
  });

  it("names nothing a poster cannot draw", () => {
    // An Episodes entry sat behind a prop nothing ever passed, for a strip
    // posters stopped carrying when episode counts moved to a show's own page.
    const { container } = render(<TitleMarkBarLegend type="show" />);

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
    // The `type` is threaded so the swatch can paint from the bar SURFACES on a
    // shelf that has adopted DESIGN-006 — a legend has to be drawn in the
    // colours it is explaining, and it was not.
    expect(railSource).toMatch(/<MarkStrip mark=\{chip\.mark\} type=\{[^}]+\} sheen \/>/);
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

  it("uses the adopted TV bar surface and the same unmonitored override", () => {
    const watched = render(<EpisodeProgressBar item={show} type="show" />)
      .container.querySelector<HTMLElement>("span[aria-hidden] span")!;
    const ignored = render(<EpisodeProgressBar item={{ ...show, monitored: false }} type="show" />)
      .container.querySelector<HTMLElement>("span[aria-hidden] span")!;

    expect(watched.style.backgroundColor).toContain(TITLE_MARK_PAINT.upgrade.surface);
    expect(ignored.style.backgroundColor).toContain(UNMONITORED_PAINT.surface);

    const watchedTrack = watched.closest<HTMLElement>("[role='img']")!;
    const ignoredTrack = ignored.closest<HTMLElement>("[role='img']")!;
    expect(watchedTrack.style.backgroundColor).toContain(TITLE_MARK_PAINT.missing.surface);
    expect(ignoredTrack.style.backgroundColor).toContain(UNMONITORED_PAINT.surface);
  });

  it("uses the state surface as the track when TV coverage is empty", () => {
    const { container } = render(
      <EpisodeProgressBar item={{ ...show, airedWithFileCount: 0 }} type="show" />
    );
    const bar = container.querySelector<HTMLElement>("span[role='img']")!;

    expect(bar.style.backgroundColor).toContain(TITLE_MARK_PAINT.missing.surface);
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
    return container.querySelector<HTMLElement>("span[aria-hidden]")?.style.width;
  }

  it("is filled to how far through the show you are", () => {
    // Three of twenty aired episodes drew a full red bar, identical to a show
    // holding none. `titleProgress` had computed this fraction since DESIGN-001
    // and nothing drew it.
    const { container } = render(
      <TitleMarkTopBar item={{ monitored: true, airedEpisodeCount: 20, airedWithFileCount: 3 }} />
    );

    expect(fill(container)).toBe("15%");
  });

  it("is solid for a film, which is not partway through itself", () => {
    // Filling by hasFile would leave every missing film with an empty strip and
    // no state on the poster at all.
    expect(fill(render(<TitleMarkTopBar item={{ monitored: true, hasFile: false }} />).container))
      .toBe("100%");
    expect(fill(render(<TitleMarkTopBar item={{ monitored: true, hasFile: true }} />).container))
      .toBe("100%");
  });

  it("is solid for a show whose episode counts have not arrived", () => {
    expect(fill(render(<TitleMarkTopBar item={{ monitored: true, airedEpisodeCount: 0 }} />).container))
      .toBe("100%");
  });

  it("says the fraction out loud, since it is drawn and not written", () => {
    const { container } = render(
      <TitleMarkTopBar item={{ monitored: true, airedEpisodeCount: 20, airedWithFileCount: 3 }} />
    );

    expect(container.querySelector("div[role='img']")?.getAttribute("aria-label"))
      .toBe("Missing · 3 of 20 aired episodes on disk");
  });

  it("carries no words at all", () => {
    // Three rounds of wash-out were a word on a bar whose ground changes with
    // the episode count. There is no word now, so there is nothing to colour.
    const { container } = render(
      <TitleMarkTopBar item={{ monitored: true, airedEpisodeCount: 20, airedWithFileCount: 3 }} />
    );

    expect(container.querySelector("div[role='img']")?.textContent).toBe("");
  });

  it("uses full-strength colour and a solid remainder, never a dimmed mark", () => {
    // James: "ensure colours are full and not transparent or washed out". The
    // unfilled part is the bit you do not have yet, not a faded version of the
    // state — and the state is never lost with it, because the corner carries
    // it at full strength whatever the bar is filled to.
    const { container } = render(
      <TitleMarkTopBar item={{ monitored: true, airedEpisodeCount: 29, airedWithFileCount: 0 }} />
    );

    const bar = container.querySelector<HTMLElement>("div[role='img']");
    expect(bar?.className).toContain("bg-mark-idle");

    const fillLayer = container.querySelector<HTMLElement>("span[aria-hidden]");
    expect(fillLayer?.className).toContain(TITLE_MARK_PRESENTATION.missing.dot);
    // No opacity modifier on the mark's own colour anywhere.
    expect(bar?.outerHTML).not.toMatch(/--destructive\)\s*\/\s*0/);
  });
});

describe("the corner, where the dot used to be", () => {
  it("carries the count for a show and the state's word for a film", () => {
    const show = render(
      <TitleMarkCorner item={{ monitored: true, airedEpisodeCount: 20, airedWithFileCount: 3 }} />
    ).container.querySelector("span[role='img']");
    expect(show?.textContent?.trim()).toBe("3 / 20");

    // "1 / 1" would be a fraction invented to fill a shape, so a film gets a
    // word. Which word is `titleMark`'s business and is asserted elsewhere.
    const film = render(<TitleMarkCorner item={{ monitored: true, hasFile: true }} />)
      .container.querySelector("span[role='img']");
    const words = Object.values(TITLE_MARK_PRESENTATION).map((rung) => rung.label);

    expect(film?.textContent?.trim()).not.toMatch(/\d+\s*\/\s*\d+/);
    expect(words).toContain(film?.textContent?.trim());
  });

  it("is opaque, because a translucent pill is a different colour on every poster", () => {
    const { container } = render(
      <TitleMarkCorner item={{ monitored: true, airedEpisodeCount: 20, airedWithFileCount: 3 }} />
    );
    const pill = container.querySelector<HTMLElement>("span[role='img']");

    expect(pill?.className).toContain("bg-surface-1");
    expect(pill?.className).not.toMatch(/bg-black\/|backdrop-blur/);
  });

  it("wears the mark at full strength, so the state survives an empty bar", () => {
    const { container } = render(
      <TitleMarkCorner item={{ monitored: true, airedEpisodeCount: 29, airedWithFileCount: 0 }} />
    );
    const pip = container.querySelector<HTMLElement>("span[aria-hidden]");

    expect(pip?.className).toContain(TITLE_MARK_PRESENTATION.missing.dot);
    // No Tailwind opacity modifier on the mark's own colour — `bg-destructive/40`
    // and friends are exactly the wash James asked to be rid of.
    expect(pip?.className).not.toContain(`${TITLE_MARK_PRESENTATION.missing.dot}/`);
  });
});

describe("the legend's swatch keeps the gold leaf", () => {
  it("paints the shelf's surface WITHOUT cancelling the grail gradient", () => {
    // `background: <colour>` resets background-image, and background-image is
    // the whole of `.mark-grail`. Painting the legend from the card's surfaces
    // with the shorthand therefore turned Quality met flat while the card's own
    // bar stayed gold — a legend explaining a palette its shelf does not draw,
    // which is the one thing this swatch exists to prevent.
    const { container } = render(<MarkStrip mark="covered" type="movie" sheen />);
    const swatch = container.firstElementChild as HTMLElement;

    expect(swatch.className).toContain("mark-grail");
    expect(swatch.style.backgroundColor).not.toBe("");
    expect(swatch.style.background).not.toMatch(/^hsl/);
  });

  it("uses the TV surface for the Continuing swatch once that shelf adopts bars", () => {
    const { container } = render(<MarkStrip mark="airing" type="show" sheen />);
    const swatch = container.firstElementChild as HTMLElement;

    expect(swatch.style.backgroundColor).toContain(TITLE_MARK_PAINT.airing.surface);
    expect(swatch.className).not.toContain("mark-grail");
  });
});

describe("unmonitored overrides the mark's colour everywhere it is drawn", () => {
  it("greys a list row's strip the way the card greys its bars", () => {
    // The card paints an unmonitored title one flat grey, fill and track alike.
    // This strip did not, so the compact list drew Missing red for a title
    // whose poster two clicks away drew it grey — measured on the rig as
    // rgb(192,17,28) against rgb(108,114,127). A list and the shelf it mirrors
    // must not disagree about a colour.
    const watched = render(<MarkStrip mark="missing" type="movie" />).container.firstElementChild as HTMLElement;
    const ignored = render(<MarkStrip mark="missing" type="movie" monitored={false} />).container.firstElementChild as HTMLElement;

    expect(watched.style.backgroundColor).toContain("--mark-missing-surface");
    expect(ignored.style.backgroundColor).toContain("--mark-unmonitored");
  });

  it("takes the gold leaf off a title nothing is watching", () => {
    // The grail says "Deluno has finished". It has not been asked to start.
    const ignored = render(<MarkStrip mark="covered" type="movie" monitored={false} sheen />)
      .container.firstElementChild as HTMLElement;

    expect(ignored.className).not.toContain("mark-grail");
  });

  it("keeps the legend's own swatches at full colour", () => {
    // A legend is not a title. The chips above a shelf explain what each colour
    // means, and "Unmonitored" is its own chip beside them rather than a state
    // the other five can be in — so the default must be "watched".
    const legend = render(<MarkStrip mark="missing" type="movie" />).container.firstElementChild as HTMLElement;
    expect(legend.style.backgroundColor).toContain("--mark-missing-surface");
  });
});
