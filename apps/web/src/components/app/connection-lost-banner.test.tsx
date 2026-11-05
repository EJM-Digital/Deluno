import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const live = vi.hoisted(() => ({ status: "connected" as string }));
vi.mock("../../lib/use-signalr", () => ({ useSignalRStatus: () => live.status }));

const { ConnectionLostBanner } = await import("./connection-lost-banner");

beforeEach(() => { vi.useFakeTimers(); live.status = "connected"; });
afterEach(() => { vi.useRealTimers(); });

describe("the connection-lost notice (#1497)", () => {
  it("says nothing while connected, and while the very first connection is made", () => {
    const { rerender, container } = render(<ConnectionLostBanner />);
    expect(container).toBeEmptyDOMElement();
    live.status = "connecting";
    rerender(<ConnectionLostBanner />);
    expect(container).toBeEmptyDOMElement();
  });

  it("says Reconnecting to Deluno while the connection is down, and goes when it is back", () => {
    live.status = "reconnecting";
    const { rerender, container } = render(<ConnectionLostBanner />);
    expect(screen.getByRole("status")).toHaveTextContent("Reconnecting to Deluno");
    live.status = "connected";
    rerender(<ConnectionLostBanner />);
    expect(container).toBeEmptyDOMElement();
  });

  it("after a minute without a connection says what to do instead", () => {
    live.status = "disconnected";
    render(<ConnectionLostBanner />);
    expect(screen.getByRole("status")).toHaveTextContent("Reconnecting to Deluno");
    act(() => { vi.advanceTimersByTime(60_000); });
    expect(screen.getByRole("status")).toHaveTextContent("Check that it is running, then reload this page");
  });

  it("starts the minute again after a connection that came back and dropped once more", () => {
    live.status = "disconnected";
    const { rerender } = render(<ConnectionLostBanner />);
    act(() => { vi.advanceTimersByTime(60_000); });
    live.status = "connected";
    rerender(<ConnectionLostBanner />);
    live.status = "reconnecting";
    rerender(<ConnectionLostBanner />);
    expect(screen.getByRole("status")).toHaveTextContent("Reconnecting to Deluno");
  });
});

describe("where the banner sits (#1508)", () => {
  it("is drawn over the page, not in its flow, so the screen under it is not pushed down", () => {
    live.status = "reconnecting";
    const { container } = render(<ConnectionLostBanner />);
    const banner = container.querySelector("[data-connection-lost]")!;
    expect(banner.className).toContain("fixed");
  });
});
