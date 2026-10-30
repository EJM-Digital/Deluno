import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { TitleSynopsis } from "./title-synopsis";

/** The synopsis box at different widths and heights, and on a browser without the usual layout helpers. */

function layout({ wide = true, client = 60, scroll = 240, resizeObserver = true, mediaEvents = true } = {}) {
  window.matchMedia = ((query: string) => ({
    matches: wide, media: query,
    ...(mediaEvents ? { addEventListener() {}, removeEventListener() {} } : {}),
    addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false
  })) as unknown as typeof window.matchMedia;
  if (resizeObserver) globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  else (globalThis as { ResizeObserver?: unknown }).ResizeObserver = undefined;
  vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(client);
  vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(scroll);
}

afterEach(() => vi.restoreAllMocks());

const TEXT = "A long synopsis. ".repeat(40).trim();
const more = () => screen.queryByRole("button", { name: "Read the full synopsis" });

describe("the synopsis layout", () => {
  it("shows the whole text with no More under the wide breakpoint", () => {
    layout({ wide: false });
    render(<TitleSynopsis text={TEXT} />);
    expect(more()).toBeNull();
    expect(screen.getByText(TEXT).style.webkitLineClamp).toBe("");
  });

  it("puts More on the last line when there is hardly room for a line and the button", () => {
    layout({ client: 30, scroll: 240 });
    render(<TitleSynopsis text={TEXT} />);
    expect(more()!.className).toContain("absolute");
  });

  it("puts More under the text when there is room for both", () => {
    layout({ client: 120, scroll: 400 });
    render(<TitleSynopsis text={TEXT} />);
    expect(more()!.className).not.toContain("absolute");
  });

  it("works on a browser with no resize observer and a media query without change events", () => {
    layout({ resizeObserver: false, mediaEvents: false });
    render(<TitleSynopsis text={TEXT} />);
    expect(more()).not.toBeNull();
  });

  it("works with media query events, and tidies them away on leaving", () => {
    const add = vi.fn();
    const remove = vi.fn();
    layout();
    window.matchMedia = (() => ({ matches: true, addEventListener: add, removeEventListener: remove })) as unknown as typeof window.matchMedia;
    render(<TitleSynopsis text={TEXT} />).unmount();
    expect(add).toHaveBeenCalledWith("change", expect.any(Function));
    expect(remove).toHaveBeenCalledWith("change", expect.any(Function));
  });

  it("measures again when the text changes", () => {
    layout({ scroll: 40 });
    const { rerender } = render(<TitleSynopsis text="Short." />);
    expect(more()).toBeNull();
    vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(240);
    rerender(<TitleSynopsis text={TEXT} />);
    expect(more()).not.toBeNull();
  });

  it("other keys do not close the panel, and a press on More's own box does not either", async () => {
    layout();
    const user = userEvent.setup();
    render(<TitleSynopsis text={TEXT} />);
    await user.click(more()!);
    fireEvent.keyDown(document.body, { key: "a" });
    expect(screen.getByRole("dialog", { name: "Synopsis" })).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByTestId("title-synopsis"));
    expect(screen.getByRole("dialog", { name: "Synopsis" })).toBeInTheDocument();
  });
});
