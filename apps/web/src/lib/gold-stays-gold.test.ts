/// <reference types="node" />
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * Read off disk rather than imported.
 *
 * <p>`import css from "../index.css?raw"` is the obvious way and it returns an
 * empty string: Vitest does not process CSS by default, so the import resolves
 * to nothing and every assertion below passes over no content at all. A guard
 * that cannot fail is worse than no guard, which is why the length is checked
 * before anything is parsed.</p>
 */
const css = readFileSync(resolve(process.cwd(), "src/index.css"), "utf8");

/**
 * Quality met is gold, and every part of it is gold.
 *
 * <p>The mark that means "Deluno has finished" is the only one drawn as leaf
 * rather than a flat fill: a three-stop gradient with a travelling shine. Three
 * of those four colours were tuned separately, at different times, by eye — and
 * two of them drifted out of gold without anything noticing. The deep end sat
 * at hue 34, which is amber, and the shine was pure white. White over amber
 * mixes to a pale peach, so the brightest part of a poster's state bar was the
 * one part that was not gold. James, off the screen: <i>"it still has a red
 * pink to it"</i>, and then <i>"lets make it a proper gold that shines, no
 * other colours"</i>.</p>
 *
 * <p>Nothing in a screenshot test would have caught it, and nothing in a unit
 * test was looking. This reads the values themselves, because a hue is exactly
 * the kind of thing that gets nudged one token at a time until the set no
 * longer agrees with itself.</p>
 */

/** Where gold lives. Amber is below it and lime is above it. */
const GOLD_HUE = { min: 40, max: 56 } as const;

/**
 * Below this lightness a saturated yellow stops reading as gold and starts
 * reading as bronze — which is the "red" in James's three reports.
 */
const BRONZE_FLOOR = 50;

/** The stops `.mark-grail` runs its gradient between. */
const LEAF = ["mark-leaf-high", "mark-leaf", "mark-leaf-deep"] as const;

interface Hsl {
  hue: number;
  saturation: number;
  lightness: number;
}

/**
 * Every `h s% l%` triple a named token is defined as — one entry per theme, so
 * a colour that is right in dark and wrong in light fails here rather than on
 * somebody's screen.
 */
function tokens(name: string): Hsl[] {
  const pattern = new RegExp(`--${name}:\\s*([\\d.]+)\\s+([\\d.]+)%\\s+([\\d.]+)%`, "g");
  return [...css.matchAll(pattern)].map((match) => ({
    hue: Number(match[1]),
    saturation: Number(match[2]),
    lightness: Number(match[3])
  }));
}

/**
 * The colours written directly into `.mark-grail`, rather than through a token.
 *
 * <p>The rule bodies, not "everything between the first mention of the class
 * and the keyframes" — the class is named in a comment much earlier in the
 * file, so that slice was most of the stylesheet and this was asserting the
 * gold rule over every blue in the app.</p>
 */
function grailLiterals(): Hsl[] {
  const rules = [...css.matchAll(/\.mark-grail(?:::after)?\s*\{([^}]*)\}/g)].map((match) => match[1]);
  expect(rules.length, "the .mark-grail rules were not found").toBeGreaterThanOrEqual(2);

  return rules.flatMap((rule) =>
    [...rule.matchAll(/hsl\(\s*([\d.]+)\s+([\d.]+)%\s+([\d.]+)%/g)].map((match) => ({
      hue: Number(match[1]),
      saturation: Number(match[2]),
      lightness: Number(match[3])
    })));
}

describe("the gold mark", () => {
  it("is reading the stylesheet at all", () => {
    // The assertions below are all "nothing in this set is wrong". Over an
    // empty string every one of them is vacuously true.
    expect(css.length).toBeGreaterThan(1000);
    expect(css).toContain("--mark-quality-met:");
  });

  it("draws its surface from one set of stops, the same in both themes", () => {
    // The leaf used to borrow `--mark-quality-met` for its middle stop, and
    // that token is the Quality met *text* colour — dark in the light theme, so
    // the middle of a gold bar went to 40% lightness and the stop below it to
    // 30%. A gold bar sits on artwork, not on the page, so it does not invert.
    for (const name of LEAF) {
      expect(tokens(name), `--${name} is not defined exactly once`).toHaveLength(1);
    }
  });

  it("keeps every stop of the surface in gold, and above the bronze floor", () => {
    for (const name of LEAF) {
      const [value] = tokens(name);

      expect(value.hue, `--${name} at hue ${value.hue} is not gold`)
        .toBeGreaterThanOrEqual(GOLD_HUE.min);
      expect(value.hue, `--${name} at hue ${value.hue} is not gold`)
        .toBeLessThanOrEqual(GOLD_HUE.max);
      // A washed-out gold is a beige.
      expect(value.saturation, `--${name} is too grey at ${value.saturation}%`)
        .toBeGreaterThanOrEqual(90);
      // And a dark yellow is not a dark gold, it is brown. This is the floor
      // the whole complaint was about: the old shadow stop sat at 30% and 40%.
      expect(value.lightness, `--${name} at ${value.lightness}% reads as bronze`)
        .toBeGreaterThanOrEqual(BRONZE_FLOOR);
    }
  });

  it("shines in gold rather than in white", () => {
    const literals = grailLiterals();
    expect(literals.length).toBeGreaterThan(0);

    for (const colour of literals) {
      // A pure white shine is `0 0% 100%`, and its saturation of zero is what
      // desaturates the gold underneath it into peach.
      expect(colour.saturation, `the shine is achromatic at ${colour.saturation}%`)
        .toBeGreaterThanOrEqual(90);
      expect(colour.hue).toBeGreaterThanOrEqual(GOLD_HUE.min);
      expect(colour.hue).toBeLessThanOrEqual(GOLD_HUE.max);
    }
  });

  it("keeps its stops in order, so the leaf reads as one surface", () => {
    // Highlight, body, shadow. Getting these the wrong way round would still be
    // gold and would still look like a mistake.
    const [high] = tokens("mark-leaf-high");
    const [body] = tokens("mark-leaf");
    const [deep] = tokens("mark-leaf-deep");

    expect(high.lightness).toBeGreaterThan(body.lightness);
    expect(deep.lightness).toBeLessThan(body.lightness);
  });

  it("still names the mark itself in gold, in both themes", () => {
    // The semantic colour — the dot, the count, the tint — is a separate
    // question from the surface, and it is allowed to be dark for contrast.
    // It is not allowed to stop being gold.
    const values = tokens("mark-quality-met");
    expect(values, "--mark-quality-met is not defined for both themes").toHaveLength(2);

    for (const value of values) {
      expect(value.hue, `--mark-quality-met at hue ${value.hue} is not gold`)
        .toBeGreaterThanOrEqual(GOLD_HUE.min);
      expect(value.hue).toBeLessThanOrEqual(GOLD_HUE.max);
      expect(value.saturation).toBeGreaterThanOrEqual(85);
    }
  });
});
