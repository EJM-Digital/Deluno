import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AlertActions } from "../../lib/alert-actions";

const mocks = vi.hoisted(() => ({
  authedFetch: vi.fn(),
  toast: { success: vi.fn(), error: vi.fn() }
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../../components/shell/toaster", () => ({ toast: mocks.toast }));

const { AlertButtons } = await import("./needs-you");
const { useAction } = await import("./downloads-kit");
const { queryClient } = await import("../../lib/route-cache");

const actions: AlertActions = [
  { id: "retry", label: "Retry", hint: "Try again.", request: { method: "POST", path: "/api/jobs/1/retry", done: "Queued to run again.", failure: "Could not retry." } },
  { id: "dismiss", label: "Dismiss", hint: "Stop showing this.", quiet: true, request: { method: "POST", path: "/api/jobs/1/dismiss", done: "Dismissed.", failure: "Could not dismiss." } }
];

function Harness() {
  const state = useAction();
  return <AlertButtons actions={actions} subject="Sintel (2010)" state={state} />;
}

function open() {
  return render(<MemoryRouter><Harness /></MemoryRouter>);
}

beforeEach(() => {
  mocks.authedFetch.mockReset();
  mocks.toast.success.mockReset();
  mocks.toast.error.mockReset();
});
afterEach(cleanup);

describe("Dashboard Needs you buttons", () => {
  it("pressing a request button sends its request, says what the server or the button said in a toast, and re-reads the open lists", async () => {
    mocks.authedFetch.mockResolvedValue(new Response(JSON.stringify({ message: "Sintel will run again shortly." }), { status: 200 }));
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    open();

    await userEvent.setup().click(screen.getByRole("button", { name: "Retry: Sintel (2010)" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Sintel will run again shortly."));
    expect(mocks.authedFetch).toHaveBeenCalledWith("/api/jobs/1/retry", expect.objectContaining({ method: "POST" }));
    expect(invalidate).toHaveBeenCalled();

    mocks.authedFetch.mockResolvedValue(new Response("", { status: 200 }));
    await userEvent.setup().click(screen.getByRole("button", { name: "Dismiss: Sintel (2010)" }));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Dismissed."));
    invalidate.mockRestore();
  });

  it("while one button runs every button in the list is disabled and the pressed one shows a spinner", async () => {
    let finish: (response: Response) => void = () => undefined;
    mocks.authedFetch.mockReturnValue(new Promise<Response>((resolve) => { finish = resolve; }));
    const { container } = open();
    const retry = screen.getByRole("button", { name: "Retry: Sintel (2010)" });
    const dismiss = screen.getByRole("button", { name: "Dismiss: Sintel (2010)" });
    expect(retry).toBeEnabled();
    expect(container.querySelector(".animate-spin")).toBeNull();

    await userEvent.setup().click(retry);

    await waitFor(() => expect(retry).toBeDisabled());
    expect(dismiss).toBeDisabled();
    expect(retry.querySelector(".animate-spin")).not.toBeNull();
    expect(dismiss.querySelector(".animate-spin")).toBeNull();

    await act(async () => { finish(new Response("{}", { status: 200 })); });
    await waitFor(() => expect(retry).toBeEnabled());
    expect(dismiss).toBeEnabled();
    expect(retry.querySelector(".animate-spin")).toBeNull();
  });
});
