import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const pushed = vi.hoisted(() => ({ handlers: [] as Array<(event: unknown) => void>, resyncs: [] as Array<() => void> }));
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard" },
  useSignalREvent: (event: string, _subject: unknown, handler: (event: unknown) => void) => {
    if (event === "ConnectivityChanged") pushed.handlers.push(handler);
  },
  useSignalRResync: (handler: () => void) => {
    pushed.resyncs.push(handler);
  }
}));

const { OutageBanner } = await import("./outage-banner");

const outage = {
  isOutage: true,
  sinceUtc: "2026-09-27T14:02:00Z",
  checkedUtc: "2026-09-27T14:05:00Z",
  checkedHosts: ["1.1.1.1:443"],
  message: "Internet down since 14:02 — cleanup and searching paused."
};

const connected = {
  isOutage: false,
  sinceUtc: null,
  checkedUtc: "2026-09-27T14:05:00Z",
  checkedHosts: ["1.1.1.1:443"],
  message: "Connected."
};

describe("outage banner (#901)", () => {
  afterEach(() => {
    fetchJson.mockReset();
    pushed.handlers = [];
    pushed.resyncs = [];
    vi.useRealTimers();
  });

  it("shows nothing while the internet is up", async () => {
    fetchJson.mockResolvedValueOnce(connected);

    const { container } = render(<OutageBanner />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());

    expect(container.textContent).toBe("");
  });

  it("says the internet is down and since when", async () => {
    fetchJson.mockResolvedValueOnce(outage);

    render(<OutageBanner />);

    expect(await screen.findByText(/Internet down since/)).toBeTruthy();
    expect(screen.getByText(/cleanup and searching paused/)).toBeTruthy();
  });

  it("follows the server's push when the connection goes and comes back, with no timer (#1179)", async () => {
    fetchJson.mockResolvedValueOnce(connected);

    const { container } = render(<OutageBanner />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    expect(container.textContent).toBe("");

    act(() => pushed.handlers.at(-1)?.({ isOutage: true, sinceUtc: "2026-09-27T14:02:00Z", checkedUtc: "2026-09-27T14:02:00Z", message: "Internet down" }));
    expect(await screen.findByText(/Internet down since/)).toBeTruthy();

    act(() => pushed.handlers.at(-1)?.({ isOutage: false, sinceUtc: null, checkedUtc: "2026-09-27T14:09:00Z", message: "Connected." }));
    await vi.waitFor(() => expect(container.textContent).toBe(""));
    // Neither change asked the server again.
    expect(fetchJson).toHaveBeenCalledTimes(1);
  });

  it("asks once more when events could not be replayed after a dropped connection", async () => {
    fetchJson.mockResolvedValueOnce(connected);
    fetchJson.mockResolvedValueOnce(outage);

    render(<OutageBanner />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));

    act(() => pushed.resyncs.at(-1)?.());
    expect(await screen.findByText(/Internet down since/)).toBeTruthy();
  });

  it("does nothing when the poll fails", async () => {
    fetchJson.mockRejectedValueOnce(new Error("network error"));

    const { container } = render(<OutageBanner />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());

    expect(container.textContent).toBe("");
  });
});
