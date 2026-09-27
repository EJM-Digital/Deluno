import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { CalendarSubscribeDrawer } from "./calendar-subscribe-drawer";

/**
 * #786: the calendar link now carries the feed's own unmonitored/tag options,
 * so a person does not have to hand-edit the URL Sonarr/Radarr users are used
 * to typing in by hand.
 */

const authedFetch = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));

function jsonResponse(body: unknown, ok = true) {
  return Promise.resolve({
    ok,
    status: ok ? 200 : 500,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body)),
    url: "test"
  } as Response);
}

describe("CalendarSubscribeDrawer feed options (#786)", () => {
  it("bakes the unmonitored and tag choices into the generated feed URL", async () => {
    authedFetch.mockImplementation((path: string, init?: RequestInit) => {
      if (path === "/api/api-keys" && (!init || init.method === undefined)) {
        return jsonResponse([]);
      }
      if (path === "/api/api-keys" && init?.method === "POST") {
        return jsonResponse({ apiKey: "deluno_test-key", item: { id: "key-1", name: "Calendar subscription" } });
      }
      return jsonResponse([]);
    });

    render(<CalendarSubscribeDrawer open onOpenChange={() => undefined} />);

    await waitFor(() => expect(screen.getByRole("button", { name: /Create calendar link/ })).not.toBeDisabled());

    fireEvent.click(screen.getByRole("switch", { name: /Include unmonitored titles/ }));
    fireEvent.change(screen.getByLabelText("Only these tags (optional)"), { target: { value: "Anime, Kids" } });
    fireEvent.click(screen.getByRole("button", { name: /Create calendar link/ }));

    await waitFor(() => expect(screen.getByText(/feed\.ics/)).toBeInTheDocument());

    const url = screen.getByText(/feed\.ics/).textContent ?? "";
    expect(url).toContain("apikey=deluno_test-key");
    expect(url).toContain("unmonitored=true");
    expect(url).toContain(encodeURIComponent("Anime,Kids").replace(/%2C/g, "%2C"));
  });

  it("leaves unmonitored and tag out of the URL when neither is set", async () => {
    authedFetch.mockImplementation((path: string, init?: RequestInit) => {
      if (path === "/api/api-keys" && init?.method === "POST") {
        return jsonResponse({ apiKey: "deluno_plain-key", item: { id: "key-2", name: "Calendar subscription" } });
      }
      return jsonResponse([]);
    });

    render(<CalendarSubscribeDrawer open onOpenChange={() => undefined} />);

    await waitFor(() => expect(screen.getByRole("button", { name: /Create calendar link/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole("button", { name: /Create calendar link/ }));

    await waitFor(() => expect(screen.getByText(/feed\.ics/)).toBeInTheDocument());
    const url = screen.getByText(/feed\.ics/).textContent ?? "";
    expect(url).not.toContain("unmonitored");
    expect(url).not.toContain("tag=");
  });
});
