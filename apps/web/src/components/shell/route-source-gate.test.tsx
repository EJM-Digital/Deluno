import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { RouteSourceState, RouteSources } from "../../lib/route-cache";
import { RouteSourceGate } from "./route-source-gate";

function state(overrides: Partial<RouteSourceState<RouteSources>> = {}): RouteSourceState<RouteSources> {
  return {
    data: {},
    ready: true,
    pending: new Set(),
    failed: [],
    lastReadFailed: new Set(),
    blocked: false,
    isFetching: false,
    refetch: vi.fn(() => Promise.resolve()),
    revalidator: { revalidate: vi.fn(), state: "idle" },
    ...overrides
  } as RouteSourceState<RouteSources>;
}

describe("the gate in front of a screen's data (#1081)", () => {
  it("shows the screen's own shape while the first answers arrive", () => {
    const { container } = render(<RouteSourceGate state={state({ ready: false })}>{() => <p>The screen</p>}</RouteSourceGate>);
    expect(container.querySelector("[data-route-skeleton]")).not.toBeNull();
    expect(screen.queryByText("The screen")).toBeNull();
  });

  it("draws the screen with no warning when every source answered", () => {
    render(<RouteSourceGate state={state()}>{() => <p>The screen</p>}</RouteSourceGate>);
    expect(screen.getByText("The screen")).toBeVisible();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("draws the screen under a warning naming the one list that could not be read, with its reason", () => {
    render(
      <RouteSourceGate state={state({ failed: [{ name: "tags", label: "Tags", message: "The server did not answer." }] })}>
        {() => <p>The screen</p>}
      </RouteSourceGate>
    );
    expect(screen.getByRole("alert")).toHaveTextContent("Tags could not be read. The server did not answer.");
    expect(screen.getByText("The screen")).toBeVisible();
  });

  it("names every list that failed when there is more than one, and offers a retry", async () => {
    const refetch = vi.fn(() => Promise.resolve());
    render(
      <RouteSourceGate
        state={state({
          refetch,
          failed: [
            { name: "tags", label: "Tags", message: "" },
            { name: "libraries", label: "Libraries", message: "" }
          ]
        })}
      >
        {() => <p>The screen</p>}
      </RouteSourceGate>
    );
    expect(screen.getByRole("alert")).toHaveTextContent("Tags, Libraries could not be read.");
    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it("replaces the screen with the problem when a list it cannot do without failed", () => {
    render(
      <RouteSourceGate state={state({ blocked: true, failed: [{ name: "tags", label: "Tags", message: "Offline." }] })}>
        {() => <p>The screen</p>}
      </RouteSourceGate>
    );
    expect(screen.getByRole("alert")).toHaveTextContent("This screen could not load everything it needs. Tags could not be read. Offline.");
    expect(screen.queryByText("The screen")).toBeNull();
  });
});
