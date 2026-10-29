import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useNavigate } from "react-router-dom";
import { HeaderTabs } from "./header-tabs";

/** Six tabs of 80px, the More button 60px, a room of 300px: two show, four fold (the same measuring the other header-tabs tests use). */
function measureWith(room: number) {
  return vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    const text = this.textContent ?? "";
    const width = this.className.includes("relative w-full") ? room : text === "More" ? 60 : this.tagName === "SPAN" && text.length <= 5 ? 80 : 0;
    return { width, height: 14, top: 0, left: 0, right: width, bottom: 14, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
  });
}

afterEach(() => vi.restoreAllMocks());

const links = ["One", "Two", "Three", "Four", "Five", "Six"].map((label, index) => ({ key: label, label, selected: index === 0, to: `/${label.toLowerCase()}` }));

function GoTo() {
  const navigate = useNavigate();
  return <button type="button" onClick={() => navigate("/elsewhere")}>Go elsewhere</button>;
}

describe("the More menu of a header's tabs (low rows)", () => {
  it("a folded tab that is a button switches the tab through onSelect and closes the menu", async () => {
    measureWith(300);
    const onSelect = vi.fn();
    const items = ["One", "Two", "Three", "Four", "Five", "Six"].map((label, index) => ({
      key: label, label, selected: index === 0, buttonId: `tab-${label}`, controls: "panel", onSelect: label === "Four" ? onSelect : vi.fn()
    }));
    render(<MemoryRouter><HeaderTabs items={items} label="Dashboard sections" role="tablist" /></MemoryRouter>);

    const tablist = screen.getByRole("tablist", { name: "Dashboard sections" });
    expect(within(tablist).getAllByRole("tab").map((tab) => tab.textContent)).toEqual(["One", "Two"]);
    expect(within(tablist).getAllByRole("tab")[0]).toHaveAttribute("aria-controls", "panel");

    await userEvent.click(within(tablist).getByRole("button", { name: "More" }));
    await userEvent.click(within(await screen.findByRole("menu")).getByRole("menuitem", { name: "Four" }));

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("ArrowDown on More opens it, the arrows, Home and End move between the items, and Tab closes it", async () => {
    measureWith(300);
    render(<MemoryRouter><HeaderTabs items={links} label="Sections" /></MemoryRouter>);
    const more = screen.getByRole("button", { name: "More" });

    more.focus();
    fireEvent.keyDown(more, { key: "ArrowDown" });
    const menu = await screen.findByRole("menu");
    const entries = within(menu).getAllByRole("menuitem");
    expect(entries[0]).toHaveFocus();

    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(entries[1]).toHaveFocus();
    fireEvent.keyDown(menu, { key: "ArrowUp" });
    expect(entries[0]).toHaveFocus();
    fireEvent.keyDown(menu, { key: "ArrowUp" });
    expect(entries[entries.length - 1]).toHaveFocus();
    fireEvent.keyDown(menu, { key: "Home" });
    expect(entries[0]).toHaveFocus();
    fireEvent.keyDown(menu, { key: "End" });
    expect(entries[entries.length - 1]).toHaveFocus();

    fireEvent.keyDown(menu, { key: "Tab" });
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("a press outside closes it, a press inside does not", async () => {
    measureWith(300);
    render(<MemoryRouter><HeaderTabs items={links} label="Sections" /></MemoryRouter>);
    await userEvent.click(screen.getByRole("button", { name: "More" }));
    const menu = await screen.findByRole("menu");

    fireEvent.pointerDown(menu);
    expect(screen.getByRole("menu")).toBeInTheDocument();

    fireEvent.pointerDown(document.body);
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("a change of route closes it", async () => {
    measureWith(300);
    render(<MemoryRouter initialEntries={["/one"]}><HeaderTabs items={links} label="Sections" /><GoTo /></MemoryRouter>);
    await userEvent.click(screen.getByRole("button", { name: "More" }));
    expect(await screen.findByRole("menu")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Go elsewhere" }));

    expect(screen.queryByRole("menu")).toBeNull();
  });
});
