import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { ConnectInstalledApps } = await import("./connect-installed-apps");

const base = { reachable: true, defaultHost: "localhost" };
const qbittorrent = { ...base, kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, connected: false, defaultPort: 8081 };
const nzbget = { ...base, kind: "nzbget", displayName: "NZBGet", category: "download-client", installed: true, connected: true, defaultPort: 6789 };
const weirNew = { ...base, kind: "weir", displayName: "Weir", category: "processor", installed: true, connected: false, needsAccount: true, accountExists: false, defaultPort: 9347 };
const weirExisting = { ...weirNew, needsAccount: false, accountExists: true };
const sabnzbd = { ...base, kind: "sabnzbd", displayName: "SABnzbd", category: "download-client", installed: false, connected: false, defaultPort: 8080 };

function show(props: Partial<Parameters<typeof ConnectInstalledApps>[0]> = {}) {
  return render(<MemoryRouter><ConnectInstalledApps {...props} /></MemoryRouter>);
}

describe("Connect the apps you installed (#1061)", () => {
  afterEach(() => fetchJson.mockReset());

  it("shows nothing when no bundled app is installed", async () => {
    fetchJson.mockResolvedValueOnce([sabnzbd]);
    const { container } = show();
    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });

  it("shows each installed app with its state and a uniquely named Connect button", async () => {
    fetchJson.mockResolvedValueOnce([qbittorrent, nzbget, weirNew, sabnzbd]);
    show();

    expect(await screen.findByRole("button", { name: "Connect qBittorrent" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Connect Weir" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Connect all" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Connect NZBGet" })).toBeNull(); // already connected
    expect(screen.queryByText("SABnzbd")).toBeNull(); // not installed
    expect(screen.getByText("Connected")).toBeTruthy();
    expect(screen.getByText("Not connected")).toBeTruthy();
    expect(screen.getByText("Needs your Weir account")).toBeTruthy();
  });

  it("connects one app and says so in plain words", async () => {
    fetchJson
      .mockResolvedValueOnce([qbittorrent])
      .mockResolvedValueOnce({ kind: "qbittorrent", name: "qBittorrent", healthy: true, message: "qBittorrent is connected." })
      .mockResolvedValueOnce([{ ...qbittorrent, connected: true }]);
    show();

    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));

    expect(await screen.findByText("qBittorrent is connected.")).toBeTruthy();
    expect(fetchJson).toHaveBeenCalledWith("/api/system/bundled-components/qbittorrent/connect", { method: "POST" });
    expect(await screen.findByText("Connected")).toBeTruthy();
  });

  it("shows a failure and hands the host and port to the connection form when a sign-in is needed", async () => {
    const failed = { kind: "qbittorrent", name: "qBittorrent", healthy: false, message: "Nothing was saved: enter your own login.", needsSignIn: true, protocol: "qbittorrent", host: "localhost", port: 8081 };
    fetchJson.mockResolvedValueOnce([qbittorrent]).mockResolvedValueOnce(failed).mockResolvedValueOnce([qbittorrent]);
    const onNeedsSignIn = vi.fn();
    show({ onNeedsSignIn });

    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));

    expect(await screen.findByText(/Nothing was saved/)).toBeTruthy();
    expect(onNeedsSignIn).toHaveBeenCalledWith(expect.objectContaining({ kind: "qbittorrent" }), failed);
    expect(screen.getByRole("button", { name: "Enter your own login for qBittorrent" })).toBeTruthy();
  });

  it("shows a plain failure when the request itself fails", async () => {
    fetchJson.mockResolvedValueOnce([qbittorrent]).mockRejectedValueOnce(new Error("qBittorrent did not answer."));
    show();

    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));

    expect(await screen.findByText("qBittorrent did not answer.")).toBeTruthy();
  });

  it("connects everything at once and leaves Weir waiting for the account", async () => {
    fetchJson
      .mockResolvedValueOnce([qbittorrent, weirNew])
      .mockResolvedValueOnce({ results: [
        { kind: "qbittorrent", name: "qBittorrent", healthy: true, message: "qBittorrent is connected." },
        { kind: "weir", name: "Weir", healthy: false, needsAccount: true, message: "Weir waits for your own account." }
      ] })
      .mockResolvedValueOnce([{ ...qbittorrent, connected: true }, weirNew]);
    show();

    fireEvent.click(await screen.findByRole("button", { name: "Connect all" }));

    expect(await screen.findByText("qBittorrent is connected.")).toBeTruthy();
    expect(screen.getByText("Weir waits for your own account.")).toBeTruthy();
    expect(fetchJson).toHaveBeenCalledWith("/api/system/bundled-components/connect-all", { method: "POST" });
    expect(screen.getByText("Needs your Weir account")).toBeTruthy();
  });
});

describe("Weir account (#1061, #968)", () => {
  afterEach(() => fetchJson.mockReset());

  it("asks to create an account, with a confirm field, when Weir has none yet", async () => {
    fetchJson.mockResolvedValueOnce([weirNew]);
    show();

    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));

    expect(screen.getByLabelText("Weir username")).toBeTruthy();
    expect(screen.getByLabelText("Weir password")).toBeTruthy();
    expect(screen.getByLabelText("Confirm Weir password")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Create Weir account and connect" })).toBeTruthy();
  });

  it("asks only to sign in when Weir already has an account", async () => {
    fetchJson.mockResolvedValueOnce([weirExisting]);
    show();

    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));

    expect(screen.queryByLabelText("Confirm Weir password")).toBeNull();
    expect(screen.getByRole("button", { name: "Sign in to Weir and connect" })).toBeTruthy();
  });

  it("refuses mismatched passwords without sending anything", async () => {
    fetchJson.mockResolvedValueOnce([weirNew]);
    show();
    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));

    fireEvent.change(screen.getByLabelText("Weir username"), { target: { value: "james" } });
    fireEvent.change(screen.getByLabelText("Weir password"), { target: { value: "one" } });
    fireEvent.change(screen.getByLabelText("Confirm Weir password"), { target: { value: "two" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Weir account and connect" }));

    expect(await screen.findByText("The two passwords do not match.")).toBeTruthy();
    expect(fetchJson).toHaveBeenCalledTimes(1);
  });

  it("sends the account once, then clears the password fields and never echoes them", async () => {
    fetchJson
      .mockResolvedValueOnce([weirNew])
      .mockResolvedValueOnce({ kind: "weir", name: "Weir", healthy: false, message: "Weir did not accept that." })
      .mockResolvedValueOnce([weirNew]);
    const { container } = show();
    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));

    fireEvent.change(screen.getByLabelText("Weir username"), { target: { value: "james" } });
    fireEvent.change(screen.getByLabelText("Weir password"), { target: { value: "s3cret-pass" } });
    fireEvent.change(screen.getByLabelText("Confirm Weir password"), { target: { value: "s3cret-pass" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Weir account and connect" }));

    await screen.findByText("Weir did not accept that.");
    expect(fetchJson).toHaveBeenCalledWith("/api/system/bundled-components/weir/connect", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: "james", password: "s3cret-pass" })
    });
    expect((screen.getByLabelText("Weir password") as HTMLInputElement).value).toBe("");
    expect((screen.getByLabelText("Confirm Weir password") as HTMLInputElement).value).toBe("");
    expect(container.innerHTML).not.toContain("s3cret-pass");
  });

  it("says in plain words that Weir may still be starting while the connect waits (#1258)", async () => {
    let finish: (value: unknown) => void = () => {};
    fetchJson
      .mockResolvedValueOnce([weirExisting])
      .mockReturnValueOnce(new Promise((resolve) => { finish = resolve; }))
      .mockResolvedValueOnce([weirExisting]);
    show();
    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));
    fireEvent.change(screen.getByLabelText("Weir username"), { target: { value: "james" } });
    fireEvent.change(screen.getByLabelText("Weir password"), { target: { value: "pw" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in to Weir and connect" }));

    expect(await screen.findByText(/If Weir is still starting, this can take a few minutes after an update/)).toBeTruthy();
    finish({ kind: "weir", name: "Weir", healthy: false, message: "Weir did not accept that." });
    await screen.findByText("Weir did not accept that.");
    expect(screen.queryByText(/If Weir is still starting/)).toBeNull();
  });

  it("closes the account form and marks Weir connected after a good connect", async () => {
    fetchJson
      .mockResolvedValueOnce([weirExisting])
      .mockResolvedValueOnce({ kind: "weir", name: "Weir", healthy: true, message: "Weir is connected." })
      .mockResolvedValueOnce([{ ...weirExisting, connected: true }]);
    show();
    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));
    fireEvent.change(screen.getByLabelText("Weir username"), { target: { value: "james" } });
    fireEvent.change(screen.getByLabelText("Weir password"), { target: { value: "pw" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in to Weir and connect" }));

    expect(await screen.findByText("Weir is connected.")).toBeTruthy();
    expect(screen.queryByLabelText("Weir password")).toBeNull();
  });
});

describe("Weir account inside the guided setup's own form (#1132)", () => {
  afterEach(() => fetchJson.mockReset());

  it("sends the Weir account from inside another form without submitting that form", async () => {
    fetchJson
      .mockResolvedValueOnce([weirNew])
      .mockResolvedValueOnce({ kind: "weir", name: "Weir", healthy: true, message: "Weir is connected." })
      .mockResolvedValueOnce([{ ...weirNew, connected: true }]);
    const outerSubmit = vi.fn((event: { preventDefault: () => void }) => event.preventDefault());
    render(
      <MemoryRouter>
        <form onSubmit={outerSubmit}>
          <ConnectInstalledApps />
        </form>
      </MemoryRouter>
    );
    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));
    fireEvent.change(screen.getByLabelText("Weir username"), { target: { value: "james" } });
    fireEvent.change(screen.getByLabelText("Weir password"), { target: { value: "pw" } });
    fireEvent.change(screen.getByLabelText("Confirm Weir password"), { target: { value: "pw" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Weir account and connect" }));

    expect(await screen.findByText("Weir is connected.")).toBeTruthy();
    expect(fetchJson).toHaveBeenCalledWith("/api/system/bundled-components/weir/connect", expect.objectContaining({ method: "POST" }));
    expect(outerSubmit).not.toHaveBeenCalled();
  });
});

describe("The panel follows the connections (#1130)", () => {
  afterEach(() => fetchJson.mockReset());

  it("lists the apps as a table with a name, a status and an action column", async () => {
    fetchJson.mockResolvedValueOnce([qbittorrent, nzbget]);
    show();

    await screen.findByRole("button", { name: "Connect qBittorrent" });
    expect(screen.getAllByRole("row").length).toBeGreaterThanOrEqual(3); // header + one per app
    expect(screen.getByRole("columnheader", { name: "Status" })).toBeTruthy();
  });

  it("tells the screen each time one more app is connected, so its table can show it", async () => {
    fetchJson
      .mockResolvedValueOnce([qbittorrent, nzbget])
      .mockResolvedValueOnce({ kind: "qbittorrent", name: "qBittorrent", healthy: true, message: "qBittorrent is connected." })
      .mockResolvedValueOnce([{ ...qbittorrent, connected: true }, nzbget]);
    const onConnected = vi.fn();
    show({ onConnected });

    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));

    await waitFor(() => expect(onConnected).toHaveBeenCalledTimes(1));
    expect(await screen.findAllByText("Connected")).toHaveLength(2);
  });

  it("gets out of the way once everything is connected, saying so in one line", async () => {
    fetchJson
      .mockResolvedValueOnce([qbittorrent])
      .mockResolvedValueOnce({ kind: "qbittorrent", name: "qBittorrent", healthy: true, message: "qBittorrent is connected." })
      .mockResolvedValueOnce([{ ...qbittorrent, connected: true }]);
    show({ hideConnected: true });

    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));

    expect(await screen.findByText("qBittorrent is connected.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Connect all" })).toBeNull();
    expect(screen.queryByRole("row")).toBeNull();
  });
});
