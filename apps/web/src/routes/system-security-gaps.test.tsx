import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AllowedHostsOverview, LocalNetworkAuthOverview } from "../lib/api";

/**
 * System > Security (#1294): the Allowed Hosts card and the Skip sign-in card, control by control. The page is
 * handed the two overviews the route reads; saving goes through the shared JSON helper, mocked here so each test
 * shows the request that was made and the sentence the person then reads.
 */
const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: fetchJsonMock
}));

const hosts: AllowedHostsOverview = {
  enabled: false,
  allowedHosts: [],
  detectedHosts: [
    { host: "localhost", reason: "Always answers from this machine" },
    { host: "my-pc", reason: "This computer's name" }
  ],
  currentHost: "my-pc"
};
const network: LocalNetworkAuthOverview = {
  enabled: false,
  additionalLocalNetworks: [],
  allowList: [],
  trustedProxies: [],
  currentAddress: "192.168.1.99",
  currentAddressIsLocal: true,
  currentAddressTrusted: true,
  allowedHostsEnabled: false
};
const loaded = vi.hoisted(() => ({ allowedHosts: null as unknown, localNetworkAuth: null as unknown }));
const revalidate = vi.hoisted(() => vi.fn());

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaded,
  useRevalidator: () => ({ revalidate })
}));

const { SystemSecurityPage } = await import("./system-security-page");

let bump: () => void = () => undefined;
function Harness() {
  const [, setTick] = useState(0);
  bump = () => setTick((tick) => tick + 1);
  return <SystemSecurityPage />;
}

/** After a save the route reads the overviews again; this hands the page what the server now holds. */
function serverNowHolds(next: { allowedHosts?: Partial<AllowedHostsOverview>; localNetworkAuth?: Partial<LocalNetworkAuthOverview> }) {
  revalidate.mockImplementation(() => {
    loaded.allowedHosts = { ...(loaded.allowedHosts as object), ...next.allowedHosts };
    loaded.localNetworkAuth = { ...(loaded.localNetworkAuth as object), ...next.localNetworkAuth };
    act(() => bump());
  });
}

function renderPage(over: { allowedHosts?: Partial<AllowedHostsOverview>; localNetworkAuth?: Partial<LocalNetworkAuthOverview> } = {}) {
  loaded.allowedHosts = { ...hosts, ...over.allowedHosts };
  loaded.localNetworkAuth = { ...network, ...over.localNetworkAuth };
  return render(
    <MemoryRouter>
      <Harness />
    </MemoryRouter>
  );
}

afterEach(() => {
  fetchJsonMock.mockReset();
  revalidate.mockReset();
});

/** Ticks my-pc (so the save would not lock the current address out), turns the switch on and presses Save. */
function saveAllowedHostsOn() {
  fireEvent.click(screen.getByRole("checkbox", { name: /my-pc/ }));
  fireEvent.click(screen.getByRole("switch", { name: /Only answer on the addresses you choose/ }));
  fireEvent.click(screen.getByRole("button", { name: "Save Allowed Hosts" }));
}

describe("Allowed Hosts: adding an address", () => {
  it("a typed address is added when Add is pressed and the box clears; it goes out with the save", async () => {
    renderPage();
    const box = screen.getByPlaceholderText("*.example.com");
    fireEvent.change(box, { target: { value: "deluno.example.com" } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(box).toHaveValue("");

    fetchJsonMock.mockResolvedValueOnce({});
    saveAllowedHostsOn();
    await waitFor(() =>
      expect(fetchJsonMock).toHaveBeenCalledWith(
        "/api/security/allowed-hosts",
        expect.objectContaining({ body: JSON.stringify({ enabled: true, allowedHosts: ["deluno.example.com", "my-pc"] }) })
      )
    );
  });

  it("Enter adds a wildcard like *.example.com", async () => {
    renderPage();
    const box = screen.getByPlaceholderText("*.example.com");
    fireEvent.change(box, { target: { value: "*.example.com" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(box).toHaveValue("");

    fetchJsonMock.mockResolvedValueOnce({});
    saveAllowedHostsOn();
    await waitFor(() =>
      expect(fetchJsonMock).toHaveBeenCalledWith(
        "/api/security/allowed-hosts",
        expect.objectContaining({ body: JSON.stringify({ enabled: true, allowedHosts: ["*.example.com", "my-pc"] }) })
      )
    );
  });

  it("a blank entry is ignored: Add is off and Enter adds nothing", () => {
    renderPage();
    const box = screen.getByPlaceholderText("*.example.com");
    expect(screen.getByRole("button", { name: "Add" })).toBeDisabled();
    fireEvent.change(box, { target: { value: "   " } });
    expect(screen.getByRole("button", { name: "Add" })).toBeDisabled();
    fireEvent.keyDown(box, { key: "Enter" });
    // Nothing was added, so nothing is unsaved.
    expect(screen.getByRole("button", { name: "Save Allowed Hosts" })).toBeDisabled();
  });

  it("the same address twice, in any letter case, is added once", async () => {
    renderPage();
    const box = screen.getByPlaceholderText("*.example.com");
    for (const value of ["Deluno.Example.com", "deluno.example.com", "DELUNO.EXAMPLE.COM"]) {
      fireEvent.change(box, { target: { value } });
      fireEvent.keyDown(box, { key: "Enter" });
    }
    fetchJsonMock.mockResolvedValueOnce({});
    saveAllowedHostsOn();
    await waitFor(() => expect(fetchJsonMock).toHaveBeenCalled());
    const body = JSON.parse(fetchJsonMock.mock.calls[0]![1].body as string) as { allowedHosts: string[] };
    expect(body.allowedHosts.filter((host) => host.toLowerCase() === "deluno.example.com")).toHaveLength(1);
  });
});

describe("Allowed Hosts: taking an added address off again", () => {
  it("an address added by hand is listed as such, and unticking it removes it from what is saved", async () => {
    renderPage({ allowedHosts: { allowedHosts: ["deluno.example.com", "my-pc"] } });
    const typed = screen.getByRole("checkbox", { name: /deluno.example.com/ });
    expect(typed).toBeChecked();
    expect(screen.getByText("Added by hand")).toBeInTheDocument();

    fireEvent.click(typed);
    expect(typed).not.toBeChecked();
    fetchJsonMock.mockResolvedValueOnce({});
    fireEvent.click(screen.getByRole("button", { name: "Save Allowed Hosts" }));
    await waitFor(() => expect(fetchJsonMock).toHaveBeenCalled());
    expect(JSON.parse(fetchJsonMock.mock.calls[0]![1].body as string)).toEqual({ enabled: false, allowedHosts: ["my-pc"] });
  });

  // Defect: an address typed into the box and added is kept for the save but is not drawn in the list until it has been saved
  // (the rows come only from the detected addresses and the saved ones), so the box clears and nothing shows it was taken
  // (system-security-page.tsx, `rows`).
  it.skip("an address that was just added shows in the list at once, ticked", () => {
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("*.example.com"), { target: { value: "deluno.example.com" } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(screen.getByRole("checkbox", { name: /deluno.example.com/ })).toBeChecked();
  });
});

describe("Allowed Hosts: saving", () => {
  it("Save Allowed Hosts is off until something changes", () => {
    renderPage();
    expect(screen.getByRole("button", { name: "Save Allowed Hosts" })).toBeDisabled();
    fireEvent.click(screen.getByRole("checkbox", { name: /localhost/ }));
    expect(screen.getByRole("button", { name: "Save Allowed Hosts" })).not.toBeDisabled();
    expect(screen.getByText("Unsaved changes")).toBeInTheDocument();
  });

  it("saving with it on puts the enabled flag and the ticked addresses, and says Deluno now only answers on them", async () => {
    renderPage();
    serverNowHolds({ allowedHosts: { enabled: true, allowedHosts: ["my-pc"] } });
    fetchJsonMock.mockResolvedValueOnce({});
    saveAllowedHostsOn();

    await waitFor(() =>
      expect(fetchJsonMock).toHaveBeenCalledWith(
        "/api/security/allowed-hosts",
        expect.objectContaining({ method: "PUT", body: JSON.stringify({ enabled: true, allowedHosts: ["my-pc"] }) })
      )
    );
    expect(await screen.findByText("Deluno now only answers on the addresses you ticked.")).toBeInTheDocument();
  });

  it("turning it off and saving says Deluno answers on any address again", async () => {
    renderPage({ allowedHosts: { enabled: true, allowedHosts: ["my-pc"] } });
    serverNowHolds({ allowedHosts: { enabled: false } });
    fireEvent.click(screen.getByRole("switch", { name: /Only answer on the addresses you choose/ }));
    fetchJsonMock.mockResolvedValueOnce({});
    fireEvent.click(screen.getByRole("button", { name: "Save Allowed Hosts" }));

    expect(await screen.findByText("Deluno answers on any address again.")).toBeInTheDocument();
  });

  it("a save the server refuses shows the error text", async () => {
    renderPage();
    fetchJsonMock.mockRejectedValueOnce(new Error("That would lock you out of this address."));
    saveAllowedHostsOn();

    expect(await screen.findByText("That would lock you out of this address.")).toBeInTheDocument();
    expect(screen.queryByText("Deluno now only answers on the addresses you ticked.")).toBeNull();
  });
});

describe("Skip sign-in on the local network", () => {
  it("a warning shows when it is on while Allowed Hosts is off, and goes when Allowed Hosts is on", () => {
    const first = renderPage();
    fireEvent.click(screen.getByRole("switch", { name: /Skip sign-in on the local network/ }));
    expect(screen.getByText(/Allowed Hosts \(above\) is off\. Deluno currently answers on any address/)).toBeInTheDocument();
    first.unmount();

    renderPage({ localNetworkAuth: { allowedHostsEnabled: true } });
    fireEvent.click(screen.getByRole("switch", { name: /Skip sign-in on the local network/ }));
    expect(screen.queryByText(/Allowed Hosts \(above\) is off/)).toBeNull();
  });

  it("no warning shows while the setting is off", () => {
    renderPage();
    expect(screen.queryByText(/Allowed Hosts \(above\) is off/)).toBeNull();
  });

  it("turning it on and pressing Save puts the flag and the lists, and says devices on the local network now skip sign-in", async () => {
    renderPage({ localNetworkAuth: { additionalLocalNetworks: ["10.9.0.0/24"], trustedProxies: ["192.168.1.2"] } });
    serverNowHolds({ localNetworkAuth: { enabled: true } });
    fireEvent.click(screen.getByRole("switch", { name: /Skip sign-in on the local network/ }));
    fetchJsonMock.mockResolvedValueOnce({});
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(fetchJsonMock).toHaveBeenCalledWith(
        "/api/security/local-network-auth",
        expect.objectContaining({
          method: "PUT",
          body: JSON.stringify({ enabled: true, additionalLocalNetworks: ["10.9.0.0/24"], allowList: [], trustedProxies: ["192.168.1.2"] })
        })
      )
    );
    expect(await screen.findByText("Devices on your local network now skip sign-in.")).toBeInTheDocument();
  });

  it("turning it off and saving says every device signs in again", async () => {
    renderPage({ localNetworkAuth: { enabled: true } });
    serverNowHolds({ localNetworkAuth: { enabled: false } });
    fireEvent.click(screen.getByRole("switch", { name: /Skip sign-in on the local network/ }));
    fetchJsonMock.mockResolvedValueOnce({});
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Every device signs in again, including ones on your local network.")).toBeInTheDocument();
  });

  it("a save the server refuses shows the error text", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: /Skip sign-in on the local network/ }));
    fetchJsonMock.mockRejectedValueOnce(new Error("Only the owner can change this."));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Only the owner can change this.")).toBeInTheDocument();
  });
});
