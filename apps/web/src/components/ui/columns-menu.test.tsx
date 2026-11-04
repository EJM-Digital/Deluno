import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ColumnsAnnouncements, ColumnsButton, useSharedColumnsHint, type ColumnsControls } from "./columns-menu";

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({ top: 20, bottom: 100, left: 0, right: 300, width: 100, height: 80, x: 0, y: 20, toJSON: () => ({}) } as DOMRect);
  vi.stubGlobal("innerWidth", 1000);
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

const show = (onReset = vi.fn(), className?: string) => {
  const view = render(<div><ColumnsButton onReset={onReset} className={className} /><p>outside</p></div>);
  return { onReset, ...view };
};
const trigger = () => screen.getByRole("button", { name: "Columns" });

describe("the Columns button", () => {
  it("is a closed menu button, shown from medium screens up", () => {
    const { container } = show(vi.fn(), "extra");
    expect(trigger()).toHaveAttribute("aria-haspopup", "menu");
    expect(trigger()).toHaveAttribute("aria-expanded", "false");
    expect(trigger()).not.toHaveAttribute("aria-controls");
    const root = container.firstElementChild!.firstElementChild as HTMLElement;
    expect(root.className).toContain("hidden shrink-0 md:block");
    expect(root.className).toContain("extra");
    expect(screen.queryByRole("menu")).toBeNull();
    expect(trigger().className).not.toContain("bg-surface-2 text-foreground");
  });

  it("opens a menu under the button with one thing in it, Reset columns, which has focus", () => {
    show();
    fireEvent.click(trigger());
    const menu = screen.getByRole("menu", { name: "Columns" });
    expect(trigger()).toHaveAttribute("aria-expanded", "true");
    expect(trigger()).toHaveAttribute("aria-controls", menu.id);
    expect(trigger().className).toContain("bg-surface-2 text-foreground");
    expect(menu.style.position).toBe("fixed");
    expect(menu.style.top).toBe("106px");
    expect(menu.style.right).toBe("700px");
    expect(screen.getAllByRole("menuitem").map((item) => item.textContent)).toEqual(["Reset columns"]);
    expect(screen.getByRole("menuitem", { name: "Reset columns" })).toHaveFocus();
    expect(menu.parentElement).toBe(document.body);
  });

  it("resets the columns, closes, and gives focus back to the button", () => {
    const { onReset } = show();
    fireEvent.click(trigger());
    fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));
    expect(onReset).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menu")).toBeNull();
    expect(trigger()).toHaveFocus();
  });

  it("closes when pressed again, and on a press outside, but not on a press inside it", () => {
    show();
    fireEvent.click(trigger());
    fireEvent.pointerDown(screen.getByRole("menu"));
    fireEvent.pointerDown(trigger());
    expect(screen.getByRole("menu")).toBeInTheDocument();
    fireEvent.pointerDown(screen.getByText("outside"));
    expect(screen.queryByRole("menu")).toBeNull();
    fireEvent.click(trigger());
    fireEvent.click(trigger());
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("closes on Escape, with focus back on the button, and ignores other keys", () => {
    show();
    fireEvent.click(trigger());
    fireEvent.keyDown(document, { key: "ArrowDown" });
    expect(screen.getByRole("menu")).toBeInTheDocument();
    expect(fireEvent.keyDown(document, { key: "Escape" })).toBe(false);
    expect(screen.queryByRole("menu")).toBeNull();
    expect(trigger()).toHaveFocus();
  });

  it("follows its button on resize and scroll while open, and stops once closed", () => {
    show();
    fireEvent.click(trigger());
    const rect = vi.mocked(HTMLElement.prototype.getBoundingClientRect);
    rect.mockReturnValue({ top: 0, bottom: 200, left: 0, right: 400, width: 100, height: 80, x: 0, y: 0, toJSON: () => ({}) } as DOMRect);
    fireEvent(window, new Event("resize"));
    expect(screen.getByRole("menu").style.top).toBe("206px");
    expect(screen.getByRole("menu").style.right).toBe("600px");
    rect.mockReturnValue({ top: 0, bottom: 300, left: 0, right: 500, width: 100, height: 80, x: 0, y: 0, toJSON: () => ({}) } as DOMRect);
    fireEvent.scroll(document.body);
    expect(screen.getByRole("menu").style.top).toBe("306px");
    fireEvent.click(trigger());
    const calls = rect.mock.calls.length;
    fireEvent(window, new Event("resize"));
    fireEvent.scroll(document.body);
    expect(rect.mock.calls.length).toBe(calls);
  });

  it("stops listening for presses and keys once closed", () => {
    show();
    fireEvent.click(trigger());
    fireEvent.click(trigger());
    expect(fireEvent.keyDown(document, { key: "Escape" })).toBe(true);
  });
});

function Hint({ sortable }: { sortable: boolean }) {
  const id = useSharedColumnsHint(sortable);
  return <output aria-label="hint id">{id}</output>;
}

describe("the one hint about moving columns", () => {
  it("is a hidden line on the page that every movable heading can point at", () => {
    render(<Hint sortable={false} />);
    const hint = document.getElementById("deluno-columns-hint")!;
    expect(screen.getByLabelText("hint id")).toHaveTextContent("deluno-columns-hint");
    expect(hint.textContent).toBe("Drag a heading to move its column, or press Alt with the left or right arrow on it.");
    expect(hint.className).toBe("sr-only");
    expect(hint.tagName).toBe("P");
    expect(hint.parentElement).toBe(document.body);
  });

  it("says how to sort too when the headings sort", () => {
    render(<Hint sortable />);
    expect(screen.getByLabelText("hint id")).toHaveTextContent("deluno-columns-hint-sort");
    expect(document.getElementById("deluno-columns-hint-sort")!.textContent).toBe("Drag a heading to move its column, or press Alt with the left or right arrow on it. Click a heading to sort by it, and click again to reverse.");
    expect(document.getElementById("deluno-columns-hint")).toBeNull();
  });

  it("is made once however many tables use it, and removed with the last", () => {
    const first = render(<Hint sortable={false} />);
    const second = render(<Hint sortable={false} />);
    expect(document.querySelectorAll("#deluno-columns-hint")).toHaveLength(1);
    first.unmount();
    expect(document.getElementById("deluno-columns-hint")).not.toBeNull();
    second.unmount();
    expect(document.getElementById("deluno-columns-hint")).toBeNull();
  });

  it("is made again for a table that comes later", () => {
    render(<Hint sortable={false} />).unmount();
    render(<Hint sortable={false} />);
    expect(document.getElementById("deluno-columns-hint")).not.toBeNull();
  });

  it("does not make a second line when one is already there, and leaves nothing behind when it was taken away", () => {
    const existing = document.createElement("p");
    existing.id = "deluno-columns-hint";
    existing.textContent = "already here";
    document.body.appendChild(existing);
    const view = render(<Hint sortable={false} />);
    expect(document.querySelectorAll("#deluno-columns-hint")).toHaveLength(1);
    expect(document.getElementById("deluno-columns-hint")!.textContent).toBe("already here");
    existing.remove();
    expect(() => view.unmount()).not.toThrow();
  });

  it("changes line when the headings start or stop sorting", () => {
    const view = render(<Hint sortable={false} />);
    view.rerender(<Hint sortable />);
    expect(document.getElementById("deluno-columns-hint")).toBeNull();
    expect(document.getElementById("deluno-columns-hint-sort")).not.toBeNull();
  });
});

describe("what a screen reader is told after a move", () => {
  const controls = (...announcements: string[]) => announcements.map((announcement) => ({ hintId: "h", sortable: true, announcement, reset: () => undefined })) as ColumnsControls[];

  it("joins the announcements of every table that has one, politely", () => {
    render(<ColumnsAnnouncements controls={controls("Size moved left.", "", "Added moved right.")} />);
    const status = screen.getByRole("status");
    expect(status).toHaveTextContent("Size moved left. Added moved right.");
    expect(status).toHaveAttribute("aria-live", "polite");
    expect(status).toHaveClass("sr-only");
  });

  it("says nothing when no table has anything to say", () => {
    render(<ColumnsAnnouncements controls={controls("", "")} />);
    expect(screen.getByRole("status")).toBeEmptyDOMElement();
  });
});
