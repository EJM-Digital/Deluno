import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
const authedFetch = vi.hoisted(() => vi.fn());
const toastSuccess = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: { success: toastSuccess, error: vi.fn() } }));
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard" },
  useSignalREvent: () => undefined,
  useSignalRResync: () => undefined
}));

const { AutomationPausedNotice } = await import("./automation-paused-notice");

describe("the automation-paused notice's words and request", () => {
  afterEach(() => {
    fetchJson.mockReset();
    authedFetch.mockReset();
    toastSuccess.mockReset();
  });

  it("says this title when it is not told what kind of title is being added", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    render(<AutomationPausedNotice context="add" />);
    expect(await screen.findByText("Automation is paused, so this title won't be searched until you resume it.")).toBeTruthy();
  });

  it("says which screen it is drawn on", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    const { container } = render(<AutomationPausedNotice context="dashboard" />);
    await screen.findByRole("status");
    expect(container.querySelector("[data-automation-paused='dashboard']")).not.toBeNull();
  });

  it("asks the server to turn automation on, in JSON, and tells the person it is resumed", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    authedFetch.mockResolvedValue({ ok: true });
    render(<AutomationPausedNotice context="dashboard" />);

    fireEvent.click(await screen.findByRole("button", { name: "Resume automation" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Automation resumed"));
    expect(authedFetch).toHaveBeenCalledWith("/api/settings/automation", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ isEnabled: true })
    });
  });

  it("goes away as soon as Resume is accepted, without reading the settings again", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    authedFetch.mockResolvedValue({ ok: true });
    const { container } = render(<AutomationPausedNotice context="dashboard" />);

    fireEvent.click(await screen.findByRole("button", { name: "Resume automation" }));

    await waitFor(() => expect(container.textContent).toBe(""));
    expect(fetchJson).toHaveBeenCalledTimes(1);
  });
});
