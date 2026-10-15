import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * WCAG AA for the colour tokens (#1340). Every text token is checked against every surface it is drawn on, in the light
 * and the dark theme: 4.5:1 for normal text, 3:1 for the parts that are large text or a non-text mark. A new screen
 * that only uses these tokens inherits the result, so a token that slips under AA fails here before any screen does.
 */

type Theme = Record<string, string>;
const css = readFileSync(resolve(__dirname, "../index.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");

/** Every `--name: value;` declared in the blocks opened by a line that is exactly `selector {` (the file has several). */
function declarations(selector: ":root" | ".dark"): Theme {
  const out: Theme = {};
  let from = 0;
  for (;;) {
    const line = new RegExp("^[ \t]*" + selector.replace(".", "[.]") + "[ \t]*[{]", "m").exec(css.slice(from));
    if (!line) break;
    let i = from + line.index + line[0].length;
    const start = i;
    let depth = 1;
    while (depth > 0 && i < css.length) {
      if (css[i] === "{") depth++;
      else if (css[i] === "}") depth--;
      i++;
    }
    for (const d of css.slice(start, i - 1).matchAll(/(--[a-z0-9-]+):\s*([^;]+);/g)) out[d[1]] = d[2].trim();
    from = i;
  }
  return out;
}

const light = declarations(":root");
const dark = { ...light, ...declarations(".dark") };

type Rgb = [number, number, number];

function hsl(token: string, theme: Theme): Rgb {
  const raw = theme[`--${token}`];
  const m = /^(-?[\d.]+)\s+([\d.]+)%\s+([\d.]+)%$/.exec(raw ?? "");
  if (!m) throw new Error(`--${token} is not an "h s% l%" colour: ${raw}`);
  const h = Number(m[1]);
  const s = Number(m[2]) / 100;
  const l = Number(m[3]) / 100;
  const k = (n: number) => (n + h / 30) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return [f(0) * 255, f(8) * 255, f(4) * 255];
}

const mix = (top: Rgb, bottom: Rgb, alpha: number): Rgb => top.map((c, i) => c * alpha + bottom[i] * (1 - alpha)) as Rgb;

function luminance([r, g, b]: Rgb) {
  const lin = (c: number) => {
    const v = Math.round(c) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

export function contrast(a: Rgb, b: Rgb) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

/** The surfaces body text can sit on. */
const surfaces = ["background", "card", "card-elevated", "popover", "surface-1", "surface-2"];
/** Text colours that are drawn on any of those surfaces. */
const onAnySurface = ["foreground", "muted-foreground", "primary", "destructive", "success", "warning", "info"];
/** Signal colours also drawn as a pill or banner on their own 5% to 15% tint of the surface. */
const signals = ["primary", "destructive", "success", "warning", "info"];
/** Text colours that belong to one surface. */
const own: Array<[text: string, surface: string]> = [
  ["card-foreground", "card"],
  ["popover-foreground", "popover"],
  ["secondary-foreground", "secondary"],
  ["muted-foreground", "muted"],
  ["accent-foreground", "accent"],
  ["sidebar-foreground", "sidebar-background"],
  ["sidebar-accent-foreground", "sidebar-accent"],
  ["muted-foreground", "sidebar-background"],
  ["primary-foreground", "primary"],
  ["destructive-foreground", "destructive"],
  ["success-foreground", "success"],
  ["info-foreground", "info"],
];
/** Marks that carry a status word on the page; 3:1 because they are drawn large or as a non-text mark. */
const marks = ["mark-missing", "mark-downloading", "mark-upgrade", "mark-quality-met", "mark-upcoming"];

describe.each([["light", light], ["dark", dark]] as const)("colour tokens meet WCAG AA in the %s theme", (_name, theme) => {
  it.each(onAnySurface.flatMap((t) => surfaces.map((s) => [t, s] as const)))("%s text on %s is at least 4.5:1", (t, s) => {
    expect(contrast(hsl(t, theme), hsl(s, theme))).toBeGreaterThanOrEqual(4.5);
  });

  it.each(signals.flatMap((t) => [0.05, 0.1, 0.15].map((a) => [t, a] as const)))("%s text on its own %s tint of the card is at least 4.5:1", (t, a) => {
    expect(contrast(hsl(t, theme), mix(hsl(t, theme), hsl("card", theme), a))).toBeGreaterThanOrEqual(4.5);
  });

  it.each(signals.filter((t) => t !== "primary"))("%s text on a selected row (the primary tint of the card, up to 10%) is at least 4.5:1", (t) => {
    for (const a of [0.08, 0.1]) expect(contrast(hsl(t, theme), mix(hsl("primary", theme), hsl("card", theme), a))).toBeGreaterThanOrEqual(4.5);
  });

  it.each([0.1, 0.15])("warning-foreground text on the warning banner (its %s tint of the card) is at least 4.5:1", (a) => {
    expect(contrast(hsl("warning-foreground", theme), mix(hsl("warning", theme), hsl("card", theme), a))).toBeGreaterThanOrEqual(4.5);
  });

  it.each(["primary"])("%s text on the active sidebar row (its 14% tint of the sidebar) is at least 4.5:1", (t) => {
    expect(contrast(hsl(t, theme), mix(hsl(t, theme), hsl("sidebar-background", theme), 0.14))).toBeGreaterThanOrEqual(4.5);
  });

  it.each(own)("%s on its own surface %s is at least 4.5:1", (t, s) => {
    expect(contrast(hsl(t, theme), hsl(s, theme))).toBeGreaterThanOrEqual(4.5);
  });

  it.each(marks.flatMap((t) => ["background", "card"].map((s) => [t, s] as const)))("%s on %s is at least 3:1", (t, s) => {
    expect(contrast(hsl(t, theme), hsl(s, theme))).toBeGreaterThanOrEqual(3);
  });
});
