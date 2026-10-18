import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { COLOR_IMPAIRED_MARK_PALETTE, ColorModeProvider, useColorMode } from "./use-color-mode";

/** The colour-impaired palette: one choice changes the page's mode mark, the title-mark colours and what is remembered. */

function Probe() {
  const { colorMode, setColorMode } = useColorMode();
  return (
    <div>
      <output data-testid="mode">{colorMode}</output>
      <button type="button" onClick={() => setColorMode("impaired")}>Impaired</button>
      <button type="button" onClick={() => setColorMode("standard")}>Standard</button>
    </div>
  );
}

const root = () => document.documentElement;

beforeEach(() => {
  localStorage.clear();
  delete root().dataset.colorMode;
  for (const property of Object.keys(COLOR_IMPAIRED_MARK_PALETTE)) root().style.removeProperty(property);
});
afterEach(() => localStorage.clear());

describe("colour mode", () => {
  it("starts standard: data-color-mode says so and no mark colour is overridden", () => {
    render(<ColorModeProvider><Probe /></ColorModeProvider>);

    expect(root().dataset.colorMode).toBe("standard");
    expect(document.body.dataset.colorMode).toBe("standard");
    for (const property of Object.keys(COLOR_IMPAIRED_MARK_PALETTE)) expect(root().style.getPropertyValue(property)).toBe("");
  });

  it("choosing the colour-impaired palette sets data-color-mode and swaps every title-mark colour", () => {
    render(<ColorModeProvider><Probe /></ColorModeProvider>);

    act(() => screen.getByRole("button", { name: "Impaired" }).click());

    expect(root().dataset.colorMode).toBe("impaired");
    expect(document.body.dataset.colorMode).toBe("impaired");
    for (const [property, value] of Object.entries(COLOR_IMPAIRED_MARK_PALETTE)) expect(root().style.getPropertyValue(property)).toBe(value);
    expect(screen.getByTestId("mode").textContent).toBe("impaired");
  });

  it("choosing standard again restores every mark colour", () => {
    render(<ColorModeProvider><Probe /></ColorModeProvider>);
    act(() => screen.getByRole("button", { name: "Impaired" }).click());

    act(() => screen.getByRole("button", { name: "Standard" }).click());

    expect(root().dataset.colorMode).toBe("standard");
    for (const property of Object.keys(COLOR_IMPAIRED_MARK_PALETTE)) expect(root().style.getPropertyValue(property)).toBe("");
  });

  it("remembers the choice for the next visit, and starts from it", () => {
    const first = render(<ColorModeProvider><Probe /></ColorModeProvider>);
    act(() => screen.getByRole("button", { name: "Impaired" }).click());
    expect(localStorage.getItem("deluno-color-mode")).toBe("impaired");
    first.unmount();

    render(<ColorModeProvider><Probe /></ColorModeProvider>);
    expect(screen.getByTestId("mode").textContent).toBe("impaired");
    expect(root().dataset.colorMode).toBe("impaired");
  });

  it("a stored value that is neither mode is ignored and the page is standard", () => {
    localStorage.setItem("deluno-color-mode", "purple");
    render(<ColorModeProvider><Probe /></ColorModeProvider>);
    expect(root().dataset.colorMode).toBe("standard");
  });
});
