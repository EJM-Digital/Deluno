import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SignalRStatus } from "../../lib/use-signalr";
import { WsStatusBadge } from "./ws-status-badge";

/** The one honest word about the live connection: silent while it is up, plain words while it is not. */

const state = vi.hoisted(() => ({ status: "connected" as SignalRStatus }));
vi.mock("../../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/use-signalr")>()),
  useSignalRStatus: () => state.status
}));

beforeEach(() => { state.status = "connected"; });

describe("the live connection badge", () => {
  it("says nothing while the connection is up", () => {
    const { container } = render(<WsStatusBadge />);
    expect(container).toBeEmptyDOMElement();
  });

  it("says 'Not live: reconnecting' and that the numbers on screen may be out of date while the connection is coming back", () => {
    state.status = "reconnecting";
    render(<WsStatusBadge />);

    const badge = screen.getByRole("status");
    expect(badge).toHaveTextContent("Not live: reconnecting");
    expect(badge).toHaveAttribute("aria-label", expect.stringContaining("the numbers on screen may be out of date"));
  });

  it("says Connecting… in grey while the first connection is made, and Not live: reconnecting is an amber pulsing dot", () => {
    state.status = "connecting";
    const { unmount } = render(<WsStatusBadge />);
    const connecting = screen.getByRole("status");
    expect(connecting).toHaveTextContent("Connecting…");
    expect(connecting.querySelector("span")!.className).toContain("bg-muted-foreground");
    unmount();

    state.status = "reconnecting";
    render(<WsStatusBadge />);
    const reconnecting = screen.getByRole("status");
    expect(reconnecting.querySelector("span")!.className).toContain("bg-warning animate-pulse");
  });

  it("says 'Not live: trying again' when the connection is down", () => {
    state.status = "disconnected";
    render(<WsStatusBadge />);
    expect(screen.getByRole("status")).toHaveTextContent("Not live: trying again");
  });

  it("goes quiet again by itself once the server is back", () => {
    state.status = "disconnected";
    const { rerender, container } = render(<WsStatusBadge />);
    expect(screen.getByRole("status")).toBeVisible();

    state.status = "connected";
    rerender(<WsStatusBadge />);

    expect(container).toBeEmptyDOMElement();
  });
});

describe("the live connection badge, status by status", () => {
  const paused = "Live updates are paused, so the numbers on screen may be out of date.";

  it("while connecting: grey, and no claim that updates are paused", () => {
    state.status = "connecting";
    render(<WsStatusBadge className="ml-2" />);
    const badge = screen.getByRole("status");
    expect(badge).toHaveAttribute("aria-label", "Connecting…");
    expect(badge).not.toHaveAttribute("title");
    expect(badge).toHaveAttribute("aria-live", "polite");
    expect(badge).toHaveAttribute("data-realtime-status", "connecting");
    expect(badge).toHaveClass("border-hairline", "bg-card", "text-muted-foreground", "ml-2", "inline-flex", "rounded-full");
    expect(badge.firstElementChild).toHaveClass("h-1.5", "w-1.5", "rounded-full", "bg-muted-foreground");
    expect(badge.firstElementChild).not.toHaveClass("animate-pulse");
  });

  it("while reconnecting: amber, saying updates are paused and Deluno keeps trying", () => {
    state.status = "reconnecting";
    render(<WsStatusBadge />);
    const badge = screen.getByRole("status");
    expect(badge).toHaveAttribute("aria-label", `Not live: reconnecting. ${paused}`);
    expect(badge).toHaveAttribute("title", `${paused} Deluno keeps trying to reconnect.`);
    expect(badge).toHaveAttribute("data-realtime-status", "reconnecting");
    expect(badge).toHaveClass("border-warning/25", "bg-warning/10", "text-warning");
  });

  it("when disconnected: red, saying updates are paused and Deluno keeps trying", () => {
    state.status = "disconnected";
    render(<WsStatusBadge />);
    const badge = screen.getByRole("status");
    expect(badge).toHaveAttribute("aria-label", `Not live: trying again. ${paused}`);
    expect(badge).toHaveAttribute("title", `${paused} Deluno keeps trying to reconnect.`);
    expect(badge).toHaveAttribute("data-realtime-status", "disconnected");
    expect(badge).toHaveClass("border-destructive/25", "text-destructive");
    expect(badge.firstElementChild).toHaveClass("bg-destructive");
  });
});
