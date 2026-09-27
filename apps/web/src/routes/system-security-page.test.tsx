import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AllowedHostsOverview } from "../lib/api";

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

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({ allowedHosts: overview }),
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
