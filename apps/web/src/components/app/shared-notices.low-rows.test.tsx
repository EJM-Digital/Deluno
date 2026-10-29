import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { ComponentRecoveryNotices } = await import("./component-recovery-notices");
const { HowThisWorks } = await import("./how-this-works");

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

describe("the How this works explainer, low rows (#1329)", () => {
  const steps = (count: number) => Array.from({ length: count }, (_, index) => ({ title: `Step ${index + 1}`, body: `Body ${index + 1}` }));

  it.each([
    [2, "md:grid-cols-2"],
    [3, "md:grid-cols-3"],
    [4, "md:grid-cols-4"]
  ])("lays %s steps out in the %s columns, numbers each step, and puts a rule between all but the last", (count, columns) => {
    const { container } = render(<HowThisWorks id={`layout-${count}`} lead="The lead." steps={steps(count)} />);

    const list = container.querySelector("ol")!;
    expect(list.className).toContain(columns);
    const items = Array.from(list.querySelectorAll("li"));
    expect(items.map((item) => item.querySelector("p")!.textContent)).toEqual(steps(count).map((step, index) => `${index + 1}. ${step.title}`));
    expect(items.slice(0, -1).every((item) => item.className.includes("md:border-r"))).toBe(true);
    expect(items.at(-1)!.className).not.toContain("md:border-r");
  });

  it("an empty step list renders just the lead", () => {
    const { container } = render(<HowThisWorks id="no-steps" lead="Only the lead." steps={[]} />);

    expect(container.querySelector("ol")).toBeNull();
    expect(screen.getByText("Only the lead.")).toBeTruthy();
  });

  it("with the browser's storage blocked it neither crashes nor loses the first open", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
    const user = userEvent.setup();

    render(<HowThisWorks id="blocked" lead="The lead." steps={steps(2)} />);
    const toggle = screen.getByRole("button", { name: "How this works" });
    expect(toggle).toHaveAttribute("aria-expanded", "false");

    await user.click(toggle);

    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("The lead.")).toBeVisible();
  });
});
