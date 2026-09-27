import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

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

  it("clears itself once a later poll finds the connection back", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    fetchJson.mockResolvedValueOnce(outage);
    fetchJson.mockResolvedValueOnce(connected);

    const { container } = render(<OutageBanner />);
    await vi.waitFor(() => expect(screen.queryByText(/Internet down since/)).toBeTruthy());

    // Fast-forward past the poll interval instead of waiting on the real
    // clock, so a later poll finding the connection back is exercised
    // without the test itself taking 15 real seconds.
    await vi.advanceTimersByTimeAsync(20_000);

    await vi.waitFor(() => expect(container.textContent).toBe(""));
  });

  it("does nothing when the poll fails", async () => {
    fetchJson.mockRejectedValueOnce(new Error("network error"));

    const { container } = render(<OutageBanner />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());

    expect(container.textContent).toBe("");
  });
});
