import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { fitHeaderTabs, HeaderTabs } from "./header-tabs";

// Six tabs of 80px with 18px gaps: 6*80 + 5*18 = 570. The More button is 60px wide.
const widths = [80, 80, 80, 80, 80, 80];

describe("fitHeaderTabs", () => {
  it("shows every tab, and no More, when they all fit", () => {
    expect(fitHeaderTabs(widths, 570, 60, 0)).toEqual({ visible: [0, 1, 2, 3, 4, 5], folded: [] });
  });

  it("folds from the end when they do not fit, leaving room for More", () => {
    // 569 room: More + gap take 78, 491 left; 4 tabs need 4*80 + 3*18 = 374, 5 need 472 (fits), 6 would not.
    expect(fitHeaderTabs(widths, 569, 60, 0)).toEqual({ visible: [0, 1, 2, 3, 4], folded: [5] });
    // 300 room: 222 left; 2 tabs need 178, 3 need 276.
    expect(fitHeaderTabs(widths, 300, 60, 1)).toEqual({ visible: [0, 1], folded: [2, 3, 4, 5] });
  });

  it("keeps the chosen tab: it takes the last visible place and the tab it displaced folds", () => {
    const result = fitHeaderTabs(widths, 300, 60, 5);
    expect(result.visible).toEqual([0, 5]);
    expect(result.folded).toEqual([1, 2, 3, 4]);
  });

  it("never folds the chosen tab, even when only it fits", () => {
    const result = fitHeaderTabs(widths, 150, 60, 3);
    expect(result.visible).toEqual([3]);
    expect(result.folded).toEqual([0, 1, 2, 4, 5]);
  });

  it("folds everything but a chosen tab that does not exist in the list", () => {
    expect(fitHeaderTabs(widths, 100, 60, -1)).toEqual({ visible: [], folded: [0, 1, 2, 3, 4, 5] });
  });

  it("uses each tab's own width", () => {
    const uneven = [40, 200, 40];
    // Everything: 40 + 200 + 40 + 36 = 316.
    expect(fitHeaderTabs(uneven, 316, 50, 0).folded).toEqual([]);
    // 200 room: 132 left after More; 40 fits, 40+18+200 does not.
    expect(fitHeaderTabs(uneven, 200, 50, 0)).toEqual({ visible: [0], folded: [1, 2] });
  });
});

describe("HeaderTabs", () => {
  const items = ["One", "Two", "Three", "Four", "Five", "Six"].map((label, index) => ({
    key: label,
    label,
    selected: index === 0,
    to: `/${label.toLowerCase()}`
  }));

  function measureWith(room: number) {
    return vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      const text = this.textContent ?? "";
      const width = this.className.includes("relative w-full") ? room : text === "More" ? 60 : this.tagName === "SPAN" && text.length <= 5 ? 80 : 0;
      return { width, height: 14, top: 0, left: 0, right: width, bottom: 14, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
    });
  }

  afterEach(() => vi.restoreAllMocks());

  it("folds what does not fit into a More menu that lists the rest as links, and never marks More chosen", async () => {
    measureWith(300);
    render(
      <MemoryRouter initialEntries={["/one"]}>
        <HeaderTabs items={items} label="Sections" />
      </MemoryRouter>
    );
    const nav = screen.getByRole("navigation", { name: "Sections" });
    expect(within(nav).getAllByRole("link").map((link) => link.textContent)).toEqual(["One", "Two"]);
    const more = within(nav).getByRole("button", { name: "More" });
    expect(more).toHaveAttribute("aria-haspopup", "menu");
    expect(more).toHaveAttribute("aria-expanded", "false");
    expect(more.className).not.toMatch(/text-primary/);

    await userEvent.click(more);
    expect(more).toHaveAttribute("aria-expanded", "true");
    const menu = await screen.findByRole("menu");
    expect(within(menu).getAllByRole("menuitem").map((item) => item.textContent)).toEqual(["Three", "Four", "Five", "Six"]);
    expect(within(menu).getAllByRole("menuitem")[0]).toHaveAttribute("href", "/three");

    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("menu")).toBeNull();
    expect(more).toHaveFocus();
  });

  it("shows no More button when everything fits", () => {
    measureWith(900);
    render(
      <MemoryRouter initialEntries={["/one"]}>
        <HeaderTabs items={items} label="Sections" />
      </MemoryRouter>
    );
    const nav = screen.getByRole("navigation", { name: "Sections" });
    expect(within(nav).getAllByRole("link")).toHaveLength(6);
    expect(within(nav).queryByRole("button", { name: "More" })).toBeNull();
  });

  it("keeps the chosen tab visible when it would have folded", () => {
    measureWith(300);
    render(
      <MemoryRouter initialEntries={["/six"]}>
        <HeaderTabs items={items.map((item) => ({ ...item, selected: item.label === "Six" }))} label="Sections" />
      </MemoryRouter>
    );
    const nav = screen.getByRole("navigation", { name: "Sections" });
    expect(within(nav).getAllByRole("link").map((link) => link.textContent)).toEqual(["One", "Six"]);
  });
});
