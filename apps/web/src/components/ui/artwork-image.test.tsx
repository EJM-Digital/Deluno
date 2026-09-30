import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ArtworkImage } from "./artwork-image";

describe("ArtworkImage", () => {
  it("shows the placeholder, not a broken image, when the file fails to load", () => {
    const { container } = render(<ArtworkImage src="/api/metadata/artwork/abc.jpg" alt="Heat" className="h-10 w-10" />);
    const img = container.querySelector("img")!;
    fireEvent.error(img);
    expect(container.querySelector("img")).toBeNull();
    expect(screen.getByLabelText("Heat artwork unavailable")).toBeTruthy();
  });

  it("shows the placeholder when there is no address", () => {
    const { container } = render(<ArtworkImage src={null} alt="Heat" />);
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector("[data-artwork-placeholder]")).not.toBeNull();
  });

  it("renders nothing for decorative art when fallback is null", () => {
    const { container } = render(<ArtworkImage src="/x.jpg" fallback={null} />);
    fireEvent.error(container.querySelector("img")!);
    expect(container.innerHTML).toBe("");
  });

  it("tries again when the address changes", () => {
    const { container, rerender } = render(<ArtworkImage src="/a.jpg" alt="Heat" />);
    fireEvent.error(container.querySelector("img")!);
    rerender(<ArtworkImage src="/b.jpg" alt="Heat" />);
    expect(container.querySelector("img")?.getAttribute("src")).toBe("/b.jpg");
  });
});
