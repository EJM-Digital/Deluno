import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { CardSkeleton, LibraryGridSkeleton, PosterSkeleton, RoutePending, RowSkeleton, Skeleton } from "./skeleton";

// The placeholder shapes shown while a screen loads (shell area, screen states). Each test counts what is drawn.

afterEach(() => cleanup());

describe("the loading placeholders", () => {
  it("a plain Skeleton is a pulsing block marked as a placeholder", () => {
    const { container } = render(<Skeleton className="h-3 w-10" />);
    const block = container.querySelector("[data-skeleton]")!;
    expect(block.className).toContain("h-3 w-10");
    expect(block.className).toContain("before:animate-shimmer");
  });

  it("RowSkeleton draws four rows by default and the count it is given", () => {
    const { container, unmount } = render(<RowSkeleton />);
    expect(container.querySelectorAll(".flex.items-center.gap-3.rounded-xl")).toHaveLength(4);
    unmount();

    const { container: two } = render(<RowSkeleton count={2} />);
    expect(two.querySelectorAll(".flex.items-center.gap-3.rounded-xl")).toHaveLength(2);
  });

  it("CardSkeleton draws a card with three placeholder lines", () => {
    const { container } = render(<CardSkeleton />);
    expect(container.querySelectorAll("[data-skeleton]")).toHaveLength(3);
  });

  it("PosterSkeleton draws a poster-shaped block with a title and a sub-line", () => {
    const { container } = render(<PosterSkeleton />);
    expect(container.querySelectorAll("[data-skeleton]")).toHaveLength(3);
    expect(container.querySelector("[data-skeleton].rounded-xl")).not.toBeNull();
  });

  it("LibraryGridSkeleton draws eighteen posters by default and is announced as busy", () => {
    const { container, unmount } = render(<LibraryGridSkeleton />);
    expect(container.firstElementChild!.getAttribute("aria-busy")).toBe("true");
    expect(container.firstElementChild!.children).toHaveLength(18);
    unmount();

    const { container: five } = render(<LibraryGridSkeleton count={5} />);
    expect(five.firstElementChild!.children).toHaveLength(5);
  });

  it("RoutePending is an empty busy region that takes the page height", () => {
    const { container } = render(<RoutePending />);
    const region = container.firstElementChild!;
    expect(region.getAttribute("aria-busy")).toBe("true");
    expect(region.className).toContain("min-h-[60vh]");
    expect(region.children).toHaveLength(0);
  });
});
