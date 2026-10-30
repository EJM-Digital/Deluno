import { fireEvent, render, screen } from "@testing-library/react";
import type { CSSProperties, ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useCutValueTitles } from "./use-cut-value-titles";

function Harness({ children }: { children: ReactNode }) {
  useCutValueTitles();
  return <div>{children}</div>;
}

function size(el: HTMLElement, dims: Partial<Record<"scrollWidth" | "clientWidth" | "scrollHeight" | "clientHeight", number>>) {
  for (const [key, value] of Object.entries(dims)) Object.defineProperty(el, key, { configurable: true, value });
}

afterEach(() => vi.restoreAllMocks());

describe("hover titles on values the screen cuts short", () => {
  it("writes the full text as a title when a value is cut with an ellipsis", () => {
    render(<Harness><span data-testid="v" style={{ textOverflow: "ellipsis", overflowX: "hidden" }}>  A very   long caption </span></Harness>);
    const v = screen.getByTestId("v");
    size(v, { scrollWidth: 300, clientWidth: 100 });
    fireEvent.pointerOver(v);
    expect(v.getAttribute("title")).toBe("A very long caption");
  });

  it("writes a title for a line-clamped value, found from a child", () => {
    render(<Harness><p data-testid="p"><b><i data-testid="inner">text</i></b></p></Harness>);
    const p = screen.getByTestId("p");
    vi.spyOn(window, "getComputedStyle").mockImplementation((el) => ({
      textOverflow: "clip",
      overflowX: "visible",
      overflowY: el === p ? "hidden" : "visible",
      getPropertyValue: () => (el === p ? "2" : "none")
    }) as unknown as CSSStyleDeclaration);
    size(p, { scrollHeight: 90, clientHeight: 40 });
    fireEvent.focusIn(screen.getByTestId("inner"));
    expect(p.getAttribute("title")).toBe("text");
  });

  it("leaves a value alone that fits, that already has a title, or that has no text", () => {
    const cut = { textOverflow: "ellipsis", overflowX: "hidden" } as CSSProperties;
    render(<Harness>
      <span data-testid="fits" style={cut}>short</span>
      <span data-testid="titled" title="mine" style={cut}>cut</span>
      <span data-testid="empty" style={cut}>   </span>
    </Harness>);
    size(screen.getByTestId("fits"), { scrollWidth: 300, clientWidth: 300 });
    size(screen.getByTestId("titled"), { scrollWidth: 300, clientWidth: 100 });
    size(screen.getByTestId("empty"), { scrollWidth: 300, clientWidth: 100 });
    for (const id of ["fits", "titled", "empty"]) fireEvent.pointerOver(screen.getByTestId(id));
    expect(screen.getByTestId("fits").hasAttribute("title")).toBe(false);
    expect(screen.getByTestId("titled").getAttribute("title")).toBe("mine");
    expect(screen.getByTestId("empty").hasAttribute("title")).toBe(false);
  });

  it("ignores events whose target is not an element, and stops listening when unmounted", () => {
    render(<Harness>x</Harness>).unmount();
    document.dispatchEvent(new Event("pointerover"));
    const remove = vi.spyOn(document, "removeEventListener");
    render(<Harness>y</Harness>).unmount();
    expect(remove).toHaveBeenCalledWith("pointerover", expect.any(Function), true);
    expect(remove).toHaveBeenCalledWith("focusin", expect.any(Function), true);
  });

  it("does not write a title when the target is the document itself", () => {
    render(<Harness>z</Harness>);
    document.dispatchEvent(new Event("pointerover"));
    expect(document.querySelectorAll("[title]").length).toBe(0);
  });
});
