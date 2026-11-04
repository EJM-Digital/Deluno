import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

async function load() {
  vi.resetModules();
  return import("./caption-fit");
}

describe("measuring a poster caption's words", () => {
  beforeEach(() => { vi.restoreAllMocks(); });
  afterEach(() => { vi.unstubAllGlobals(); document.body.replaceChildren(); });

  it("measures text in the font it was given, on a canvas", async () => {
    const measureText = vi.fn((text: string) => ({ width: text.length * 5 }));
    const ctx = { font: "", measureText } as unknown as CanvasRenderingContext2D;
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(ctx);
    const { canvasMeasure } = await load();

    const measure = canvasMeasure("italic 400 11px Inter");

    expect(measure).not.toBeNull();
    expect(measure!("abcd")).toBe(20);
    expect(ctx.font).toBe("italic 400 11px Inter");
  });

  it("makes the canvas once and reuses it", async () => {
    const getContext = vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue({ font: "", measureText: () => ({ width: 1 }) } as unknown as CanvasRenderingContext2D);
    const { canvasMeasure } = await load();
    canvasMeasure("a");
    canvasMeasure("b");
    expect(getContext).toHaveBeenCalledTimes(1);
  });

  it("gives no measure without a font", async () => {
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue({ font: "", measureText: () => ({ width: 1 }) } as unknown as CanvasRenderingContext2D);
    const { canvasMeasure } = await load();
    expect(canvasMeasure("")).toBeNull();
  });

  it("gives no measure where the browser has no canvas", async () => {
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
    const { canvasMeasure } = await load();
    expect(canvasMeasure("12px sans-serif")).toBeNull();
  });

  it("gives no measure where asking for a canvas fails", async () => {
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockImplementation(() => { throw new Error("blocked"); });
    const { canvasMeasure } = await load();
    expect(canvasMeasure("12px sans-serif")).toBeNull();
  });

  it("gives no measure where there is no document at all", async () => {
    vi.stubGlobal("document", undefined);
    const { canvasMeasure } = await load();
    expect(canvasMeasure("12px sans-serif")).toBeNull();
  });

  it("reads the status font from a hidden probe laid out like the caption, and removes the probe", async () => {
    const { statusFont } = await load();
    const host = document.createElement("div");
    document.body.append(host);
    const seen: string[] = [];
    vi.spyOn(window, "getComputedStyle").mockImplementation((element: Element) => {
      const cap = element.parentElement?.parentElement as HTMLElement;
      seen.push(`${element.parentElement?.className}>${cap.className}:${cap.style.visibility}:${host.contains(cap)}`);
      return { fontStyle: "italic", fontWeight: "600", fontSize: "11px", fontFamily: "Inter" } as CSSStyleDeclaration;
    });

    expect(statusFont(host)).toBe("italic 600 11px Inter");
    expect(seen).toEqual(["lsh-status>lsh-cap:hidden:true"]);
    expect(host.children).toHaveLength(0);
  });
});

describe("the words a poster caption has room for", () => {
  const widths = (text: string) => text.length * 10;

  it("takes the fullest words that fit, with a pixel to spare", async () => {
    const { lineWords } = await load();
    expect(lineWords(["downloading now", "downloading", "now", ""], 111, widths)).toBe("downloading");
    expect(lineWords(["downloading", "now"], 110, widths)).toBe("now");
  });

  it("falls back to the last words, the dot alone, when none fit", async () => {
    const { lineWords } = await load();
    expect(lineWords(["downloading", "now", ""], -5, widths)).toBe("");
  });

  it("exports the width of the status mark", async () => {
    const { STATUS_MARK_PX } = await load();
    expect(STATUS_MARK_PX).toBe(11);
  });
});
