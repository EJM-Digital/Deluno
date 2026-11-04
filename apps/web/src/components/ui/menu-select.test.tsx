import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MenuSelect, type MenuSelectOption } from "./menu-select";

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({ top: 20, bottom: 100, left: 40, right: 240, width: 200, height: 80, x: 40, y: 20, toJSON: () => ({}) } as DOMRect);
  vi.stubGlobal("innerWidth", 1000);
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

const options: MenuSelectOption[] = [
  { value: "a", label: "Alpha", hint: "The first" },
  { value: "b", label: "Beta" },
  { value: "c", label: "Gamma" }
];
const show = (over: Partial<Parameters<typeof MenuSelect>[0]> = {}) => {
  const onChange = vi.fn();
  const view = render(<div><MenuSelect value="b" options={options} onChange={onChange} label="Sort by" {...over} /><p>outside</p></div>);
  return { onChange, ...view };
};
const trigger = () => screen.getByRole("combobox", { name: "Sort by" });
const open = () => fireEvent.click(trigger());
const option = (name: RegExp | string) => screen.getByRole("option", { name });

describe("the menu select", () => {
  it("shows the chosen option's label on a closed combobox named by its label", () => {
    show();
    expect(trigger()).toHaveTextContent("Beta");
    expect(trigger()).toHaveAttribute("aria-haspopup", "listbox");
    expect(trigger()).toHaveAttribute("aria-expanded", "false");
    expect(trigger()).toHaveAttribute("data-open", "false");
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("shows the first option when the value is not among them, and nothing when there are none", () => {
    show({ value: "zzz" });
    expect(trigger()).toHaveTextContent("Alpha");
    cleanup();
    show({ options: [] });
    expect(trigger().querySelector("span")).toHaveTextContent("");
  });

  it("shows what comes before the label, and takes classes for itself and its button", () => {
    const { container } = show({ leading: <i data-testid="dot" />, className: "wrap", triggerClassName: "btn" });
    expect(screen.getByTestId("dot")).toBeInTheDocument();
    expect((container.firstElementChild!.firstElementChild as HTMLElement).className).toContain("wrap");
    expect(trigger().className).toContain("btn");
    expect(trigger().firstElementChild).toBe(screen.getByTestId("dot"));
  });

  it("opens a listbox under the trigger, as wide as it, with every option and the chosen one marked and focused", () => {
    show();
    open();
    const list = screen.getByRole("listbox", { name: "Sort by" });
    expect(trigger()).toHaveAttribute("aria-expanded", "true");
    expect(trigger()).toHaveAttribute("data-open", "true");
    expect(trigger()).toHaveAttribute("aria-controls", list.id);
    expect(list.parentElement).toBe(document.body);
    expect(list.style.position).toBe("fixed");
    expect(list.style.top).toBe("108px");
    expect(list.style.left).toBe("40px");
    expect(list.style.minWidth).toBe("200px");
    expect(list.style.right).toBe("");
    expect(within(list).getAllByRole("option").map((each) => [each.textContent, each.getAttribute("aria-selected")])).toEqual([["AlphaThe first", "false"], ["Beta", "true"], ["Gamma", "false"]]);
    expect(option("Beta")).toHaveFocus();
    expect(option("Beta").className).toContain("ring-primary/20");
    expect(option("AlphaThe first").className).toContain("text-muted-foreground");
    expect(within(option("AlphaThe first")).getByText("The first")).toBeInTheDocument();
  });

  it("opens against the right edge when asked, and at the width it is given", () => {
    show({ align: "end", menuWidth: "w-72" });
    open();
    const list = screen.getByRole("listbox");
    expect(list.style.right).toBe("760px");
    expect(list.style.left).toBe("");
    expect(list.className).toContain("w-72");
  });

  it("chooses an option, closes, and gives focus back to the trigger", () => {
    const { onChange } = show();
    open();
    fireEvent.click(option("Gamma"));
    expect(onChange).toHaveBeenCalledWith("c");
    expect(screen.queryByRole("listbox")).toBeNull();
    expect(trigger()).toHaveFocus();
  });

  it("opens from the trigger with the down or up arrow, and not with other keys", () => {
    show();
    expect(fireEvent.keyDown(trigger(), { key: "ArrowDown" })).toBe(false);
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    cleanup();
    show();
    expect(fireEvent.keyDown(trigger(), { key: "ArrowUp" })).toBe(false);
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    cleanup();
    show();
    expect(fireEvent.keyDown(trigger(), { key: "Enter" })).toBe(true);
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("closes when the trigger is pressed again", () => {
    show();
    open();
    open();
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("moves with the arrow keys, round at both ends, and with Home and End", () => {
    show({ value: "a" });
    open();
    expect(option("AlphaThe first")).toHaveFocus();
    expect(fireEvent.keyDown(option("AlphaThe first"), { key: "ArrowDown" })).toBe(false);
    expect(option("Beta")).toHaveFocus();
    fireEvent.keyDown(option("Beta"), { key: "ArrowDown" });
    expect(option("Gamma")).toHaveFocus();
    fireEvent.keyDown(option("Gamma"), { key: "ArrowDown" });
    expect(option("AlphaThe first")).toHaveFocus();
    expect(fireEvent.keyDown(option("AlphaThe first"), { key: "ArrowUp" })).toBe(false);
    expect(option("Gamma")).toHaveFocus();
    fireEvent.keyDown(option("Gamma"), { key: "ArrowUp" });
    expect(option("Beta")).toHaveFocus();
    expect(fireEvent.keyDown(option("Beta"), { key: "Home" })).toBe(false);
    expect(option("AlphaThe first")).toHaveFocus();
    expect(fireEvent.keyDown(option("AlphaThe first"), { key: "End" })).toBe(false);
    expect(option("Gamma")).toHaveFocus();
  });

  it("closes on Escape with focus back on the trigger, and on Tab without taking it", () => {
    show();
    open();
    expect(fireEvent.keyDown(option("Beta"), { key: "Escape" })).toBe(false);
    expect(screen.queryByRole("listbox")).toBeNull();
    expect(trigger()).toHaveFocus();
    open();
    trigger().blur();
    expect(fireEvent.keyDown(option("Beta"), { key: "Tab" })).toBe(true);
    expect(screen.queryByRole("listbox")).toBeNull();
    expect(trigger()).not.toHaveFocus();
  });

  it("ignores other keys inside the list", () => {
    const { onChange } = show();
    open();
    expect(fireEvent.keyDown(option("Beta"), { key: "x" })).toBe(true);
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("closes on a press outside, and not on a press inside the list or on its trigger", () => {
    show();
    open();
    fireEvent.pointerDown(option("Gamma"));
    fireEvent.pointerDown(trigger());
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    fireEvent.pointerDown(screen.getByText("outside"));
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("follows its trigger on resize and scroll while open, and stops once closed", () => {
    show();
    open();
    const rect = vi.mocked(HTMLElement.prototype.getBoundingClientRect);
    rect.mockReturnValue({ top: 0, bottom: 200, left: 60, right: 460, width: 400, height: 80, x: 60, y: 0, toJSON: () => ({}) } as DOMRect);
    fireEvent(window, new Event("resize"));
    expect(screen.getByRole("listbox").style.top).toBe("208px");
    expect(screen.getByRole("listbox").style.left).toBe("60px");
    expect(screen.getByRole("listbox").style.minWidth).toBe("400px");
    fireEvent.scroll(document.body);
    expect(screen.getByRole("listbox").style.top).toBe("208px");
    open();
    const calls = rect.mock.calls.length;
    fireEvent(window, new Event("resize"));
    fireEvent.scroll(document.body);
    expect(rect.mock.calls.length).toBe(calls);
  });

  it("turns its chevron over while open", () => {
    show();
    const chevron = () => trigger().lastElementChild!;
    expect(chevron()).not.toHaveClass("rotate-180");
    open();
    expect(chevron()).toHaveClass("rotate-180");
  });

  it("stops listening for presses once closed", () => {
    show();
    open();
    open();
    fireEvent.pointerDown(screen.getByText("outside"));
    expect(screen.queryByRole("listbox")).toBeNull();
  });
});
