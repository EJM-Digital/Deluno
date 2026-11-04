import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { ComponentRecoveryNotices } = await import("./component-recovery-notices");

const notice = (overrides: Record<string, unknown> = {}) => ({ kind: "qbittorrent", displayName: "qBittorrent", message: "Its folder was rebuilt.", itemCount: 3, canReadd: true, ...overrides });

describe("an installed app that was set up again", () => {
  beforeEach(() => { fetchJson.mockReset(); });

  it("shows nothing at all when there is nothing to say", async () => {
    fetchJson.mockResolvedValueOnce([]);
    const { container } = render(<ComponentRecoveryNotices />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    expect(container.textContent).toBe("");
  });

  it("shows nothing when the notices cannot be read", async () => {
    fetchJson.mockRejectedValueOnce(new Error("offline"));
    const { container } = render(<ComponentRecoveryNotices />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    await Promise.resolve();
    expect(container.textContent).toBe("");
  });

  it("does not show notices that arrive after the screen has gone", async () => {
    let answer: (value: unknown) => void = () => undefined;
    let fail: (reason: unknown) => void = () => undefined;
    fetchJson.mockReturnValueOnce(new Promise((resolve) => { answer = resolve; }));
    const first = render(<ComponentRecoveryNotices />);
    first.unmount();
    answer([notice()]);
    await Promise.resolve();
    expect(first.container.textContent).toBe("");

    fetchJson.mockReturnValueOnce(new Promise((_resolve, reject) => { fail = reject; }));
    const second = render(<ComponentRecoveryNotices />);
    second.unmount();
    fail(new Error("late"));
    await Promise.resolve();
    expect(second.container.textContent).toBe("");
  });

  it("names the app and what happened, with both answers when the torrents can be added back", async () => {
    fetchJson.mockResolvedValueOnce([notice()]);
    render(<ComponentRecoveryNotices />);
    expect(await screen.findByText("qBittorrent was set up again")).toBeInTheDocument();
    expect(screen.getByText("Its folder was rebuilt.")).toBeInTheDocument();
    expect(screen.getByRole("status", { name: "qBittorrent was rebuilt" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add them back" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeEnabled();
  });

  it("offers only Dismiss when nothing can be added back", async () => {
    fetchJson.mockResolvedValueOnce([notice({ canReadd: false })]);
    render(<ComponentRecoveryNotices />);
    await screen.findByText("qBittorrent was set up again");
    expect(screen.queryByRole("button", { name: "Add them back" })).toBeNull();
  });

  it("adds them back, says what happened in the server's words, and asks again what is left", async () => {
    fetchJson.mockResolvedValueOnce([notice()]);
    fetchJson.mockResolvedValueOnce({ done: true, message: "3 torrents were added back." });
    fetchJson.mockResolvedValueOnce([]);
    render(<ComponentRecoveryNotices />);

    await userEvent.click(await screen.findByRole("button", { name: "Add them back" }));

    expect(await screen.findByText("3 torrents were added back.")).toBeInTheDocument();
    expect(fetchJson).toHaveBeenNthCalledWith(2, "/api/system/component-recovery/qbittorrent/readd", { method: "POST" });
    expect(fetchJson).toHaveBeenNthCalledWith(3, "/api/system/component-recovery");
    expect(screen.queryByRole("button", { name: "Dismiss" })).toBeNull();
  });

  it("encodes the app's name in the address", async () => {
    fetchJson.mockResolvedValueOnce([notice({ kind: "a b" })]);
    fetchJson.mockResolvedValueOnce({ done: true, message: "ok" });
    fetchJson.mockResolvedValueOnce([]);
    render(<ComponentRecoveryNotices />);
    await userEvent.click(await screen.findByRole("button", { name: "Add them back" }));
    await waitFor(() => expect(fetchJson).toHaveBeenCalledWith("/api/system/component-recovery/a%20b/readd", { method: "POST" }));
  });

  it("says why when adding them back fails, using the error's words", async () => {
    fetchJson.mockResolvedValueOnce([notice()]);
    fetchJson.mockRejectedValueOnce(new Error("qBittorrent did not answer."));
    render(<ComponentRecoveryNotices />);

    await userEvent.click(await screen.findByRole("button", { name: "Add them back" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("qBittorrent did not answer.");
    expect(screen.getByRole("button", { name: "Add them back" })).toBeEnabled();
  });

  it("says why in plain words when adding them back fails with something that is not an error", async () => {
    fetchJson.mockResolvedValueOnce([notice()]);
    fetchJson.mockRejectedValueOnce("no");
    render(<ComponentRecoveryNotices />);

    await userEvent.click(await screen.findByRole("button", { name: "Add them back" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Deluno could not add the torrents back to qBittorrent.");
  });

  it("dismisses the notice and asks again what is left", async () => {
    fetchJson.mockResolvedValueOnce([notice()]);
    fetchJson.mockResolvedValueOnce(undefined);
    fetchJson.mockResolvedValueOnce([]);
    const { container } = render(<ComponentRecoveryNotices />);

    await userEvent.click(await screen.findByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(container.textContent).toBe(""));
    expect(fetchJson).toHaveBeenNthCalledWith(2, "/api/system/component-recovery/qbittorrent/dismiss", { method: "POST" });
  });

  it("says why when dismissing fails, in the error's words or plain ones", async () => {
    fetchJson.mockResolvedValueOnce([notice()]);
    fetchJson.mockRejectedValueOnce(new Error("The server said no."));
    render(<ComponentRecoveryNotices />);
    await userEvent.click(await screen.findByRole("button", { name: "Dismiss" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The server said no.");

    fetchJson.mockRejectedValueOnce("no");
    await userEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("That could not be dismissed."));
  });

  it("keeps every button still while one answer is being sent", async () => {
    fetchJson.mockResolvedValueOnce([notice()]);
    fetchJson.mockReturnValueOnce(new Promise(() => undefined));
    render(<ComponentRecoveryNotices />);

    const addBack = await screen.findByRole("button", { name: "Add them back" });
    await userEvent.click(addBack);

    expect(addBack).toBeDisabled();
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeDisabled();
    expect(addBack.querySelector("svg.animate-spin")).not.toBeNull();
  });

  it("does not spin the buttons of a notice that was not the one pressed", async () => {
    fetchJson.mockResolvedValueOnce([notice(), notice({ kind: "sabnzbd", displayName: "SABnzbd" })]);
    fetchJson.mockReturnValueOnce(new Promise(() => undefined));
    render(<ComponentRecoveryNotices />);

    const buttons = await screen.findAllByRole("button", { name: "Add them back" });
    await userEvent.click(buttons[0]);

    expect(buttons[0].querySelector("svg.animate-spin")).not.toBeNull();
    expect(buttons[1].querySelector("svg.animate-spin")).toBeNull();
    expect(buttons[1]).toBeDisabled();
  });
});
