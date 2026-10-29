import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AllowedHostsOverview, LocalNetworkAuthOverview } from "../lib/api";

// The password question (#1433) is answered at once here; its own tests ask it for real.
vi.mock("../components/app/password-prompt", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/app/password-prompt")>()),
  usePasswordPrompt: () => ({ ask: async () => "the-password", prompt: null })
}));

/**
 * #939: Allowed Hosts is a tick list built from detected addresses, never a
 * raw text box, and saving must be refused client-side before it ever
 * reaches the server if the address answering right now is not ticked -- so
 * the warning shows before a person can lock themselves out.
 */
const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: fetchJsonMock
}));

const overview: AllowedHostsOverview = {
  enabled: false,
  allowedHosts: [],
  detectedHosts: [
    { host: "localhost", reason: "Always answers from this machine" },
    { host: "my-pc", reason: "This computer's name" },
    { host: "192.168.1.10", reason: "A LAN address of this computer" }
  ],
  currentHost: "my-pc"
};

const localNetworkAuthOverview: LocalNetworkAuthOverview = {
  enabled: false,
  additionalLocalNetworks: [],
  allowList: [],
  trustedProxies: [],
  currentAddress: "192.168.1.99",
  currentAddressIsLocal: true,
  currentAddressTrusted: true,
  allowedHostsEnabled: false
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({ allowedHosts: overview, localNetworkAuth: localNetworkAuthOverview }),
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SystemSecurityPage } = await import("./system-security-page");

describe("system security page allowed hosts", () => {
  afterEach(() => {
    fetchJsonMock.mockReset();
  });

  it("lists every detected address as an unticked checkbox while off", () => {
    render(
      <MemoryRouter>
        <SystemSecurityPage />
      </MemoryRouter>
    );

    expect(screen.getByText("localhost")).toBeInTheDocument();
    expect(screen.getByText("my-pc")).toBeInTheDocument();
    expect(screen.getByText("192.168.1.10")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /my-pc/ })).not.toBeChecked();
  });

  it("warns instead of letting a save lock out the current address", () => {
    render(
      <MemoryRouter>
        <SystemSecurityPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("switch", { name: /Only answer on the addresses you choose/ }));

    expect(screen.getByText(/would be refused rather than lock you out/)).toBeInTheDocument();
  });

  it("ticking the current address before turning it on clears the warning and saves", async () => {
    fetchJsonMock.mockResolvedValueOnce({ enabled: true, allowedHosts: ["my-pc"] });

    render(
      <MemoryRouter>
        <SystemSecurityPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("checkbox", { name: /my-pc/ }));
    fireEvent.click(screen.getByRole("switch", { name: /Only answer on the addresses you choose/ }));

    expect(screen.queryByText(/would be refused rather than lock you out/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Save Allowed Hosts" }));

    await vi.waitFor(() => expect(fetchJsonMock).toHaveBeenCalledWith(
      "/api/security/allowed-hosts",
      expect.objectContaining({ method: "PUT" })
    ));
  });
});

/**
 * #993: Skip sign-in for devices on your home network. The allow list, when
 * it has entries, narrows the trusted set -- add/remove happens on this
 * card, and an obviously invalid entry is refused before it ever reaches
 * the server.
 */
describe("system security page local network auth", () => {
  afterEach(() => {
    fetchJsonMock.mockReset();
  });

  it("is off by default and shows the current address", () => {
    render(
      <MemoryRouter>
        <SystemSecurityPage />
      </MemoryRouter>
    );

    expect(screen.getByRole("switch", { name: /Skip sign-in on the local network/ })).not.toBeChecked();
    expect(screen.getByText(/192\.168\.1\.99/)).toBeInTheDocument();
  });

  it("saves the setting when turned on", async () => {
    fetchJsonMock.mockResolvedValueOnce({ enabled: true, additionalLocalNetworks: [], allowList: [], trustedProxies: [] });

    render(
      <MemoryRouter>
        <SystemSecurityPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("switch", { name: /Skip sign-in on the local network/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await vi.waitFor(() => expect(fetchJsonMock).toHaveBeenCalledWith(
      "/api/security/local-network-auth",
      expect.objectContaining({
        method: "PUT",
        body: JSON.stringify({ enabled: true, additionalLocalNetworks: [], allowList: [], trustedProxies: [] })
      })
    ));
  });

  it("adds and removes an allow-list entry, and refuses an invalid one", () => {
    render(
      <MemoryRouter>
        <SystemSecurityPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("switch", { name: /Skip sign-in on the local network/ }));

    const input = screen.getByPlaceholderText("192.168.1.0/28");
    const addButton = screen.getByRole("button", { name: "Add address to the allow list" });
    fireEvent.change(input, { target: { value: "not-an-address" } });
    fireEvent.click(addButton);
    expect(screen.getByText(/is not a valid address or subnet/)).toBeInTheDocument();

    fireEvent.change(input, { target: { value: "192.168.1.20" } });
    fireEvent.click(addButton);
    expect(screen.getByText("192.168.1.20")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Remove 192.168.1.20 from the allow list" }));
    expect(screen.queryByText("192.168.1.20")).not.toBeInTheDocument();
  });
});
