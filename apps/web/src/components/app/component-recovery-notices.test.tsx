import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { ComponentRecoveryNotices } = await import("./component-recovery-notices");

const notice = {
  kind: "transmission",
  displayName: "Transmission",
  message: "Transmission's settings were rebuilt after Deluno was reinstalled; 3 torrents may need adding back.",
  itemCount: 3,
  canReadd: true
};

describe("an app that was rebuilt after a reinstall (#1177)", () => {
  afterEach(() => fetchJson.mockReset());

  it("shows nothing when there is nothing to say", async () => {
    fetchJson.mockResolvedValueOnce([]);
    const { container } = render(<ComponentRecoveryNotices />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });

  it("says what happened in plain words and adds the torrents back with one button", async () => {
    fetchJson
      .mockResolvedValueOnce([notice])
      .mockResolvedValueOnce({ done: true, message: "Deluno added 3 torrents back to Transmission." })
      .mockResolvedValueOnce([]);

    render(<ComponentRecoveryNotices />);

    expect(await screen.findByText(/3 torrents may need adding back/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Add them back" }));

    expect(await screen.findByText("Deluno added 3 torrents back to Transmission.")).toBeTruthy();
    expect(fetchJson).toHaveBeenCalledWith("/api/system/component-recovery/transmission/readd", { method: "POST" });
    expect(screen.queryByRole("button", { name: "Add them back" })).toBeNull();
  });

  it("keeps the notice and says why when Transmission is not running", async () => {
    const stillThere = { done: false, message: "Transmission is not running, so Deluno cannot read the torrents it had. Start Transmission, then try again." };
    fetchJson.mockResolvedValueOnce([notice]).mockResolvedValueOnce(stillThere).mockResolvedValueOnce([notice]);

    render(<ComponentRecoveryNotices />);
    fireEvent.click(await screen.findByRole("button", { name: "Add them back" }));

    expect(await screen.findByText(/Transmission is not running/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add them back" })).toBeTruthy();
  });

  it("can be dismissed", async () => {
    fetchJson.mockResolvedValueOnce([notice]).mockResolvedValueOnce(undefined).mockResolvedValueOnce([]);

    render(<ComponentRecoveryNotices />);
    fireEvent.click(await screen.findByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(screen.queryByText(/may need adding back/)).toBeNull());
    expect(fetchJson).toHaveBeenCalledWith("/api/system/component-recovery/transmission/dismiss", { method: "POST" });
  });
});
