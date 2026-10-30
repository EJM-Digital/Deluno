import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ErrorState } from "./error-state";
import { ErrorArt, OfflineArt } from "./illustrations";

// The error card's own words, variants and buttons (shell area, screen states). Each assertion is on what a person sees.

afterEach(() => cleanup());

describe("the ErrorState card", () => {
  it("says Something went wrong with the plain server-unreachable message by default", () => {
    render(<ErrorState />);
    expect(screen.getByRole("heading", { name: "Something went wrong" })).toBeTruthy();
    expect(screen.getByText("Deluno couldn't load that area. The server may be unreachable.")).toBeTruthy();
  });

  it("the offline variant draws the offline tower, not the broken-film art", () => {
    const { container, unmount } = render(<ErrorState variant="offline" />);
    const drawn = container.querySelector(".relative.mb-4")!.innerHTML;
    unmount();
    const { container: offline } = render(<OfflineArt size={150} />);
    expect(drawn).toBe(offline.innerHTML);
    const { container: broken } = render(<ErrorArt size={150} />);
    expect(broken.innerHTML).not.toBe(drawn);
  });

  it("the compact size uses the smaller padding and the smaller art", () => {
    render(<ErrorState size="compact" />);
    const card = screen.getByRole("alert");
    expect(card.className).toContain("py-10");
    expect(card.className).not.toContain("py-14");
  });

  it("Try again and Report issue call their handlers, and neither shows without one", () => {
    const onRetry = vi.fn();
    const onReport = vi.fn();
    const { unmount } = render(<ErrorState onRetry={onRetry} onReport={onReport} />);
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    fireEvent.click(screen.getByRole("button", { name: "Report issue" }));
    expect(onRetry).toHaveBeenCalledTimes(1);
    expect(onReport).toHaveBeenCalledTimes(1);
    unmount();

    render(<ErrorState />);
    expect(screen.queryByRole("button", { name: "Try again" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Report issue" })).toBeNull();
  });
});

describe("the ErrorState card, other shapes", () => {
  it("shows the error code under the message only when one is given", () => {
    const { unmount } = render(<ErrorState code="HTTP 503" />);
    expect(screen.getByText("HTTP 503")).toBeTruthy();
    unmount();
    render(<ErrorState />);
    expect(screen.queryByText("HTTP 503")).toBeNull();
  });

  it("draws the offline art at the smaller size in a compact card", () => {
    const { container } = render(<ErrorState variant="offline" size="compact" />);
    const { container: small } = render(<OfflineArt size={112} />);
    expect(container.querySelector(".relative.mb-4")!.innerHTML).toBe(small.innerHTML);
  });
});
