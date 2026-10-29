import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FilterMenu } from "./filter-menu";

function setViewport(width: number, height: number) {
  Object.defineProperty(window, "innerWidth", { configurable: true, value: width });
  Object.defineProperty(window, "innerHeight", { configurable: true, value: height });
}

function placeButton(rect: { right: number; bottom: number }) {
  return vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    return this.tagName === "BUTTON" ? ({ right: rect.right, bottom: rect.bottom, left: 0, top: 0, width: 0, height: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect) : ({} as DOMRect);
  });
}

const trigger = () => screen.getByRole("button", { name: /^Filter/ });
const panel = () => screen.getByRole("group", { name: "Search filters" });

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  setViewport(1024, 768);
});

describe("FilterMenu (#1427)", () => {
  it("is a closed Filter button that says nothing is narrowing, and opens a focused group of its children", async () => {
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    expect(trigger()).toHaveAccessibleName("Filter");
    expect(trigger()).toHaveAttribute("aria-haspopup", "true");
    expect(trigger()).toHaveAttribute("aria-expanded", "false");
    expect(trigger()).not.toHaveAttribute("aria-controls");
    expect(trigger()).not.toHaveClass("text-primary");
    expect(trigger().textContent).toBe("Filter");
    expect(screen.queryByText("inside")).toBeNull();

    fireEvent.click(trigger());
    await waitFor(() => expect(panel()).toHaveFocus());
    expect(panel()).toHaveTextContent("inside");
    expect(panel()).toHaveAttribute("tabindex", "-1");
    expect(trigger()).toHaveAttribute("aria-expanded", "true");
    expect(trigger()).toHaveAttribute("aria-controls", panel().id);
    expect(panel().parentElement).toBe(document.body);
  });

  it("says how many filters are narrowing, in its name, its text and its colour", () => {
    render(<FilterMenu active={2}><p>inside</p></FilterMenu>);
    expect(trigger()).toHaveAccessibleName("Filter, 2 on");
    expect(trigger().textContent).toBe("Filter2");
    expect(trigger()).toHaveClass("text-primary", "bg-primary/10", "border-primary/40");
    expect(trigger()).toHaveClass("h-12", "rounded-xl");
  });

  it("opens under the button, pulled back inside the screen, never wider than it, and no taller than the room below", async () => {
    setViewport(1000, 700);
    placeButton({ right: 900, bottom: 100 });
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    fireEvent.click(trigger());
    await waitFor(() => expect(panel().style.top).toBe("108px"));
    expect(panel().style.position).toBe("fixed");
    expect(panel().style.left).toBe("324px");
    expect(panel().style.width).toContain("36rem");
    expect(panel().style.maxHeight).toBe("580px");
  });

  it("keeps the panel 12px inside a narrow screen and keeps at least 160px of height", async () => {
    setViewport(400, 300);
    placeButton({ right: 390, bottom: 250 });
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    fireEvent.click(trigger());
    await waitFor(() => expect(panel().style.top).toBe("258px"));
    // 400 - 24 = 376 wide: the button's right edge would put it at 14, but the screen only has room from 12.
    expect(panel().style.left).toBe("12px");
    expect(panel().style.maxHeight).toBe("160px");
  });

  it("pulls the panel back from the right edge when the button sits past it", async () => {
    setViewport(500, 700);
    placeButton({ right: 700, bottom: 50 });
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    fireEvent.click(trigger());
    // wide = 476; the button's right minus wide is 224, but the panel may not pass 500 - 12 - 476 = 12.
    await waitFor(() => expect(panel().style.left).toBe("12px"));
  });

  it("follows the button when the window is resized or anything scrolls, and stops following once closed", async () => {
    setViewport(1000, 700);
    const spy = placeButton({ right: 900, bottom: 100 });
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    fireEvent.click(trigger());
    await waitFor(() => expect(panel().style.top).toBe("108px"));

    spy.mockRestore();
    placeButton({ right: 900, bottom: 200 });
    fireEvent(window, new Event("resize"));
    await waitFor(() => expect(panel().style.top).toBe("208px"));

    spy.mockRestore();
    placeButton({ right: 900, bottom: 300 });
    fireEvent.scroll(window);
    await waitFor(() => expect(panel().style.top).toBe("308px"));

    const removed: string[] = [];
    const remove = vi.spyOn(window, "removeEventListener").mockImplementation((type: string) => { removed.push(type); });
    const removeDoc = vi.spyOn(document, "removeEventListener").mockImplementation((type: string) => { removed.push(`doc:${type}`); });
    fireEvent.click(trigger());
    await waitFor(() => expect(screen.queryByRole("group", { name: "Search filters" })).toBeNull());
    expect(removed).toEqual(expect.arrayContaining(["resize", "scroll", "doc:pointerdown", "doc:keydown"]));
    remove.mockRestore();
    removeDoc.mockRestore();
  });

  it("closes on Escape, hands focus back to the button, and keeps the key from reaching anything else", async () => {
    const outside = vi.fn();
    document.addEventListener("keydown", outside);
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    fireEvent.click(trigger());
    await waitFor(() => expect(panel()).toHaveFocus());

    fireEvent.keyDown(panel(), { key: "a" });
    expect(screen.getByRole("group", { name: "Search filters" })).toBeInTheDocument();

    outside.mockClear();
    fireEvent.keyDown(panel(), { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("group", { name: "Search filters" })).toBeNull());
    expect(trigger()).toHaveFocus();
    expect(outside).not.toHaveBeenCalled();
    document.removeEventListener("keydown", outside);
  });

  it("stays open when its own button or panel is pressed and closes when anything else is", async () => {
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    fireEvent.click(trigger());
    await waitFor(() => expect(panel()).toBeInTheDocument());
    fireEvent.pointerDown(trigger());
    fireEvent.pointerDown(screen.getByText("inside"));
    expect(screen.getByRole("group", { name: "Search filters" })).toBeInTheDocument();
    fireEvent.pointerDown(document.body);
    await waitFor(() => expect(screen.queryByRole("group", { name: "Search filters" })).toBeNull());
  });

  it("takes no listeners while closed, and passes a class to its wrapper", () => {
    const add = vi.spyOn(document, "addEventListener");
    const { container } = render(<FilterMenu active={0} className="extra"><p>inside</p></FilterMenu>);
    expect(add.mock.calls.filter(([type]) => type === "pointerdown" || type === "keydown")).toEqual([]);
    expect(container.firstElementChild).toHaveClass("relative", "shrink-0", "extra");
  });

  it("listens in the capture phase, so Escape here wins over a handler lower down", async () => {
    const add = vi.spyOn(document, "addEventListener");
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    fireEvent.click(trigger());
    await waitFor(() => expect(panel()).toBeInTheDocument());
    expect(add).toHaveBeenCalledWith("keydown", expect.any(Function), true);
    expect(add).toHaveBeenCalledWith("pointerdown", expect.any(Function));
  });

  it("styles the panel as a popover that scrolls only itself", async () => {
    render(<FilterMenu active={0}><p>inside</p></FilterMenu>);
    fireEvent.click(trigger());
    await waitFor(() => expect(panel()).toBeInTheDocument());
    expect(panel()).toHaveClass("z-80", "grid", "overflow-y-auto", "rounded-xl", "bg-popover", "shadow-lg");
  });
});
