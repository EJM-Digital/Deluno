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

describe("CalendarSubscribeDrawer link lifecycle", () => {
  const existingKey = { id: "old-key", name: "Calendar subscription", scopes: "read" };

  it("says a link already exists, and revokes it before making the replacement", async () => {
    const calls: string[] = [];
    authedFetch.mockImplementation((path: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? "GET"} ${path}`);
      if (path === "/api/api-keys" && init?.method === "POST") {
        return jsonResponse({ apiKey: "deluno_new-key", item: { id: "new-key", name: "Calendar subscription" } });
      }
      if (path === "/api/api-keys") return jsonResponse([existingKey, { id: "other", name: "Something else", scopes: "read" }]);
      return jsonResponse({});
    });

    render(<CalendarSubscribeDrawer open onOpenChange={() => undefined} />);

    expect(await screen.findByText(/A calendar link already exists but cannot be shown again/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /calendar link/ }));
    await waitFor(() => expect(screen.getByText(/apikey=deluno_new-key/)).toBeInTheDocument());

    expect(calls).toContain("DELETE /api/api-keys/old-key");
    expect(calls).not.toContain("DELETE /api/api-keys/other");
    expect(calls.indexOf("DELETE /api/api-keys/old-key")).toBeLessThan(calls.indexOf("POST /api/api-keys"));
  });

  it("treats a key list it cannot read as no link yet, and shows nothing made when the create is refused", async () => {
    authedFetch.mockImplementation((path: string, init?: RequestInit) => {
      // A refusal (400), not a 5xx: a 5xx is retried by the request helper before it gives up.
      const refused = (message: string) => Promise.resolve({ ok: false, status: 400, json: () => Promise.resolve({ message }), text: () => Promise.resolve(JSON.stringify({ message })), url: "test" } as Response);
      if (path === "/api/api-keys" && init?.method === "POST") return refused("The key could not be saved.");
      return refused("The key list could not be read.");
    });

    render(<CalendarSubscribeDrawer open onOpenChange={() => undefined} />);

    expect(await screen.findByText(/Deluno will create a read-only key/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Create calendar link/ }));
    await waitFor(() => expect(screen.getByRole("button", { name: /Create calendar link/ })).not.toBeDisabled());
    expect(screen.queryByText(/feed\.ics/)).toBeNull();
  });

  it("copies the link, and says so; when the browser refuses it still shows the link to copy by hand", async () => {
    authedFetch.mockImplementation((path: string, init?: RequestInit) => {
      if (path === "/api/api-keys" && init?.method === "POST") {
        return jsonResponse({ apiKey: "deluno_copy-key", item: { id: "k", name: "Calendar subscription" } });
      }
      return jsonResponse([]);
    });
    const writeText = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error("denied"));
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });

    render(<CalendarSubscribeDrawer open onOpenChange={() => undefined} />);
    await waitFor(() => expect(screen.getByRole("button", { name: /Create calendar link/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole("button", { name: /Create calendar link/ }));
    await waitFor(() => expect(screen.getByText(/apikey=deluno_copy-key/)).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: /^Copy$/ }));
    await waitFor(() => expect(screen.getByRole("button", { name: /^Copied$/ })).toBeInTheDocument());
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining("apikey=deluno_copy-key"));

    fireEvent.click(screen.getByRole("button", { name: /^Copied$/ }));
    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(2));
    expect(screen.getByText(/apikey=deluno_copy-key/)).toBeInTheDocument();
  });
});
