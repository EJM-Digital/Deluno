import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
const authedFetch = vi.hoisted(() => vi.fn());
const toastError = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: toastError } }));
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard" },
  useSignalREvent: () => undefined,
  useSignalRResync: () => undefined
}));

const { AutomationPausedNotice } = await import("./automation-paused-notice");

describe("automation paused notice (#936)", () => {
  afterEach(() => {
    fetchJson.mockReset();
    authedFetch.mockReset();
    toastError.mockReset();
  });

  it("says plainly that the film will not be searched, with a Resume button, while automation is paused", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    render(<AutomationPausedNotice context="add" noun="film" />);
    expect(await screen.findByText("Automation is paused, so this film won't be searched until you resume it.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Resume automation" })).toBeTruthy();
  });

  it("says what is held on the Dashboard", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    render(<AutomationPausedNotice context="dashboard" />);
    expect(await screen.findByText(/Automation is paused, so nothing is searched/)).toBeTruthy();
  });

  it("shows nothing while automation is running", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: true });
    const { container } = render(<AutomationPausedNotice context="dashboard" />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });

  it("draws nothing while the answer is not known yet and when the read fails", async () => {
    fetchJson.mockReturnValue(new Promise(() => undefined));
    const waiting = render(<AutomationPausedNotice context="dashboard" />);
    expect(waiting.container.textContent).toBe("");
    waiting.unmount();

    fetchJson.mockRejectedValue(new Error("offline"));
    const failed = render(<AutomationPausedNotice context="dashboard" />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(2));
    expect(failed.container.textContent).toBe("");
  });

  it("a failed Resume toasts the error and the notice stays", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    authedFetch.mockResolvedValue({ ok: false });
    render(<AutomationPausedNotice context="dashboard" />);
    fireEvent.click(await screen.findByRole("button", { name: "Resume automation" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Deluno could not resume automation."));
    expect(screen.getByText(/Automation is paused, so nothing is searched/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Resume automation" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("the Resume button shows a spinner and is disabled while the request is in flight", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    let finish: (value: unknown) => void = () => undefined;
    authedFetch.mockReturnValue(new Promise((resolve) => { finish = resolve; }));
    render(<AutomationPausedNotice context="dashboard" />);
    const button = (await screen.findByRole("button", { name: "Resume automation" })) as HTMLButtonElement;
    fireEvent.click(button);
    await waitFor(() => expect(button.disabled).toBe(true));
    expect(button.querySelector("svg.animate-spin")).not.toBeNull();
    finish({ ok: true });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Resume automation" })).toBeNull());
  });

  it("resumes automation and goes away", async () => {
    fetchJson.mockResolvedValue({ autoStartJobs: false });
    authedFetch.mockResolvedValue({ ok: true });
    render(<AutomationPausedNotice context="dashboard" />);
    fireEvent.click(await screen.findByRole("button", { name: "Resume automation" }));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Resume automation" })).toBeNull());
    expect(authedFetch).toHaveBeenCalledWith("/api/settings/automation", expect.objectContaining({ method: "PUT", body: JSON.stringify({ isEnabled: true }) }));
  });
});
