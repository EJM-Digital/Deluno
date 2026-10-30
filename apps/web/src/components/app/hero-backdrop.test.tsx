import { act, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../ui/artwork-image", () => ({
  ArtworkImage: ({ src, style }: { src: string; style: React.CSSProperties }) => <img data-testid="art" src={src} alt="" style={style} />
}));

const { HeroBackdrop } = await import("./hero-backdrop");

/** The scrim over a title's artwork is set from how bright the artwork is where the text sits. */

let images: Array<{ onload?: () => void; onerror?: () => void; src: string; crossOrigin?: string }> = [];
let pixel = 255;
let context: unknown;

beforeEach(() => {
  images = [];
  pixel = 255;
  vi.stubGlobal("Image", class { onload?: () => void; onerror?: () => void; src = ""; crossOrigin?: string; constructor() { images.push(this); } });
  context = {
    drawImage: vi.fn(),
    getImageData: (_x: number, _y: number, w: number, h: number) => ({ data: new Uint8ClampedArray(w * h * 4).fill(pixel) })
  };
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockImplementation(() => context as never);
});
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

const scrimStyle = (container: HTMLElement) => (container.querySelector("div[style]") as HTMLElement).getAttribute("style")!;

describe("the artwork behind a title header", () => {
  it("draws no artwork without a url and uses the safe, heavy scrim", () => {
    const { container } = render(<HeroBackdrop />);
    expect(container.querySelector("img")).toBeNull();
    expect(scrimStyle(container)).toContain("0.82");
  });

  it("gives a bright plate a heavy scrim and lets the artwork show less", async () => {
    const { container, getByTestId } = render(<HeroBackdrop url="https://art.example/a.jpg" />);
    expect(images[0]!.crossOrigin).toBe("anonymous");
    expect(images[0]!.src).toBe("https://art.example/a.jpg");
    act(() => images[0]!.onload!());
    expect(scrimStyle(container)).toContain("0.92");
    expect(Number(getByTestId("art").style.opacity)).toBeCloseTo(0.42 + 0.08 * 0.38, 2);
  });

  it("gives a dark plate a light scrim, never below 0.40", () => {
    pixel = 0;
    const { container } = render(<HeroBackdrop url="https://art.example/dark.jpg" />);
    act(() => images[0]!.onload!());
    expect(scrimStyle(container)).toContain("0.40");
  });

  it("keeps the safe scrim when the pixels cannot be read, there is no canvas, or the image fails", () => {
    const { container, rerender } = render(<HeroBackdrop url="https://art.example/a.jpg" />);
    act(() => images[0]!.onerror!());
    expect(scrimStyle(container)).toContain("0.82");

    context = { drawImage: vi.fn(), getImageData: () => { throw new Error("tainted"); } };
    rerender(<HeroBackdrop url="https://art.example/b.jpg" />);
    act(() => images[1]!.onload!());
    expect(scrimStyle(container)).toContain("0.82");

    context = null;
    rerender(<HeroBackdrop url="https://art.example/c.jpg" />);
    act(() => images[2]!.onload!());
    expect(scrimStyle(container)).toContain("0.82");
  });

  it("goes back to the safe scrim when the artwork goes away, and ignores an answer after leaving", () => {
    const { container, rerender, unmount } = render(<HeroBackdrop url="https://art.example/a.jpg" />);
    act(() => images[0]!.onload!());
    expect(scrimStyle(container)).toContain("0.92");
    rerender(<HeroBackdrop url={null} />);
    expect(scrimStyle(container)).toContain("0.82");

    rerender(<HeroBackdrop url="https://art.example/z.jpg" />);
    unmount();
    act(() => { images[1]!.onload!(); images[1]!.onerror!(); });
  });
});
