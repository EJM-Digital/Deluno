/// <reference types="node" />
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * Read off disk rather than imported, for the reason `gold-stays-gold` gives:
 * Vitest does not process CSS, so `?raw` resolves to an empty string and every
 * assertion below would pass over no content at all.
 */
const css = readFileSync(resolve(process.cwd(), "src/index.css"), "utf8");

interface Hsl {
  hue: number;
  saturation: number;
  lightness: number;
}

/**
 * Every `h s% l%` a token is defined as, in source order. The light `:root`
 * comes first in the file, so index 0 is the light theme's value and index 1
 * is the dark one.
 */
function tokens(name: string): Hsl[] {
  // Read by hand rather than by regex. `--background:` cannot be found inside
  // `--sidebar-background:` — two hyphens have to sit immediately before the
  // name — so a plain search is exact here and has no escaping to get wrong.
  const needle = "--" + name + ":";
  const found: Hsl[] = [];

  for (let at = css.indexOf(needle); at !== -1; at = css.indexOf(needle, at + 1)) {
    const declaration = css.slice(at + needle.length, css.indexOf(";", at));
    const parts = declaration.trim().split(/\s+/);
    if (parts.length !== 3) continue;

    const numbers = parts.map((part) => Number(part.replace("%", "")));
    if (numbers.some((value) => Number.isNaN(value))) continue;

    found.push({ hue: numbers[0], saturation: numbers[1], lightness: numbers[2] });
  }

  return found;
}

function light(name: string): Hsl {
  const values = tokens(name);
  expect(values.length, `--${name} is not defined at all`).toBeGreaterThan(0);
  return values[0];
}

function channels({ hue, saturation, lightness }: Hsl): [number, number, number] {
  const s = saturation / 100;
  const l = lightness / 100;
  const c = (1 - Math.abs(2 * l - 1)) * s;
  const x = c * (1 - Math.abs(((hue / 60) % 2) - 1));
  const m = l - c / 2;
  const wheel: [number, number, number][] = [
    [c, x, 0], [x, c, 0], [0, c, x], [0, x, c], [x, 0, c], [c, 0, x]
  ];
  const [r, g, b] = wheel[Math.min(5, Math.floor(hue / 60))];
  return [r + m, g + m, b + m];
}

/** WCAG 2.1 relative luminance. */
function luminance(colour: Hsl): number {
  const [r, g, b] = channels(colour).map((value) =>
    value <= 0.03928 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: Hsl, b: Hsl): number {
  const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (high + 0.05) / (low + 0.05);
}

/**
 * The dark theme's value for a token — index 1, since the light `:root` is
 * declared first.
 */
function dark(name: string): Hsl {
  const values = tokens(name);
  expect(values.length, `--${name} is not defined for both themes`).toBeGreaterThan(1);
  return values[1];
}

/**
 * CIE L*, perceptual lightness on a 0-100 scale.
 *
 * <p>Used instead of a WCAG ratio for comparing two large flat planes, because
 * a ratio is a metric for text on a background and answers the wrong question
 * here — asked whether the two themes' planes were separated alike, it said
 * yes while the light theme visibly read flatter. L* found the gap at once.</p>
 */
function perceptualLightness(colour: Hsl): number {
  const y = luminance(colour);
  const delta = 6 / 29;
  return y > delta * delta * delta ? 116 * Math.cbrt(y) - 16 : (y * 8) / (delta * delta * delta);
}

/** How far apart two planes look, regardless of which theme they are in. */
function step(a: Hsl, b: Hsl): number {
  return Math.abs(perceptualLightness(a) - perceptualLightness(b));
}

/**
 * The light theme is allowed to be light. It is not allowed to be a lightbulb.
 *
 * <p>It used to paint the page at 97% and every card at a flat 100%, which is
 * two faults wearing one appearance. A card three points off the page is not a
 * different plane, it is the same white with a hairline drawn on it; and a
 * screen of that at full size glares. James: <i>"light mode needs to be toned
 * down a bit and not so bright smack in your face"</i>.</p>
 *
 * <p>What is guarded here is the second half of that fix, because it is the
 * half that is invisible and therefore the half that rots. Contrast is a
 * ratio: darkening the ground silently darkens every signal's standing against
 * it, and Missing would have slipped from 4.87:1 to 4.48:1 — under AA — with
 * nobody editing a single red. So each signal was darkened to match, and these
 * are the ratios it has to keep. Raise the surfaces back towards white, or
 * lighten one mark by eye, and this fails rather than somebody's screen.</p>
 */
describe("the light theme's brightness", () => {
  it("reads the file it is asserting about", () => {
    expect(css.length).toBeGreaterThan(2000);
  });

  it("paints nothing pure white", () => {
    // Not even the highest plane. A dialog is the brightest thing on screen
    // and it still stops short of 100%.
    for (const name of ["background", "card", "card-elevated", "popover", "surface-1", "surface-2"]) {
      expect(light(name).lightness, `--${name} is pure white`).toBeLessThan(100);
    }
  });

  it("keeps the page below the card, and the card below what floats over it", () => {
    // The old theme had card and card-elevated both at 100%, so a dialog had
    // no plane of its own — it was distinguishable only by its shadow.
    const page = light("background").lightness;
    const card = light("card").lightness;
    const floating = light("card-elevated").lightness;

    expect(page).toBeLessThan(card);
    expect(card).toBeLessThan(floating);
    // And far enough below to be seen as a different plane rather than a
    // rendering artefact. The old gap was three points and read as none.
    expect(card - page).toBeGreaterThanOrEqual(3.5);
  });

  it("still reads as a light theme rather than a dimmed dark one", () => {
    expect(light("background").lightness).toBeGreaterThanOrEqual(88);
    expect(light("card").lightness).toBeGreaterThanOrEqual(93);
  });

  /**
   * The minimums are what each colour measured against the old pure-white
   * card. Meeting them on a 96.5% card is the whole claim: the ground came
   * down and took nothing with it.
   */
  it.each([
    ["muted-foreground", 6.01],
    ["mark-missing", 4.87],
    ["mark-downloading", 4.2],
    ["mark-upgrade", 3.85],
    ["mark-quality-met", 2.89],
    ["mark-upcoming", 6.38],
    ["mark-airing", 9.21],
    ["state-warn", 4.94],
    ["primary", 5.09]
  ])("keeps --%s at least as legible on the card as it was on white", (name, required) => {
    expect(contrast(light(name), light("card"))).toBeGreaterThanOrEqual(required);
  });

  it("clears AA for the colours that carry words rather than counts", () => {
    for (const name of ["foreground", "muted-foreground", "primary"]) {
      expect(contrast(light(name), light("card")), `--${name} on the card`).toBeGreaterThanOrEqual(4.5);
      expect(contrast(light(name), light("background")), `--${name} on the page`).toBeGreaterThanOrEqual(4.5);
    }
  });

  /**
   * Body text went the other way on purpose — 10% to 12% — because near-black
   * on near-white was the other half of the glare. This is the floor that
   * stops "softer" becoming "grey".
   */
  it("softens body text without letting go of it", () => {
    expect(light("foreground").lightness).toBeGreaterThan(10);
    expect(contrast(light("foreground"), light("card"))).toBeGreaterThanOrEqual(12);
  });

  /**
   * The two themes should feel like each other's opposite, and for a while
   * this one did not. The check is in L* rather than in HSL lightness,
   * because equal HSL steps are not equal steps to an eye — the sRGB curve
   * compresses the top end, so the same numeric gap that reads clearly at
   * 7% lightness nearly vanishes at 93%.
   */
  it.each([
    ["the page from the card", "background", "card"],
    ["a dialog from the card", "card", "card-elevated"]
  ])("separates %s as far as dark mode does", (_, lower, upper) => {
    const inDark = step(dark(lower), dark(upper));
    const inLight = step(light(lower), light(upper));

    // Within half a unit of dark's step, in either direction. Exactness is
    // not the point; being visibly flatter than the other theme is.
    expect(Math.abs(inLight - inDark), `dark ${inDark.toFixed(1)}, light ${inLight.toFixed(1)}`)
      .toBeLessThanOrEqual(0.5);
  });

  /**
   * Except the rail, where a literal mirror is wrong. Dark's rail is only
   * 1.6 L* below its page and still reads as chrome because it sits at the
   * floor of the scale; light has no floor to sit on, so the same number
   * gives a slightly tinted margin instead of a region.
   */
  it("steps the rail further from the page than dark mode needs to", () => {
    const inDark = step(dark("sidebar-background"), dark("background"));
    const inLight = step(light("sidebar-background"), light("background"));

    expect(inLight).toBeGreaterThan(inDark);
    expect(inLight, "the rail is a whisper rather than a region").toBeGreaterThanOrEqual(4);
  });

  /**
   * The bar surfaces sit on artwork, not on the page, so a darker page tells
   * them nothing. They were left exactly where DESIGN-006 put them, and this
   * says so out loud in case a future tone-down sweeps the whole file.
   */
  it("leaves the colours that live on artwork alone", () => {
    expect(light("mark-missing-surface")).toEqual({ hue: 356, saturation: 84, lightness: 41 });
    expect(light("mark-downloading-surface")).toEqual({ hue: 214, saturation: 94, lightness: 40 });
    expect(light("mark-upgrade-surface")).toEqual({ hue: 150, saturation: 90, lightness: 25 });
    expect(light("mark-leaf")).toEqual({ hue: 49, saturation: 100, lightness: 62 });
  });
});
