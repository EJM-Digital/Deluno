import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TitleSynopsis } from "./title-synopsis";

beforeEach(() => {
  window.matchMedia = ((query: string) => ({ matches: true, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
  globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(60);
  vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(240);
});
afterEach(() => vi.restoreAllMocks());

const TEXT = "A long synopsis. ".repeat(40).trim();

describe("the synopsis panel closes (low rows)", () => {
  async function openPanel() {
    const user = userEvent.setup();
    render(<TitleSynopsis text={TEXT} />);
    const more = screen.getByRole("button", { name: "Read the full synopsis" });
    await user.click(more);
    expect(screen.getByRole("dialog", { name: "Synopsis" })).toBeInTheDocument();
    return { user, more };
  }

  it("the close button closes it and puts focus back on More", async () => {
    const { user, more } = await openPanel();

    await user.click(screen.getByRole("button", { name: "Close the synopsis" }));

    expect(screen.queryByRole("dialog", { name: "Synopsis" })).toBeNull();
    expect(more).toHaveFocus();
  });

  it("a press outside closes it, and a press inside the panel does not", async () => {
    await openPanel();

    fireEvent.mouseDown(screen.getByRole("dialog", { name: "Synopsis" }));
    expect(screen.getByRole("dialog", { name: "Synopsis" })).toBeInTheDocument();

    fireEvent.mouseDown(document.body);
    expect(screen.queryByRole("dialog", { name: "Synopsis" })).toBeNull();
  });

  it("resizing the window closes it", async () => {
    await openPanel();

    fireEvent(window, new Event("resize"));

    expect(screen.queryByRole("dialog", { name: "Synopsis" })).toBeNull();
  });
});
