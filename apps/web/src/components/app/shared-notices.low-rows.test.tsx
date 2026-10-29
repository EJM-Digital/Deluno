import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { ComponentRecoveryNotices } = await import("./component-recovery-notices");

const notice = { kind: "transmission", displayName: "Transmission", message: "Transmission's settings were rebuilt; 3 torrents may need adding back.", itemCount: 3, canReadd: true };

afterEach(() => { cleanup(); fetchJson.mockReset(); vi.restoreAllMocks(); });

describe("the recovery notice, low rows (#1329)", () => {
  it("a failing Add them back shows the reason beside the buttons and keeps the notice", async () => {
    fetchJson.mockResolvedValueOnce([notice]).mockRejectedValueOnce(new Error("Transmission is not running."));
    render(<ComponentRecoveryNotices />);

    fireEvent.click(await screen.findByRole("button", { name: "Add them back" }));

    expect((await screen.findByRole("alert")).textContent).toBe("Transmission is not running.");
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeTruthy();
  });

  it("a failing Dismiss shows the reason and keeps the notice", async () => {
    fetchJson.mockResolvedValueOnce([notice]).mockRejectedValueOnce(new Error("Deluno could not clear it."));
    render(<ComponentRecoveryNotices />);

    fireEvent.click(await screen.findByRole("button", { name: "Dismiss" }));

    expect((await screen.findByRole("alert")).textContent).toBe("Deluno could not clear it.");
    expect(screen.getByRole("status", { name: "Transmission was rebuilt" })).toBeTruthy();
  });

  it("a request with no message falls back to a plain sentence naming the app", async () => {
    fetchJson.mockResolvedValueOnce([notice]).mockRejectedValueOnce("nope");
    render(<ComponentRecoveryNotices />);

    fireEvent.click(await screen.findByRole("button", { name: "Add them back" }));

    expect((await screen.findByRole("alert")).textContent).toBe("Deluno could not add the torrents back to Transmission.");
  });

  it("the list falls to empty when the load fails, and Add them back is hidden when the app cannot be re-added", async () => {
    fetchJson.mockRejectedValueOnce(new Error("down"));
    const failed = render(<ComponentRecoveryNotices />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(failed.container.textContent).toBe("");
    failed.unmount();

    fetchJson.mockReset();
    fetchJson.mockResolvedValueOnce([{ ...notice, canReadd: false }]);
    render(<ComponentRecoveryNotices />);

    expect(await screen.findByRole("button", { name: "Dismiss" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Add them back" })).toBeNull();
  });

  it("both buttons are disabled while one of them is running", async () => {
    let release!: (value: unknown) => void;
    fetchJson.mockResolvedValueOnce([notice]).mockReturnValueOnce(new Promise((resolve) => { release = resolve; }));
    render(<ComponentRecoveryNotices />);

    fireEvent.click(await screen.findByRole("button", { name: "Add them back" }));

    await waitFor(() => expect((screen.getByRole("button", { name: "Dismiss" }) as HTMLButtonElement).disabled).toBe(true));
    expect((screen.getByRole("button", { name: "Add them back" }) as HTMLButtonElement).disabled).toBe(true);
    fetchJson.mockResolvedValueOnce([]);
    release({ done: true, message: "Added." });
    expect(await screen.findByText("Added.")).toBeTruthy();
  });
});
