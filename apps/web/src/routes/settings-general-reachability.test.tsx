import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/** #1431: how Deluno is reached is two read-only boxes, whatever the server answers or when it fails to. */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({ libraries: [], qualityProfiles: [], settings: emptyPlatformSettingsSnapshot }),
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsGeneralPage } = await import("./settings-general-page");

function lan(reply: object | "fail") {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes("lan-access")) { if (reply === "fail") throw new Error("offline"); return new Response(JSON.stringify(reply), { status: 200 }); }
    if (url.includes("log-level")) return new Response("no", { status: 500 });
    return new Response("{}", { status: 200 });
  });
  return render(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
}

describe("How Deluno is reached", () => {
  afterEach(() => vi.clearAllMocks());

  it.each([
    ["open", "Yes"],
    ["blocked", "No"],
    ["notChecked", "Unknown from here"]
  ])("reads %s as %s and shows the port and the server's sentence", async (state, label) => {
    lan({ state, port: 5099, message: "Checked a moment ago." });
    await waitFor(() => expect(screen.getByLabelText("Other devices on your network can connect")).toHaveValue(label));
    expect(screen.getByLabelText("Port")).toHaveValue("5099");
    expect(screen.getByText("Checked a moment ago.")).toBeInTheDocument();
    expect(screen.getByLabelText("Other devices on your network can connect")).toHaveAttribute("readonly");
  });

  it("shows a dash for a missing port and no sentence when the server gives none", async () => {
    lan({ state: "open", port: null, message: "" });
    await waitFor(() => expect(screen.getByLabelText("Port")).toHaveValue("—"));
    expect(document.getElementById(screen.getByLabelText("Other devices on your network can connect").getAttribute("aria-describedby") ?? "none")).toBeNull();
  });

  it("says Loading… until the server answers and Unavailable when it cannot", async () => {
    lan({ state: "open", port: 1, message: "" });
    expect(screen.getByLabelText("Port")).toHaveValue("Loading…");
    expect(screen.getByLabelText("Other devices on your network can connect")).toHaveValue("Loading…");
    await waitFor(() => expect(screen.getByLabelText("Port")).toHaveValue("1"));
  });

  it("says Unavailable in both boxes when the reachability cannot be read", async () => {
    lan("fail");
    await waitFor(() => expect(screen.getByLabelText("Port")).toHaveValue("Unavailable"));
    expect(screen.getByLabelText("Other devices on your network can connect")).toHaveValue("Unavailable");
  });

  it("keeps the long explanation of where the port is set behind an (i) that shows and hides it", async () => {
    lan({ state: "open", port: 1, message: "" });
    await waitFor(() => expect(screen.getByLabelText("Port")).toHaveValue("1"));
    const user = userEvent.setup();
    const icon = screen.getByRole("button", { name: "More about How Deluno is reached" });
    expect(icon).toHaveAttribute("aria-expanded", "false");
    expect(document.body.textContent).not.toContain("Server:Port");

    await user.click(icon);
    expect(icon).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("note").textContent).toContain("Server:Port and Server:AllowLan");

    await user.click(icon);
    expect(icon).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("note")).toBeNull();
  });
});
