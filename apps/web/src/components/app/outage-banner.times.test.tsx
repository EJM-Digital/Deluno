import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const pushed = vi.hoisted(() => ({ handlers: [] as Array<(event: unknown) => void> }));
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard" },
  useSignalREvent: (event: string, _subject: unknown, handler: (event: unknown) => void) => {
    if (event === "ConnectivityChanged") pushed.handlers.push(handler);
  },
  useSignalRResync: () => undefined
}));

const { OutageBanner } = await import("./outage-banner");

function status(overrides: Record<string, unknown>) {
  return { isOutage: true, sinceUtc: "2026-09-27T14:02:00Z", checkedUtc: "2026-09-27T14:05:00Z", checkedHosts: ["1.1.1.1:443"], message: "Down.", ...overrides };
}

describe("the outage banner's wording", () => {
  afterEach(() => {
    fetchJson.mockReset();
    pushed.handlers = [];
  });

  it("gives the time the outage began, in the person's own clock", async () => {
    fetchJson.mockResolvedValueOnce(status({}));
    render(<OutageBanner />);
    const clock = new Date("2026-09-27T14:02:00Z").toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
    expect(await screen.findByText(`Internet down since ${clock} — cleanup and searching paused until it's back.`)).toBeTruthy();
  });

  it("just says the internet is down when it does not know since when", async () => {
    fetchJson.mockResolvedValueOnce(status({ sinceUtc: null }));
    render(<OutageBanner />);
    expect(await screen.findByText("Internet down — cleanup and searching paused until it's back.")).toBeTruthy();
  });

  it("just says the internet is down when the time it was given is not a time", async () => {
    fetchJson.mockResolvedValueOnce(status({ sinceUtc: "yesterday-ish" }));
    render(<OutageBanner />);
    expect(await screen.findByText("Internet down — cleanup and searching paused until it's back.")).toBeTruthy();
  });

  it("shows an outage the server pushes before it has been asked, and keeps nothing it did not know", async () => {
    fetchJson.mockRejectedValueOnce(new Error("offline"));
    const { container } = render(<OutageBanner />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");

    act(() => pushed.handlers.at(-1)?.({ isOutage: true, sinceUtc: null, checkedUtc: "2026-09-27T14:02:00Z", message: "Down." }));

    expect(await screen.findByText("Internet down — cleanup and searching paused until it's back.")).toBeTruthy();
    expect(screen.getByRole("status")).toBeTruthy();
  });

  it("does not show an answer that arrives after the banner has gone", async () => {
    let answer: (value: unknown) => void = () => undefined;
    fetchJson.mockReturnValueOnce(new Promise((resolve) => { answer = resolve; }));
    const { container, unmount } = render(<OutageBanner />);
    unmount();
    await act(async () => { answer(status({})); await Promise.resolve(); });
    expect(container.textContent).toBe("");
  });

  it("reads the connectivity once when the app opens", async () => {
    fetchJson.mockResolvedValueOnce(status({ isOutage: false }));
    render(<OutageBanner />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalledWith("/api/system/connectivity"));
    expect(fetchJson).toHaveBeenCalledTimes(1);
  });
});
