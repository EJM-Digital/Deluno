import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { CalendarSubscribeDrawer } from "./calendar-subscribe-drawer";

const authedFetch = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));

function jsonResponse(body: unknown) {
  return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)), url: "test" } as Response);
}

describe("the calendar link drawer (low rows)", () => {
  it("a new link is shown with the warning that this is the only time it can be shown", async () => {
    authedFetch.mockImplementation((path: string, init?: RequestInit) => (
      path === "/api/api-keys" && init?.method === "POST"
        ? jsonResponse({ apiKey: "deluno_once-key", item: { id: "k", name: "Calendar subscription" } })
        : jsonResponse([])
    ));

    render(<CalendarSubscribeDrawer open onOpenChange={() => undefined} />);
    await waitFor(() => expect(screen.getByRole("button", { name: /Create calendar link/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole("button", { name: /Create calendar link/ }));

    await waitFor(() => expect(screen.getByText(/apikey=deluno_once-key/)).toBeInTheDocument());
    expect(screen.getByText(/this is the only time the link can be shown/)).toBeVisible();
  });
});
