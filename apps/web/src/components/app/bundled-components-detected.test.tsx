import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { DetectedComponents } = await import("./bundled-components-detected");

const qbittorrent = { kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, reachable: true, defaultHost: "localhost", defaultPort: 8080 };
const weir = { kind: "weir", displayName: "Weir", category: "processor", installed: false, reachable: true, defaultHost: "localhost", defaultPort: 9347 };
const sabnzbd = { kind: "sabnzbd", displayName: "SABnzbd", category: "download-client", installed: false, reachable: false, defaultHost: "localhost", defaultPort: 8080 };

describe("what the installer found on this machine (#640)", () => {
  afterEach(() => fetchJson.mockReset());

  it("offers a detected, reachable download client with no host or port to type", async () => {
    fetchJson.mockResolvedValueOnce([qbittorrent, weir, sabnzbd]);
    const onUse = vi.fn();

    render(<DetectedComponents category="download-client" configuredKinds={[]} onUse={onUse} />);

    const button = await screen.findByRole("button", { name: "Use qBittorrent (localhost:8080)" });
    expect(screen.queryByText(/SABnzbd/)).toBeNull(); // neither installed nor reachable
    expect(screen.queryByText(/Weir/)).toBeNull(); // wrong category

    fireEvent.click(button);
    expect(onUse).toHaveBeenCalledWith(qbittorrent);
  });

  it("shows nothing once the detected component already has a saved connection", async () => {
    fetchJson.mockResolvedValueOnce([qbittorrent]);

    const { container } = render(<DetectedComponents category="download-client" configuredKinds={["qbittorrent"]} onUse={vi.fn()} />);

    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });

  it("shows nothing when detection fails or finds nothing in this category", async () => {
    fetchJson.mockResolvedValueOnce([qbittorrent]);

    const { container } = render(<DetectedComponents category="processor" configuredKinds={[]} onUse={vi.fn()} />);

    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });
});

describe("the sign-in the installer gave a client (#1050)", () => {
  afterEach(() => fetchJson.mockReset());

  it("reads it from the credentials endpoint for that client", async () => {
    const { fetchDetectedCredentials } = await import("./bundled-components-detected");
    fetchJson.mockResolvedValueOnce({ username: "deluno", password: "generated" });

    await expect(fetchDetectedCredentials("qbittorrent")).resolves.toEqual({ username: "deluno", password: "generated" });
    expect(fetchJson).toHaveBeenCalledWith("/api/system/bundled-components/qbittorrent/credentials");
  });

  it("falls back to no sign-in when there is none saved", async () => {
    const { fetchDetectedCredentials } = await import("./bundled-components-detected");
    fetchJson.mockRejectedValueOnce(new Error("404"));

    await expect(fetchDetectedCredentials("nzbget")).resolves.toBeNull();
  });
});
