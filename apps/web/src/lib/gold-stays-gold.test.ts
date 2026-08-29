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
const GOLD_HUE = { min: 38, max: 56 } as const;

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

/** The colours written directly into `.mark-grail`, rather than through a token. */
function grailLiterals(): Hsl[] {
  const block = css.slice(css.indexOf(".mark-grail"), css.indexOf("@keyframes mark-grail-sheen"));
  return [...block.matchAll(/hsl\(\s*([\d.]+)\s+([\d.]+)%\s+([\d.]+)%/g)].map((match) => ({
    hue: Number(match[1]),
    saturation: Number(match[2]),
    lightness: Number(match[3])
  }));
}

describe("the gold mark", () => {
  it("is reading the stylesheet at all", () => {
    // The assertions below are all "nothing in this set is wrong". Over an
    // empty string every one of them is vacuously true.
    expect(css.length).toBeGreaterThan(1000);
    expect(css).toContain("--mark-quality-met:");
  });

  it("defines its whole ladder inside the gold hues", () => {
    const ladder = ["mark-quality-met", "mark-quality-met-high", "mark-quality-met-deep"];

    for (const name of ladder) {
      const values = tokens(name);
      // Two themes. One would mean a token was renamed or a theme lost it.
      expect(values, `--${name} is not defined twice`).toHaveLength(2);

      for (const value of values) {
        expect(value.hue, `--${name} at hue ${value.hue} is not gold`)
          .toBeGreaterThanOrEqual(GOLD_HUE.min);
        expect(value.hue, `--${name} at hue ${value.hue} is not gold`)
          .toBeLessThanOrEqual(GOLD_HUE.max);
        // A washed-out gold is a beige. The mark that means "finished" is the
        // one that has to carry across a wall of artwork.
        expect(value.saturation, `--${name} is too grey at ${value.saturation}%`)
          .toBeGreaterThanOrEqual(85);
      }
    }
  });

  it("shines in gold rather than in white", () => {
    const literals = grailLiterals();
    expect(literals.length).toBeGreaterThan(0);

    for (const colour of literals) {
      // A pure white shine is `0 0% 100%`, and its saturation of zero is what
      // desaturates the gold underneath it into peach. Any colour written into
      // this treatment has to be gold itself.
      expect(colour.saturation, `the shine is achromatic at ${colour.saturation}%`)
        .toBeGreaterThanOrEqual(85);
      expect(colour.hue).toBeGreaterThanOrEqual(GOLD_HUE.min);
      expect(colour.hue).toBeLessThanOrEqual(GOLD_HUE.max);
    }
  });

  it("keeps its ladder in order, so the leaf reads as one surface", () => {
    // High is lighter than the base, and deep is darker. Getting these the
    // wrong way round would still be gold and would look like a mistake.
    for (const theme of [0, 1]) {
      const base = tokens("mark-quality-met")[theme];
      const high = tokens("mark-quality-met-high")[theme];
      const deep = tokens("mark-quality-met-deep")[theme];

      expect(high.lightness).toBeGreaterThan(base.lightness);
      expect(deep.lightness).toBeLessThan(base.lightness);
    }
  });
});
