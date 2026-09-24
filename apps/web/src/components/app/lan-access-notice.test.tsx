import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { LanAccessNotice } = await import("./lan-access-notice");

const blocked = {
  state: "blocked",
  port: 5099,
  ruleName: "Deluno (port 5099)",
  message: "Windows Firewall has no rule for Deluno's port 5099, so other devices on your network cannot open Deluno.",
  canAllow: true
};

describe("other devices on the network (#623)", () => {
  afterEach(() => fetchJson.mockReset());

  it("says when the firewall turns other devices away, and lets them in with one step", async () => {
    fetchJson.mockResolvedValueOnce(blocked);
    fetchJson.mockResolvedValueOnce({ ...blocked, state: "open", canAllow: false, message: "Open." });

    render(<LanAccessNotice />);

    expect(await screen.findByText("Other devices cannot reach Deluno")).toBeTruthy();
    expect(screen.getByText(/port 5099/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Allow other devices" }));

    await vi.waitFor(() => expect(screen.queryByText("Other devices cannot reach Deluno")).toBeNull());
    expect(fetchJson).toHaveBeenLastCalledWith("/api/system/lan-access/allow", { method: "POST" });
  });

  it("keeps saying so when Windows was not given permission", async () => {
    fetchJson.mockResolvedValueOnce(blocked);
    fetchJson.mockResolvedValueOnce({ ...blocked, message: "Windows asked for permission to change the firewall, and it was not given." });

    render(<LanAccessNotice />);
    fireEvent.click(await screen.findByRole("button", { name: "Allow other devices" }));

    expect(await screen.findByText(/it was not given/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Allow other devices" })).toBeTruthy();
  });

  it.each([
    ["open", { ...blocked, state: "open", canAllow: false }],
    ["notChecked", { state: "notChecked", port: null, ruleName: null, message: "Nothing to manage.", canAllow: false }]
  ])("shows nothing when the state is %s", async (_state, status) => {
    fetchJson.mockResolvedValueOnce(status);

    const { container } = render(<LanAccessNotice />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());

    expect(container.textContent).toBe("");
  });
});
