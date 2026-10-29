import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

let slot: HTMLElement | null = null;
vi.mock("../ui/header-tabs", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../ui/header-tabs")>()),
  useHeaderSlot: () => slot
}));
const { TransfersViewChips } = await import("./transfers-view-chips");

beforeEach(() => { slot = null; });

describe("the view chips of Transfers (#1426)", () => {
  it("sit in a row of their own when the header has no room for them", () => {
    const { container } = render(<MemoryRouter><TransfersViewChips /></MemoryRouter>);
    const wrapper = container.firstElementChild as HTMLElement;
    expect(wrapper.className).toContain("flex");
    expect(wrapper.querySelector('[role="radiogroup"]')).not.toBeNull();
  });

  it("are the bare group, with no wrapper, when a toolbar lays them out", () => {
    const { container } = render(<MemoryRouter><TransfersViewChips bare /></MemoryRouter>);
    expect(container.firstElementChild).toHaveAttribute("role", "radiogroup");
  });

  it("go to the header's title line when it has the slot, and nowhere else", () => {
    slot = document.createElement("div");
    document.body.appendChild(slot);
    const { container } = render(<MemoryRouter><TransfersViewChips /></MemoryRouter>);
    expect(container.firstElementChild).toBeNull();
    expect(slot.querySelector('[role="radiogroup"]')).not.toBeNull();
    slot.remove();
  });

  it("show a count beside a view only when one is given, and mark the view in the address", () => {
    render(<MemoryRouter initialEntries={["/queue?view=history"]}><TransfersViewChips bare counts={{ live: 4 }} /></MemoryRouter>);
    expect(screen.getByRole("radio", { name: "Live downloads 4" })).toHaveAttribute("aria-checked", "false");
    expect(screen.getByRole("radio", { name: "Hand-offs" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "History & events" })).toHaveAttribute("aria-checked", "true");
    fireEvent.click(screen.getByRole("radio", { name: "Hand-offs" }));
    expect(screen.getByRole("radio", { name: "Hand-offs" })).toHaveAttribute("aria-checked", "true");
  });
});
