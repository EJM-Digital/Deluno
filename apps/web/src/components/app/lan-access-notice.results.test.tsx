import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { LanAccessNotice } = await import("./lan-access-notice");

const open = { state: "open", port: 5099, ruleName: "Deluno (port 5099)", message: "The firewall lets them in.", canAllow: false };
const blocked = { state: "blocked", port: 5099, ruleName: "Deluno (port 5099)", message: "The firewall turns them away.", canAllow: true };

describe("the other-devices answer shown in setup", () => {
  afterEach(() => fetchJson.mockReset());

  it("says it is checking until the answer arrives", async () => {
    fetchJson.mockReturnValue(new Promise(() => undefined));
    render(<LanAccessNotice showResult />);
    expect(screen.getByRole("status").textContent).toContain("Checking whether other devices can reach Deluno...");
  });

  it("says other devices can reach Deluno, and the address to use from one", async () => {
    fetchJson.mockResolvedValueOnce(open);
    render(<LanAccessNotice showResult address="192.168.1.10" />);
    expect(await screen.findByText("Other devices can reach Deluno. The firewall lets them in. From another device, open http://192.168.1.10:5099.")).toBeTruthy();
  });

  it("leaves the address out when it does not know one or the port", async () => {
    fetchJson.mockResolvedValueOnce(open);
    const first = render(<LanAccessNotice showResult />);
    expect(await screen.findByText("Other devices can reach Deluno. The firewall lets them in.")).toBeTruthy();
    first.unmount();

    fetchJson.mockResolvedValueOnce({ ...open, port: null });
    render(<LanAccessNotice showResult address="192.168.1.10" />);
    expect(await screen.findByText("Other devices can reach Deluno. The firewall lets them in.")).toBeTruthy();
  });

  it("says there is nothing to allow when the check was not made", async () => {
    fetchJson.mockResolvedValueOnce({ state: "notChecked", port: null, ruleName: null, message: "No firewall to look after.", canAllow: false });
    render(<LanAccessNotice showResult />);
    expect(await screen.findByText("Nothing to allow here. No firewall to look after.")).toBeTruthy();
  });

  it("says plainly that it could not check, and that nothing it can see is blocked, when the question fails", async () => {
    fetchJson.mockRejectedValueOnce(new Error("offline"));
    render(<LanAccessNotice showResult />);
    expect(await screen.findByText("Deluno could not check whether other devices can reach it. Nothing is blocked that Deluno can see.")).toBeTruthy();
    expect(screen.getByRole("status", { name: "Other devices on your network" })).toBeTruthy();
  });

  it("shows nothing when the question fails and no result is wanted", async () => {
    fetchJson.mockRejectedValueOnce(new Error("offline"));
    const { container } = render(<LanAccessNotice />);
    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });

  it("shows the blocked card in setup too, with its button", async () => {
    fetchJson.mockResolvedValueOnce(blocked);
    render(<LanAccessNotice showResult />);
    expect(await screen.findByText("Other devices cannot reach Deluno")).toBeTruthy();
    expect(screen.getByText("The firewall turns them away.")).toBeTruthy();
  });

  it("offers no button when Deluno cannot ask Windows itself", async () => {
    fetchJson.mockResolvedValueOnce({ ...blocked, canAllow: false });
    render(<LanAccessNotice />);
    expect(await screen.findByText("Other devices cannot reach Deluno")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Allow other devices" })).toBeNull();
  });

  it("shows a spinner and cannot be pressed again while Windows is being asked", async () => {
    fetchJson.mockResolvedValueOnce(blocked);
    fetchJson.mockReturnValueOnce(new Promise(() => undefined));
    render(<LanAccessNotice />);
    const button = await screen.findByRole("button", { name: "Allow other devices" });
    fireEvent.click(button);
    await vi.waitFor(() => expect((button as HTMLButtonElement).disabled).toBe(true));
    expect(button.querySelector("svg.animate-spin")).not.toBeNull();
  });

  it("says why when asking Windows fails with an error, using its words", async () => {
    fetchJson.mockResolvedValueOnce(blocked);
    fetchJson.mockRejectedValueOnce(new Error("Windows said no."));
    render(<LanAccessNotice />);
    fireEvent.click(await screen.findByRole("button", { name: "Allow other devices" }));
    expect(await screen.findByText("Windows said no.")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Allow other devices" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("says why in plain words when asking Windows fails with something that is not an error", async () => {
    fetchJson.mockResolvedValueOnce(blocked);
    fetchJson.mockRejectedValueOnce("no");
    render(<LanAccessNotice />);
    fireEvent.click(await screen.findByRole("button", { name: "Allow other devices" }));
    expect(await screen.findByText("Deluno could not ask Windows to change the firewall.")).toBeTruthy();
  });

  it("does not show an answer that arrives after the notice has gone", async () => {
    let answer: (value: unknown) => void = () => undefined;
    fetchJson.mockReturnValueOnce(new Promise((resolve) => { answer = resolve; }));
    const { container, unmount } = render(<LanAccessNotice />);
    unmount();
    answer(blocked);
    await Promise.resolve();
    expect(container.textContent).toBe("");

    let fail: (reason: unknown) => void = () => undefined;
    fetchJson.mockReturnValueOnce(new Promise((_resolve, reject) => { fail = reject; }));
    const second = render(<LanAccessNotice showResult />);
    second.unmount();
    fail(new Error("late"));
    await Promise.resolve();
    expect(second.container.textContent).toBe("");
  });
});
