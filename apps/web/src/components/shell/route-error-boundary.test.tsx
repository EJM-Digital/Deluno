import { render, screen } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { RouteErrorBoundary } from "./route-error-boundary";

const reload = vi.hoisted(() => vi.fn());
vi.mock("../../lib/stale-build", async (actual) => ({
  ...(await actual<typeof import("../../lib/stale-build")>()),
  reloadOntoNewBuild: reload
}));

vi.mock("./error-state", () => ({
  ErrorState: ({ title, message, code }: { title: string; message: string; code: string }) => (
    <div>
      <h1>{title}</h1>
      <p>{message}</p>
      <span>{code}</span>
    </div>
  )
}));

function failWith(error: Error) {
  const router = createMemoryRouter(
    [{ path: "/", errorElement: <RouteErrorBoundary />, loader: () => { throw error; }, element: <div /> }],
    { initialEntries: ["/"] }
  );
  render(<RouterProvider router={router} />);
}

describe("the route error screen", () => {
  afterEach(() => {
    reload.mockReset();
  });

  it("says Deluno was updated, not a module error, for a screen the update replaced (#622)", async () => {
    failWith(new TypeError("Failed to fetch dynamically imported module: http://192.168.1.51:5099/assets/connections-screen.DoED8niD.js"));

    expect(await screen.findByRole("heading", { name: "Deluno was updated" })).toBeTruthy();
    expect(screen.queryByText(/dynamically imported module/)).toBeNull();
    expect(screen.getByText("route:stale-build")).toBeTruthy();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("still names any other failure as it is", async () => {
    failWith(new Error("The metadata provider did not answer."));

    expect(await screen.findByRole("heading", { name: "This area could not load" })).toBeTruthy();
    expect(screen.getByText("The metadata provider did not answer.")).toBeTruthy();
    expect(reload).not.toHaveBeenCalled();
  });
});
