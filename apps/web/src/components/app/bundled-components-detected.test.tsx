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

describe("which apps connect themselves (#1061)", () => {
  it("connects qBittorrent when a sign-in is held and every installed app that ships its own login", async () => {
    const { delunoConnectsItself } = await import("./bundled-components-detected");
    const base = { displayName: "x", category: "download-client", reachable: true, defaultHost: "localhost", defaultPort: 1 };
    expect(delunoConnectsItself({ ...base, kind: "qbittorrent", installed: true, hasCredentials: true })).toBe(true);
    for (const kind of ["nzbget", "deluge", "transmission"]) {
      expect(delunoConnectsItself({ ...base, kind, installed: true })).toBe(true);
    }
    // Not installed by anyone we know of, and nothing held: the form opens.
    expect(delunoConnectsItself({ ...base, kind: "nzbget", installed: false })).toBe(false);
    expect(delunoConnectsItself({ ...base, kind: "qbittorrent", installed: true })).toBe(false);
  });
});

describe("connecting an installed app (#1061)", () => {
  afterEach(() => fetchJson.mockReset());

  it("asks Deluno to connect it and gets back only whether it worked, never a sign-in", async () => {
    const { connectDetectedApp } = await import("./bundled-components-detected");
    fetchJson.mockResolvedValueOnce({ name: "qBittorrent", healthy: true, message: "qBittorrent is connected." });

    await expect(connectDetectedApp("qbittorrent")).resolves.toEqual({ name: "qBittorrent", healthy: true, message: "qBittorrent is connected." });
    expect(fetchJson).toHaveBeenCalledWith("/api/system/bundled-components/qbittorrent/connect", { method: "POST" });
  });

  it("has no way to read an app's sign-in", async () => {
    const module = await import("./bundled-components-detected");

    expect(Object.keys(module)).not.toContain("fetchDetectedCredentials");
  });
});

describe("a Use that could not sign in (#1061)", () => {
  afterEach(() => fetchJson.mockReset());

  it("hands back the host and port to pre-fill the form with, and no password", async () => {
    const { connectDetectedApp } = await import("./bundled-components-detected");
    const failed = { name: "NZBGet", healthy: false, message: "Nothing was saved: enter your own login and save it.", needsSignIn: true, protocol: "nzbget", host: "localhost", port: 6789 };
    fetchJson.mockResolvedValueOnce(failed);

    const result = await connectDetectedApp("nzbget");

    expect(result.needsSignIn).toBe(true);
    expect(result.port).toBe(6789);
    expect(Object.keys(result)).not.toContain("password");
  });
});
