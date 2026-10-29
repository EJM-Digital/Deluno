import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { AllowedHostsOverview, LocalNetworkAuthOverview } from "../lib/api";

/** System > Security, the parts a person reads and follows (#1329): the detected addresses, the localhost case, the allow-list and the links out. */

vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api")>()), fetchJson: vi.fn() }));

const loaded = vi.hoisted(() => ({ allowedHosts: null as unknown, localNetworkAuth: null as unknown }));
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaded,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));

const { SystemSecurityPage } = await import("./system-security-page");

const hosts: AllowedHostsOverview = {
  enabled: false,
  allowedHosts: [],
  currentHost: "my-pc",
  detectedHosts: [
    { host: "my-pc", reason: "The address you are using right now" },
    { host: "DESKTOP-ABC", reason: "This computer's name" },
    { host: "192.168.1.10", reason: "A LAN address of this computer" },
    { host: "localhost", reason: "Always answers from this machine" },
    { host: "media.example.com", reason: "Seen forwarded by a reverse proxy" }
  ]
};
const network: LocalNetworkAuthOverview = {
  enabled: false, additionalLocalNetworks: [], allowList: [], trustedProxies: [],
  currentAddress: "192.168.1.99", currentAddressIsLocal: true, currentAddressTrusted: true, allowedHostsEnabled: false
};

function renderPage(over: Partial<AllowedHostsOverview> = {}) {
  loaded.allowedHosts = { ...hosts, ...over };
  loaded.localNetworkAuth = network;
  return render(
    <MemoryRouter initialEntries={["/system/security"]}>
      <Routes>
        <Route path="/system/security" element={<SystemSecurityPage />} />
        <Route path="/system/api" element={<p>The API keys page</p>} />
      </Routes>
    </MemoryRouter>
  );
}


describe("Allowed Hosts lists every address Deluno detected", () => {
  it("shows the current address, the computer's name, the LAN address, localhost and a forwarded address, each with why it is offered", () => {
    renderPage();

    for (const [host, reason] of [
      ["my-pc", "The address you are using right now"],
      ["DESKTOP-ABC", "This computer's name"],
      ["192.168.1.10", "A LAN address of this computer"],
      ["localhost", "Always answers from this machine"],
      ["media.example.com", "Seen forwarded by a reverse proxy"]
    ]) {
      expect(screen.getByRole("checkbox", { name: new RegExp(host.replaceAll(".", "[.]")) })).toBeInTheDocument();
      expect(screen.getByText(reason)).toBeInTheDocument();
    }
    expect(screen.getAllByRole("checkbox")).toHaveLength(5);
  });

  it("from localhost the lock-out warning never shows, and the page says localhost always works", () => {
    renderPage({ currentHost: "localhost" });

    fireEvent.click(screen.getByRole("switch", { name: /Only answer on the addresses you choose/ }));
    expect(screen.queryByText(/would be refused rather than lock you out/)).not.toBeInTheDocument();
    expect(screen.getByText(/localhost and 127\.0\.0\.1 on this machine always work/)).toBeInTheDocument();
  });
});

describe("The allow-list under Skip sign-in", () => {
  it("adds a typed address with Enter as well as with Add, refuses an invalid one with a message, and removes an entry", () => {
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: /Skip sign-in on the local network/ }));

    const input = screen.getByPlaceholderText("192.168.1.0/28");
    fireEvent.change(input, { target: { value: "999.1.1.1/99" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(screen.getByText(/is not a valid address or subnet/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Remove .* from the allow list$/ })).not.toBeInTheDocument();

    fireEvent.change(input, { target: { value: "192.168.1.0/28" } });
    expect(screen.queryByText(/is not a valid address or subnet/)).not.toBeInTheDocument();
    fireEvent.keyDown(input, { key: "Enter" });
    expect(screen.getByRole("button", { name: "Remove 192.168.1.0/28 from the allow list" })).toBeInTheDocument();
    expect(input).toHaveValue("");

    fireEvent.change(input, { target: { value: "192.168.1.20" } });
    fireEvent.click(screen.getByRole("button", { name: "Add address to the allow list" }));
    expect(screen.getAllByRole("button", { name: /^Remove .* from the allow list$/ })).toHaveLength(2);

    fireEvent.click(screen.getByRole("button", { name: "Remove 192.168.1.0/28 from the allow list" }));
    expect(screen.queryByRole("button", { name: "Remove 192.168.1.0/28 from the allow list" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove 192.168.1.20 from the allow list" })).toBeInTheDocument();
  });
});

describe("The Sign-in & password card and the API keys link", () => {
  it("tells the person where to change the password, and Manage API keys opens /system/api", () => {
    renderPage();

    const card = screen.getByText("Sign-in & password").closest("div[class]")!.parentElement as HTMLElement;
    expect(within(card).getByText(/Change your password from the account menu, top right of any page/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("link", { name: /Manage API keys/ }));
    expect(screen.getByText("The API keys page")).toBeInTheDocument();
  });
});
