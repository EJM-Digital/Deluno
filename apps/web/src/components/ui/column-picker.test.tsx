import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ColumnDefinition } from "../../lib/use-column-visibility";
import { ColumnPicker } from "./column-picker";

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({ top: 20, bottom: 100, left: 0, right: 300, width: 100, height: 80, x: 0, y: 20, toJSON: () => ({}) } as DOMRect);
  vi.stubGlobal("innerWidth", 1000);
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

const columns: ColumnDefinition[] = [
  { key: "size", label: "Size", defaultVisible: true },
  { key: "added", label: "Added", defaultVisible: false }
];
const show = (over: Partial<Parameters<typeof ColumnPicker>[0]> = {}) => {
  const onToggle = vi.fn();
  const view = render(<div><ColumnPicker columns={columns} isVisible={(key) => key === "size"} onToggle={onToggle} {...over} /><p>outside</p></div>);
  return { onToggle, ...view };
};
const trigger = () => screen.getByRole("button", { name: /Columns|Options/ });
const open = () => fireEvent.click(trigger());

describe("the column picker", () => {
  it("is a closed menu button named Columns until it is asked for another name", () => {
    show();
    expect(trigger()).toHaveAttribute("aria-haspopup", "menu");
    expect(trigger()).toHaveAttribute("aria-expanded", "false");
    expect(trigger()).not.toHaveAttribute("aria-controls");
    expect(screen.queryByRole("menu")).toBeNull();
    cleanup();
    show({ label: "Options" });
    expect(trigger()).toHaveTextContent("Options");
  });

  it("opens a menu of every column with a tick on those showing, placed under the button", () => {
    show();
    open();
    const menu = screen.getByRole("menu", { name: "Columns: choose which columns to show" });
    expect(trigger()).toHaveAttribute("aria-expanded", "true");
    expect(trigger()).toHaveAttribute("aria-controls", menu.id);
    expect(menu.style.position).toBe("fixed");
    expect(menu.style.top).toBe("108px");
    expect(menu.style.right).toBe("700px");
    const items = within(menu).getAllByRole("menuitemcheckbox");
    expect(items.map((item) => [item.textContent, item.getAttribute("aria-checked")])).toEqual([["Size", "true"], ["Added", "false"]]);
    expect(items[0].querySelector("svg")).not.toBeNull();
    expect(items[1].querySelector("svg")).toBeNull();
    expect(menu.parentElement).toBe(document.body);
  });

  it("names the menu by the label it was given", () => {
    show({ label: "Options" });
    open();
    expect(screen.getByRole("menu", { name: "Options: choose which columns to show" })).toBeInTheDocument();
  });

  it("toggles a column and stays open for the next", () => {
    const { onToggle } = show();
    open();
    fireEvent.click(screen.getByRole("menuitemcheckbox", { name: "Added" }));
    expect(onToggle).toHaveBeenCalledWith("added");
    expect(screen.getByRole("menu")).toBeInTheDocument();
  });

  it("closes when the button is pressed again", () => {
    show();
    open();
    open();
    expect(screen.queryByRole("menu")).toBeNull();
    expect(trigger()).toHaveAttribute("aria-expanded", "false");
  });

  it("closes on a press outside, and not on a press inside the menu or on its button", () => {
    show();
    open();
    fireEvent.pointerDown(screen.getByRole("menuitemcheckbox", { name: "Size" }));
    expect(screen.getByRole("menu")).toBeInTheDocument();
    fireEvent.pointerDown(trigger());
    expect(screen.getByRole("menu")).toBeInTheDocument();
    fireEvent.pointerDown(screen.getByText("outside"));
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("closes on Escape, giving focus back to the button, and ignores other keys", () => {
    show();
    open();
    fireEvent.keyDown(document, { key: "Enter" });
    expect(screen.getByRole("menu")).toBeInTheDocument();
    expect(fireEvent.keyDown(document, { key: "Escape" })).toBe(false);
    expect(screen.queryByRole("menu")).toBeNull();
    expect(trigger()).toHaveFocus();
  });

  it("follows its button when the window is resized or anything scrolls, and stops once closed", () => {
    show();
    open();
    const rect = vi.mocked(HTMLElement.prototype.getBoundingClientRect);
    rect.mockReturnValue({ top: 0, bottom: 200, left: 0, right: 400, width: 100, height: 80, x: 0, y: 0, toJSON: () => ({}) } as DOMRect);
    fireEvent(window, new Event("resize"));
    expect(screen.getByRole("menu").style.top).toBe("208px");
    expect(screen.getByRole("menu").style.right).toBe("600px");
    rect.mockReturnValue({ top: 0, bottom: 300, left: 0, right: 500, width: 100, height: 80, x: 0, y: 0, toJSON: () => ({}) } as DOMRect);
    fireEvent.scroll(document.body);
    expect(screen.getByRole("menu").style.top).toBe("308px");
    open();
    const calls = rect.mock.calls.length;
    fireEvent(window, new Event("resize"));
    fireEvent.scroll(document.body);
    expect(rect.mock.calls.length).toBe(calls);
  });

  it("stops listening for keys and presses once closed, and takes an extra class", () => {
    const { container } = show({ className: "extra" });
    expect((container.firstElementChild!.firstElementChild as HTMLElement).className).toContain("extra");
    open();
    open();
    fireEvent.pointerDown(screen.getByText("outside"));
    expect(fireEvent.keyDown(document, { key: "Escape" })).toBe(true);
    expect(trigger()).toHaveAttribute("aria-expanded", "false");
  });
});
