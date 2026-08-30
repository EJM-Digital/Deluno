import { describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { TitleBars } from "./title-mark";
import { CARD_DESIGN } from "../../lib/card-design";
import { TITLE_MARK_PAINT, UNMONITORED_PAINT } from "../../lib/status-tones";

/**
 * The card's bars, DESIGN-006.
 *
 * Every assertion here is a rule that was actually broken while the design was
 * being made, and each was found by looking at the rendered thing rather than at
 * the code. They are written to fail if the rule is reversed — break one and it
 * goes red.
 */

const film = (over: Record<string, unknown> = {}) => ({
  type: "movie" as const,
  monitored: true,
  wantedStatus: "covered",
  hasFile: true,
  quality: "Bluray-1080p",
  subtitleLanguagesWanted: 2,
  subtitleLanguagesHeld: 2,
  ...over
});

function bars(item: ReturnType<typeof film>, media = true, subtitles = true) {
  const { container } = render(
    <TitleBars item={item} showMediaText={media} showSubtitleText={subtitles} />
  );
  return [...container.querySelectorAll<HTMLElement>('[role="img"]')];
}

const fillOf = (bar: HTMLElement) => bar.querySelector<HTMLElement>("span[aria-hidden]")!;
const layers = (bar: HTMLElement) =>
  [...bar.querySelectorAll<HTMLElement>("span[aria-hidden]")].slice(1);

describe("the film card's bars", () => {
  it("says the quality on the top bar and the subtitle count on the bottom", () => {
    const [top, bottom] = bars(film());
    expect(top.textContent).toContain("Bluray-1080p");
    expect(bottom.textContent).toContain("2 / 2");
    expect(bottom.textContent).toContain("SUBS");
  });

  it("fills solid for a held film and empty for a missing one", () => {
    expect(fillOf(bars(film())[0]).style.width).toBe("100%");
    expect(fillOf(bars(film({ wantedStatus: "missing", hasFile: false }))[0]).style.width).toBe("0%");
  });

  it("draws an Upcoming film solid, not as 0% of anything", () => {
    // A film that is not out yet is not partway through itself. This was a 0%
    // bar while an Upcoming SHOW drew solid, so the two shelves disagreed about
    // one state.
    const [top] = bars(film({ wantedStatus: "upcoming", hasFile: false }));
    expect(fillOf(top).style.width).toBe("100%");
    expect(top.textContent).toContain("Upcoming");
  });
});

describe("the subtitle bar inherits Upcoming, and only Upcoming", () => {
  it("says Upcoming for a title that is not out", () => {
    const [, bottom] = bars(film({ wantedStatus: "upcoming", hasFile: false, subtitleLanguagesHeld: 0 }));
    expect(bottom.textContent).toContain("Upcoming");
    expect(bottom.textContent).not.toContain("Missing");
  });

  it("says Missing — never Downloading — for a title whose bytes are moving", () => {
    // A subtitle is a few kilobytes: a progress state for one would be gone
    // before it could be read. And the file exists, so its subtitles exist and
    // you do not have them, which is what Missing means.
    const [, bottom] = bars(film({ wantedStatus: "downloading", hasFile: false, subtitleLanguagesHeld: 0 }));
    expect(bottom.textContent).toContain("Missing");
    expect(bottom.textContent).not.toContain("Downloading");
  });

  it("says Missing for a title that is out and absent", () => {
    const [, bottom] = bars(film({ wantedStatus: "missing", hasFile: false, subtitleLanguagesHeld: 0 }));
    expect(bottom.textContent).toContain("Missing");
  });
});

describe("unmonitored is the one override", () => {
  it("paints both bars the same single grey, fill and track alike", () => {
    // Applied to the fill alone this produced TWO greys — a 0%-wide fill shows
    // the track, a full one shows the fill — so which grey you saw depended on
    // the title's rung, the very thing an override exists to stop mattering.
    for (const status of ["covered", "missing", "upcoming", "upgrade"]) {
      const hasFile = status === "covered" || status === "upgrade";
      for (const bar of bars(film({ monitored: false, wantedStatus: status, hasFile }))) {
        expect(bar.style.background).toContain(UNMONITORED_PAINT.surface);
        expect(fillOf(bar).style.background).toContain(UNMONITORED_PAINT.surface);
      }
    }
  });

  it("shows a rung's own colour again the moment it is monitored", () => {
    const [top] = bars(film({ monitored: true }));
    expect(fillOf(top).style.background).toContain(TITLE_MARK_PAINT.covered.surface);
    expect(fillOf(top).style.background).not.toContain(UNMONITORED_PAINT.surface);
  });

  it("never paints a monitored card with the unmonitored grey", () => {
    for (const status of ["covered", "missing", "upcoming", "downloading", "upgrade"]) {
      for (const bar of bars(film({ monitored: true, wantedStatus: status, hasFile: status === "covered" }))) {
        expect(bar.style.background).not.toContain(UNMONITORED_PAINT.surface);
      }
    }
  });
});

describe("the label is drawn twice and clipped into halves", () => {
  it("clips the front to the fill and the back to its complement", () => {
    // Leaving the back layer unclipped makes a full bar paint the identical
    // glyphs twice, compositing every antialiased edge into an opaque one — the
    // "overexposed" look. The two clips must be complements.
    const [, bottom] = bars(film({ subtitleLanguagesWanted: 4, subtitleLanguagesHeld: 1 }));
    const [back, front] = layers(bottom);
    expect(back.style.clipPath).toBe("inset(0 0 0 25%)");
    expect(front.style.clipPath).toBe("inset(0 75% 0 0)");
  });

  it("hides the back layer entirely on a full bar", () => {
    const [top] = bars(film());
    const [back, front] = layers(top);
    expect(back.style.clipPath).toBe("inset(0 0 0 100%)");
    expect(front.style.clipPath).toBe("inset(0 0% 0 0)");
  });

  it("keeps both layers out of the accessibility tree", () => {
    // One string rendered twice must not be heard twice.
    const [top] = bars(film());
    for (const layer of layers(top)) expect(layer.getAttribute("aria-hidden")).toBe("true");
    expect(top.getAttribute("aria-label")).toContain("Quality met");
  });
});

describe("every label is coloured for the ground it sits on", () => {
  it("puts the surface's own label on a surface track, not the grey-track one", () => {
    // `onTrack` is solved against the NEUTRAL grey track. This shelf's track is
    // Missing red, which is a surface — so its label is that surface's label,
    // white. Wiring `onTrack` here measured 2.85:1 on the live shelf, under the
    // 4.5 the spec requires of every visible label. The spec said both things and
    // the wrong two were connected.
    const [top, bottom] = bars(film({ wantedStatus: "missing", hasFile: false, subtitleLanguagesHeld: 0 }));
    for (const bar of [top, bottom]) {
      const back = layers(bar)[0];
      expect(back.style.color).toContain(TITLE_MARK_PAINT.missing.onSurface);
      expect(back.style.color).not.toContain(TITLE_MARK_PAINT.missing.onTrack);
    }
  });

  it("keeps the unmonitored track's label white too", () => {
    const [top] = bars(film({ monitored: false, wantedStatus: "missing", hasFile: false }));
    expect(layers(top)[0].style.color).toContain(UNMONITORED_PAINT.onSurface);
  });
});

describe("the bars are pinned to the artwork's edges", () => {
  it("positions them absolutely, at top and bottom", () => {
    // **This is the test that was missing.** The first build laid the bars out in
    // normal flow. The artwork box is `relative aspect-[2/3] overflow-hidden`
    // with the image at `h-full w-full`, so an in-flow bar is pushed below a
    // full-height image and clipped away — the card rendered with NO BARS AT
    // ALL, and every other assertion still passed because the elements existed
    // in the DOM. James: "there are no bars at all on the movies".
    //
    // Present in the DOM is not the same as on the card.
    const [top, bottom] = bars(film());
    for (const bar of [top, bottom]) {
      expect(bar.className).toContain("absolute");
      expect(bar.className).toContain("inset-x-0");
    }
    expect(top.className).toContain("top-0");
    expect(bottom.className).toContain("bottom-0");
    expect(top.className).not.toContain("bottom-0");
  });

  it("keeps them above the artwork rather than behind it", () => {
    for (const bar of bars(film())) expect(bar.className).toContain("z-10");
  });
});

describe("the switches remove words, never facts", () => {
  it("shrinks the bar to the 5px strip when its words are off, per bar", () => {
    // A 16px band exists to carry a label. With no label it is 16px of chrome
    // over the artwork saying what 5px says. Each switch governs its own bar, so
    // one off and one on must give one strip and one band.
    const [top, bottom] = bars(film(), true, true);
    expect(top.className).toContain("h-4");
    expect(bottom.className).toContain("h-4");

    const [topOff, bottomOff] = bars(film(), false, false);
    expect(topOff.className).toContain("h-[5px]");
    expect(bottomOff.className).toContain("h-[5px]");

    const [topMixed, bottomMixed] = bars(film(), true, false);
    expect(topMixed.className).toContain("h-4");
    expect(bottomMixed.className).toContain("h-[5px]");
  });

  it("keeps the colour and the fill when the text is switched off", () => {
    const [on] = bars(film(), true, true);
    const [off] = bars(film(), false, false);
    expect(off.textContent).toBe("");
    expect(fillOf(off).style.width).toBe(fillOf(on).style.width);
    expect(fillOf(off).style.background).toBe(fillOf(on).style.background);
    // The state is still announced even with every word gone.
    expect(off.getAttribute("aria-label")).toContain("Quality met");
  });
});

describe("the two shelves are declared apart", () => {
  it("keeps Continuing off the movie ladder", () => {
    expect(CARD_DESIGN.movie.ladder).not.toContain("airing");
    expect(CARD_DESIGN.show.ladder).toContain("airing");
  });

  it("has the film bar say quality and the show bar say episodes", () => {
    expect(CARD_DESIGN.movie.mediaBar).toBe("quality");
    expect(CARD_DESIGN.show.mediaBar).toBe("episodes");
  });

  it("leaves the show shelf on its existing card until it is settled", () => {
    // James asked that the shelves not move together. Flipping this is how TV
    // adopts DESIGN-006, and it must be a deliberate act.
    expect(CARD_DESIGN.movie.bars).toBe(true);
    expect(CARD_DESIGN.show.bars).toBe(false);
  });
});

describe("monitoring is said one way, everywhere", () => {
  // One fact had SEVEN wordings: Monitored / Not monitored, Monitored /
  // Unmonitored, On / Paused, Monitoring paused, Monitor / Stop monitoring,
  // Monitor or unmonitor. None could check the others — the same defect shape as
  // everything else in this design, in words rather than colour.
  //
  // The rule: **a state word describes, a verb instructs.** Anything showing the
  // fact says Monitored / Unmonitored; anything you press says what pressing
  // does. Read off the sources, so a sixth place cannot quietly reintroduce one.
  //
  // **Unmonitored, not "Not monitored"**, because it pairs with the verb: the
  // action is already Unmonitor, so Monitored/Unmonitored and Monitor/Unmonitor
  // are one word family learned once. "Not monitored" pairs with nothing — there
  // is no verb "not monitor" — and it is two words, so it wraps in a chip. It is
  // also the arr word, which is what a reader arriving from Sonarr knows.
  const sources = [
    "src/routes/movie-detail-page.tsx",
    "src/routes/show-detail-page.tsx",
    "src/components/app/library-bulk-tools-dialog.tsx",
    "src/components/app/library-grid.tsx",
    "src/components/app/library-overview.tsx"
  ].map(f => readFileSync(resolve(process.cwd(), f), "utf8"));

  it("has no leftover vocabulary for the state", () => {
    for (const src of sources) {
      expect(src).not.toContain("Monitoring paused");
      expect(src).not.toContain("Stop monitoring");
      expect(src).not.toContain(">Not monitored<");
      expect(src).not.toContain('"Watching this');
    }
  });

  it("gives the pressable controls the state AND the action", () => {
    for (const src of sources.slice(0, 2)) {
      expect(src).toContain("Monitored — click to unmonitor");
      expect(src).toContain("Unmonitored — click to monitor");
    }
  });

  it("uses the shield, which is the app's own icon for it", () => {
    for (const src of sources.slice(0, 2)) {
      expect(src).toContain("ShieldCheck");
      expect(src).toContain("ShieldOff");
      // The eye reads as "watching" in English but is not the app's word.
      expect(src).not.toMatch(/<EyeOff className/);
    }
  });
});

describe("the tokens exist and mean one thing each", () => {
  // Read from the project root, not `import.meta.url` — under Vitest that is not
  // a file: URL and `readFileSync` refuses it.
  const css = readFileSync(resolve(process.cwd(), "src/index.css"), "utf8");

  it("reads a stylesheet that is actually there", () => {
    // `import css from "../index.css?raw"` returns an EMPTY STRING under Vitest,
    // so every assertion over it passes vacuously. Read it and prove it.
    expect(css.length).toBeGreaterThan(5000);
  });

  it("defines every surface and on-track token the paint table names", () => {
    for (const paint of Object.values(TITLE_MARK_PAINT)) {
      expect(css).toContain(`${paint.surface}:`);
      expect(css).toContain(`${paint.onTrack}:`);
    }
    expect(css).toContain(`${UNMONITORED_PAINT.surface}:`);
  });

  it("keeps the surfaces out of the dark block, because a bar does not invert", () => {
    const dark = css.slice(css.indexOf("  .dark {"));
    expect(dark).not.toContain("--mark-missing-surface:");
    expect(dark).not.toContain("--mark-unmonitored:");
    // The on-track labels DO invert, because the track itself does.
    expect(dark).toContain("--mark-missing-on-track:");
  });
});
