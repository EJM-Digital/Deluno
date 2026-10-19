import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { CalendarSubscribeDrawer } from "./calendar-subscribe-drawer";

/**
 * The calendar link (coverage register, area library): the key made for it is read-only and named Calendar subscription, the link
 * carries that key and the tags exactly as typed (comma-joined), and the three calendar apps are told where to paste it.
 */
const authedFetch = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));

const answer = (body: unknown) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)), url: "test" } as Response);

describe("CalendarSubscribeDrawer link", () => {
  it("asks for a read-only key named Calendar subscription, builds the link on it with the tags comma-joined, and tells Google, Apple and Outlook where to paste it", async () => {
    const posted: unknown[] = [];
    authedFetch.mockImplementation((path: string, init?: RequestInit) => {
      if (path === "/api/api-keys" && init?.method === "POST") {
        posted.push(JSON.parse(String(init.body)));
        return answer({ apiKey: "deluno_abc", item: { id: "k1", name: "Calendar subscription" } });
      }
      return answer([]);
    });

    render(<CalendarSubscribeDrawer open onOpenChange={() => undefined} />);
    await waitFor(() => expect(screen.getByRole("button", { name: /Create calendar link/ })).not.toBeDisabled());
    fireEvent.change(screen.getByLabelText("Only these tags (optional)"), { target: { value: " Anime ,  Kids ,, " } });
    fireEvent.click(screen.getByRole("button", { name: /Create calendar link/ }));

    await waitFor(() => expect(screen.getByText(/feed\.ics/)).toBeInTheDocument());
    expect(posted).toEqual([{ name: "Calendar subscription", scopes: "read" }]);
    const url = new URL(screen.getByText(/feed\.ics/).textContent!);
    expect(url.pathname).toBe("/api/calendar/feed.ics");
    expect(url.searchParams.get("apikey")).toBe("deluno_abc");
    expect(url.searchParams.get("tag")).toBe("Anime,Kids");
    expect(url.searchParams.has("unmonitored")).toBe(false);
    expect(screen.getByText("Google Calendar")).toBeInTheDocument();
    expect(screen.getByText("Other calendars → From URL")).toBeInTheDocument();
    expect(screen.getByText("Apple Calendar")).toBeInTheDocument();
    expect(screen.getByText("File → New Calendar Subscription")).toBeInTheDocument();
    expect(screen.getByText("Outlook")).toBeInTheDocument();
    expect(screen.getByText("Add calendar → Subscribe from web")).toBeInTheDocument();
  });
});
