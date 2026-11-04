import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RouteErrorBoundary } from "./route-error-boundary";

const reloadOntoNewBuild = vi.hoisted(() => vi.fn());
vi.mock("../../lib/stale-build", async (actual) => ({
  ...(await actual<typeof import("../../lib/stale-build")>()),
  reloadOntoNewBuild
}));

// The real error screen is under test elsewhere; here its two buttons are what the boundary wires up.
vi.mock("./error-state", () => ({
  ErrorState: ({ title, message, code, onRetry, onReport }: { title: string; message: string; code: string; onRetry: () => void; onReport: () => void }) => (
    <div>
      <h1>{title}</h1>
      <p>{message}</p>
      <span>{code}</span>
      <button type="button" onClick={onRetry}>Try again</button>
      <button type="button" onClick={onReport}>Report it</button>
    </div>
  )
}));

function failWith(thrown: unknown) {
  const router = createMemoryRouter(
    [
      { path: "/", errorElement: <RouteErrorBoundary />, loader: () => { throw thrown; }, element: <div /> },
      { path: "/system", element: <div>System screen</div> }
    ],
    { initialEntries: ["/"] }
  );
  render(<RouterProvider router={router} />);
  return router;
}

describe("the route error screen's words and buttons", () => {
  const reload = vi.fn();

  beforeEach(() => {
    reload.mockReset();
    vi.stubGlobal("location", { ...window.location, reload });
  });
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    reloadOntoNewBuild.mockReset();
  });

  it("says a missing page is missing, with its code", async () => {
    failWith(new Response("", { status: 404, statusText: "Not Found" }));

    expect(await screen.findByRole("heading", { name: "That page is missing" })).toBeTruthy();
    expect(screen.getByText("The route is not available in this Deluno build.")).toBeTruthy();
    expect(screen.getByText("route:404")).toBeTruthy();
  });

  it("uses the server's own words for any other refusal", async () => {
    failWith(new Response("", { status: 503, statusText: "Service Unavailable" }));

    expect(await screen.findByRole("heading", { name: "This area could not load" })).toBeTruthy();
    expect(screen.getByText("Service Unavailable")).toBeTruthy();
    expect(screen.getByText("route:503")).toBeTruthy();
  });

  it("explains in plain words when a refusal came with no words of its own", async () => {
    failWith(new Response("", { status: 500, statusText: "" }));

    expect(await screen.findByText("The route loader failed before Deluno could render the page.")).toBeTruthy();
  });

  it("explains in plain words when what went wrong was not an error at all", async () => {
    failWith("something odd");

    expect(await screen.findByText("The route failed before Deluno could render the page.")).toBeTruthy();
    expect(screen.getByText("route:unexpected")).toBeTruthy();
  });

  it.each([
    ["a refusal", () => new Response("", { status: 500, statusText: "Boom" })],
    ["an error", () => new Error("broken")],
    ["a tab from before an update", () => new TypeError("Failed to fetch dynamically imported module: http://example/assets/a.js")]
  ])("Try again reloads the page after %s", async (_name, make) => {
    failWith(make());
    await userEvent.click(await screen.findByRole("button", { name: "Try again" }));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["a refusal", () => new Response("", { status: 404, statusText: "Nope" })],
    ["an error", () => new Error("broken")],
    ["a tab from before an update", () => new TypeError("Failed to fetch dynamically imported module: http://example/assets/a.js")]
  ])("Report it opens the System screen after %s", async (_name, make) => {
    failWith(make());
    await userEvent.click(await screen.findByRole("button", { name: "Report it" }));
    expect(await screen.findByText("System screen")).toBeTruthy();
  });
});
