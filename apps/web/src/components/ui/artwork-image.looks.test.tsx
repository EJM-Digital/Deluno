import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ArtworkImage, ArtworkPlaceholder } from "./artwork-image";

describe("the artwork tile's look", () => {
  it("is a soft tile with centred type, and takes the caller's class", () => {
    const { container } = render(<ArtworkPlaceholder title="Heat" className="h-10 w-10" />);
    const tile = container.querySelector("[data-artwork-placeholder]")!;
    expect(tile).toHaveClass("flex", "items-center", "justify-center", "text-center", "text-muted-foreground", "from-surface-2", "to-surface-3", "h-10", "w-10");
  });

  it("sets its initials in the display face", () => {
    const { container } = render(<ArtworkPlaceholder title="Heat" />);
    expect(container.querySelector("span")).toHaveClass("px-2", "font-display", "font-semibold", "tracking-tight");
  });

  it("has no label of its own when the picture was given no description", () => {
    const { container } = render(<ArtworkImage src={null} />);
    const tile = container.querySelector("[data-artwork-placeholder]")!;
    expect(tile.getAttribute("role")).toBeNull();
    expect(tile.textContent).toBe("");
  });

  it("gives a picture with no description an empty one, so a screen reader skips it", () => {
    const { container } = render(<ArtworkImage src="/x.jpg" />);
    expect(container.querySelector("img")!.getAttribute("alt")).toBe("");
  });

  it("tries an address again when it comes back after another one in between", () => {
    const { container, rerender } = render(<ArtworkImage src="/a.jpg" alt="Heat" />);
    fireEvent.error(container.querySelector("img")!);
    expect(container.querySelector("img")).toBeNull();

    rerender(<ArtworkImage src="/b.jpg" alt="Heat" />);
    expect(container.querySelector("img")?.getAttribute("src")).toBe("/b.jpg");

    rerender(<ArtworkImage src="/a.jpg" alt="Heat" />);
    expect(container.querySelector("img")?.getAttribute("src")).toBe("/a.jpg");
    expect(screen.queryByLabelText("Heat artwork unavailable")).toBeNull();
  });
});
