/**
 * What the tiny caption's status line says at a poster's width, decided by measuring the words rather than letting them clip
 * (Weir's caption-fit, the same rule): the fullest of its words that fit beside the dot, and the dot alone where none does.
 */

/** The width in px of a line of text in the caption's own font. */
export type MeasureText = (text: string) => number;

/** The status dot (6px) and the 5px after it, which the words do not have. */
export const STATUS_MARK_PX = 11;

/** The room left by the type's own rounding: a line must be this much narrower than the room, or its end is cut. */
const LINE_MARGIN_PX = 1;

/** The canvas context everything is measured on, made once. */
let context: CanvasRenderingContext2D | null | undefined;

function measuringContext(): CanvasRenderingContext2D | null {
  if (context === undefined) {
    try {
      context = typeof document === "undefined" ? null : document.createElement("canvas").getContext("2d");
    } catch {
      context = null;
    }
  }
  return context;
}

/** Measures text in `font` (a CSS font shorthand) on a canvas, or null where the browser has no canvas. */
export function canvasMeasure(font: string): MeasureText | null {
  const canvas = measuringContext();
  if (!canvas || !font) return null;
  return (text) => {
    canvas.font = font;
    return canvas.measureText(text).width;
  };
}

/** The font the status line is set in, read from a probe laid out as the caption's status line inside `host`. */
export function statusFont(host: HTMLElement): string {
  const cap = document.createElement("span");
  cap.className = "lsh-cap";
  cap.style.visibility = "hidden";
  const line = document.createElement("span");
  line.className = "lsh-status";
  const words = document.createElement("span");
  line.append(words);
  cap.append(line);
  host.append(cap);
  // The `font` shorthand reads back empty in some browsers, so the font is put together from its parts.
  const { fontStyle, fontWeight, fontSize, fontFamily } = getComputedStyle(words);
  cap.remove();
  return `${fontStyle} ${fontWeight} ${fontSize} ${fontFamily}`;
}

/** The words for a line `room` px wide: the fullest that fit, else the last (the dot alone, where the list ends in nothing). */
export function lineWords(words: readonly string[], room: number, measure: MeasureText): string {
  return words.find((candidate) => measure(candidate) <= room - LINE_MARGIN_PX) ?? words[words.length - 1];
}
