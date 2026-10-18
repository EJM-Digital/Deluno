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
