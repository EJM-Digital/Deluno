import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { ConnectInstalledApps } = await import("./connect-installed-apps");
const { DetectedComponents } = await import("./bundled-components-detected");

const base = { reachable: true, defaultHost: "localhost" };
const qbittorrent = { ...base, kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, connected: false, defaultPort: 8081 };
const weirExisting = { ...base, kind: "weir", displayName: "Weir", category: "processor", installed: true, connected: false, needsAccount: false, accountExists: true, defaultPort: 9347 };

const show = (props: Partial<Parameters<typeof ConnectInstalledApps>[0]> = {}) => render(<MemoryRouter><ConnectInstalledApps {...props} /></MemoryRouter>);

afterEach(() => fetchJson.mockReset());

describe("Connect the apps you installed, low rows (#1329)", () => {
  it("Connect all failing as a whole shows its error line", async () => {
    fetchJson.mockResolvedValueOnce([qbittorrent]).mockRejectedValueOnce(new Error("Deluno could not reach its own service."));
    show();

    fireEvent.click(await screen.findByRole("button", { name: "Connect all" }));

    expect((await screen.findByRole("alert")).textContent).toBe("Deluno could not reach its own service.");
    expect(screen.getByRole("button", { name: "Connect qBittorrent" })).toBeTruthy();
  });

  it("an empty username or password in the Weir account shows the line asking for both and sends nothing", async () => {
    fetchJson.mockResolvedValueOnce([weirExisting]);
    show();
    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));

    fireEvent.click(await screen.findByRole("button", { name: "Sign in to Weir and connect" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Enter a username and password for your Weir account.");

    fireEvent.change(screen.getByLabelText("Weir username"), { target: { value: "sam" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in to Weir and connect" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Enter a username and password for your Weir account.");
    expect(fetchJson).toHaveBeenCalledTimes(1);
  });

  it("pressing Enter in a Weir account field submits the account", async () => {
    fetchJson
      .mockResolvedValueOnce([weirExisting])
      .mockResolvedValueOnce({ kind: "weir", name: "Weir", healthy: true, message: "Weir is connected." })
      .mockResolvedValueOnce([{ ...weirExisting, connected: true }]);
    show();
    fireEvent.click(await screen.findByRole("button", { name: "Connect Weir" }));
    fireEvent.change(await screen.findByLabelText("Weir username"), { target: { value: "sam" } });
    fireEvent.change(screen.getByLabelText("Weir password"), { target: { value: "pw-1" } });

    fireEvent.keyDown(screen.getByLabelText("Weir password"), { key: "Enter" });

    await waitFor(() => expect(fetchJson).toHaveBeenCalledWith(
      "/api/system/bundled-components/weir/connect",
      expect.objectContaining({ method: "POST", body: JSON.stringify({ username: "sam", password: "pw-1" }) })
    ));
    expect(await screen.findByText("Weir is connected.")).toBeTruthy();
  });

  it("each Connect button holds the short word for a phone and the app's name for a wider screen", async () => {
    fetchJson.mockResolvedValueOnce([qbittorrent]);
    show();

    const button = await screen.findByRole("button", { name: "Connect qBittorrent" });

    const phone = button.querySelector('span[class~="md:hidden"]');
    const wide = button.querySelector('span[class~="hidden"][class~="md:inline"]');
    expect(phone?.textContent).toBe("Connect");
    expect(wide?.textContent).toBe("Connect qBittorrent");
  });

  it("a refresh that fails after a connect leaves the list as it was, and a first load that fails renders nothing", async () => {
    fetchJson
      .mockResolvedValueOnce([qbittorrent])
      .mockResolvedValueOnce({ kind: "qbittorrent", name: "qBittorrent", healthy: true, message: "qBittorrent is connected." })
      .mockRejectedValueOnce(new Error("offline"));
    const first = show();
    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));
    expect(await screen.findByText("qBittorrent is connected.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Connect qBittorrent" })).toBeTruthy();
    first.unmount();

    fetchJson.mockReset();
    fetchJson.mockRejectedValueOnce(new Error("offline"));
    const second = show();
    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(second.container.textContent).toBe("");
  });

  it("where the host screen gives no sign-in handler, an app that needs a login falls back to the Use handler", async () => {
    const failed = { kind: "qbittorrent", name: "qBittorrent", healthy: false, message: "Enter your own login.", needsSignIn: true, host: "localhost", port: 8081 };
    fetchJson.mockResolvedValueOnce([qbittorrent]).mockResolvedValueOnce([]).mockResolvedValueOnce(failed).mockResolvedValueOnce([qbittorrent]);
    const onUse = vi.fn();
    render(<MemoryRouter><DetectedComponents category="download-client" configuredKinds={[]} onUse={onUse} /></MemoryRouter>);

    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));

    await waitFor(() => expect(onUse).toHaveBeenCalledTimes(1));
    expect(onUse.mock.calls[0][0]).toMatchObject({ kind: "qbittorrent", defaultPort: 8081 });
  });
});
