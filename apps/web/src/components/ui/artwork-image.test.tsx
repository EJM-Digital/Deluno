import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ArtworkImage, ArtworkPlaceholder } from "./artwork-image";

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
  it("shows a given fallback instead of the placeholder, and still tells the caller about the failure", () => {
    const seen = vi.fn();
    const { container } = render(<ArtworkImage src="/x.jpg" alt="Heat" fallback={<span>No poster</span>} onError={seen} />);
    fireEvent.error(container.querySelector("img")!);
    expect(screen.getByText("No poster")).toBeTruthy();
    expect(seen).toHaveBeenCalledTimes(1);
  });

  it("passes the loading hint and other attributes on to the picture", () => {
    const { container } = render(<ArtworkImage src="/x.jpg" alt="Heat" loading="lazy" className="poster" data-kind="poster" />);
    const img = container.querySelector("img")!;
    expect(img.getAttribute("loading")).toBe("lazy");
    expect(img.getAttribute("alt")).toBe("Heat");
    expect(img.className).toBe("poster");
    expect(img.getAttribute("data-kind")).toBe("poster");
  });
});

describe("ArtworkPlaceholder", () => {
  it("shows the first two letters of the title, in capitals, and says the artwork is unavailable", () => {
    render(<ArtworkPlaceholder title="heat" />);
    expect(screen.getByRole("img", { name: "heat artwork unavailable" }).textContent).toBe("HE");
  });

  it("is just a blank tile, not announced, when there is no title", () => {
    const { container } = render(<ArtworkPlaceholder title="" />);
    const tile = container.querySelector("[data-artwork-placeholder]")!;
    expect(tile.getAttribute("role")).toBeNull();
    expect(tile.getAttribute("aria-label")).toBeNull();
    expect(tile.textContent).toBe("");
  });

  it("uses small type when compact and the usual size otherwise", () => {
    const { container, rerender } = render(<ArtworkPlaceholder title="Heat" compact />);
    expect(container.querySelector("span")!.className).toContain("--type-micro");
    rerender(<ArtworkPlaceholder title="Heat" />);
    expect(container.querySelector("span")!.className).toContain("text-sm");
  });
});
