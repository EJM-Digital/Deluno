import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TitleSynopsis } from "./title-synopsis";

/** jsdom has no layout: say the header is wide and the text taller than its box, so the synopsis clamps and offers More. */
beforeEach(() => {
  window.matchMedia = ((query: string) => ({ matches: true, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
  globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(60);
  vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(240);
});
afterEach(() => vi.restoreAllMocks());

const TEXT = "A long synopsis. ".repeat(40).trim();

describe("the synopsis in a title's header", () => {
  it("More opens the full text in a floating panel", async () => {
    const user = userEvent.setup();
    render(<TitleSynopsis text={TEXT} />);

    await user.click(screen.getByRole("button", { name: "Read the full synopsis" }));

    const panel = screen.getByRole("dialog", { name: "Synopsis" });
    expect(panel).toHaveTextContent(TEXT);
    expect(screen.getByRole("button", { name: "Read the full synopsis" })).toHaveAttribute("aria-expanded", "true");
  });

  it("Escape closes the panel and puts focus back on More", async () => {
    const user = userEvent.setup();
    render(<TitleSynopsis text={TEXT} />);
    const more = screen.getByRole("button", { name: "Read the full synopsis" });
    await user.click(more);
    expect(screen.getByRole("dialog", { name: "Synopsis" })).toBeInTheDocument();

    fireEvent.keyDown(document.body, { key: "Escape" });

    expect(screen.queryByRole("dialog", { name: "Synopsis" })).toBeNull();
    expect(more).toHaveFocus();
  });

  it("offers no More when the whole text fits", () => {
    vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(40);
    render(<TitleSynopsis text="Short." />);

    expect(screen.queryByRole("button", { name: "Read the full synopsis" })).toBeNull();
  });
});
