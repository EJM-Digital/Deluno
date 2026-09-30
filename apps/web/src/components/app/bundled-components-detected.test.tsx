import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { DetectedComponents } = await import("./bundled-components-detected");

const qbittorrent = { kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, reachable: true, connected: false, defaultHost: "localhost", defaultPort: 8080 };
const weir = { kind: "weir", displayName: "Weir", category: "processor", installed: false, reachable: true, defaultHost: "localhost", defaultPort: 9347 };
const sabnzbd = { kind: "sabnzbd", displayName: "SABnzbd", category: "download-client", installed: false, reachable: false, defaultHost: "localhost", defaultPort: 8080 };
const nzbgetRunning = { kind: "nzbget", displayName: "NZBGet", category: "download-client", installed: false, reachable: true, defaultHost: "localhost", defaultPort: 6789 };

function show(props: Parameters<typeof DetectedComponents>[0]) {
  return render(<MemoryRouter><DetectedComponents {...props} /></MemoryRouter>);
}

describe("Connections placement of the installed-apps step (#1061)", () => {
  afterEach(() => fetchJson.mockReset());

  it("lists an installed download client with its own Connect button, not an automatic connection", async () => {
    fetchJson.mockResolvedValue([qbittorrent, weir, sabnzbd]);

    show({ category: "download-client", configuredKinds: [], onUse: vi.fn() });

    expect(await screen.findByRole("button", { name: "Connect qBittorrent" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Connect all" })).toBeTruthy();
    expect(screen.queryByText(/SABnzbd/)).toBeNull(); // neither installed nor reachable
    expect(screen.queryByText(/Weir/)).toBeNull(); // wrong category
    expect(fetchJson).not.toHaveBeenCalledWith(expect.stringContaining("/connect"), expect.anything());
  });

  it("still offers a running-but-not-installed app with no host or port to type", async () => {
    fetchJson.mockResolvedValue([nzbgetRunning]);
    const onUse = vi.fn();

    show({ category: "download-client", configuredKinds: [], onUse });

    fireEvent.click(await screen.findByRole("button", { name: "Use NZBGet (localhost:6789)" }));
    expect(onUse).toHaveBeenCalledWith(nzbgetRunning);
  });

  it("shows nothing once the app is connected or already has a saved connection", async () => {
    fetchJson.mockResolvedValue([{ ...qbittorrent, connected: true }, nzbgetRunning]);

    const { container } = show({ category: "download-client", configuredKinds: ["nzbget"], onUse: vi.fn() });

    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(container.textContent).toBe("");
  });
});
